using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    /// <summary>Closes JIT admission for an exact Fargate executor before a verified unused or revoked stop.</summary>
    public async Task<LifecycleWriteOutcome> CloseFargateAdmissionAsync(ControllerLease lease, ExecutorSnapshot expected,
        TimeSpan observationFreshness, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(observationFreshness, TimeSpan.Zero);
        var binding = expected.Binding;
        RequireProfileLease(binding.Profile, lease.Fence);
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        var current = await ReadExecutorAsync(binding.Profile, binding.AllocationId, cancellationToken).ConfigureAwait(false);
        if (current?.Binding != binding || binding.Provider is not FargateExecutorIdentity ||
            current.State is not { HasConflict: false, Job: null, Process: null, Resource: { } resource } ||
            resource.State is not (ExecutorResourceState.Starting or ExecutorResourceState.Running))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var allocation = Guid.ParseExact(binding.AllocationId, "D");
        var handoff = await ReadJitHandoffAsync(binding.Profile, allocation, cancellationToken).ConfigureAwait(false);
        FargateAdmissionClosure closure;
        if (handoff is null && current.State.RunnerId is null)
        {
            closure = FargateAdmissionClosure.Unused;
        }
        else if (handoff is { State: JitHandoffState.Revoked, OfferSha256: not null, RunnerId: > 0 } &&
            handoff.RunnerId == current.State.RunnerId && MatchesJitExecutor(current, handoff.Identity))
        {
            closure = FargateAdmissionClosure.OfferRevoked;
        }
        else
        {
            return LifecycleWriteOutcome.Conflict;
        }
        var zeroAcquisitions = await ReadZeroFargateAcquisitionsAsync(binding.Profile, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (zeroAcquisitions is null || resource.ObservedAt == default || resource.ObservedAt > now ||
            now - resource.ObservedAt > observationFreshness)
        {
            return LifecycleWriteOutcome.Conflict;
        }
        if (current.State.AdmissionClosure == closure)
        {
            var owner = await ReadLeaseAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
            return owner is { Mode: ControllerLeaseMode.Active } && owner.Fence == lease.Fence && owner.ValidUntil > ConservativeLatest(now)
                ? LifecycleWriteOutcome.Committed : LifecycleWriteOutcome.LeaseRejected;
        }
        if (current != expected || current.State.AdmissionClosure is not null)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var inventory = await ReadExecutorInventoryHeadAsync(binding.Profile, cancellationToken).ConfigureAwait(false) ?? throw InvalidRecord();
        var transition = Guid.NewGuid();
        var actions = new List<TransactionAction>
        {
            LeaseCondition(lease.Fence),
            RecordRevisionCondition(zeroAcquisitions),
        };
        if (handoff is null)
        {
            actions.Add(RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Handoff(binding.Profile, allocation), null)));
        }
        else
        {
            actions.Add(ExactJitHandoffCondition(handoff));
            actions.Add(OfferAbsentCondition(handoff));
        }
        int change = AddExecutorActions(actions, current, current.State with { AdmissionClosure = closure }, transition);
        actions.Add(WriteExecutorInventoryHead(binding.Profile, inventory, change, transition));
        var outcome = await WriteAsync(actions, transition, cancellationToken).ConfigureAwait(false);
        return await ResolveExecutorWriteAsync(binding, transition, outcome, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<LifecycleRecordCondition?> ReadZeroFargateAcquisitionsAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var key = LifecycleKey.Admission(profile);
        var balance = await ReadRecordAsync(key, cancellationToken).ConfigureAwait(false);
        return await HasNoReservationsAsync(profile, balance, cancellationToken).ConfigureAwait(false)
            ? new LifecycleRecordCondition(key, balance?.Revision) : null;
    }
}
