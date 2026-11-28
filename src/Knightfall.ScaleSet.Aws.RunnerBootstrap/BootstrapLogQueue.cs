using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

internal sealed class BootstrapLogQueue : ILogger, IAsyncDisposable
{
    internal static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(200);
    private readonly Channel<Action<ILogger>> _events = Channel.CreateBounded<Action<ILogger>>(new BoundedChannelOptions(8)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly TimeProvider _clock;
    private readonly Task _worker;
    private int _stop;

    private BootstrapLogQueue(ILogger logger, TimeProvider clock)
    {
        _clock = clock;
        using IDisposable? noContext = ExecutionContext.IsFlowSuppressed() ? null : ExecutionContext.SuppressFlow();
        _worker = Task.Run(async () =>
        {
            await foreach (var emit in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _stop) != 0)
                {
                    break;
                }
                BootstrapLog.Write(logger, emit);
            }
        });
    }

    /// <summary>Starts a worker only when the caller has not already provided a queue.</summary>
    internal static BootstrapLogQueue? Start(ILogger? logger, TimeProvider clock) =>
        logger is null or BootstrapLogQueue ? null : new BootstrapLogQueue(logger, clock);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _events.Writer.TryWrite(sink =>
        {
            if (sink.IsEnabled(logLevel))
            {
                sink.Log(logLevel, eventId, state, null, formatter);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        try
        {
            await _worker.WaitAsync(DrainBudget, _clock).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A synchronous provider cannot be cancelled. Drop pending entries and stop waiting.
            Volatile.Write(ref _stop, 1);
        }
    }
}
