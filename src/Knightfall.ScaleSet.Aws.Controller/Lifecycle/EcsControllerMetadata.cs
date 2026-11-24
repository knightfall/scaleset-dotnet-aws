using System.Net;
using System.Text.Json;
using Amazon;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

public sealed record ControllerTaskAuthority(string TaskArn, string ClusterArn, ControllerClockEvidence Clock);

public sealed class EcsControllerMetadata : IDisposable
{
    private const string MetadataVariable = "ECS_CONTAINER_METADATA_URI_V4";
    private const string PathPrefix = "/v4/";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumReferenceAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumProofLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumSkew = TimeSpan.FromSeconds(1);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 16, AllowDuplicateProperties = false };

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        MaxResponseHeadersLength = 16,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly TimeProvider _clock;
    private readonly Uri? _taskEndpoint;
    private readonly string _clusterName;

    public EcsControllerMetadata(RuntimeConfiguration configuration, TimeProvider clock)
        : this(configuration, clock, Environment.GetEnvironmentVariable(MetadataVariable), allowLoopback: false)
    {
    }

    internal EcsControllerMetadata(RuntimeConfiguration configuration, TimeProvider clock, string? metadataEndpoint, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clock);
        ClusterArn = ResolveClusterArn(configuration);
        _clusterName = ClusterArn[(ClusterArn.LastIndexOf('/') + 1)..];
        _taskEndpoint = ParseTaskEndpoint(metadataEndpoint, allowLoopback);
        _clock = clock;
    }

    public string ClusterArn { get; }

    internal static string ResolveClusterArn(RuntimeConfiguration configuration)
    {
        string account = configuration.Aws.Account;
        string region = configuration.Aws.Region;
        string prefix = $"arn:{RegionEndpoint.GetBySystemName(region).PartitionName}:ecs:{region}:{account}:cluster/";
        string? arn = configuration.EcsClusters[configuration.Controller.Cluster] switch
        {
            CreateEcsClusterConfiguration created => prefix + created.Name,
            ImportEcsClusterConfiguration imported => imported.Arn,
            _ => null,
        };
        if (arn?.StartsWith(prefix, StringComparison.Ordinal) != true ||
            arn[prefix.Length..] is not { Length: > 0 and <= 255 } name || !IsResourceSegment(name))
        {
            throw new ArgumentException("The configured controller cluster must be in the deployment account and Region.", nameof(configuration));
        }
        return arn;
    }

    internal static bool MatchesTask(string clusterArn, string taskArn)
    {
        string prefix = clusterArn.Replace(":cluster/", ":task/", StringComparison.Ordinal) + "/";
        return taskArn.StartsWith(prefix, StringComparison.Ordinal) && taskArn.Length == prefix.Length + 32 &&
            taskArn[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    public async Task<ControllerClockEvidence?> ReadClockAsync(string expectedTaskArn, CancellationToken cancellationToken)
    {
        var observation = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return observation is not null && observation.TaskArn == expectedTaskArn ? observation.Clock : null;
    }

    public async Task<ControllerTaskAuthority?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_taskEndpoint is null)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(ReadTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = _clock.GetUtcNow();
        long timestamp = _clock.GetTimestamp();
        try
        {
            using var response = await _http.GetAsync(_taskEndpoint, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            byte[] body = new byte[MaximumResponseBytes + 1];
            int length = await stream.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, linked.Token).ConfigureAwait(false);
            if (length > MaximumResponseBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body.AsMemory(0, length), JsonOptions);
            var now = _clock.GetUtcNow();
            var elapsed = _clock.GetElapsedTime(timestamp);
            if (now < started || elapsed < TimeSpan.Zero || elapsed >= ReadTimeout ||
                (now - started - elapsed).Duration() >= MaximumSkew)
            {
                return null;
            }

            return ParseAuthority(document.RootElement, now, elapsed);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private ControllerTaskAuthority? ParseAuthority(JsonElement task, DateTimeOffset now, TimeSpan elapsed)
    {
        string? cluster = StringValue(task, "Cluster");
        if ((cluster != ClusterArn && cluster != _clusterName) || StringValue(task, "LaunchType") != "FARGATE" ||
            StringValue(task, "TaskARN") is not { } taskArn || !MatchesTask(ClusterArn, taskArn) ||
            !task.TryGetProperty("ClockDrift", out var drift) || StringValue(drift, "ClockSynchronizationStatus") != "SYNCHRONIZED" ||
            !drift.TryGetProperty("ReferenceTimestamp", out var timestamp) || StringValue(drift, "ReferenceTimestamp")?.EndsWith('Z') != true ||
            !timestamp.TryGetDateTimeOffset(out var reference) || !drift.TryGetProperty("ClockErrorBound", out var error) ||
            error.ValueKind != JsonValueKind.Number || !error.TryGetDecimal(out decimal milliseconds) ||
            milliseconds < 0 || milliseconds >= 1000)
        {
            return null;
        }

        var skew = TimeSpan.FromTicks((long)decimal.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond)) + elapsed;
        if (skew >= MaximumSkew || reference - now > skew || now - reference >= MaximumReferenceAge)
        {
            return null;
        }

        var freshness = MaximumReferenceAge - (now - reference);
        var remaining = freshness < MaximumProofLifetime ? freshness : MaximumProofLifetime;
        return remaining > skew ? new ControllerTaskAuthority(taskArn, ClusterArn, new ControllerClockEvidence(now, now + remaining, skew)) : null;
    }

    internal static Uri? ParseTaskEndpoint(string? raw, bool allowLoopback)
    {
        if (raw is not { Length: > 0 and <= 256 } || !raw.StartsWith("http://", StringComparison.Ordinal) ||
            raw.Contains('%') || raw.Contains('\\') || raw.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.AbsoluteUri != raw ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !((uri.Host == "169.254.170.2" && uri.IsDefaultPort) || (allowLoopback && uri.Host == "127.0.0.1")) ||
            !uri.AbsolutePath.StartsWith(PathPrefix, StringComparison.Ordinal) ||
            uri.AbsolutePath[PathPrefix.Length..] is not { Length: > 0 and <= 128 } context || !IsResourceSegment(context))
        {
            return null;
        }

        return new Uri(uri.AbsoluteUri + "/task");
    }

    private static bool IsResourceSegment(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string? StringValue(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public void Dispose() => _http.Dispose();
}
