using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbEffectJournal
{
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
