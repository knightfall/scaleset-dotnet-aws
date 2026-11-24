using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record ProfileHandoffObservation(
    DateTimeOffset ObservedAt, bool IsComplete, int OutstandingOffers, ImmutableArray<LifecycleRecordCondition> ReadGuards);

public sealed record ProfileRetirementTiming(TimeSpan ClearSeparation, TimeSpan Freshness, TimeSpan LogFlushGrace, TimeSpan WriteReserve);

public enum ProfileRetirementStatus
{
    NotDraining = 0,
    OwnershipUnavailable = 1,
    ReconciliationPending = 2,
    FirstClearRecorded = 3,
    WaitingForSecondPass = 4,
    ClosureHeld = 5,
    SessionRetirementRequired = 6,
    Retired = 7,
    StateChanged = 8,
}

public sealed record ProfileRetirementResult(ProfileRetirementStatus Status, SessionIdentity? Session = null);

public sealed class ProfileRetirementCoordinator
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly TimeProvider _clock;
    private readonly ProfileRetirementTiming _timing;
    private readonly Func<LifecycleProfile, CancellationToken, Task<ProfileHandoffObservation?>> _handoffs;

    public ProfileRetirementCoordinator(DynamoDbLifecycleStore store, TimeProvider clock, ProfileRetirementTiming timing,
        Func<LifecycleProfile, CancellationToken, Task<ProfileHandoffObservation?>> handoffs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(handoffs);
        if (timing.ClearSeparation <= TimeSpan.Zero || timing.Freshness <= timing.ClearSeparation ||
            timing.LogFlushGrace <= TimeSpan.Zero || timing.WriteReserve <= TimeSpan.Zero)
        {
            throw new ArgumentException("Positive retirement windows and a freshness period longer than the pass separation are required.", nameof(timing));
        }
        _store = store;
        _clock = clock;
        _timing = timing;
        _handoffs = handoffs;
    }

    public async Task<ProfileRetirementResult> AdvanceAsync(
        LifecycleProfile profile, ActiveControllerAuthority authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(authority);
        if (profile.Deployment != authority.Lease.Deployment || authority.Lease.Mode != ControllerLeaseMode.Active ||
            !HasTime(authority, authority.Lease))
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.OwnershipUnavailable);
        }

        var lease = await _store.ReadLeaseAsync(profile.Deployment, cancellationToken).ConfigureAwait(false);
        if (lease is not { Mode: ControllerLeaseMode.Active } || lease.Fence != authority.Lease.Fence || !HasTime(authority, lease))
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.OwnershipUnavailable);
        }
        var catalog = await _store.ReadCatalogProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        if (catalog?.State == CatalogProfileState.Tombstoned)
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.Retired);
        }
        if (catalog is not { State: CatalogProfileState.Draining or CatalogProfileState.ClosureHeld })
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.NotDraining);
        }

        var handoffs = await _handoffs(profile, cancellationToken).ConfigureAwait(false);
        var proof = await _store.ReadDrainProofAsync(profile, lease.Fence, _timing, handoffs, cancellationToken).ConfigureAwait(false);
        if (!HasTime(authority, lease))
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.OwnershipUnavailable);
        }

        return catalog.State == CatalogProfileState.ClosureHeld
            ? await CompleteRetirementAsync(catalog, lease, authority.Clock, proof, cancellationToken).ConfigureAwait(false)
            : await ObserveClearPassAsync(catalog, lease, authority.Clock, proof, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionChange> BeginSessionRetirementAsync(
        LifecycleProfile profile, ActiveControllerAuthority authority, SessionRecord expected, TimeSpan sendWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sendWindow, TimeSpan.Zero);
        if (profile.Deployment != authority.Lease.Deployment || expected.Binding.Profile != profile || !HasTime(authority, authority.Lease))
        {
            return new SessionChange(LifecycleWriteOutcome.LeaseRejected);
        }
        var lease = await _store.ReadLeaseAsync(profile.Deployment, cancellationToken).ConfigureAwait(false);
        if (lease is not { Mode: ControllerLeaseMode.Active } || lease.Fence != authority.Lease.Fence)
        {
            return new SessionChange(LifecycleWriteOutcome.LeaseRejected);
        }
        var catalog = await _store.ReadCatalogProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        if (catalog is not { State: CatalogProfileState.ClosureHeld })
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }
        var handoffs = await _handoffs(profile, cancellationToken).ConfigureAwait(false);
        var proof = await _store.ReadDrainProofAsync(profile, lease.Fence, _timing, handoffs, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (!authority.Clock.TryGetUpperBound(now, lease.ValidUntil, _timing.WriteReserve, out var upper))
        {
            return new SessionChange(LifecycleWriteOutcome.LeaseRejected);
        }
        if (proof.Session != expected || !proof.Clear)
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }
        var remaining = Min(lease.ValidUntil - upper, authority.Clock.ValidUntil - upper);
        remaining = Min(remaining, _timing.Freshness - (upper - proof.OldestEvidenceAt)) - _timing.WriteReserve;
        if (remaining <= TimeSpan.Zero)
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }
        var deadline = upper + Min(remaining, sendWindow);
        return await _store.BeginProfileRetirementSessionCloseAsync(expected, lease.Fence, catalog, proof.Guards, upper,
            deadline, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileRetirementResult> CompleteRetirementAsync(
        CatalogProfile catalog, ControllerLease lease, ControllerClockEvidence clockEvidence, DynamoDbLifecycleStore.DrainProof proof,
        CancellationToken cancellationToken)
    {
        bool clear = ClearWithMargin(clockEvidence, proof);
        if (clear && proof.Session is { Phase: SessionPhase.Active, Identity: { } identity })
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.SessionRetirementRequired, identity);
        }
        if (!clear || proof.Session is not (null or { Phase: SessionPhase.Retired }))
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.ReconciliationPending);
        }

        var outcome = await _store.TryTombstoneAsync(lease.Fence, catalog, proof.Guards, cancellationToken).ConfigureAwait(false);
        return Result(outcome, ProfileRetirementStatus.Retired);
    }

    private async Task<ProfileRetirementResult> ObserveClearPassAsync(
        CatalogProfile catalog, ControllerLease lease, ControllerClockEvidence clockEvidence, DynamoDbLifecycleStore.DrainProof proof,
        CancellationToken cancellationToken)
    {
        var prior = await _store.ReadDrainPassAsync(catalog.Profile, cancellationToken).ConfigureAwait(false);
        if (!ClearWithMargin(clockEvidence, proof))
        {
            if (prior is not { FirstClearAt: not null })
            {
                return new ProfileRetirementResult(ProfileRetirementStatus.ReconciliationPending);
            }
            var reset = await _store.WriteDrainPassAsync(lease.Fence, catalog, prior, null, [], cancellationToken).ConfigureAwait(false);
            return Result(reset, ProfileRetirementStatus.ReconciliationPending);
        }

        var now = _clock.GetUtcNow();
        var upper = now + clockEvidence.MaximumAbsoluteSkew;
        var lower = now - clockEvidence.MaximumAbsoluteSkew;
        if (prior is not { FirstClearAt: { } first } || prior.AppliedGeneration != catalog.AppliedGeneration ||
            upper - first > _timing.Freshness)
        {
            var written = await _store.WriteDrainPassAsync(lease.Fence, catalog, prior, upper, proof.Guards, cancellationToken).ConfigureAwait(false);
            return Result(written, ProfileRetirementStatus.FirstClearRecorded);
        }
        if (lower - first < _timing.ClearSeparation || proof.OldestExternalAt - first <= clockEvidence.MaximumAbsoluteSkew)
        {
            return new ProfileRetirementResult(ProfileRetirementStatus.WaitingForSecondPass);
        }

        var guards = proof.Guards.Add(new LifecycleRecordCondition(LifecycleKey.Drain(catalog.Profile), prior.Revision));
        var held = await _store.TryHoldCatalogClosureAsync(lease.Fence, catalog, guards, cancellationToken).ConfigureAwait(false);
        return Result(held, ProfileRetirementStatus.ClosureHeld);
    }

    private bool HasTime(ActiveControllerAuthority authority, ControllerLease lease) =>
        authority.Clock.TryGetUpperBound(_clock.GetUtcNow(), lease.ValidUntil, _timing.WriteReserve, out _);

    private bool ClearWithMargin(ControllerClockEvidence clockEvidence, DynamoDbLifecycleStore.DrainProof proof) =>
        proof.Clear && _clock.GetUtcNow() + clockEvidence.MaximumAbsoluteSkew - proof.OldestEvidenceAt < _timing.Freshness - _timing.WriteReserve;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static ProfileRetirementResult Result(LifecycleWriteOutcome outcome, ProfileRetirementStatus committed) =>
        new(outcome == LifecycleWriteOutcome.Committed ? committed : ProfileRetirementStatus.StateChanged);
}
