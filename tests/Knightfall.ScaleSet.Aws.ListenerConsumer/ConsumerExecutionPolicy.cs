using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.ListenerConsumer;

public sealed class ConsumerExecutionPolicy(TimeProvider clock, int capacity, CancellationToken owner)
    : IScaleSetListenerExecutionPolicy
{
    public Task<ScaleSetListenerAuthorization> AuthorizeAsync(
        ScaleSetListenerOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        owner.ThrowIfCancellationRequested();
        int? advertisedCapacity = context.Operation == ScaleSetListenerOperation.Poll ? capacity : null;
        return Task.FromResult(ScaleSetListenerAuthorization.Allow(
            clock.GetUtcNow().AddMinutes(1), advertisedCapacity, owner));
    }
}
