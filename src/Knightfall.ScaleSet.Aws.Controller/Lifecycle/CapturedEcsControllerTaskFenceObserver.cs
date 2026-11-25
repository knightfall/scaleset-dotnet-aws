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
    private const int MaximumPages = 6;
    private const int PageSize = 8;
    private const int MaximumEventLength = 64 * 1024;
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(2);
    private static readonly JsonDocumentOptions EventOptions = new() { AllowDuplicateProperties = false, MaxDepth = 32 };

    public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
        current.ReadAsync(taskArn, cancellationToken);

    public async Task<ControllerTaskFenceObservation> ObserveAsync(string taskArn, DateTimeOffset? acquiredAt, CancellationToken cancellationToken)
    {
        var state = await current.ReadAsync(taskArn, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (state != ControllerTaskFenceStatus.Missing || acquiredAt is not { } acquired || acquired > now ||
            now < capture.QualifiedFrom || now >= capture.QualifiedUntil || !EcsControllerMetadata.MatchesTask(clusterArn, taskArn))
        {
            return new ControllerTaskFenceObservation(state);
        }

        using var timeout = new CancellationTokenSource(LookupBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var start = acquired > capture.QualifiedFrom ? acquired : capture.QualifiedFrom;
        try
        {
            if (await ReadExactTerminalEventAsync(taskArn, acquired, start, now, linked.Token).ConfigureAwait(false) is { } eventId &&
                clock.GetUtcNow() < capture.QualifiedUntil && !linked.IsCancellationRequested)
            {
                ReportCapture(eventId);
                return new ControllerTaskFenceObservation(ControllerTaskFenceStatus.Stopped, capture.QualifiedUntil);
            }
            return new ControllerTaskFenceObservation(state);
        }
        catch (Exception error) when (error is AmazonClientException or AmazonServiceException or HttpRequestException or
                                      IOException or SocketException or JsonException or TimeoutException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ControllerTaskFenceObservation(state);
        }
    }

    private async Task<Guid?> ReadExactTerminalEventAsync(string taskArn, DateTimeOffset acquired, DateTimeOffset start,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? token = null;
        (Guid EventId, string Digest)? evidence = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 0; page < MaximumPages; page++)
        {
            var response = await logs.FilterLogEventsAsync(new FilterLogEventsRequest
            {
                LogGroupIdentifier = capture.LogGroupArn,
                FilterPattern = $"\"{taskArn}\"",
                StartTime = start.ToUnixTimeMilliseconds(),
                EndTime = now.ToUnixTimeMilliseconds(),
                Limit = PageSize,
                NextToken = token,
            }, cancellationToken).ConfigureAwait(false);
            if (response.HttpStatusCode != HttpStatusCode.OK || response.Events is { Count: > PageSize })
            {
                return null;
            }
            foreach (var item in response.Events ?? [])
            {
                if (item.Message is not { Length: > 0 and <= MaximumEventLength } message ||
                    ParseTerminalEvent(message, taskArn, acquired, start, now) is not { } id)
                {
                    return null;
                }
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
                if (evidence is not null && evidence != (id, digest))
                {
                    return null;
                }
                evidence = (id, digest);
            }
            token = response.NextToken;
            if (token is null)
            {
                return evidence?.EventId;
            }
            if (token is not { Length: > 0 and <= 4096 } || !visited.Add(token))
            {
                return null;
            }
        }

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
}
