using System.Collections.Immutable;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record ListenerCapacity(int Maximum, int OutsideAssigned, bool IsReady = true)
{
    public IReadOnlyList<LifecycleRecordCondition>? ReadGuards { get; init; }
    public ImmutableArray<WarmRunnerCandidate> WarmCandidates { get; init; } = [];
}

public sealed record WarmRunnerCandidate(ExecutorSnapshot Executor, DateTimeOffset ValidThrough);
