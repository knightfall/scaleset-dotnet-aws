using System.Threading.Channels;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Telemetry.Tests;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class YamlRolloutTelemetryEvidence(
    RuntimeConfiguration configuration, TimeProvider clock, DynamoDbLifecycleStore store, ControllerLease owner,
    ProfileTelemetry gauges, TelemetryPipeline pipeline, OtlpLoopback receiver, ChannelReader<OtlpCapture> incoming)
    : IAsyncDisposable
{
    private const string ActiveGauge = "scaleset.aws.capacity.active";
    private const string ConfiguredGauge = "scaleset.aws.capacity.configured_max";
    private static readonly string[] GaugeTags = ["profile", "provider"];

    internal static async Task<YamlRolloutTelemetryEvidence> StartAsync(RuntimeConfiguration configuration,
        DynamoDbLifecycleStore store, ControllerLease owner, ControllerClockEvidence proof, TimeSpan reserve,
        TimeProvider clock, CancellationToken token)
    {
        Assert.Equal(owner, await store.ReadLeaseAsync(owner.Deployment, token));
        Assert.Equal(ControllerLeaseMode.Active, owner.Mode);
        var incoming = Channel.CreateBounded<OtlpCapture>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var receiver = await OtlpLoopback.StartAsync(grpc: false, request =>
        {
            if (request.Path == "/v1/metrics")
            {
                incoming.Writer.TryWrite(request);
            }
            return ReceiverReply.Accepted;
        }, token);
        var gauges = new ProfileTelemetry(configuration, clock);
        try
        {
            gauges.SetOwnership(owner, proof, reserve);
            var settings = TelemetrySettings.Load(runtime: configuration, readEnvironment: key => key switch
            {
                "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT" => receiver.Url + "/v1/metrics",
                "OTEL_EXPORTER_OTLP_PROTOCOL" => "http/protobuf",
                "OTEL_METRIC_EXPORT_INTERVAL" => "1000",
                _ => null,
            });
            return new YamlRolloutTelemetryEvidence(configuration, clock, store, owner, gauges,
                TelemetryPipeline.Start(settings), receiver, incoming.Reader);
        }
        catch
        {
            gauges.Dispose();
            await receiver.DisposeAsync();
            throw;
        }
    }

    internal async Task AssertBeforeRetirementAsync(LifecycleProfile retiring, LifecycleProfile sibling,
        DeploymentLeaseFence previousOwner, CancellationToken token)
    {
        Assert.True(previousOwner.Epoch < owner.Epoch);
        Assert.Equal(CatalogProfileState.ClosureHeld, (await store.ReadCatalogProfileAsync(retiring, token))?.State);
        await ObserveAsync(retiring, token);
        await ObserveAsync(sibling, token);
        gauges.Retire(previousOwner, retiring.ProfileId);
        var batch = await ReadUntilAsync(points =>
            points.Count(point => point.Metric == ActiveGauge && point.Value == 0) == 2, token);
        Assert.Equal(new[] { (sibling.ProfileId, "ec2"), (retiring.ProfileId, "fargate") },
            batch.Where(point => point.Metric == ActiveGauge).Select(point => (point.Profile, point.Provider))
                .OrderBy(point => point.Profile, StringComparer.Ordinal));
        Assert.Single(batch, point => point is { Metric: ConfiguredGauge, Value: 18 } && point.Profile == sibling.ProfileId);
        Assert.Single(batch, point => point is { Metric: ConfiguredGauge, Value: 24 } && point.Profile == retiring.ProfileId);
    }

    internal async Task AssertAfterRetirementAsync(LifecycleProfile retired, LifecycleProfile sibling, CancellationToken token)
    {
        Assert.Equal(owner, await store.ReadLeaseAsync(owner.Deployment, token));
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retired, token));
        Assert.Equal(CatalogProfileState.Tombstoned, catalog.State);
        Assert.NotNull(catalog.RetiredAt);
        Assert.Equal(CatalogProfileState.Active, (await store.ReadCatalogProfileAsync(sibling, token))?.State);

        // The runtime invokes this same API after the retirement coordinator settles the catalog.
        gauges.Retire(owner.Fence, retired.ProfileId);
        await ObserveAsync(retired, token); // A delayed sample cannot republish a permanently retired profile.
        await ObserveAsync(sibling, token);
        var batch = await ReadUntilAsync(points =>
            points.Any(point => point is { Metric: ActiveGauge, Value: 0 } && point.Profile == sibling.ProfileId) &&
            points.All(point => point.Profile != retired.ProfileId), token);
        Assert.All(batch, point => Assert.Equal((sibling.ProfileId, "ec2"), (point.Profile, point.Provider)));
        Assert.Single(batch, point => point is { Metric: ActiveGauge, Value: 0 });
        Assert.Single(batch, point => point is { Metric: ConfiguredGauge, Value: 18 });
        Assert.True(await pipeline.StopAsync().WaitAsync(token));
    }

    private async Task ObserveAsync(LifecycleProfile key, CancellationToken token)
    {
        var profile = configuration.Profiles.Single(value => value.Id == key.ProfileId);
        var stored = await store.ReadExecutorInventoryAsync(key, token);
        Assert.True(stored.IsConsistent);
        Assert.Empty(stored.Executors);
        var coverage = Assert.IsType<ExecutorInventoryObservation>(stored.Coverage);
        Assert.True(coverage.IsComplete);
        var now = clock.GetUtcNow();
        var plan = CapacityPlanner.Plan(profile, demand: null, new InventorySample(coverage.ObservedAt, true,
            coverage.BackendCapacityCeiling, coverage.BackendLaunchHeadroom, []), clock);
        Assert.Equal(ObservationStatus.Fresh, plan.InventoryStatus);
        gauges.Observe(owner.Fence, key.ProfileId, new ProfileTelemetryObservation(plan, now, now.AddMinutes(1),
            DemandObservedAt: null, coverage.ObservedAt, DriftCount: null, UnfinishedEffects: null, OldestCleanupAt: null, IsReady: false));
    }

    private async Task<Gauge[]> ReadUntilAsync(Func<Gauge[], bool> matches, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            var capture = await incoming.ReadAsync(wait.Token);
            Assert.Equal("application/x-protobuf", capture.MediaType);
            var message = new OtlpMessage(capture.Payload());
            var batch = message.Messages(1)
                .SelectMany(resource => resource.Messages(2))
                .Where(scope => scope.Message(1).Text(1) == ProfileTelemetry.MeterName)
                .SelectMany(scope => scope.Messages(2))
                .SelectMany(metric => metric.Messages(5).SelectMany(gauge => gauge.Messages(1)).Select(point =>
                {
                    string name = metric.Text(1);
                    var tags = point.StringAttributes(7);
                    Assert.Equal(GaugeTags, tags.Keys.Order(StringComparer.Ordinal));
                    return new Gauge(name, tags["profile"], tags["provider"],
                        name is ActiveGauge or ConfiguredGauge ? point.Number(6) : null);
                })).ToArray();
            if (matches(batch))
            {
                return batch;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await pipeline.DisposeAsync();
        gauges.Dispose();
        await receiver.DisposeAsync();
    }

    private sealed record Gauge(string Metric, string Profile, string Provider, ulong? Value);
}
