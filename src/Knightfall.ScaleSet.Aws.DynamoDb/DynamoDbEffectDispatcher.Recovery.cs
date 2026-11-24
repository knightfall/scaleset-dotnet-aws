using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record EffectCandidateCursor(Guid Generation, string AfterSortKey);

internal sealed record EffectCandidatePage(ImmutableArray<string> IntentIds, EffectCandidateCursor? Continuation);

public sealed record EffectReconciliationPass(
    LifecycleWriteOutcome Outcome, int RebuildPages, int Examined, int Settled, EffectCandidateCursor? Continuation);

public sealed record EffectRecoveryAuthority(
    ControllerLeaseRecoveryCoordinator Coordinator, IReadOnlyCollection<VerifiedControllerIsolation> Isolation);

public sealed partial class DynamoDbEffectDispatcher
{
    public Task<EffectReconciliationPass> ReconcilePassAsync(
        ControllerLease lease,
        ControllerClockEvidence clockEvidence,
        LifecycleProfile profile,
        EffectCandidateCursor? continuation,
        IReconciliationEffectProvider provider,
        EffectRecoveryAuthority? recovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(clockEvidence);
        ArgumentNullException.ThrowIfNull(provider);
        if (Window(lease, clockEvidence) is null || (lease.Mode == ControllerLeaseMode.RecoveryOnly && recovery is null))
        {
            return Task.FromResult(new EffectReconciliationPass(LifecycleWriteOutcome.LeaseRejected, 0, 0, 0, null));
        }
        return RunPassAsync(lease, profile, continuation,
            (current, effect, token) => RecoverAsync(current, clockEvidence, effect, provider, recovery, token), cancellationToken);
    }

    public Task<EffectReconciliationPass> CancelUnstartedPassAsync(
        ControllerLease lease, LifecycleProfile profile, EffectCandidateCursor? continuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.Mode == ControllerLeaseMode.Active
            ? RunPassAsync(lease, profile, continuation, CancelPlannedAsync, cancellationToken)
            : Task.FromResult(new EffectReconciliationPass(LifecycleWriteOutcome.LeaseRejected, 0, 0, 0, null));
    }

