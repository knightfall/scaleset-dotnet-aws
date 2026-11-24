namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public static class ExecutorObservations
{
    public static ExecutorState ObserveResource(
        ExecutorState executor, ExecutorResourceObservation observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(observation.State) || observation.EvidenceId == Guid.Empty ||
            !IsLocalObservation(observation.ObservedAt, now) || observation.StartedAt > observation.ObservedAt ||
            observation.StartedAt == default(DateTimeOffset))
        {
            throw new ArgumentException("A valid resource observation is required.", nameof(observation));
        }

        var startedAt = executor.StartedAt ?? observation.StartedAt;
        if (observation.State == ExecutorResourceState.Starting && startedAt is null)
        {
            throw new ArgumentException("An original start time is required for a starting executor.", nameof(observation));
        }
        var presentAt = executor.LastResourcePresentAt;
        if (IsPresent(observation.State) && (presentAt is null || observation.ObservedAt > presentAt))
        {
            presentAt = observation.ObservedAt;
        }

        var current = executor.Resource;
        bool conflict = executor.HasConflict ||
            (executor.StartedAt is not null && observation.StartedAt is not null && executor.StartedAt != observation.StartedAt);
        if (current?.State == ExecutorResourceState.IrreversiblyTerminated)
        {
            if (observation.State == ExecutorResourceState.IrreversiblyTerminated && observation.ObservedAt < current.ObservedAt)
            {
                current = observation;
            }
            conflict |= presentAt >= current.ObservedAt;
        }
        else if (observation.State == ExecutorResourceState.IrreversiblyTerminated)
        {
            current = observation;
            conflict |= presentAt >= observation.ObservedAt;
        }
        else if (current is null || observation.ObservedAt > current.ObservedAt)
        {
            current = observation;
        }
        else if (observation.ObservedAt == current.ObservedAt && observation.State != current.State)
        {
            conflict = true;
        }

        return executor with { Resource = current, LastResourcePresentAt = presentAt, StartedAt = startedAt, HasConflict = conflict };
    }

    public static ExecutorState ObserveProcess(
        ExecutorState executor, ExecutorProcessObservation observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.RunnerId <= 0 || observation.RunnerId != executor.RunnerId || !Enum.IsDefined(observation.State) ||
            observation.EvidenceId == Guid.Empty || !IsLocalObservation(observation.ObservedAt, now))
        {
            throw new ArgumentException("An authenticated observation for the executor's exact runner is required.", nameof(observation));
        }

        var readyAt = executor.LastProcessReadyAt;
        if (observation.State == ExecutorProcessState.Ready && (readyAt is null || observation.ObservedAt > readyAt))
        {
            readyAt = observation.ObservedAt;
        }
        var current = executor.Process;
        bool conflict = executor.HasConflict;
        if (current?.State == ExecutorProcessState.Exited)
        {
            if (observation.State == ExecutorProcessState.Exited && observation.ObservedAt < current.ObservedAt)
            {
                current = observation;
            }
            conflict |= readyAt >= current.ObservedAt;
        }
        else if (observation.State == ExecutorProcessState.Exited)
        {
            current = observation;
            conflict |= readyAt >= observation.ObservedAt;
        }
        else if (current is null || observation.ObservedAt > current.ObservedAt)
        {
            current = observation;
        }
        else if (observation.ObservedAt == current.ObservedAt && observation.State != current.State)
        {
            conflict = true;
        }

        return executor with { Process = current, LastProcessReadyAt = readyAt, HasConflict = conflict };
    }

    public static ExecutorState ObserveJob(ExecutorState executor, ExecutorJobObservation observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.RequestId <= 0 || observation.RunnerId <= 0 || observation.RunnerId != executor.RunnerId ||
            observation.SessionId == Guid.Empty || observation.MessageId < 0 ||
            observation.Phase is not (ExecutorJobPhase.Started or ExecutorJobPhase.Completed) ||
            !IsLocalObservation(observation.ObservedAt, now))
        {
            throw new ArgumentException("A job observation for the executor's exact runner is required.", nameof(observation));
        }
        if (executor.Job is { } current)
        {
            if (current.RequestId != observation.RequestId)
            {
                return executor with { HasConflict = true };
            }
            if (observation.Phase < current.Phase || (observation.Phase == current.Phase &&
                (observation.ObservedAt < current.ObservedAt || (observation.ObservedAt == current.ObservedAt &&
                    observation.SessionId == current.SessionId && observation.MessageId <= current.MessageId))))
            {
                return executor with { HasConflict = executor.HasConflict || observation.HasConflict };
            }
        }
        return executor with { Job = observation, HasConflict = executor.HasConflict || observation.HasConflict };
    }

    private static bool IsPresent(ExecutorResourceState state) =>
        state is ExecutorResourceState.Running or ExecutorResourceState.Starting;

    private static bool IsLocalObservation(DateTimeOffset observedAt, DateTimeOffset now) =>
        observedAt != default && observedAt <= now;
}
