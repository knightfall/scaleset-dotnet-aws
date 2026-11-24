using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    public async Task<LifecycleWriteOutcome> WriteSessionDeliveryRecordsAsync(
        OwnedSessionFence fence, Guid transitionId, IReadOnlyList<LifecycleRecordWrite> writes,
        IReadOnlyList<ExecutorJobObservation> jobs, CancellationToken cancellationToken,
        IReadOnlyList<LifecycleRecordCondition>? readGuards = null)
    {
        RequireSessionFence(fence);
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Any(job => job.SessionId != fence.Identity.SessionId))
        {
            throw new ArgumentException("Runner observations must belong to the owned delivery session.", nameof(jobs));
        }
        var profile = fence.Identity.Profile;
        var now = _timeProvider.GetUtcNow();
        var actions = new List<TransactionAction>();
        int activeChange = 0;
        bool changed = false;
        foreach (var observations in jobs.GroupBy(job => job.RunnerId))
        {
            var indexKey = LifecycleKey.RunnerIdentity(profile, observations.Key);
            var index = await ReadRecordAsync(indexKey, cancellationToken).ConfigureAwait(false);
            actions.Add(RecordRevisionCondition(new LifecycleRecordCondition(indexKey, index?.Revision)));
            if (index is null)
            {
                continue;
            }

            var identity = ParseExecutorRecord<ExecutorRunnerBinding>(index);
            var executor = await ReadExecutorAsync(profile, identity.AllocationId, cancellationToken).ConfigureAwait(false);
            if (executor is null || executor.State.RunnerId != observations.Key || executor.Binding.Incarnation != identity.Incarnation)
            {
                throw InvalidRecord();
            }
            var next = executor.State;
            foreach (var job in observations)
            {
                next = ExecutorObservations.ObserveJob(next, job, now);
            }
            if (next == executor.State)
            {
                continue;
            }
            changed = true;
            activeChange += AddExecutorActions(actions, executor, next, transitionId);
        }
        if (changed)
        {
            var inventory = await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false) ?? throw InvalidRecord();
            actions.Add(WriteExecutorInventoryHead(profile, inventory, activeChange, transitionId));
        }
        return await WriteSessionRecordsCoreAsync(fence, transitionId, writes, readGuards, actions, cancellationToken)
            .ConfigureAwait(false);
    }
}
