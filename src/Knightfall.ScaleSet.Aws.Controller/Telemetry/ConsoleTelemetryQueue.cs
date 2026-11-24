using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal sealed class ConsoleTelemetryQueue
{
    internal const int Capacity = 256;

    private readonly Channel<LogEvent> _events = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    internal ConsoleTelemetryQueue(ILogEventSink console, CancellationToken deadline)
    {
        // Console writes can block behind their reader. Only this worker may call the sink.
        using IDisposable? noContext = ExecutionContext.IsFlowSuppressed() ? null : ExecutionContext.SuppressFlow();
        Completion = Task.Run(async () =>
        {
            try
            {
                await foreach (var log in _events.Reader.ReadAllAsync(deadline).ConfigureAwait(false))
                {
                    if (deadline.IsCancellationRequested)
                    {
                        return false;
                    }
                    console.Emit(log);
                }
                return true;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                _events.Writer.TryComplete();
                (console as IDisposable)?.Dispose();
            }
        });
    }

    internal Task<bool> Completion { get; }

    internal bool TryWrite(LogEvent log) => _events.Writer.TryWrite(log);

    internal void Stop() => _events.Writer.TryComplete();
}
