using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;

namespace Knightfall.ScaleSet.Aws.Core.Telemetry;

public sealed record ProfileTelemetryObservation(
    CapacityPlan Plan,
    DateTimeOffset ReconciledAt,
    DateTimeOffset ValidUntil,
    DateTimeOffset? DemandObservedAt,
    DateTimeOffset? InventoryObservedAt,
    int? DriftCount,
    long? UnfinishedEffects,
    DateTimeOffset? OldestCleanupAt,
    bool IsReady);

/// <summary>Publishes bounded snapshots of evidence already read by the owner.</summary>
public sealed class ProfileTelemetry : IDisposable
{
    public const string MeterName = "Knightfall.ScaleSet.Aws.Core";

    private readonly Meter _meter = new(MeterName);
    private readonly TimeProvider _clock;
    private readonly string _deployment;
    private Cache _cache;

    public ProfileTelemetry(RuntimeConfiguration configuration, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _deployment = RunnerOwnershipTags.DeploymentIdentity(configuration);

        var profiles = ImmutableDictionary.CreateBuilder<string, Profile>(StringComparer.Ordinal);
        foreach (RunnerProfile profile in configuration.Profiles)
        {
            if (profile.State != RunnerProfileState.Disabled && TelemetryProfile.From(profile) is { } identity)
            {
                profiles.Add(profile.Id, new Profile(identity, profile.Capacity, null, null, null));
            }
        }
        _cache = new Cache(null, false, profiles.ToImmutable());

        Gauge("capacity.assigned", "{job}", static (profile, now) => profile.Sample?.Assigned(now));
        Gauge("capacity.active", "{runner}", static (profile, now) => profile.Sample?.Inventory(now)?.Active);
        Gauge("capacity.idle", "{runner}", static (profile, now) => profile.Sample?.Inventory(now)?.Idle);
        Gauge("capacity.starting", "{runner}", static (profile, now) => profile.Sample?.Inventory(now)?.Starting);
        Gauge("capacity.unknown", "{runner}", static (profile, now) => profile.Sample?.Inventory(now)?.Unknown);
        Gauge("capacity.deficit", "{runner}", static (profile, now) => profile.Sample?.Deficit(now));
        Gauge("capacity.drift", "{runner}", static (profile, now) => profile.Sample?.Inventory(now)?.Drift);
        Gauge("capacity.orphan_candidates", "{runner}", static (profile, now) => profile.Sample?.Inventory(now)?.Orphans);
        Gauge("capacity.configured_max", "{runner}", static (profile, _) => profile.Limits.MaxRunners);
        Gauge("capacity.configured_idle_reserve", "{runner}", static (profile, _) => profile.Limits.IdleReserve);
        Gauge("effects.unfinished", "{operation}", static (profile, now) => profile.Sample?.Current(now)?.UnfinishedEffects);
        Gauge("profile.ready", "1", static (profile, now) => profile.Sample?.IsReadyAt(now) == true ? 1 : 0, always: true);

        AgeGauge("inventory.age", static (profile, _) => profile.LastCompleteInventoryAt);
        AgeGauge("reconcile.age", static (profile, _) => profile.Sample?.ReconciledAt);
        AgeGauge("cleanup.oldest_age", static (profile, now) => profile.Sample?.Current(now)?.OldestCleanupAt);
        _meter.CreateObservableGauge("scaleset.aws.listener.cooldown.remaining",
            () => Read(static (profile, now) => profile.CooldownUntil is { } until ? Math.Max(0, (until - now).TotalSeconds) : (double?)null),
            "s");
    }

    /// <summary>Updates the confirmed active lease. An explicitly ended epoch cannot be reopened.</summary>
    public void SetOwnership(ControllerLease lease, ControllerClockEvidence clockEvidence, TimeSpan reserve)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(clockEvidence);
        var now = _clock.GetUtcNow();
        if (lease.Deployment != _deployment || lease.Mode != ControllerLeaseMode.Active ||
            !clockEvidence.TryGetUpperBound(now, lease.ValidUntil, reserve, out _))
        {
            return;
        }

