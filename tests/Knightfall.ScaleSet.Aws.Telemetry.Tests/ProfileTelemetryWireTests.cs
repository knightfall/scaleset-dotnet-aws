using System.Security.Cryptography;
using System.Threading.Channels;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

[Collection(nameof(TelemetryWireTests))]
public sealed class ProfileTelemetryWireTests
{
    [Fact]
    public async Task CollectorReceivesValidZeroAndDropsStaleOwnershipAndRetiredSeries()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "mixed.json"), token);
        var config = RuntimeConfigurationLoader.Load(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var clock = new FixedClock();
        using var gauges = new ProfileTelemetry(config, clock);
        var incoming = Channel.CreateBounded<OtlpCapture>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, request =>
        {
            if (request.Path == "/v1/metrics")
            {
                incoming.Writer.TryWrite(request);
            }
            return ReceiverReply.Accepted;
        }, token);
        var settings = TelemetrySettings.Load(runtime: config, readEnvironment: key => key switch
        {
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT" => receiver.Url + "/v1/metrics",
            "OTEL_EXPORTER_OTLP_PROTOCOL" => "http/protobuf",
            "OTEL_METRIC_EXPORT_INTERVAL" => "1000",
            _ => null,
        });
        await using var pipeline = TelemetryPipeline.Start(settings);

        var now = clock.GetUtcNow();
        var first = new ControllerLease(RunnerOwnershipTags.DeploymentIdentity(config), "first", "first-task", 1, 1,
            now.AddMinutes(2), 1, Guid.NewGuid(), ControllerLeaseMode.Active, null, null);
        var proof = new ControllerClockEvidence(now, now.AddMinutes(1), TimeSpan.Zero);
        gauges.SetOwnership(first, proof, TimeSpan.Zero);
        foreach (var profile in config.Profiles)
        {
            Observe(first, profile, 0);
        }

        var initial = await ReadUntilAsync(incoming.Reader, batch => batch.Count(point => point.Name == "capacity.assigned" && point.Value == 0) == 4, token);
        var assignments = initial.Where(point => point.Name == "capacity.assigned").ToArray();
        Assert.Equal(config.Profiles.Select(profile => profile.Id).Order(StringComparer.Ordinal),
            assignments.Select(point => point.Profile).Order(StringComparer.Ordinal));
        Assert.Equal(2, assignments.Count(point => point.Provider == "ec2"));
        Assert.Equal(2, assignments.Count(point => point.Provider == "fargate"));
        Assert.Equal(4, initial.Count(point => point.Name == "profile.ready" && point.Value == 1));

        gauges.EndOwnership(first.Fence);
        var lost = await ReadUntilAsync(incoming.Reader,
            batch => batch.Count(point => point.Name == "profile.ready" && point.Value == 0) == 4, token);
        Assert.DoesNotContain(lost, point => point.Name == "capacity.assigned");
        Assert.DoesNotContain(lost, point => point.Name == "capacity.unknown");

        var successor = first with { OwnerId = "successor", OwnerTaskArn = "successor-task", Epoch = 2 };
        gauges.SetOwnership(successor, proof, TimeSpan.Zero);
        string retired = config.Profiles[0].Id;
        string retained = config.Profiles[1].Id;
        gauges.Retire(successor.Fence, retired);
        Observe(successor, config.Profiles[0], 9);
        Observe(successor, config.Profiles[1], 7);
        var current = await ReadUntilAsync(incoming.Reader,
            batch => batch.Any(point => point is { Name: "capacity.assigned", Value: 7 } && point.Profile == retained), token);
        Assert.DoesNotContain(current, point => point.Profile == retired);
        Assert.Equal(3, current.Count(point => point.Name == "profile.ready"));
        Assert.True(await pipeline.StopAsync().WaitAsync(token));

        void Observe(ControllerLease lease, RunnerProfile profile, int assigned)
        {
            var plan = CapacityPlanner.Plan(profile, new DemandSample(now, assigned), new InventorySample(now, true, 20, 20, []), clock);
            gauges.Observe(lease.Fence, profile.Id, new ProfileTelemetryObservation(plan, now, now.AddMinutes(1), now, now,
                DriftCount: 0, UnfinishedEffects: 0, OldestCleanupAt: null, IsReady: true));
        }
    }

    private static async Task<Gauge[]> ReadUntilAsync(ChannelReader<OtlpCapture> incoming, Func<Gauge[], bool> condition, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            var message = new OtlpMessage((await incoming.ReadAsync(deadline.Token)).Payload());
            var values = message.Messages(1)
                .SelectMany(resource => resource.Messages(2))
                .Where(scope => scope.Message(1).Text(1) == ProfileTelemetry.MeterName)
                .SelectMany(scope => scope.Messages(2))
                .Where(metric => metric.Text(1) is "scaleset.aws.capacity.assigned" or "scaleset.aws.capacity.unknown" or "scaleset.aws.profile.ready")
                .SelectMany(metric => metric.Message(5).Messages(1).Select(point =>
                {
                    var tags = point.StringAttributes(7);
                    Assert.Equal(2, tags.Count);
                    return new Gauge(metric.Text(1)["scaleset.aws.".Length..], tags["profile"], tags["provider"], point.Number(6));
                })).ToArray();
            if (condition(values))
            {
                return values;
            }
        }
    }

    private sealed record Gauge(string Name, string Profile, string Provider, ulong Value);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
