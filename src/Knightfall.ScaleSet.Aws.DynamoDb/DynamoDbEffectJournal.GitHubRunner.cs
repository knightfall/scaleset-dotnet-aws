using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbEffectJournal
{
    /// <summary>Settles an exact initial GitHub runner effect whose send authorization can still be discarded.</summary>
    public async Task<LifecycleWriteOutcome> AbandonUnsentRunnerEffectAsync(
        ControllerLease lease, EffectSendAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(authorization);
        if (lease.Mode != ControllerLeaseMode.Active || lease.Fence != authorization.Lease)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        var envelope = authorization.Envelope;
        if (envelope.Request is not (GitHubJitGenerateEffectRequest or GitHubRunnerRemoveEffectRequest) ||
            envelope.ProviderReplayDeadline is not null || authorization.TryDiscard() is not { } discard)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var owner = await _store.ReadLeaseAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
        if (owner is not { Mode: ControllerLeaseMode.Active } || owner.Fence != authorization.Lease ||
            owner.ValidUntil <= authorization.EstimateUtcNow())
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }

        var current = await ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
        if (MatchesAbandonment(current, authorization, discard))
        {
            return LifecycleWriteOutcome.Committed;
        }
        if (current is not { Entry.State: EffectState.Executing, Entry.EarlierSendUncertain: false } ||
            current.Revision != authorization.Revision || current.TransitionId != authorization.BeginTransitionId ||
            current.Entry.Attempt != authorization.Attempt || !current.Entry.Envelope.Matches(envelope))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var settled = current.Entry.AbandonRunnerEffectBeforeSend(authorization.Attempt, discard.ObservedAt);
        var outcome = await _store.WriteAbandonedRunnerEffectAsync(owner.Fence, discard.TransitionId, current, settled,
            authorization.EstimateUtcNow(), cancellationToken).ConfigureAwait(false);
        if (outcome is LifecycleWriteOutcome.Conflict or LifecycleWriteOutcome.Indeterminate &&
            MatchesAbandonment(await ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken).ConfigureAwait(false), authorization, discard))
        {
            return LifecycleWriteOutcome.Committed;
        }

        return outcome;
    }

    private static bool MatchesAbandonment(EffectJournalSnapshot? current, EffectSendAuthorization authorization,
        EffectSendAuthorization.DiscardWitness discard) =>
        current is { Entry.State: EffectState.Settled, Entry.Resolution.Kind: EffectResolutionKind.AbandonedBeforeSend } &&
        current.Revision == checked(authorization.Revision + 1) && current.TransitionId == discard.TransitionId &&
        current.Entry.Attempt == authorization.Attempt && current.Entry.Envelope.Matches(authorization.Envelope) &&
        current.Entry.Resolution.ObservedAt == discard.ObservedAt;

    public async Task<EffectBeginOutcome> BeginJitAsync(
        ControllerLease lease, CatalogProfile catalog, ExecutorSnapshot executor, JitHandoffSnapshot handoff,
        EffectJournalSnapshot intent, DateTimeOffset upperUtcNow, DateTimeOffset deadline, TimeSpan writeReserve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        if (intent.Entry.Envelope.Request is not GitHubJitGenerateEffectRequest request ||
            !DynamoDbLifecycleStore.MatchesJitRequest(intent.Entry.Envelope, handoff) ||
            executor.State is not { Resource.State: ExecutorResourceState.Running, HasConflict: false, RunnerId: null })
        {
            return new EffectBeginOutcome(LifecycleWriteOutcome.Conflict, null);
        }

        return await BeginGitHubRunnerAsync(lease, catalog, executor, intent, request.ScopeIdentity, request.ScaleSetId,
            request.AllocationId, request.ResourceArn, request.Incarnation, upperUtcNow, deadline, writeReserve, handoff, null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EffectBeginOutcome> BeginRunnerRemovalAsync(
        ControllerLease lease, CatalogProfile catalog, ExecutorSnapshot executor, JitHandoffSnapshot handoff, EffectJournalSnapshot intent,
        DateTimeOffset upperUtcNow, DateTimeOffset deadline, TimeSpan writeReserve, CancellationToken cancellationToken)
    {
        if (intent.Entry.Envelope.Request is not GitHubRunnerRemoveEffectRequest request || !AllowsRemoval(executor, request.RunnerId) ||
            intent.Entry.Envelope.IntentId != "remove-" + handoff.OfferId ||
            handoff.RunnerId != request.RunnerId || handoff.RunnerName != request.RunnerName ||
            handoff.Identity.AllocationId != request.AllocationId || handoff.Identity.ResourceArn != request.ResourceArn ||
            handoff.Identity.Incarnation != request.Incarnation || handoff.Identity.ScaleSetId != request.ScaleSetId ||
            handoff.Identity.ScopeIdentity != request.ScopeIdentity)
        {
            return new EffectBeginOutcome(LifecycleWriteOutcome.Conflict, null);
        }

        return await BeginGitHubRunnerAsync(lease, catalog, executor, intent, request.ScopeIdentity, request.ScaleSetId,
            request.AllocationId, request.ResourceArn, request.Incarnation, upperUtcNow, deadline, writeReserve, null, handoff,
            cancellationToken).ConfigureAwait(false);
    }

    public static bool AllowsRemoval(ExecutorSnapshot executor, long runnerId) =>
        executor.State is { HasConflict: false, Resource.State: ExecutorResourceState.IrreversiblyTerminated } state &&
        state.RunnerId == runnerId && state.Job is null or { Phase: ExecutorJobPhase.Completed, HasConflict: false };

    private async Task<EffectBeginOutcome> BeginGitHubRunnerAsync(
        ControllerLease lease, CatalogProfile catalog, ExecutorSnapshot executor, EffectJournalSnapshot intent,
        string scopeIdentity, long setId, Guid allocationId, string resourceArn, Guid incarnation, DateTimeOffset upperUtcNow,
        DateTimeOffset deadline, TimeSpan writeReserve, JitHandoffSnapshot? handoff, JitHandoffSnapshot? removal, CancellationToken cancellationToken)
    {
        var envelope = intent.Entry.Envelope;
        var binding = executor.Binding;
        if (lease.Mode != ControllerLeaseMode.Active || lease.Deployment != envelope.Profile.Deployment ||
            catalog.Profile != envelope.Profile || catalog.Profile != binding.Profile || envelope.Generation != catalog.AppliedGeneration ||
            catalog.State is not (CatalogProfileState.Active or CatalogProfileState.Draining) ||
            catalog.Binding.ScopeIdentity != scopeIdentity || catalog.Binding.ScaleSetId != setId ||
            binding.AllocationId != allocationId.ToString("D") || binding.ResourceArn != resourceArn || binding.Incarnation != incarnation)
        {
            return new EffectBeginOutcome(LifecycleWriteOutcome.Conflict, null);
        }

        var index = await _store.ReadEffectIndexAsync(envelope.Profile, cancellationToken).ConfigureAwait(false);
        if (index is not { State: EffectIndexState.Ready } || index.OwnerEpoch != lease.Epoch || index.WriterProtocol != lease.WriterProtocol)
        {
            return new EffectBeginOutcome(LifecycleWriteOutcome.RebuildRequired, null);
        }
        return await BeginGuardedAsync(lease, intent, upperUtcNow, deadline, writeReserve,
            new EffectDispatchGuard(catalog, index, binding.AllocationId, executor.Revision, handoff, removal), cancellationToken).ConfigureAwait(false);
    }
}