        var leaseCutoff = lease.ValidUntil - reserve - clockEvidence.MaximumAbsoluteSkew;
        var proofCutoff = clockEvidence.ValidUntil - clockEvidence.MaximumAbsoluteSkew;
        var cutoff = leaseCutoff < proofCutoff ? leaseCutoff : proofCutoff;
        ImmutableInterlocked.Update(ref _cache, static (cache, candidate) =>
        {
            if (cache.Authority is { } authority)
            {
                var current = authority.Lease;
                if (candidate.Lease.Epoch < current.Epoch || (candidate.Lease.Epoch == current.Epoch &&
                    (cache.Ended || candidate.Lease.Fence != current.Fence || candidate.Lease.Revision < current.Revision ||
                    candidate.ClockObservedAt < authority.ClockObservedAt)))
                {
                    return cache;
                }
                if (candidate.Lease.Fence == current.Fence)
                {
                    if (candidate.Lease.Revision == current.Revision &&
                        (candidate.Lease != current || (candidate.ClockObservedAt == authority.ClockObservedAt && candidate.Cutoff >= authority.Cutoff)))
                    {
                        return cache;
                    }
                    return cache with { Authority = candidate };
                }
            }
            return new Cache(candidate, false, ClearSamples(cache.Profiles));
        }, new Authority(lease, clockEvidence.ObservedAt, cutoff));
    }

    public void EndOwnership(DeploymentLeaseFence fence) =>
        ImmutableInterlocked.Update(ref _cache, static (cache, expected) =>
            cache.Authority?.Lease.Fence == expected && !cache.Ended
                ? cache with { Ended = true, Profiles = ClearSamples(cache.Profiles) }
                : cache, fence);

    public void Observe(DeploymentLeaseFence fence, string profileId, ProfileTelemetryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var now = _clock.GetUtcNow();
        if (observation.ReconciledAt > now || observation.ValidUntil <= now)
        {
            return;
        }

        var plan = observation.Plan;
        var inventoryAt = plan.InventoryStatus == ObservationStatus.Fresh && observation.InventoryObservedAt <= now
            ? observation.InventoryObservedAt : null;
        var demandAt = plan.DemandStatus == ObservationStatus.Fresh && observation.DemandObservedAt <= now
            ? observation.DemandObservedAt : null;
        var inventory = inventoryAt is { } observed ? new InventoryCounts(observed,
            plan.BusySlots, plan.EligibleServing - plan.BusySlots, plan.CrediblePending, plan.UncertainSlots, observation.DriftCount,
            plan.OrphanCandidates.IsDefault ? null : plan.OrphanCandidates.Length) : null;
        var sample = new Sample(observation.ReconciledAt, observation.ValidUntil, demandAt, plan.TotalAssignedJobs, inventory,
            plan.Deficit, observation.UnfinishedEffects, observation.OldestCleanupAt, observation.IsReady);

        ImmutableInterlocked.Update(ref _cache, static (cache, update) =>
        {
            if (!cache.Owns(update.Fence, update.Now) || !cache.Profiles.TryGetValue(update.Id, out var profile) ||
                profile.Sample?.ReconciledAt > update.Sample.ReconciledAt)
            {
                return cache;
            }

            var latest = update.Sample.InventoryCounts?.ObservedAt;
            var previous = profile.LastCompleteInventoryAt;
            return cache with { Profiles = cache.Profiles.SetItem(update.Id, profile with
            {
                Sample = update.Sample,
                LastCompleteInventoryAt = previous is null || latest > previous ? latest : previous,
            }) };
        }, (Fence: fence, Id: profileId, Sample: sample, Now: now));
    }

    public void SetListenerCooldown(DeploymentLeaseFence fence, string profileId, DateTimeOffset? until)
    {
        var now = _clock.GetUtcNow();
        ImmutableInterlocked.Update(ref _cache, static (cache, update) =>
            cache.Owns(update.Fence, update.Now) && cache.Profiles.TryGetValue(update.Id, out var profile)
                ? cache with { Profiles = cache.Profiles.SetItem(update.Id, profile with
                {
                    CooldownUntil = update.Until ?? update.Now,
                }) }
                : cache, (Fence: fence, Id: profileId, Until: until, Now: now));
    }

    public void Retire(DeploymentLeaseFence fence, string profileId)
    {
        var now = _clock.GetUtcNow();
        ImmutableInterlocked.Update(ref _cache, static (cache, retire) =>
            cache.Owns(retire.Fence, retire.Now)
                ? cache with { Profiles = cache.Profiles.Remove(retire.Id) }
                : cache, (Fence: fence, Id: profileId, Now: now));
    }

    public void Dispose() => _meter.Dispose();

    private void Gauge(string suffix, string unit, Func<Profile, DateTimeOffset, long?> select, bool always = false) =>
        _meter.CreateObservableGauge("scaleset.aws." + suffix, () => Read(select, always), unit);

    private void AgeGauge(string suffix, Func<Profile, DateTimeOffset, DateTimeOffset?> select) =>
        _meter.CreateObservableGauge("scaleset.aws." + suffix,
            () => Read((profile, now) => select(profile, now) is { } timestamp && timestamp <= now
                ? (now - timestamp).TotalSeconds : (double?)null), "s");

    private IEnumerable<Measurement<T>> Read<T>(Func<Profile, DateTimeOffset, T?> select, bool always = false) where T : struct
    {
        var cache = Volatile.Read(ref _cache);
        var now = _clock.GetUtcNow();
        bool owned = cache.IsCurrent(now);
        if (owned || always)
        {
            foreach (var profile in cache.Profiles.Values)
            {
                if (select(owned ? profile : profile with { Sample = null }, now) is { } value)
                {
                    yield return new Measurement<T>(value, profile.Identity.Tags());
                }
            }
        }
    }

    private static ImmutableDictionary<string, Profile> ClearSamples(ImmutableDictionary<string, Profile> profiles) =>
        profiles.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value with { Sample = null, LastCompleteInventoryAt = null, CooldownUntil = null }, profiles.KeyComparer);

    private sealed record Authority(ControllerLease Lease, DateTimeOffset ClockObservedAt, DateTimeOffset Cutoff);

    private sealed record Cache(Authority? Authority, bool Ended, ImmutableDictionary<string, Profile> Profiles)
    {
        internal bool IsCurrent(DateTimeOffset now) => !Ended && Authority is { } authority &&
            authority.ClockObservedAt <= now && now < authority.Cutoff;

        internal bool Owns(DeploymentLeaseFence fence, DateTimeOffset now) =>
            IsCurrent(now) && Authority?.Lease.Fence == fence;
    }

    private sealed record Profile(TelemetryProfile Identity, RunnerCapacityConfiguration Limits, Sample? Sample,
        DateTimeOffset? LastCompleteInventoryAt, DateTimeOffset? CooldownUntil);

    private sealed record InventoryCounts(DateTimeOffset ObservedAt, int? Active, int? Idle, int? Starting, int? Unknown,
        int? Drift, int? Orphans);

    private sealed record Sample(DateTimeOffset ReconciledAt, DateTimeOffset ValidUntil, DateTimeOffset? DemandAt,
        int? AssignedCount, InventoryCounts? InventoryCounts, int? DeficitCount, long? UnfinishedEffects, DateTimeOffset? OldestCleanupAt,
        bool IsReady)
    {
        internal Sample? Current(DateTimeOffset now) => now < ValidUntil ? this : null;

        internal int? Assigned(DateTimeOffset now) => Current(now) is not null && Fresh(DemandAt, now) ? AssignedCount : null;

        internal InventoryCounts? Inventory(DateTimeOffset now) => Current(now) is not null && Fresh(InventoryCounts?.ObservedAt, now)
            ? InventoryCounts : null;

        internal int? Deficit(DateTimeOffset now) => Fresh(DemandAt, now) && Inventory(now) is not null ? DeficitCount : null;

        internal bool IsReadyAt(DateTimeOffset now) => IsReady && Fresh(DemandAt, now) && Inventory(now) is not null;

        private static bool Fresh(DateTimeOffset? timestamp, DateTimeOffset now) => timestamp is { } at &&
            CapacityPlanner.GetObservationStatus(at, now) == ObservationStatus.Fresh;
    }
}
