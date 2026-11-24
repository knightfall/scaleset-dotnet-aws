using System.Text;
using Knightfall.ScaleSet.Aws.Telemetry.Tests;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed record MixedOtlpMetricPoint(
    string InstanceId, string Name, IReadOnlyDictionary<string, string> Tags, double Number, int Batch, string Scope);

internal static class MixedOtlpAssertions
{
    internal const string Ready = "scaleset.aws.profile.ready";
    internal const string Assigned = "scaleset.aws.capacity.assigned";
    internal const string Active = "scaleset.aws.capacity.active";

    private const string AwsPrefix = "Knightfall.ScaleSet.Aws.";
    private const string Core = AwsPrefix + "Core";
    private const string GitHub = AwsPrefix + "GitHub";
    private const string Client = "ScaleSet.Client";
    private const string Listener = "ScaleSet.Listener";
    private const string Http = "System.Net.Http";
    private const string Runtime = "System.Runtime";
    private const string Instance = "service.instance.id";
    private const int MetricCardinalityLimit = 2_000;

    private static readonly string[] SignalPaths = ["/v1/traces", "/v1/metrics", "/v1/logs"];
    private static readonly HashSet<string> AwsComponents =
    [
        Core, GitHub, AwsPrefix + "Controller", AwsPrefix + "DynamoDb", AwsPrefix + "Providers.Ec2",
        AwsPrefix + "Providers.Fargate", AwsPrefix + "Handoff", AwsPrefix + "RunnerBootstrap",
    ];
    private static readonly string[] AwsDimensions = ["profile", "provider", "operation", "outcome", "reason"];
    private static readonly string[] GaugeDimensions = ["profile", "provider"];
    private static readonly string[] ClientDimensions = ["dependency", "method", "status_class"];
    private static readonly string[] ClientRetryDimensions = ["dependency", "method", "retry_kind"];
    private static readonly string[] HttpDimensions = ["http.request.method", "http.response.status_code", "network.protocol.version"];
    private static readonly string[] RuntimeDimensions = ["gc.heap.generation"];
    private static readonly string[] LogDimensions = ["SourceContext", "profile", "provider", "operation", "outcome", "reason"];
    private static readonly string[] ResourceKeys =
    [
        "service.name", "service.namespace", "service.version", Instance, "deployment.environment.name",
        "scaleset.deployment.name", "cloud.provider", "cloud.account.id", "cloud.region",
    ];

    /// <summary>Returns controller profile gauges with the originating export request index for batch-based polling.</summary>
    internal static IReadOnlyList<MixedOtlpMetricPoint> MetricPoints(IReadOnlyList<OtlpCapture> captures) =>
        GaugePoints(ReadResources(captures, "/v1/metrics")).ToArray();

    /// <summary>Checks captured AWS and released SDK telemetry. Select the current process for coverage assertions after replacement.</summary>
    internal static IReadOnlyCollection<string> AssertPayloads(
        IReadOnlyList<OtlpCapture> captures, IReadOnlyDictionary<string, string> expectedProfiles,
        IEnumerable<string> forbiddenValues, IReadOnlyDictionary<string, string> expectedResource,
        string? currentInstanceId = null, bool requireLogs = false)
    {
        Assert.NotEmpty(expectedProfiles);
        string[] forbidden = forbiddenValues.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var capture in captures)
        {
            Assert.Contains(capture.Path, SignalPaths);
            Assert.Equal("application/x-protobuf", capture.MediaType);
            Assert.Equal("HTTP/1.1", capture.Protocol);
            string payload = Encoding.UTF8.GetString(capture.Payload());
            Assert.All(forbidden.Where(value => value.Length >= 8),
                value => Assert.False(payload.Contains(value, StringComparison.Ordinal), "Sensitive fixture content appeared in an OTLP payload."));
        }

        var resources = ReadResources(captures).ToArray();
        var instances = resources.GroupBy(resource => resource.InstanceId, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(instances);
        foreach (var instance in instances)
        {
            Assert.True(Guid.TryParseExact(instance.Key, "N", out _));
            var identity = instance.First().Attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal).ToArray();
            foreach (var resource in instance)
            {
                var attributes = resource.Attributes;
                Assert.Equal(ResourceKeys.Order(StringComparer.Ordinal), attributes.Keys.Order(StringComparer.Ordinal));
                Assert.Equal(identity, attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal));
                Assert.Equal("knightfall.scaleset.aws", attributes["service.namespace"]);
                Assert.Equal("aws", attributes["cloud.provider"]);
                foreach (var (key, value) in expectedResource)
                {
                    Assert.Equal(value, attributes[key]);
                }
                Assert.All(attributes.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
                Assert.All(attributes.Values, value => AssertSafeText(value, forbidden));
            }
        }

