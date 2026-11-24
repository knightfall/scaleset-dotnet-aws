using System.Runtime.InteropServices;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed class ControllerRunLifetime : IDisposable
{
    private static readonly TimeSpan WorkBudget = TimeSpan.FromSeconds(53);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(60);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _shutdownDeadline = new();
    private readonly CancellationTokenSource _exitDeadline = new();
    private int _requested;

    internal ControllerRunLifetime()
    {
        Stopping = _stopping.Token;
        ShutdownDeadline = _shutdownDeadline.Token;
        ExitDeadline = _exitDeadline.Token;
    }

    internal CancellationToken Stopping { get; }
    internal CancellationToken ShutdownDeadline { get; }
    internal CancellationToken ExitDeadline { get; }

    internal void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        RequestStop();
    }

    internal void RequestStop()
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return;
        }

        var clock = TimeProvider.System;
        long started = clock.GetTimestamp();
        var totalRemaining = TotalBudget - clock.GetElapsedTime(started);
        _exitDeadline.CancelAfter(totalRemaining > TimeSpan.Zero ? totalRemaining : TimeSpan.Zero);
        var workRemaining = WorkBudget - clock.GetElapsedTime(started);
        _shutdownDeadline.CancelAfter(workRemaining > TimeSpan.Zero ? workRemaining : TimeSpan.Zero);
        _ = NotifyStoppingAsync();
    }

    private async Task NotifyStoppingAsync()
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            // A cancellation callback cannot stop the process shutdown deadline.
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _shutdownDeadline.Dispose();
        _exitDeadline.Dispose();
    }
}
