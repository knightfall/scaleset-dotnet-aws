using System.Text.Json;
using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.DryRun;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class DryRunEngineTests
{
    private const string Profile = "linux-ec2-docker";
    private static readonly DateTimeOffset InitialTime = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReplaySortsObservationsAndAdvancesItsOwnClockWithoutRefreshingDemand()
    {
        var configuration = ConfigureDemand(new { type = "replay", path = "evidence.json" });
        byte[] evidence = ReplayBytes();
        var disordered = DryRunEngine.CreateReport(configuration, evidence, new FixedClock(InitialTime.AddDays(90)));
        var orderedEvidence = JsonNode.Parse(evidence)!.AsObject();
        JsonArray records = orderedEvidence["records"]!.AsArray();
        orderedEvidence["records"] = new JsonArray(records[1]!.DeepClone(), records[2]!.DeepClone(), records[0]!.DeepClone());
        var ordered = DryRunEngine.CreateReport(configuration, JsonSerializer.SerializeToUtf8Bytes(orderedEvidence),
            new FixedClock(InitialTime.AddDays(-90)));

        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(ordered), JsonSerializer.SerializeToUtf8Bytes(disordered));
        Assert.Equal("replay", disordered.DemandSource);
        Assert.Equal(configuration.Sha256, disordered.RuntimeSha256);
        Assert.True(disordered.Inspection.Requested);
        Assert.False(disordered.Inspection.Performed);
        Assert.Equal("offline", disordered.Inspection.Reason);

        Assert.Collection(disordered.Replay,
            frame =>
            {
                Assert.Equal(InitialTime, frame.ObservedAt);
                var report = Assert.Single(frame.Profiles);
                Assert.Equal(3, report.Capacity.Target);
                Assert.Equal(1, report.Capacity.CrediblePending);
                Assert.Equal(1, report.Capacity.Deficit);
                Assert.Equal(1, report.Capacity.ProposedStarts);
            },
            frame =>
            {
                Assert.Equal(InitialTime.AddMinutes(4), frame.ObservedAt);
                var report = Assert.Single(frame.Profiles);
                Assert.Equal(InitialTime, report.DemandObservedAt);
                Assert.Equal(0, report.Capacity.CrediblePending);
                Assert.Equal(3, report.Capacity.OccupiedSlots);
                Assert.Equal(2, report.Capacity.Deficit);
                Assert.Equal(2, report.Capacity.ProposedStarts);
            },
            frame =>
            {
                Assert.Equal(InitialTime.AddMinutes(12), frame.ObservedAt);
                var report = Assert.Single(frame.Profiles);
                Assert.Equal(InitialTime, report.DemandObservedAt);
                Assert.Equal(frame.ObservedAt, report.InventoryObservedAt);
                Assert.Equal(ObservationStatus.Stale, report.Capacity.DemandStatus);
                Assert.Null(report.Capacity.Target);
                Assert.Equal(0, report.Capacity.ProposedStarts);
                Assert.Equal(1, report.Capacity.BusySlots);
                Assert.Equal("unknown-runner", Assert.Single(report.Capacity.OrphanCandidates));
            });

        var final = Assert.Single(disordered.Profiles);
        Assert.Equal(InitialTime.AddMinutes(12), final.InventoryObservedAt);
        Assert.Equal(ObservationStatus.Stale, final.Capacity.DemandStatus);
    }

    [Fact]
    public void NoneKeepsDemandUnknownWhileReportingSuppliedInventoryAndOrphans()
    {
        var configuration = ConfigureDemand(new { type = "none" });
        var inventory = new InventorySample(InitialTime, true, 8, 0,
            [new RunnerSlot("owned-busy", RunnerSlotState.Busy, true), new RunnerSlot("orphan", RunnerSlotState.Unknown, false)]);
        var report = DryRunEngine.CreateReport(configuration, timeProvider: new FixedClock(InitialTime),
            verifiedInventory: new Dictionary<string, InventorySample>(StringComparer.Ordinal) { [Profile] = inventory });

        Assert.Equal("none", report.DemandSource);
        Assert.Empty(report.Replay);
        var profile = Assert.Single(report.Profiles);
        Assert.Equal(Profile, profile.ProfileId);
        Assert.Equal(ObservationStatus.Unknown, profile.Capacity.DemandStatus);
        Assert.Equal(0, profile.Capacity.BackendLaunchHeadroom);
        Assert.Equal(2, profile.Capacity.OccupiedSlots);
        Assert.Null(profile.Capacity.TotalAssignedJobs);
        Assert.Null(profile.Capacity.Deficit);
        Assert.Equal(0, profile.Capacity.ProposedStarts);
        Assert.Equal("orphan", Assert.Single(profile.Capacity.OrphanCandidates));
    }

    [Fact]
    public void SnapshotUsesClockFreshnessAndVerifiesSuppliedVersionedObjectBytes()
    {
        byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(Snapshot());
        var configuration = ConfigureDemand(new
        {
            type = "snapshot",
            s3Uri = "s3://config-fixtures/demand.json",
            versionId = "test-version",
            sha256 = ConfigurationFixtures.Sha256(snapshot),
        });
        var current = DryRunEngine.CreateReport(configuration, snapshot, new FixedClock(InitialTime.AddMinutes(2)));
        var stale = DryRunEngine.CreateReport(configuration, snapshot, new FixedClock(InitialTime.AddMinutes(11)));

        Assert.Equal("snapshot", current.DemandSource);
        Assert.Empty(current.Replay);
        Assert.Equal(1, Assert.Single(current.Profiles).Capacity.ProposedStarts);
        Assert.Equal(ObservationStatus.Stale, Assert.Single(stale.Profiles).Capacity.DemandStatus);
        Assert.Equal(0, Assert.Single(stale.Profiles).Capacity.ProposedStarts);

        byte[] changed = [.. snapshot, (byte)'\n'];
        var error = Assert.Throws<DryRunEvidenceException>(() => DryRunEngine.CreateReport(configuration, changed));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scopeURL", "https://github.com/other-owner", "binding")]
    [InlineData("observedAt", "2026-11-24T00:00:00", "UTC offset")]
    [InlineData("profileId", "unconfigured", "configured profile")]
    public void EvidenceRejectsInvalidBindingsOrTimeBeforePlanning(string property, string value, string diagnostic)
    {
        var configuration = ConfigureDemand(new { type = "snapshot", path = "evidence.json" });
        JsonObject snapshot = Snapshot();
        snapshot["records"]![0]![property] = value;
        var error = Assert.Throws<DryRunEvidenceException>(() =>
            DryRunEngine.CreateReport(configuration, JsonSerializer.SerializeToUtf8Bytes(snapshot), new FixedClock(InitialTime)));

        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayRefusesConflictingObservationsAtOneInstant()
    {
        var configuration = ConfigureDemand(new { type = "replay", path = "evidence.json" });
        JsonObject replay = JsonNode.Parse(ReplayBytes())!.AsObject();
        JsonArray records = replay["records"]!.AsArray();
        records.Add(records[1]!.DeepClone());

        var error = Assert.Throws<DryRunEvidenceException>(() =>
            DryRunEngine.CreateReport(configuration, JsonSerializer.SerializeToUtf8Bytes(replay)));
        Assert.Contains("same time", error.Message, StringComparison.Ordinal);
    }

    private static RuntimeConfiguration ConfigureDemand<T>(T demand)
    {
        byte[] bytes = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Ec2Latest,
            ConfigurationEdit.Set("/spec/dryRun/demand", demand));
        return RuntimeConfigurationLoader.Load(bytes, ConfigurationFixtures.Sha256(bytes));
    }

    private static byte[] ReplayBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DryRun", "replay.json"));

    private static JsonObject Snapshot()
    {
        JsonObject snapshot = JsonNode.Parse(ReplayBytes())!.AsObject();
        snapshot["kind"] = "RunnerDemandSnapshot";
        snapshot["records"] = new JsonArray(snapshot["records"]![1]!.DeepClone());
        return snapshot;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
