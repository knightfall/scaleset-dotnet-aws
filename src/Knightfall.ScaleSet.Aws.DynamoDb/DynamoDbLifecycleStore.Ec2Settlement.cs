using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    internal async Task<LifecycleWriteOutcome> WriteEc2ResourceSettlementAsync(
        ControllerLease lease, EffectJournalSnapshot previous, EffectJournalEntry settled, Guid transition,
        CancellationToken cancellationToken)
    {
        var profile = settled.Envelope.Profile;
        RequireProfileLease(profile, lease.Fence);
        if (settled.Resolution is not { ExecutorBinding: { } binding, ResourceObservation: { } observation } ||
            observation.ObservedAt > _timeProvider.GetUtcNow())
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var actions = new List<TransactionAction>();
        var executor = await ReadExecutorAsync(profile, binding.AllocationId, cancellationToken).ConfigureAwait(false);
        if (settled.Envelope.Request is Ec2LaunchEffectRequest launch)
        {
            var lineage = await ReadEc2LaunchLineageAsync(profile, launch.AllocationId, cancellationToken).ConfigureAwait(false);
            if (!MatchesEc2Lineage(lineage, settled.Envelope) ||
                !await AddEc2ResourceClaimAsync(actions, profile, binding, executor, transition, cancellationToken).ConfigureAwait(false))
            {
                return LifecycleWriteOutcome.Conflict;
            }
            actions.Add(ExactEc2LineageCondition(lineage!));
        }
        else if (executor?.Binding != binding)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var inventory = await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        var state = executor?.State ?? new ExecutorState(binding, StartedAt: (binding.Provider as Ec2ExecutorIdentity)?.InstanceLaunchTime);
        var observed = ExecutorObservations.ObserveResource(state, observation, _timeProvider.GetUtcNow());
        int change = AddExecutorActions(actions, executor, observed, transition);
        actions.Add(WriteExecutorInventoryHead(profile, inventory, change, transition));

        var access = lease.Mode == ControllerLeaseMode.RecoveryOnly ? LeaseAccess.Recovery : LeaseAccess.Active;
        return await WriteEffectAsync(profile, lease.Fence, transition,
            new EffectWrite(settled.Envelope.IntentId, previous.Revision, settled.State, settled.WritePayload()),
            access, cancellationToken, accompanying: actions, requestToken: Guid.NewGuid()).ConfigureAwait(false);
    }

    private async Task<bool> AddEc2ResourceClaimAsync(List<TransactionAction> actions, LifecycleProfile profile,
        ExecutorBinding binding, ExecutorSnapshot? executor, Guid transition, CancellationToken cancellationToken)
    {
        var key = LifecycleKey.Claim("aws-resource#" + binding.ResourceArn);
        var claim = await ReadRecordAsync(key, cancellationToken).ConfigureAwait(false);
        if (executor is null && claim is null)
        {
            actions.Add(ExecutorWrite(key, binding, null, transition, profile.Deployment));
            return true;
        }
        if (executor?.Binding != binding || claim is null || ParseExecutorRecord<ExecutorBinding>(claim) != binding)
        {
            return false;
        }
        actions.Add(ExactExecutorClaimCondition(key, claim, profile.Deployment));
        return true;
    }
}
