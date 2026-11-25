using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Planning;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public static class ExecutorClassifier
{
    private static readonly ExecutorClassification Unknown = new(true, true, false, RunnerSlotState.Unknown);
    private static readonly ExecutorClassification Gone = new(false, false, false, RunnerSlotState.Unknown);
    private static readonly ExecutorClassification Starting = new(true, false, false, RunnerSlotState.Starting);
    private static readonly ExecutorClassification Idle = new(true, false, false, RunnerSlotState.Idle);
    private static readonly ExecutorClassification Busy = new(true, false, false, RunnerSlotState.Busy);
    private static readonly ExecutorClassification Completed = new(true, false, true, RunnerSlotState.Unknown);
    private static readonly ExecutorClassification Warm = Unknown with { CoversIdleReserve = true };

    public static ExecutorClassification Classify(
        ExecutorState executor, DateTimeOffset now, TimeSpan freshness, TimeSpan logFlushGrace)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshness, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logFlushGrace, TimeSpan.Zero);

        if (executor.HasConflict || executor.Resource is not { } resource || resource.ObservedAt > now ||
            executor.Process?.ObservedAt > now || executor.Job?.ObservedAt > now)
        {
            return Unknown;
        }
        if (resource.State == ExecutorResourceState.IrreversiblyTerminated)
        {
            return Gone;
        }
        if (executor.AdmissionClosure is not null || !IsFresh(resource.ObservedAt, now, freshness))
        {
            return Unknown;
        }
        if (resource.State == ExecutorResourceState.Starting && executor.StartedAt is { } start && start <= resource.ObservedAt &&
            executor.Job is null && executor.Process is null or { State: ExecutorProcessState.Unknown })
        {
            return Starting;
        }
        if (resource.State != ExecutorResourceState.Running)
        {
            return Unknown;
        }
        // A fresh, exact Fargate allocation covers only the warm reserve, including before GitHub registration.
        if (executor.Binding.Provider is FargateExecutorIdentity && executor.Job is null && executor.Process is null)
        {
            return Warm;
        }
        if (executor.RunnerId is not long runnerId)
        {
            return Unknown;
        }

        if (executor.Job is { } job)
        {
            if (job.RunnerId != runnerId)
            {
                return Unknown;
            }
            if (job.Phase == ExecutorJobPhase.Started && executor.Process is not { State: ExecutorProcessState.Exited })
            {
                return Busy;
            }
            return job.Phase == ExecutorJobPhase.Completed &&
                executor.Process is { State: ExecutorProcessState.Exited } exit && exit.RunnerId == runnerId &&
                now - exit.ObservedAt >= logFlushGrace ? Completed : Unknown;
        }

        return executor.Process is { State: ExecutorProcessState.Ready } process && process.RunnerId == runnerId &&
            IsFresh(process.ObservedAt, now, freshness) ? Idle : Unknown;
    }

    public static ListenerCapacity ProjectListenerCapacity(
        ExecutorInventorySnapshot inventory, SessionDemandObservation? demand, int maximum,
        DateTimeOffset now, TimeSpan freshness, TimeSpan logFlushGrace)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshness, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logFlushGrace, TimeSpan.Zero);
        if (!inventory.IsConsistent || inventory.Coverage is not { IsComplete: true, BackendCapacityCeiling: >= 0 } coverage ||
            inventory.Executors.IsDefault || coverage.UnownedOccupied < 0 || !IsFresh(coverage.ObservedAt, now, freshness) ||
            demand is not { TotalAssignedJobs: >= 0 } || !IsFresh(demand.ObservedAt, now, freshness))
        {
            return new ListenerCapacity(0, 0, IsReady: false);
        }

        long occupied = coverage.UnownedOccupied;
        var matched = new HashSet<long>();
        var warm = ImmutableArray.CreateBuilder<WarmRunnerCandidate>();
        bool coherent = true;
        foreach (var executor in inventory.Executors)
        {
            var classification = Classify(executor.State, now, freshness, logFlushGrace);
            if (!classification.Occupied)
            {
                continue;
            }
            occupied++;
            if (classification.CoversIdleReserve && executor.State.RunnerId is > 0)
            {
                var observedAt = executor.State.Resource!.ObservedAt;
                var earliest = (observedAt < coverage.ObservedAt ? observedAt : coverage.ObservedAt).ToUniversalTime();
                var validThrough = DateTimeOffset.MaxValue - earliest < freshness ? DateTimeOffset.MaxValue : earliest + freshness;
                warm.Add(new WarmRunnerCandidate(executor, validThrough));
            }
            if (classification.SlotState == RunnerSlotState.Busy && demand.MessageId is long messageId &&
                executor.State.Job is { } job && job.SessionId == demand.SessionId && job.MessageId == messageId &&
                job.ObservedAt == demand.ObservedAt && !matched.Add(job.RequestId))
            {
                coherent = false;
            }
        }

        int insideAssigned = coherent && matched.Count <= demand.TotalAssignedJobs ? matched.Count : 0;
        return new ListenerCapacity(Math.Min(maximum, coverage.BackendCapacityCeiling.Value),
            (int)Math.Min(int.MaxValue, occupied - insideAssigned))
        {
            WarmCandidates = coherent ? warm.ToImmutable() : [],
        };
    }

    private static bool IsFresh(DateTimeOffset observedAt, DateTimeOffset now, TimeSpan freshness) =>
        observedAt != default && observedAt <= now && now - observedAt <= freshness;
}
