using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.Core.Telemetry;

namespace Knightfall.ScaleSet.Aws.Core.DryRun;

public static class DryRunEngine
{
    public static DryRunReport CreateReport(
        RuntimeConfiguration configuration,
        ReadOnlyMemory<byte>? evidence = null,
        TimeProvider? timeProvider = null,
        IReadOnlyDictionary<string, InventorySample>? verifiedInventory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var observations = new Dictionary<string, ProfileObservations>(StringComparer.Ordinal);
        if (configuration.DryRun.Demand is NoDryRunDemandConfiguration)
        {
            if (evidence is not null)
            {
                throw new DryRunEvidenceException("The selected demand source does not accept a demand artifact.");
            }
            if (verifiedInventory is not null)
            {
                SeedInventory(configuration, verifiedInventory, observations);
            }
            var clock = new ReportClock((timeProvider ?? TimeProvider.System).GetUtcNow());
            return Report(configuration, "none", PlanProfiles(configuration, observations, clock), []);
        }

        if (verifiedInventory is not null)
        {
            throw new DryRunEvidenceException("Use the demand artifact to supply inventory in snapshot or replay mode.");
        }
        if (evidence is not ReadOnlyMemory<byte> bytes)
        {
            throw new DryRunEvidenceException("The selected demand source requires its configured artifact.");
        }

        bool replay = configuration.DryRun.Demand is LocalReplayDemandConfiguration;
        var records = DryRunEvidence.Read(configuration, bytes, replay);
        if (!replay)
        {
            foreach (PlanningObservation observation in records)
            {
                observations.Add(observation.ProfileId, new ProfileObservations(observation.Demand, observation.Inventory));
            }
            var clock = new ReportClock((timeProvider ?? TimeProvider.System).GetUtcNow());
            return Report(configuration, "snapshot", PlanProfiles(configuration, observations, clock), []);
        }

        var frames = Replay(configuration, records, observations);
        return Report(configuration, "replay", frames[^1].Profiles, frames);
    }

    private static void SeedInventory(
        RuntimeConfiguration configuration,
        IReadOnlyDictionary<string, InventorySample> inventory,
        Dictionary<string, ProfileObservations> observations)
    {
        var profiles = configuration.Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (profileId, observation) in inventory)
        {
            if (!profiles.Contains(profileId))
            {
                throw new DryRunEvidenceException("Inventory evidence must refer to a configured profile.");
            }
            observations.Add(profileId, new ProfileObservations(null, observation));
        }
    }

    private static ImmutableArray<DryRunReplayFrame> Replay(
        RuntimeConfiguration configuration,
        ImmutableArray<PlanningObservation> records,
        Dictionary<string, ProfileObservations> observations)
    {
        var clock = new ReportClock(DateTimeOffset.MinValue);
        var frames = ImmutableArray.CreateBuilder<DryRunReplayFrame>();
        foreach (var frame in records.OrderBy(record => record.ObservedAt).GroupBy(record => record.ObservedAt))
        {
            clock.AdvanceTo(frame.Key);
            foreach (PlanningObservation observation in frame)
            {
                var previous = observations.GetValueOrDefault(observation.ProfileId);
                if ((observation.Demand is not null && previous.Demand?.ObservedAt == frame.Key) ||
                    (observation.Inventory is not null && previous.Inventory?.ObservedAt == frame.Key))
                {
                    throw new DryRunEvidenceException("Replay cannot contain two observations of the same kind for one profile at the same time.");
                }

                observations[observation.ProfileId] = new ProfileObservations(
                    observation.Demand ?? previous.Demand, observation.Inventory ?? previous.Inventory);
            }
            frames.Add(new DryRunReplayFrame(frame.Key, PlanProfiles(configuration, observations, clock)));
        }
        return frames.ToImmutable();
    }

    private static ImmutableArray<DryRunProfileReport> PlanProfiles(
        RuntimeConfiguration configuration,
        Dictionary<string, ProfileObservations> observations,
        TimeProvider clock)
    {
        var reports = ImmutableArray.CreateBuilder<DryRunProfileReport>(configuration.Profiles.Length);
        foreach (RunnerProfile profile in configuration.Profiles)
        {
            var observation = observations.GetValueOrDefault(profile.Id);
            using var diagnostics = ReconciliationDiagnostics.Start(TelemetryProfile.From(profile), dryRun: true);
            try
            {
                var plan = CapacityPlanner.Plan(profile, observation.Demand, observation.Inventory, clock);
                var (outcome, reason) = CapacityPlanner.TelemetryOutcome(profile, plan);
                diagnostics?.Complete(outcome, reason);
                reports.Add(new DryRunProfileReport(profile.Id, profile.State, profile.Provisioner.Type,
                    observation.Demand?.ObservedAt, observation.Inventory?.ObservedAt, plan));
            }
            catch (ArgumentException)
            {
                diagnostics?.Complete("rejected", "invalid_evidence");
                throw new DryRunEvidenceException("Capacity observations contain invalid counts, slot identities, or timestamps.");
            }
        }
        return reports.MoveToImmutable();
    }

    private static DryRunReport Report(
        RuntimeConfiguration configuration,
        string demandSource,
        ImmutableArray<DryRunProfileReport> profiles,
        ImmutableArray<DryRunReplayFrame> frames) =>
        new(configuration.EnvironmentName, configuration.Sha256, demandSource,
            new DryRunInspection(configuration.DryRun.InspectAws, false, configuration.DryRun.InspectAws ? "offline" : null),
            profiles, frames);

    private readonly record struct ProfileObservations(DemandSample? Demand, InventorySample? Inventory);

    private sealed class ReportClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void AdvanceTo(DateTimeOffset next)
        {
            if (next < _now)
            {
                throw new InvalidOperationException("Replay clock advancement must be monotonic.");
            }
            _now = next;
        }
    }
}