    /// <summary>Cancels inherited, provably unstarted runner launches before opening a session or planning in this epoch.</summary>
    public async Task<bool> CancelInheritedLaunchesAsync(ControllerLeaseOwnership ownership,
        IReadOnlyCollection<LifecycleProfile> configured, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(configured);
        if (ownership.ReadAuthority() is not { } initial)
        {
            return false;
        }

        var deployment = initial.Lease.Deployment;
        if (configured.Any(profile => profile.Deployment != deployment))
        {
            throw new ArgumentException("Startup profiles must belong to the controller deployment.", nameof(configured));
        }
        var durable = await _store.ReadEffectProfilesAsync(deployment, cancellationToken).ConfigureAwait(false);
        foreach (var profile in configured.Concat(durable).Distinct())
        {
            EffectCandidateCursor? continuation = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ownership.ReadAuthority() is not { } current)
                {
                    return false;
                }

                var pass = await RunPassAsync(current.Lease, profile, continuation, CancelPlannedLaunchAsync, cancellationToken).ConfigureAwait(false);
                if (pass.Outcome != LifecycleWriteOutcome.Committed)
                {
                    return false;
                }
                continuation = pass.Continuation;
            }
            while (continuation is not null);
        }
        return true;
    }

    private Task<LifecycleWriteOutcome?> CancelPlannedLaunchAsync(ControllerLease lease, EffectJournalSnapshot effect,
        CancellationToken cancellationToken) => effect.Entry.Envelope.Request is Ec2LaunchEffectRequest or FargateLaunchEffectRequest
            ? CancelPlannedAsync(lease, effect, cancellationToken)
            : Task.FromResult<LifecycleWriteOutcome?>(null);

    public async Task<bool> HasNoUnfinishedEffectsAsync(ControllerLease lease, LifecycleProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(profile);
        if (lease.Mode != ControllerLeaseMode.Active || lease.Deployment != profile.Deployment ||
            await ReadCurrentLeaseAsync(lease, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }
        var head = await _store.ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!ReadyIndex(head, lease) || head!.UnfinishedCount != 0)
        {
            return false;
        }
        var page = await _store.ReadActiveOperationsAsync(profile, lease.Fence, null, cancellationToken).ConfigureAwait(false);
        return page is { IntentIds.IsEmpty: true, Continuation: null } &&
            await _store.ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false) == head &&
            await ReadCurrentLeaseAsync(lease, cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<EffectReconciliationPass> RunPassAsync(
        ControllerLease expected,
        LifecycleProfile profile,
        EffectCandidateCursor? continuation,
        Func<ControllerLease, EffectJournalSnapshot, CancellationToken, Task<LifecycleWriteOutcome?>> process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (expected.Deployment != profile.Deployment || expected.Mode == ControllerLeaseMode.Released ||
            await ReadCurrentLeaseAsync(expected, cancellationToken).ConfigureAwait(false) is not { } lease)
        {
            return new EffectReconciliationPass(LifecycleWriteOutcome.LeaseRejected, 0, 0, 0, null);
        }

        var rebuild = lease.Mode == ControllerLeaseMode.RecoveryOnly
            ? await _store.TryReuseEffectIndexAsync(profile, lease, cancellationToken).ConfigureAwait(false)
            : new EffectRebuildPass(null, LifecycleWriteOutcome.RebuildRequired, 0);
        if (rebuild.Outcome == LifecycleWriteOutcome.RebuildRequired)
        {
            rebuild = await _store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellationToken).ConfigureAwait(false);
        }
        if (rebuild.Outcome != LifecycleWriteOutcome.Committed)
        {
            return new EffectReconciliationPass(rebuild.Outcome, rebuild.PagesRead, 0, 0, null);
        }
        if (!ReadyIndex(rebuild.Head, lease))
        {
            return new EffectReconciliationPass(LifecycleWriteOutcome.RebuildRequired, rebuild.PagesRead, 0, 0, null);
        }
        var page = await _store.ReadEffectCandidatesAsync(profile, lease.Fence, continuation, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return new EffectReconciliationPass(LifecycleWriteOutcome.Conflict, rebuild.PagesRead, 0, 0, null);
        }

        int settled = 0;
        LifecycleWriteOutcome status = LifecycleWriteOutcome.Committed;
        foreach (var intentId in page.IntentIds)
        {
            var effect = await _journal.ReadAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
            if (effect is null || effect.Entry.State == EffectState.Settled)
            {
                continue;
            }
            var outcome = await process(lease, effect, cancellationToken).ConfigureAwait(false);
            if (outcome == LifecycleWriteOutcome.Committed)
            {
                settled++;
            }
            else if (status == LifecycleWriteOutcome.Committed && outcome is { } incomplete)
            {
                status = incomplete;
            }
        }
        return new EffectReconciliationPass(status, rebuild.PagesRead, page.IntentIds.Length, settled, page.Continuation);
    }

    private async Task<LifecycleWriteOutcome?> CancelPlannedAsync(
        ControllerLease lease, EffectJournalSnapshot effect, CancellationToken cancellationToken)
    {
        if (effect.Entry.State != EffectState.Planned)
        {
            return null;
        }
        var envelope = effect.Entry.Envelope;
        var evidence = new EffectEvidence(EffectResolutionKind.CanceledBeforeSend, envelope.Sha256, envelope.Target, _clock.GetUtcNow());
        return await _journal.ResolveAsync(lease, effect, Guid.NewGuid(), evidence, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LifecycleWriteOutcome?> RecoverAsync(
        ControllerLease lease,
        ControllerClockEvidence clockEvidence,
        EffectJournalSnapshot effect,
        IReconciliationEffectProvider provider,
        EffectRecoveryAuthority? recovery,
        CancellationToken cancellationToken)
    {
        if (effect.Entry.State is not (EffectState.Executing or EffectState.Uncertain) || effect.Entry.Attempt is not { } attempt)
        {
            return null;
        }
        var current = await ReadCurrentLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
        if (current is null || Window(current, clockEvidence) is null)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        if (current.Mode == ControllerLeaseMode.RecoveryOnly)
        {
            return await recovery!.Coordinator.ResolveFencedEffectAsync(current, effect, Guid.NewGuid(), clockEvidence, recovery.Isolation,
                (_, token) => ObserveFreshAsync(current, clockEvidence, effect, provider, attempt, token), cancellationToken).ConfigureAwait(false);
        }
        if (attempt.LeaseEpoch != current.Epoch)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }

        if (effect.Entry.Envelope.Request is FargateLaunchEffectRequest { OwnershipTags: not null } &&
            provider is IFargateReplayEffectProvider fargate)
        {
            var progress = await ReplayFargateLaunchAsync(current, clockEvidence, effect.Entry.Envelope.Profile,
                effect.Entry.Envelope.IntentId, fargate, CancellationToken.None, cancellationToken).ConfigureAwait(false);
            return progress switch
            {
                EffectDispatchStatus.Settled => LifecycleWriteOutcome.Committed,
                EffectDispatchStatus.Conflict => LifecycleWriteOutcome.Conflict,
                EffectDispatchStatus.OwnershipUnavailable => LifecycleWriteOutcome.LeaseRejected,
                EffectDispatchStatus.RebuildRequired => LifecycleWriteOutcome.RebuildRequired,
                _ => null,
            };
        }

        var started = _clock.GetUtcNow();
        var evidence = await ObserveFreshAsync(current, clockEvidence, effect, provider, attempt, cancellationToken).ConfigureAwait(false);
        return evidence is null ? null : await ResolveObservedAsync(current, clockEvidence, effect, evidence, started, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<EffectEvidence?> ObserveFreshAsync(
        ControllerLease lease, ControllerClockEvidence clockEvidence, EffectJournalSnapshot effect,
        IReconciliationEffectProvider provider, EffectAttempt attempt, CancellationToken cancellationToken)
    {
        var started = _clock.GetUtcNow();
        if (Window(lease, clockEvidence) is not { } window)
        {
            return null;
        }
        var evidence = await CallBoundedAsync(
            token => provider.ObserveAsync(effect.Entry.Envelope, attempt, token), window.Duration, cancellationToken).ConfigureAwait(false);
        return ValidateEvidence(effect, clockEvidence, evidence, started);
    }

    private async Task<LifecycleWriteOutcome> ResolveObservedAsync(
        ControllerLease lease, ControllerClockEvidence clockEvidence, EffectJournalSnapshot effect,
        EffectEvidence evidence, DateTimeOffset started, CancellationToken cancellationToken, EffectSendAuthorization? sent = null)
    {
        var current = await ReadCurrentLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
        if (current is null || Window(current, clockEvidence) is null)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        var exact = ValidateEvidence(effect, clockEvidence, evidence, started, sent);
        if (exact is null)
        {
            return LifecycleWriteOutcome.Indeterminate;
        }
        Guid transition = Guid.NewGuid();
        return exact.Ec2LaunchRejection is null
            ? await _journal.ResolveAsync(current, effect, transition, exact, cancellationToken).ConfigureAwait(false)
            : await _journal.ResolveEc2ZonalRejectionAsync(current, effect, sent!, transition, exact, cancellationToken).ConfigureAwait(false);
    }

    private EffectEvidence? ValidateEvidence(
        EffectJournalSnapshot effect, ControllerClockEvidence clockEvidence, EffectEvidence? evidence, DateTimeOffset started,
        EffectSendAuthorization? sent = null)
    {
        if (evidence is null || !IsCurrentObservation(evidence.ObservedAt, started, _clock.GetUtcNow()))
        {
            return null;
        }
        if (evidence.Kind == EffectResolutionKind.Absent)
        {
            evidence = evidence with { EarliestObservedAt = evidence.ObservedAt - clockEvidence.MaximumAbsoluteSkew };
        }
        if (evidence.Ec2LaunchRejection is not null)
        {
            if (sent is not { HasStartedSending: true } || sent.Attempt != effect.Entry.Attempt)
            {
                return null;
            }
            // The exact send grant was consumed before the provider call. A fast receipt can arrive
            // while the local UTC clock is still earlier than the conservative durable begin time.
            var upper = evidence.ObservedAt + clockEvidence.MaximumAbsoluteSkew;
            evidence = evidence with { LatestObservedAt = upper < sent.Attempt.BeganAt ? sent.Attempt.BeganAt : upper };
        }
        return effect.Entry.CanResolve(evidence) ? evidence : null;
    }
}
