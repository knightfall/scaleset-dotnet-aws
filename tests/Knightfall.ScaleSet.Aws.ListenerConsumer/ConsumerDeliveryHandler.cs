using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.ListenerConsumer;

public sealed class ConsumerDeliveryHandler(
    Func<ScaleSetListenerDelivery, CancellationToken, Task<ScaleSetListenerDeliveryDisposition>> handle)
    : IScaleSetListenerDeliveryHandler
{
    public Task<ScaleSetListenerDeliveryDisposition> HandleAsync(
        ScaleSetListenerDelivery delivery,
        CancellationToken cancellationToken)
    {
        return handle(delivery, cancellationToken);
    }
}
