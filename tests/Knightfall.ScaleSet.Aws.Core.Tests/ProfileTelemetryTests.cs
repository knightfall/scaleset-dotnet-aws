using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class ProfileTelemetryTests
{
    [Fact]
    public void CapacityGaugesUseFourConfiguredProfilesAndCurrentLeaseEvidence()
    {
        byte[] bytes = ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed);
        var config = RuntimeConfigurationLoader.Load(bytes, ConfigurationFixtures.Sha256(bytes));
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var meter = new ProfileTelemetry(config, clock);
        using var capture = new GaugeCapture();
        var first = Lease(config, clock, 1);
        string one = config.Profiles[0].Id;
        string two = config.Profiles[1].Id;

        Assert.Equal(4, capture.Read("profile.ready").Length);
        Assert.All(capture.Read("profile.ready"), sample => Assert.Equal(0, sample.Value));
        Assert.Empty(capture.Read("capacity.assigned"));

        meter.SetOwnership(first);
        foreach (var profile in config.Profiles)
        {
            Observe(meter, first, profile, clock, 0);
        }

        var samples = capture.Read("capacity.assigned");
        Assert.Equal(config.Profiles.Select(p => p.Id).Order(StringComparer.Ordinal), samples.Select(s => s.Profile).Order(StringComparer.Ordinal));
        Assert.Equal(2, samples.Count(s => s.Provider == "ec2"));
        Assert.Equal(2, samples.Count(s => s.Provider == "fargate"));
        Assert.All(samples, sample => Assert.Equal(0, sample.Value));

        for (int index = 1; index <= 2_500; index++)
        {
            Observe(meter, first, config.Profiles[index % config.Profiles.Length], clock, index);
        }
        Assert.Equal(4, capture.Read("capacity.unknown").Length);
        Assert.All(capture.Read("capacity.orphan_candidates"), sample => Assert.Equal(1, sample.Value));
        Assert.All(capture.Read("capacity.active"), sample => Assert.Equal(1, sample.Value));
        Assert.All(capture.Read("capacity.idle"), sample => Assert.Equal(1, sample.Value));
        Assert.All(capture.Read("effects.unfinished"), sample => Assert.Equal(2, sample.Value));
        Assert.Equal(4, capture.Read("cleanup.oldest_age").Length);
        Assert.All(capture.Read("cleanup.oldest_age"), sample => Assert.Equal("s", sample.Unit));

        meter.SetListenerCooldown(first.Fence, one, clock.GetUtcNow().AddSeconds(4));
        Assert.Equal(4, Assert.Single(capture.Read("listener.cooldown.remaining")).Value);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, Assert.Single(capture.Read("listener.cooldown.remaining")).Value);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(capture.Read("capacity.assigned"));
        Assert.Empty(capture.Read("capacity.orphan_candidates"));
        Assert.All(capture.Read("inventory.age"), sample => Assert.Equal(604, sample.Value));

        meter.EndOwnership(first.Fence);
        meter.SetOwnership(first with { Revision = 2 });
        Observe(meter, first, config.Profiles[0], clock, 20);
        Assert.Empty(capture.Read("capacity.assigned"));
        Assert.Empty(capture.Read("inventory.age"));
        Assert.Empty(capture.Read("effects.unfinished"));
        Assert.All(capture.Read("profile.ready"), sample => Assert.Equal(0, sample.Value));

        var successor = Lease(config, clock, 2);
        meter.SetOwnership(successor);
        Observe(meter, successor, config.Profiles[0], clock, 7);
        Observe(meter, successor, config.Profiles[1], clock, 8, complete: false);
        Assert.Equal(7, capture.Read("capacity.assigned").Single(s => s.Profile == one).Value);
        Assert.DoesNotContain(capture.Read("capacity.unknown"), sample => sample.Profile == two);
        meter.Retire(successor.Fence, one);
        Observe(meter, successor, config.Profiles[0], clock, 9);
        Assert.DoesNotContain(capture.Read("profile.ready"), sample => sample.Profile == one);
        Assert.DoesNotContain(capture.Read("capacity.assigned"), sample => sample.Profile == one);
    }

    [Fact]
    public async Task ProfileScopeIsLocalToTheAwaitedCallAndCompactsDistinctLongIds()
    {
        byte[] bytes = ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed);
        var profile = RuntimeConfigurationLoader.Load(bytes, ConfigurationFixtures.Sha256(bytes)).Profiles[0];
        string prefix = new('a', 256);
        var first = TelemetryProfile.From(profile with { Id = prefix + "-1" });
        var second = TelemetryProfile.From(profile with { Id = prefix + "-2" });
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(256, first.Id.Length);
        Assert.Equal(256, second.Id.Length);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(TelemetryProfile.From(profile with { Id = "https://secret.invalid/" }));

        using (TelemetryProfileScope.Enter(first))
        {
            await Task.Yield();
            Assert.Equal(first, TelemetryProfileScope.Current);
            using (TelemetryProfileScope.Enter(second))
            {
                Assert.Equal(second, TelemetryProfileScope.Current);
            }
            Assert.Equal(first, TelemetryProfileScope.Current);
        }
        Assert.Null(TelemetryProfileScope.Current);
    }

    private static ControllerLease Lease(RuntimeConfiguration config, ManualClock clock, long epoch) =>
        new(RunnerOwnershipTags.DeploymentIdentity(config), "owner", "task", epoch, 1, clock.GetUtcNow().AddHours(1), 1, Guid.NewGuid(),
            ControllerLeaseMode.Active, null, null);

    private static void Observe(ProfileTelemetry meter, ControllerLease lease, RunnerProfile profile, ManualClock clock,
        int assigned, bool complete = true)
    {
        var now = clock.GetUtcNow();
        ImmutableArray<RunnerSlot> slots = assigned == 0 ? [] :
        [
            new($"busy-{assigned}", RunnerSlotState.Busy, true),
            new($"idle-{assigned}", RunnerSlotState.Idle, true),
            new($"unknown-{assigned}", RunnerSlotState.Idle, false),
        ];
        var plan = CapacityPlanner.Plan(profile, new DemandSample(now, assigned), new InventorySample(now, complete, 20, 20, slots), clock);
        meter.Observe(lease.Fence, profile.Id, new ProfileTelemetryObservation(plan, now, now.AddMinutes(20), now, now,
            DriftCount: complete ? 0 : null, UnfinishedEffects: 2, OldestCleanupAt: now.AddSeconds(-5), IsReady: complete));
    }

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }

    private sealed class GaugeCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Gauge> _samples = new();

        internal GaugeCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ProfileTelemetry.MeterName && instrument.IsObservable)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
            _listener.Start();
        }

        internal Gauge[] Read(string suffix)
        {
            _samples.Clear();
            _listener.RecordObservableInstruments();
            return _samples.Where(x => x.Instrument == "scaleset.aws." + suffix).ToArray();
        }

        public void Dispose() => _listener.Dispose();

        private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var fields = tags.ToArray().ToDictionary();
            Assert.Equal(2, fields.Count);
            _samples.Enqueue(new Gauge(instrument.Name, instrument.Unit, (string)fields["profile"]!, (string)fields["provider"]!, value));
        }
    }

    private sealed record Gauge(string Instrument, string? Unit, string Profile, string Provider, double Value);
}
