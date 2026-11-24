using System.Globalization;
using System.Text;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Telemetry.Tests;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

[CollectionDefinition(nameof(GitHubLifecycleIntegrationTests), DisableParallelization = true)]
public sealed class GitHubLifecycleIntegrationSuite;

[Collection(nameof(GitHubLifecycleIntegrationTests))]
public sealed partial class GitHubLifecycleIntegrationTests
{
    private const string AwsListenerScope = "Knightfall.ScaleSet.Aws.GitHub";
    private const string AwsPersistenceScope = "Knightfall.ScaleSet.Aws.DynamoDb";
    private const string ClientScope = "ScaleSet.Client";
    private const string ListenerScope = "ScaleSet.Listener";
    private const string HttpScope = "System.Net.Http";
    private const long CapturedJob = 8_215_432_123L;
    private const long HeldBackJob = CapturedJob + 1;
    private static readonly string[] RetryDimensions = ["dependency", "method", "retry_kind"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReleasedClientAndListenerExportSafeCorrelatedSignals(bool sampleTraces)
    {
        var token = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, respond: null, token);
        var result = await RunTelemetryScenarioAsync(receiver.Url,
            sampleTraces ? ExportMode.Sampled : ExportMode.Unsampled, token);

        AssertDecision(result.Decision);
        var requests = receiver.Requests;
        Assert.Contains(requests, request => request.Path == "/v1/metrics");
        Assert.DoesNotContain(requests, request => request.Path is not ("/v1/metrics" or "/v1/traces"));
        Assert.All(requests, request => Assert.Equal("application/x-protobuf", request.MediaType));
        AssertSafePayloads(requests, result.SessionId);
        AssertCapturedMetrics(requests);
        if (sampleTraces)
        {
            AssertCapturedTraces(requests);
        }
        else
        {
            Assert.DoesNotContain(requests, request => request.Path == "/v1/traces");
        }
    }

    [Fact]
    public async Task ReleasedClientReadRetryExportsOnlyItsDocumentedDimensions()
    {
        var token = TestContext.Current.CancellationToken;
        var simulator = new GitHubSimulator(Group);
        await using var github = await GitHubProtocolHost.StartAsync(simulator, token);
        using var http = github.CreateClient();
        using var client = new ScaleSetClient(GitHubTarget.Parse("https://github.com/enterprises/engineering"),
            ScaleSetCredentials.FromPersonalAccessToken(GitHubSimulator.FirstPat), httpClient: http);
        var set = await client.CreateScaleSetAsync(new RunnerScaleSet { Name = "linux", RunnerGroupId = GitHubSimulator.GroupId,
            Labels = [new Label { Name = "linux", Type = "System" }] }, token);
        var session = await client.CreateMessageSessionAsync(set.Id, "owner", token);
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, respond: null, token);
        var settings = TelemetrySettings.Load(readEnvironment: name => name switch
        {
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT" => receiver.Url + "/v1/metrics",
            "OTEL_EXPORTER_OTLP_PROTOCOL" => "http/protobuf",
            _ => null,
        });
        await using var pipeline = TelemetryPipeline.Start(settings);

        simulator.FailNext(SimulatorOperation.Poll, SimulatorFault.RateLimited);
        using var retry = ScaleSetRequestRetry.Configure(static options => options with
        {
            MaxRetries = 1,
            OnRateLimit = static (_, _) => ValueTask.FromResult(ScaleSetRateLimitDecision.RetryAfter(TimeSpan.FromMilliseconds(1))),
        });
        Assert.Null(await session.GetMessageAsync(0, 0, token));
        Assert.True(await pipeline.StopAsync().WaitAsync(token));

        Assert.Equal(2, simulator.Requests.Count(request => request.Host == GitHubProtocolHost.QueueHost && request.Method == "GET"));
        var metric = Assert.Single(Scopes(receiver.Requests, "/v1/metrics").Where(scope => scope.Name == ClientScope)
            .SelectMany(scope => scope.Records), record => record.Text(1) == "scaleset.client.retries");
        Assert.Equal("{retry}", metric.Text(3));
        var point = Assert.Single(Points(metric));
        var tags = point.Record.StringAttributes(point.Attributes);
        Assert.Equal(RetryDimensions, tags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("queue", tags["dependency"]);
        Assert.Equal("GET", tags["method"]);
        Assert.Equal("rate-limit", tags["retry_kind"]);
        Assert.Equal(1UL, point.Record.Number(6));
    }