        AssertMetrics(resources, expectedProfiles, forbidden);
        var spans = AssertSpans(resources, expectedProfiles, forbidden);
        var logs = AssertLogs(resources, expectedProfiles, forbidden);
        AssertCoverage(resources, spans, logs, expectedProfiles, currentInstanceId, requireLogs);
        return instances.Select(instance => instance.Key).ToArray();
    }

    private static void AssertMetrics(
        IEnumerable<ExportResource> resources, IReadOnlyDictionary<string, string> profiles, string[] forbidden)
    {
        var seenSeries = new Dictionary<(string Instance, string Scope, string Name), HashSet<string>>();
        foreach (var resource in resources.Where(resource => resource.Path == "/v1/metrics"))
        {
            foreach (var scope in resource.Message.Messages(2))
            {
                string name = scope.Message(1).Text(1);
                foreach (var metric in scope.Messages(2))
                {
                    string instrument = metric.Text(1);
                    string[] allowed = MetricDimensions(name, instrument);
                    var identity = (resource.InstanceId, name, instrument);
                    if (!seenSeries.TryGetValue(identity, out var series))
                    {
                        series = new HashSet<string>(StringComparer.Ordinal);
                        seenSeries.Add(identity, series);
                    }
                    var batch = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (point, attributes) in DataPoints(metric))
                    {
                        var tags = AssertAttributes(point, attributes, allowed, forbidden);
                        if (AwsComponents.Contains(name))
                        {
                            AssertProfile(tags, profiles);
                            if (IsGauge(instrument))
                            {
                                Assert.Equal(GaugeDimensions, tags.Keys.Order(StringComparer.Ordinal));
                            }
                        }
                        string signature = SeriesSignature(point, attributes);
                        Assert.True(batch.Add(signature), $"Duplicate OTLP series in one export for {name}/{instrument}.");
                        _ = series.Add(signature);
                    }
                    Assert.InRange(series.Count, 1, MetricCardinalityLimit);
                }
            }
        }
    }

    private static string[] MetricDimensions(string scope, string metric)
    {
        if (AwsComponents.Contains(scope))
        {
            Assert.StartsWith("scaleset.aws.", metric, StringComparison.Ordinal);
            return IsGauge(metric) ? GaugeDimensions : AwsDimensions;
        }
        return scope switch
        {
            Client when metric == "scaleset.client.retries" => ClientRetryDimensions,
            Client when metric is "scaleset.client.requests" or "scaleset.client.request.duration" => ClientDimensions,
            Http when metric == "http.client.request.duration" => HttpDimensions,
            Runtime when metric.StartsWith("dotnet.", StringComparison.Ordinal) => RuntimeDimensions,
            _ => throw new InvalidDataException($"Unexpected OTLP metric scope or instrument {scope}/{metric}."),
        };
    }

    private static CapturedSpan[] AssertSpans(IEnumerable<ExportResource> resources,
        IReadOnlyDictionary<string, string> profiles, string[] forbidden)
    {
        var captured = new List<CapturedSpan>();
        foreach (var resource in resources.Where(resource => resource.Path == "/v1/traces"))
        {
            foreach (var scope in resource.Message.Messages(2))
            {
                string source = scope.Message(1).Text(1);
                foreach (var span in scope.Messages(2))
                {
                    string name = span.Text(5);
                    var tags = AssertAttributes(span, 9, SpanDimensions(source), forbidden);
                    AssertSafeText(name, forbidden);
                    if (AwsComponents.Contains(source))
                    {
                        Assert.StartsWith("scaleset.aws.", name, StringComparison.Ordinal);
                        AssertProfile(tags, profiles);
                    }
                    Assert.Empty(span.Messages(11)); // Activity events may hold unapproved fields.
                    Assert.Empty(span.Messages(13)); // Activity links may hold unapproved fields.
                    captured.Add(new CapturedSpan(resource.InstanceId, source, name, tags,
                        Convert.ToHexString(span.Bytes(1)), Convert.ToHexString(span.Bytes(2)), Convert.ToHexString(span.BytesOrEmpty(4))));
                }
            }
        }
        return [.. captured];
    }

    private static string[] SpanDimensions(string source) => source switch
    {
        Client => ["http.request.method", "url.scheme", "scaleset.dependency"],
        Listener => ["scaleset.listener.operation", "scaleset.listener.outcome"],
        Http => ["http.request.method", "http.response.status_code", "network.protocol.version", "server.address"],
        _ when AwsComponents.Contains(source) => AwsDimensions,
        _ => throw new InvalidDataException($"Unexpected OTLP activity scope {source}."),
    };

    private static CapturedLog[] AssertLogs(IEnumerable<ExportResource> resources,
        IReadOnlyDictionary<string, string> profiles, string[] forbidden)
    {
        var captured = new List<CapturedLog>();
        foreach (var resource in resources.Where(resource => resource.Path == "/v1/logs"))
        {
            foreach (var log in resource.Message.Messages(2).SelectMany(scope => scope.Messages(2)))
            {
                AssertSafeText(log.Message(5).Text(1), forbidden);
                var tags = AssertAttributes(log, 6, LogDimensions, forbidden);
                AssertProfile(tags, profiles);
                captured.Add(new CapturedLog(resource.InstanceId, Convert.ToHexString(log.BytesOrEmpty(9)),
                    Convert.ToHexString(log.BytesOrEmpty(10))));
            }
        }
        return [.. captured];
    }

    private static void AssertCoverage(ExportResource[] resources, CapturedSpan[] spans, CapturedLog[] logs,
        IReadOnlyDictionary<string, string> profiles, string? current, bool requireLogs)
    {
        var gauges = GaugePoints(resources.Where(resource => resource.Path == "/v1/metrics" && Selected(resource.InstanceId))).ToArray();
        var relevantSpans = spans.Where(span => Selected(span.InstanceId)).ToArray();
        foreach (var profile in profiles)
        {
            Assert.Contains(gauges, point => point.Name == Ready && point.Tags["profile"] == profile.Key);
            Assert.Contains(relevantSpans, span => span.Scope == Core && span.Name == "scaleset.aws.reconcile.profile" && Matches(span, profile));
            string providerScope = AwsPrefix + (profile.Value == "ec2" ? "Providers.Ec2" : "Providers.Fargate");
            Assert.Contains(relevantSpans, span => span.Scope == providerScope && Matches(span, profile));
            Assert.Contains(relevantSpans, span => span.Scope == GitHub &&
                span.Name.StartsWith("scaleset.aws.listener.", StringComparison.Ordinal) && Matches(span, profile));
        }
        Assert.Contains(gauges, point => point.Name == Assigned);
        Assert.Contains(gauges, point => point.Name == Active);
        Assert.Contains(relevantSpans, span => span.Scope == Listener && span.Name == "scaleset.listener.step" &&
            relevantSpans.Any(parent => parent.InstanceId == span.InstanceId && parent.Trace == span.Trace && parent.Id == span.Parent &&
                parent.Scope == GitHub && parent.Name == "scaleset.aws.listener.step"));
        Assert.Contains(relevantSpans, span => span.Scope == Client && span.Name == "scaleset.client.request" &&
            span.Tags.ContainsKey("scaleset.dependency"));

        var clientMetrics = resources.Where(resource => resource.Path == "/v1/metrics" && Selected(resource.InstanceId))
            .SelectMany(resource => resource.Message.Messages(2)).Where(scope => scope.Message(1).Text(1) == Client)
            .SelectMany(scope => scope.Messages(2)).Select(metric => metric.Text(1)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("scaleset.client.requests", clientMetrics);
        Assert.Contains("scaleset.client.request.duration", clientMetrics);
        if (requireLogs)
        {
            Assert.Contains(logs, log => Selected(log.InstanceId) && log.Trace.Length == 32 && log.Span.Length == 16 &&
                relevantSpans.Any(span => span.InstanceId == log.InstanceId && span.Trace == log.Trace && span.Id == log.Span));
        }

        bool Selected(string instanceId) => current is null || instanceId == current;
    }

    private static bool Matches(CapturedSpan span, KeyValuePair<string, string> profile) =>
        span.Tags.GetValueOrDefault("profile") == profile.Key && span.Tags.GetValueOrDefault("provider") == profile.Value;

    private static Dictionary<string, string> AssertAttributes(OtlpMessage owner, int number, string[] allowed, string[] forbidden)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in owner.Messages(number))
        {
            string key = attribute.Text(1);
            Assert.Contains(key, allowed);
            var value = attribute.Message(2).BytesOrEmpty(1);
            if (value.Length > 0)
            {
                string text = Encoding.UTF8.GetString(value);
                AssertSafeText(text, forbidden);
                strings.Add(key, text);
            }
        }
        return strings;
    }

    private static void AssertProfile(Dictionary<string, string> tags, IReadOnlyDictionary<string, string> profiles)
    {
        if (tags.TryGetValue("provider", out var provider))
        {
            Assert.True(provider is "ec2" or "fargate", "Telemetry reported an unknown provider.");
        }
        if (tags.TryGetValue("profile", out var profile))
        {
            Assert.True(profiles.TryGetValue(profile, out var expected), "Telemetry reported a profile outside the verified configuration.");
            Assert.Equal(expected, provider);
        }
    }

    private static void AssertSafeText(string text, IEnumerable<string> forbidden)
    {
        foreach (string value in forbidden)
        {
            if (value.Length >= 8)
            {
                Assert.False(text.Contains(value, StringComparison.Ordinal), "Sensitive fixture content appeared in an OTLP string.");
                continue;
            }
            // Short numerical IDs are matched as tokens so a status, time, or another ID does not create a false match.
            int start = 0;
            while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
            {
                int end = start + value.Length;
                bool left = start == 0 || !char.IsAsciiLetterOrDigit(text[start - 1]);
                bool right = end == text.Length || !char.IsAsciiLetterOrDigit(text[end]);
                Assert.False(left && right, "A forbidden fixture identifier appeared in an OTLP string.");
                start = end;
            }
        }
    }

    private static string SeriesSignature(OtlpMessage point, int attributes) => string.Join('|',
        point.Messages(attributes).OrderBy(attribute => attribute.Text(1), StringComparer.Ordinal)
            .Select(attribute => attribute.Text(1) + "=" + Convert.ToHexString(attribute.Bytes(2))));

    private static IEnumerable<(OtlpMessage Point, int Attributes)> DataPoints(OtlpMessage metric) =>
        metric.Messages(5).Concat(metric.Messages(7)).SelectMany(data => data.Messages(1)).Select(point => (point, 7))
            .Concat(metric.Messages(9).SelectMany(data => data.Messages(1)).Select(point => (point, 9)))
            .Concat(metric.Messages(10).SelectMany(data => data.Messages(1)).Select(point => (point, 1)))
            .Concat(metric.Messages(11).SelectMany(data => data.Messages(1)).Select(point => (point, 7)));

    private static IEnumerable<MixedOtlpMetricPoint> GaugePoints(IEnumerable<ExportResource> resources)
    {
        foreach (var resource in resources)
        {
            foreach (var scope in resource.Message.Messages(2))
            {
                string name = scope.Message(1).Text(1);
                foreach (var metric in scope.Messages(2).Where(metric => IsGauge(metric.Text(1))))
                {
                    Assert.Equal(Core, name);
                    string instrument = metric.Text(1);
                    foreach (var point in metric.Message(5).Messages(1))
                    {
                        var tags = point.StringAttributes(7);
                        Assert.Equal(GaugeDimensions, tags.Keys.Order(StringComparer.Ordinal));
                        long value = unchecked((long)point.Number(6));
                        Assert.InRange(value, 0L, instrument == Ready ? 1L : long.MaxValue);
                        yield return new MixedOtlpMetricPoint(resource.InstanceId, instrument, tags, value, resource.Batch, name);
                    }
                }
            }
        }
    }

    private static bool IsGauge(string name) => name is Ready or Assigned or Active;

    private static IEnumerable<ExportResource> ReadResources(IReadOnlyList<OtlpCapture> captures, string? path = null)
    {
        for (int batch = 0; batch < captures.Count; batch++)
        {
            var capture = captures[batch];
            if (path is not null && capture.Path != path)
            {
                continue;
            }
            foreach (var resource in new OtlpMessage(capture.Payload()).Messages(1))
            {
                var attributes = resource.Message(1).StringAttributes(1);
                yield return new ExportResource(batch, capture.Path, attributes[Instance], attributes, resource);
            }
        }
    }

    private sealed record ExportResource(int Batch, string Path, string InstanceId, Dictionary<string, string> Attributes, OtlpMessage Message);
    private sealed record CapturedSpan(string InstanceId, string Scope, string Name, Dictionary<string, string> Tags, string Trace, string Id, string Parent);
    private sealed record CapturedLog(string InstanceId, string Trace, string Span);
}
