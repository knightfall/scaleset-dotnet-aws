using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.CloudWatchLogs;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Microsoft.Extensions.Logging;
using FilterLogEventsRequest = Amazon.CloudWatchLogs.Model.FilterLogEventsRequest;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

/// <summary>Accepts terminal ECS history only from a deployment-qualified, read-only capture.</summary>
internal sealed partial class CapturedEcsControllerTaskFenceObserver(
    IControllerTaskFenceObserver current, IAmazonCloudWatchLogs logs, AwsEnvironment aws,
    string clusterArn, ControllerPredecessorCapture capture, TimeProvider clock, ILogger? logger = null) : IControllerTaskFenceObserver
{
    private const int MaximumPagesPerLookup = 6;
    private const int MaximumPagesPerScan = 120;
    /// <summary>Recovery can require both the previous owner and an earlier unresolved predecessor.</summary>
    private const int MaximumScans = 2;
    private const int PageSize = 8;
    private const int MaximumEventLength = 64 * 1024;
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonDocumentOptions EventOptions = new() { AllowDuplicateProperties = false, MaxDepth = 32 };
    private readonly Dictionary<string, CaptureScan> _scans = new(StringComparer.Ordinal);
    private int _observing;

    public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
        current.ReadAsync(taskArn, cancellationToken);

    public async Task<ControllerTaskFenceObservation> ObserveAsync(string taskArn, DateTimeOffset? acquiredAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _observing, 1, 0) != 0)
        {
            return new ControllerTaskFenceObservation(ControllerTaskFenceStatus.Unknown);
        }
        try
        {
            return await ObserveCoreAsync(taskArn, acquiredAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _observing, 0);
        }
    }

    private async Task<ControllerTaskFenceObservation> ObserveCoreAsync(string taskArn, DateTimeOffset? acquiredAt, CancellationToken cancellationToken)
    {
        var state = await current.ReadAsync(taskArn, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        if (now < capture.QualifiedFrom || now >= capture.QualifiedUntil)
        {
            _scans.Clear();
            return new ControllerTaskFenceObservation(state);
        }
        if (state != ControllerTaskFenceStatus.Missing || acquiredAt is not { } acquired || acquired > now ||
            !EcsControllerMetadata.MatchesTask(clusterArn, taskArn))
        {
            _scans.Remove(taskArn);
            return new ControllerTaskFenceObservation(state);
        }

        var scan = ReadOrStartScan(taskArn, acquired, now);
        using var timeout = new CancellationTokenSource(LookupBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var evidence = await ReadExactTerminalEventAsync(taskArn, scan, linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var verifiedAt = clock.GetUtcNow();
            if (!scan.IsCurrent(verifiedAt) || verifiedAt >= capture.QualifiedUntil)
            {
                _scans.Remove(taskArn);
            }
            else if (evidence is { } eventId && !linked.IsCancellationRequested)
            {
                ReportCapture(eventId);
                return new ControllerTaskFenceObservation(ControllerTaskFenceStatus.Stopped, capture.QualifiedUntil);
            }
            return new ControllerTaskFenceObservation(state);
        }
        catch (Exception error) when (error is AmazonClientException or AmazonServiceException or HttpRequestException or
                                      IOException or SocketException or JsonException or TimeoutException or OperationCanceledException)
        {
            // A canceled lookup can resume after its last validated page. An AWS rejection, including
            // an invalid continuation, discards the entire scan before another lookup can authorize it.
            if (error is not OperationCanceledException || !linked.IsCancellationRequested || scan.NextToken is null)
            {
                _scans.Remove(taskArn);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ControllerTaskFenceObservation(state);
        }
    }

    private CaptureScan ReadOrStartScan(string taskArn, DateTimeOffset acquired, DateTimeOffset now)
    {
        if (_scans.TryGetValue(taskArn, out var scan) && scan.Acquired == acquired && scan.IsCurrent(now))
        {
            return scan;
        }

        _scans.Remove(taskArn);
        if (_scans.Count == MaximumScans)
        {
            _scans.Remove(_scans.MinBy(pair => pair.Value.End).Key);
        }
        var start = acquired > capture.QualifiedFrom ? acquired : capture.QualifiedFrom;
        scan = new CaptureScan(acquired, start, now);
        _scans.Add(taskArn, scan);
        return scan;
    }

    private async Task<Guid?> ReadExactTerminalEventAsync(string taskArn, CaptureScan scan, CancellationToken cancellationToken)
    {
        if (scan.Complete && scan.Witness is { } witness)
        {
            return await RecheckCompleteScanAsync(taskArn, scan, witness, cancellationToken).ConfigureAwait(false)
                ? witness.Evidence.Id : DiscardScan(taskArn);
        }

        bool resumed = scan.NextToken is not null;
        for (int page = 0; page < MaximumPagesPerLookup; page++)
        {
            var read = await ReadPageAsync(taskArn, scan, scan.NextToken, cancellationToken).ConfigureAwait(false);
            if (read is null || (read.Evidence is { } observed && scan.Witness is { } prior && prior.Evidence != observed))
            {
                return DiscardScan(taskArn);
            }
            if (read.Evidence is { } evidence)
            {
                scan.Witness = new CaptureWitness(scan.NextToken, evidence);
            }
            if (read.NextToken is not { } token)
            {
                var id = scan.Witness?.Evidence.Id;
                if (!resumed || id is null)
                {
                    _scans.Remove(taskArn);
                }
                scan.Complete = true;
                return id;
            }
            if (scan.Visited.Count >= MaximumPagesPerScan - 1 || !scan.Visited.Add(token))
            {
                return DiscardScan(taskArn);
            }
            scan.NextToken = token;
        }

        return null;
    }

    private async Task<bool> RecheckCompleteScanAsync(string taskArn, CaptureScan scan, CaptureWitness witness,
        CancellationToken cancellationToken)
    {
        // The adapter and effect coordinator each observe the fence. Recheck the witness and final
        // page under the same Logs permission and query instead of restarting an over-budget scan.
        var final = await ReadPageAsync(taskArn, scan, scan.NextToken, cancellationToken).ConfigureAwait(false);
        if (final is not { NextToken: null } || (final.Evidence is { } observed && observed != witness.Evidence))
        {
            return false;
        }
        if (witness.PageToken == scan.NextToken)
        {
            return final.Evidence == witness.Evidence;
        }
        var page = await ReadPageAsync(taskArn, scan, witness.PageToken, cancellationToken).ConfigureAwait(false);
        return page is { NextToken: not null } && page.NextToken != witness.PageToken && page.Evidence == witness.Evidence;
    }

    private async Task<CapturePage?> ReadPageAsync(string taskArn, CaptureScan scan, string? token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await logs.FilterLogEventsAsync(new FilterLogEventsRequest
        {
            LogGroupIdentifier = capture.LogGroupArn,
            FilterPattern = $"\"{taskArn}\"",
            // The CloudWatch timestamp can be the second-precision EventBridge envelope time.
            StartTime = scan.Start.ToUnixTimeSeconds() * 1000,
            EndTime = scan.End.ToUnixTimeMilliseconds(),
            Limit = PageSize,
            NextToken = token,
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (response is not { HttpStatusCode: HttpStatusCode.OK } || response.Events is { Count: > PageSize } ||
            response.NextToken is { Length: 0 or > 4096 })
        {
            return null;
        }

        TerminalEvent? evidence = null;
        foreach (var item in response.Events ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item?.Message is not { Length: > 0 and <= MaximumEventLength } message ||
                ParseTerminalEvent(message, taskArn, scan.Acquired, scan.Start, scan.End) is not { } id)
            {
                return null;
            }
            var observed = new TerminalEvent(id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message))));
            if (evidence is not null && evidence != observed)
            {
                return null;
            }
            evidence = observed;
        }
        return new CapturePage(response.NextToken, evidence);
    }

    private Guid? DiscardScan(string taskArn)
    {
        _scans.Remove(taskArn);
        return null;
    }

    private Guid? ParseTerminalEvent(string message, string taskArn, DateTimeOffset acquired, DateTimeOffset start, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(message, EventOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || ReadText(root, "version") != "0" ||
            ReadText(root, "source") != "aws.ecs" || ReadText(root, "detail-type") != "ECS Task State Change" ||
            root.TryGetProperty("replay-name", out _) ||
            ReadText(root, "account") != aws.Account || ReadText(root, "region") != aws.Region ||
            !Guid.TryParseExact(ReadText(root, "id"), "D", out var eventId) || eventId == Guid.Empty ||
            !ReadTimestamp(root, "time", out var eventTime) || eventTime > now || eventTime < capture.QualifiedFrom ||
            !root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array ||
            resources.GetArrayLength() != 1 || resources[0].ValueKind != JsonValueKind.String || resources[0].GetString() != taskArn ||
            !root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadText(detail, "clusterArn") == clusterArn && ReadText(detail, "taskArn") == taskArn &&
            ReadText(detail, "launchType") == "FARGATE" && ReadText(detail, "lastStatus") == "STOPPED" &&
            (!detail.TryGetProperty("desiredStatus", out _) || ReadText(detail, "desiredStatus") == "STOPPED") &&
            detail.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
            version.TryGetInt64(out var number) && number > 0 &&
            ReadTimestamp(detail, "createdAt", out var createdAt) && createdAt <= acquired &&
            ReadTimestamp(detail, "stoppedAt", out var stoppedAt) && stoppedAt >= start && stoppedAt <= now &&
            // The envelope time is second-precision. ECS stoppedAt is the terminal evidence.
            eventTime > stoppedAt - TimeSpan.FromSeconds(1) ? eventId : null;
    }

    private void ReportCapture(Guid eventId)
    {
        if (logger is null)
        {
            return;
        }
        try
        {
            string reference = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(eventId.ToString("D"))).AsSpan(0, 8));
            using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
            {
                new("reason", "predecessor_capture_verified"),
                new("evidence_reference", reference),
            });
            LogCapture(logger, "reconcile", "success");
        }
        catch (Exception error) when (error is AggregateException or IOException or InvalidOperationException)
        {
            // Diagnostics never authorize or block recovery.
        }
    }

    [LoggerMessage(EventId = 903, Level = LogLevel.Information, Message = TelemetryNames.EventTemplate)]
    private static partial void LogCapture(ILogger logger, string operation, string outcome);

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadTimestamp(JsonElement element, string name, out DateTimeOffset parsed)
    {
        parsed = default;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString()?.EndsWith('Z') == true && value.TryGetDateTimeOffset(out parsed) && parsed >= DateTimeOffset.UnixEpoch;
    }

    private sealed record TerminalEvent(Guid Id, string Digest);
    private sealed record CapturePage(string? NextToken, TerminalEvent? Evidence);
    private sealed record CaptureWitness(string? PageToken, TerminalEvent Evidence);

    private sealed class CaptureScan(DateTimeOffset acquired, DateTimeOffset start, DateTimeOffset end)
    {
        public DateTimeOffset Acquired { get; } = acquired;
        public DateTimeOffset Start { get; } = start;
        public DateTimeOffset End { get; } = end;
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
        public string? NextToken { get; set; }
        public CaptureWitness? Witness { get; set; }
        public bool Complete { get; set; }

        public bool IsCurrent(DateTimeOffset now) => now >= End && now - End < ScanLifetime;
    }
}
