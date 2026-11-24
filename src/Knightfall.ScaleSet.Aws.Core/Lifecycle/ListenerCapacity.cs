namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record ListenerCapacity(int Maximum, int OutsideAssigned, bool IsReady = true)
{
    public IReadOnlyList<LifecycleRecordCondition>? ReadGuards { get; init; }
}
