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
        EffectEvidence evidence, DateTimeOffset started, CancellationToken cancellationToken)
    {
        var current = await ReadCurrentLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
        if (current is null || Window(current, clockEvidence) is null)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        var exact = ValidateEvidence(effect, clockEvidence, evidence, started);
        return exact is null ? LifecycleWriteOutcome.Indeterminate
            : await _journal.ResolveAsync(current, effect, Guid.NewGuid(), exact, cancellationToken).ConfigureAwait(false);
    }

    private EffectEvidence? ValidateEvidence(
        EffectJournalSnapshot effect, ControllerClockEvidence clockEvidence, EffectEvidence? evidence, DateTimeOffset started)
    {
        if (evidence is null || !IsCurrentObservation(evidence.ObservedAt, started, _clock.GetUtcNow()))
        {
            return null;
        }
        if (evidence.Kind == EffectResolutionKind.Absent)
        {
            evidence = evidence with { EarliestObservedAt = evidence.ObservedAt - clockEvidence.MaximumAbsoluteSkew };
        }
        return effect.Entry.CanResolve(evidence) ? evidence : null;
    }
}