    [Fact]
    public async Task ExportRejectionPreservesAdmissionCheckpointAndOwnershipComparedToDisabledExport()
    {
        var token = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, static _ => ReceiverReply.Rejected, token);
        var disabled = await RunTelemetryScenarioAsync(receiver.Url, ExportMode.Disabled, token);
        Assert.Empty(receiver.Requests);
        var rejected = await RunTelemetryScenarioAsync(receiver.Url, ExportMode.Rejected, token);

        AssertDecision(disabled.Decision);
        Assert.Equal(disabled.Decision, rejected.Decision);
        Assert.Contains(receiver.Requests, request => request.Path == "/v1/traces");
        Assert.Contains(receiver.Requests, request => request.Path == "/v1/metrics");
        Assert.Contains("rejected", rejected.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-collector-rejection", rejected.Diagnostics, StringComparison.Ordinal);
        AssertSafePayloads(receiver.Requests, rejected.SessionId);
    }

    private static async Task<(ListenerDecision Decision, Guid SessionId, string Diagnostics)> RunTelemetryScenarioAsync(
        string endpoint, ExportMode exportMode, CancellationToken cancellationToken)
    {
        await using var rig = await SessionRig.CreateAsync(cancellationToken);
        var settings = TelemetrySettings.Load(runtime: rig.Configuration, readEnvironment: name => name switch
        {
            "OTEL_SDK_DISABLED" => exportMode == ExportMode.Disabled ? "true" : "false",
            "OTEL_EXPORTER_OTLP_ENDPOINT" => endpoint,
            "OTEL_EXPORTER_OTLP_PROTOCOL" => "http/protobuf",
            "OTEL_TRACES_SAMPLER" => "parentbased_traceidratio",
            "OTEL_TRACES_SAMPLER_ARG" => exportMode == ExportMode.Unsampled ? "0" : "1",
            _ => null,
        });
        await using var diagnostics = new ExportDiagnostic();
        await using var pipeline = TelemetryPipeline.Start(settings, diagnostics: diagnostics);

        rig.Simulator.TotalAssignedJobs = 1;
        using var lane = await rig.OpenLaneAsync(rig.Lease, cancellationToken);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(cancellationToken)).Step?.Status);
        rig.Simulator.FailNext(SimulatorOperation.Poll, SimulatorFault.RateLimited);
        var rateLimit = await lane.StepAsync(cancellationToken);
        var retryAt = Assert.IsType<DateTimeOffset>(rateLimit.RetryAt);
        Assert.Equal(429, rateLimit.Failure?.StatusCode);
        Assert.Equal(rig.Clock.Now.AddSeconds(3), retryAt);
        Assert.Equal(retryAt, lane.ReadCredentialCooldown());
        var cooldown = await lane.StepAsync(cancellationToken);
        Assert.Equal(ScaleSetListenerStepStatus.Deferred, cooldown.Step?.Status);
        rig.Clock.Now = retryAt;
        Assert.Equal(0, rig.Simulator.Offer(CapturedJob, HeldBackJob));
        var accepted = await lane.StepAsync(cancellationToken);
        int held = await rig.ReadHeldAsync(cancellationToken);
        bool firstReserved = await rig.Store.ReadRecordAsync(LifecycleKey.Acquisition(rig.Profile, CapturedJob), cancellationToken) is not null;
        bool secondReserved = await rig.Store.ReadRecordAsync(LifecycleKey.Acquisition(rig.Profile, HeldBackJob), cancellationToken) is not null;

        string diagnostic = exportMode == ExportMode.Rejected
            ? await diagnostics.FirstLine.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken) : string.Empty;
        rig.Simulator.Complete(CapturedJob);
        rig.Simulator.FailNext(SimulatorOperation.Acknowledge, SimulatorFault.DropAfterCommit);
        var lost = await lane.StepAsync(cancellationToken);
        var session = Assert.IsType<SessionRecord>(await rig.Store.ReadSessionAsync(rig.Profile, cancellationToken));
        var sessionId = Assert.IsType<Guid>(session.SessionId);
        var checkpointKey = LifecycleKey.Checkpoint(rig.Profile, sessionId);
        var durable = Assert.IsType<LifecycleRecord>(await rig.Store.ReadRecordAsync(checkpointKey, cancellationToken));
        var checkpoint = new GitHubListenerCheckpointStore(rig.Store, Assert.IsType<OwnedSessionFence>(session.Fence));
        var pending = Assert.IsType<ScaleSetListenerCheckpoint>(await checkpoint.ReadAsync(checkpoint.SessionKey, cancellationToken));
        using var replacement = await rig.OpenLaneAsync(rig.Lease, cancellationToken);
        var recovered = await replacement.StepAsync(cancellationToken);
        Assert.Equal(durable, await rig.Store.ReadRecordAsync(checkpointKey, cancellationToken));

        using var profile = TelemetryProfileScope.Enter(TelemetryProfile.From(Assert.Single(rig.Configuration.Profiles)));
        var staleFence = rig.Lease.Fence with { Epoch = rig.Lease.Epoch + 1 };
        var claim = LifecycleKey.Claim("telemetry-fixture-stale-owner");
        var fenced = await rig.Store.WriteRecordsAsync(staleFence, Guid.NewGuid(),
            [new LifecycleRecordWrite(claim, null, "telemetry-fixture-private-item")], cancellationToken);
        Assert.Null(await rig.Store.ReadRecordAsync(claim, cancellationToken));
        Assert.Equal(rig.Lease, await rig.Store.ReadLeaseAsync(rig.Lease.Deployment, cancellationToken));

        var queue = rig.Simulator.Requests.Where(request => request.Host == GitHubProtocolHost.QueueHost).ToArray();
        string messages = $"/messages/{sessionId:D}";
        Assert.Equal(new[] { ("GET", messages), ("GET", messages), ("DELETE", messages + "/0"), ("GET", messages), ("DELETE", messages + "/1") },
            queue.Select(request => (request.Method, request.Path)));
        int released = await rig.ReadHeldAsync(cancellationToken);
        _ = await pipeline.StopAsync().WaitAsync(cancellationToken);
        var decision = new ListenerDecision(accepted.Step?.Status, held, firstReserved, secondReserved, lost.Status,
            pending.LastAcknowledgedMessageId, pending.Pending?.Phase, pending.Pending?.Delivery.Message?.MessageId,
            recovered.Status, released, fenced, rig.Simulator.Requests.Count(IsAcquisition));
        return (decision, sessionId, diagnostic);
    }

    private static void AssertDecision(ListenerDecision decision)
    {
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, decision.Accepted);
        Assert.Equal(1, decision.HeldBeforeCompletion);
        Assert.True(decision.FirstReserved);
        Assert.False(decision.SecondReserved);
        Assert.True(decision.LostResponse is GitHubListenerLaneStatus.RecoveryRequired or GitHubListenerLaneStatus.Failed);
        Assert.Equal(0L, decision.AcknowledgedThrough);
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, decision.PendingPhase);
        Assert.Equal(1L, decision.PendingMessage);
        Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, decision.Reconstructed);
        Assert.Equal(0, decision.HeldAfterCompletion);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, decision.Fenced);
        Assert.Equal(1, decision.AcquisitionCalls);
    }

    private static void AssertSafePayloads(IReadOnlyList<OtlpCapture> requests, Guid sessionId)
    {
        foreach (var request in requests)
        {
            string bytes = Encoding.UTF8.GetString(request.Payload());
            Assert.DoesNotContain(GitHubSimulator.FirstPat, bytes, StringComparison.Ordinal);
            Assert.DoesNotContain(GitHubProtocolHost.QueueHost, bytes, StringComparison.Ordinal);
            Assert.DoesNotContain(GitHubProtocolHost.ServiceHost, bytes, StringComparison.Ordinal);
            Assert.DoesNotContain(sessionId.ToString("D"), bytes, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CapturedJob.ToString(CultureInfo.InvariantCulture), bytes, StringComparison.Ordinal);
            Assert.DoesNotContain("telemetry-fixture-private-item", bytes, StringComparison.Ordinal);
        }
    }

    private static void AssertCapturedTraces(IReadOnlyList<OtlpCapture> requests)
    {
        var spans = Scopes(requests, "/v1/traces").SelectMany(scope => scope.Records.Select(record =>
            new CapturedSpan(scope.Name, record.Text(5), Convert.ToHexString(record.Bytes(1)),
                Convert.ToHexString(record.Bytes(2)), Convert.ToHexString(record.BytesOrEmpty(4)), record))).ToArray();
        foreach (var span in spans)
        {
            string[] approved = span.Scope switch
            {
                AwsListenerScope or AwsPersistenceScope => ["profile", "provider", "operation", "outcome", "reason"],
                ClientScope => ["http.request.method", "scaleset.dependency", "url.scheme"],
                ListenerScope => ["scaleset.listener.operation", "scaleset.listener.outcome"],
                HttpScope => ["http.request.method", "http.response.status_code", "network.protocol.version", "server.address"],
                _ => [],
            };
            Assert.All(span.Record.AttributeNames(9), key => Assert.Contains(key, approved));
        }
        var cleanAck = Assert.Single(spans, span => span.Name == "scaleset.aws.listener.acknowledge" && span.Tags["outcome"] == "returned");
        var poll = Assert.Single(spans, span => span.Name == "scaleset.aws.listener.poll" && span.Trace == cleanAck.Trace);
        Assert.Equal("received", poll.Tags["outcome"]);
        AssertWireAncestry(spans, poll, "GET");
        AssertWireAncestry(spans, cleanAck, "DELETE");
        var unknownAck = Assert.Single(spans, span => span.Name == "scaleset.aws.listener.acknowledge" && span.Tags["outcome"] == "unknown");
        Assert.NotEqual(cleanAck.Trace, unknownAck.Trace);
        Assert.Contains(spans, span => span.Scope == AwsListenerScope && span.Name == "scaleset.aws.listener.checkpoint-acknowledging" &&
            span.Trace == unknownAck.Trace && span.Tags["outcome"] == "committed");
        Assert.DoesNotContain(spans, span => span.Name == "scaleset.aws.listener.checkpoint-settled" && span.Trace == unknownAck.Trace);
        var fenced = Assert.Single(spans, span => span.Name == "scaleset.aws.dynamodb.transaction" && span.Tags["outcome"] == "fenced");
        Assert.Equal(ProfileId, fenced.Tags["profile"]);
        Assert.Equal("fargate", fenced.Tags["provider"]);
    }

    private static void AssertWireAncestry(CapturedSpan[] spans, CapturedSpan boundary, string method)
    {
        Assert.Equal(AwsListenerScope, boundary.Scope);
        Assert.Equal(ProfileId, boundary.Tags["profile"]);
        Assert.Equal("fargate", boundary.Tags["provider"]);
        var operation = Assert.Single(spans, span => span.Id == boundary.ParentId && span.Trace == boundary.Trace);
        Assert.Equal((ListenerScope, "scaleset.listener.operation"), (operation.Scope, operation.Name));
        Assert.Equal(method == "GET" ? "Poll" : "Acknowledge", operation.Tags["scaleset.listener.operation"]);
        var step = Assert.Single(spans, span => span.Id == operation.ParentId && span.Trace == boundary.Trace);
        Assert.Equal((ListenerScope, "scaleset.listener.step"), (step.Scope, step.Name));
        var supervisor = Assert.Single(spans, span => span.Id == step.ParentId && span.Trace == boundary.Trace);
        Assert.Equal((AwsListenerScope, "scaleset.aws.listener.step"), (supervisor.Scope, supervisor.Name));
        var client = Assert.Single(spans, span => span.ParentId == boundary.Id && span.Trace == boundary.Trace);
        Assert.Equal((ClientScope, "scaleset.client.request"), (client.Scope, client.Name));
        Assert.Equal(method, client.Tags["http.request.method"]);
        Assert.Equal("queue", client.Tags["scaleset.dependency"]);
        Assert.Equal("https", client.Tags["url.scheme"]);
        var http = Assert.Single(spans, span => span.ParentId == client.Id && span.Trace == boundary.Trace);
        Assert.Equal(HttpScope, http.Scope);
        var httpMethod = Assert.Single(http.Record.Messages(9), attribute => attribute.Text(1) == "http.request.method");
        Assert.Equal(method, httpMethod.Message(2).Text(1));
    }

    private static void AssertCapturedMetrics(IReadOnlyList<OtlpCapture> requests)
    {
        var scopes = Scopes(requests, "/v1/metrics").ToArray();
        Assert.DoesNotContain(scopes, scope => scope.Name == ListenerScope);
        var metrics = scopes.SelectMany(scope => scope.Records.Select(record => (scope.Name, Record: record))).ToArray();
        Assert.Contains(metrics, entry => entry.Name == ClientScope && entry.Record.Text(1) == "scaleset.client.requests");
        Assert.Contains(metrics, entry => entry.Name == ClientScope && entry.Record.Text(1) == "scaleset.client.request.duration");
        var queueAttempts = metrics.Where(entry => entry.Name == ClientScope && entry.Record.Text(1) == "scaleset.client.requests")
            .SelectMany(entry => Points(entry.Record).Select(point => point.Record.StringAttributes(point.Attributes))).ToArray();
        Assert.Contains(queueAttempts, point => point["dependency"] == "queue" && point["method"] == "GET" && point["status_class"] == "2xx");
        Assert.Contains(queueAttempts, point => point["dependency"] == "queue" && point["method"] == "GET" && point["status_class"] == "4xx");
        Assert.Contains(queueAttempts, point => point["dependency"] == "queue" && point["method"] == "DELETE" && point["status_class"] == "2xx");
        var listenerTimes = metrics.Where(entry => entry.Name == AwsListenerScope &&
            entry.Record.Text(1) == "scaleset.aws.listener.operation.duration").ToArray();
        Assert.NotEmpty(listenerTimes);
        Assert.All(listenerTimes, entry => Assert.Equal("s", entry.Record.Text(3)));
        foreach (var (name, record) in metrics)
        {
            string[] approved = name switch
            {
                ClientScope => record.Text(1) == "scaleset.client.retries" ? RetryDimensions : ["dependency", "method", "status_class"],
                AwsListenerScope or AwsPersistenceScope => ["profile", "provider", "operation", "outcome", "reason"],
                HttpScope => ["http.request.method", "http.response.status_code", "network.protocol.version"],
                "System.Runtime" => ["gc.heap.generation"],
                _ => [],
            };
            foreach (var (point, attributes) in Points(record))
            {
                Assert.All(point.AttributeNames(attributes), key => Assert.Contains(key, approved));
            }
        }
        var measured = listenerTimes.SelectMany(entry => Points(entry.Record)
            .Select(point => point.Record.StringAttributes(point.Attributes))).ToArray();
        Assert.Contains(measured, point => point["operation"] == "poll" && point["profile"] == ProfileId && point["provider"] == "fargate");
        Assert.Contains(measured, point => point["operation"] == "poll" && point["outcome"] == "deferred" && point["reason"] == "rate_limit");
        Assert.Contains(measured, point => point["operation"] == "acknowledge" && point["outcome"] == "unknown" && point["profile"] == ProfileId);
    }

    private static IEnumerable<(string Name, IEnumerable<OtlpMessage> Records)> Scopes(IReadOnlyList<OtlpCapture> requests, string path) =>
        requests.Where(request => request.Path == path).SelectMany(request => new OtlpMessage(request.Payload()).Messages(1))
            .SelectMany(resource =>
            {
                Assert.DoesNotContain("profile", resource.Message(1).AttributeNames(1));
                Assert.DoesNotContain("provider", resource.Message(1).AttributeNames(1));
                return resource.Messages(2);
            }).Select(scope => (scope.Message(1).Text(1), scope.Messages(2)));

    private static IEnumerable<(OtlpMessage Record, int Attributes)> Points(OtlpMessage metric) =>
        metric.Messages(5).Concat(metric.Messages(7)).SelectMany(gauge => gauge.Messages(1)).Select(point => (point, 7))
            .Concat(metric.Messages(9).SelectMany(histogram => histogram.Messages(1)).Select(point => (point, 9)));

    private sealed record CapturedSpan(string Scope, string Name, string Trace, string Id, string ParentId, OtlpMessage Record)
    {
        internal Dictionary<string, string> Tags => Record.StringAttributes(9);
    }

    private sealed record ListenerDecision(ScaleSetListenerStepStatus? Accepted, int HeldBeforeCompletion, bool FirstReserved, bool SecondReserved,
        GitHubListenerLaneStatus LostResponse, long? AcknowledgedThrough, ScaleSetListenerPendingPhase? PendingPhase, long? PendingMessage,
        GitHubListenerLaneStatus Reconstructed, int HeldAfterCompletion, LifecycleWriteOutcome Fenced, int AcquisitionCalls);

    private sealed class ExportDiagnostic : TextWriter
    {
        private readonly TaskCompletionSource<string> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Encoding Encoding => Encoding.UTF8;

        internal Task<string> FirstLine => _first.Task;

        public override void WriteLine(string? value) => _first.TrySetResult(value ?? string.Empty);
    }

    private enum ExportMode
    {
        Sampled = 0,
        Unsampled = 1,
        Disabled = 2,
        Rejected = 3,
    }
}
