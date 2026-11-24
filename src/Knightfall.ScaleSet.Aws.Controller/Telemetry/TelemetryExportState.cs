using System.Diagnostics.Metrics;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal sealed class TelemetryExportState(TextWriter diagnostics, TimeProvider clock) : IDisposable
{
    private readonly TaskCompletionSource<ExportReceipt>[] _first =
    [
        new(TaskCreationOptions.RunContinuationsAsynchronously),
        new(TaskCreationOptions.RunContinuationsAsynchronously),
        new(TaskCreationOptions.RunContinuationsAsynchronously),
    ];
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private long? _lastDiagnostic;
    private int _diagnosticInProgress;

    internal void ObserveTraceBacklog()
    {
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "otel.sdk.experimental" && instrument.Name == "otel.sdk.processor.span.processed")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "error.type", Value: "queue_full" })
                {
                    ReportDrop(TelemetrySignal.Traces);
                    break;
                }
            }
        });
        _listener.Start();
    }

    internal void Report(TelemetrySignal signal, ExportReceipt receipt)
    {
        _first[(int)signal].TrySetResult(receipt);
        if (receipt != ExportReceipt.Accepted)
        {
            Diagnose($"OTLP {signal} export: {receipt}.");
        }
    }

    internal void ReportDrop(TelemetrySignal signal) => Diagnose($"OTLP {signal} queue is full. Telemetry was dropped.");

    internal void ReportConsoleDrop() => Diagnose("Console log queue is full. Telemetry was dropped.");

    internal void ReportShutdownFailure() => Diagnose("OTLP shutdown did not complete successfully.");

    internal ExportReceipt? First(TelemetrySignal signal)
    {
        var task = _first[(int)signal].Task;
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    public void Dispose() => _listener.Dispose();

    private void Diagnose(string message)
    {
        lock (_gate)
        {
            long now = clock.GetTimestamp();
            if (_diagnosticInProgress != 0 ||
                (_lastDiagnostic is long previous && clock.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(5)))
            {
                return;
            }
            _diagnosticInProgress = 1;
            _lastDiagnostic = now;
        }

        // Obvious rate limit and one outstanding write even if stderr is slow.
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                diagnostics.WriteLine(message);
            }
            catch (IOException)
            {
                // Diagnostics must not affect the telemetry worker.
            }
            catch (ObjectDisposedException)
            {
                // The host may have already closed its diagnostics writer.
            }
            finally
            {
                lock (_gate)
                {
                    _diagnosticInProgress = 0;
                }
            }
        }, state: null);
    }
}
