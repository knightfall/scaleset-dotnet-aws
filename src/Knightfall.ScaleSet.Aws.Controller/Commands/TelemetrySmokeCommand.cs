using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static partial class TelemetrySmokeCommand
{
    private static readonly ActivitySource Source = new(TelemetryNames.Controller);
    private static readonly Meter Meter = new(TelemetryNames.Controller);
    private static readonly Counter<long> AcceptedSamples = Meter.CreateCounter<long>("scaleset.aws.telemetry.smoke", "{sample}");

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        string? endpoint = null;
        if (args is ["telemetry-smoke", "--otlp-endpoint", var supplied])
        {
            endpoint = supplied;
        }
        else if (args is not ["telemetry-smoke"])
        {
            await error.WriteLineAsync("Usage: telemetry-smoke [--otlp-endpoint URL]").ConfigureAwait(false);
            return 2;
        }

        TelemetrySettings settings;
        try
        {
            settings = TelemetrySettings.Load(endpoint);
        }
        catch (TelemetrySettingsException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }
        if (settings.IsDisabled || settings.Traces is null || settings.Metrics is null || settings.Logs is null)
        {
            await error.WriteLineAsync("Telemetry smoke requires enabled endpoints for traces, metrics, and logs.").ConfigureAwait(false);
            return 2;
        }
        return await VerifyAsync(settings, output, error).ConfigureAwait(false);
    }

    internal static async Task<int> VerifyAsync(TelemetrySettings settings, TextWriter output, TextWriter error)
    {
        await using var pipeline = TelemetryPipeline.StartSmoke(settings, error);
        using (var span = Source.StartActivity("scaleset.aws.telemetry.smoke"))
        {
            span?.SetTag("operation", "smoke");
            span?.SetTag("outcome", "success");
            WriteSmokeEvent(pipeline.Logging.CreateLogger(TelemetryNames.Controller), "smoke", "success");
            AcceptedSamples.Add(1);
        }

        bool stopped = await pipeline.StopAsync().ConfigureAwait(false);
        var trace = pipeline.FirstReceipt(TelemetrySignal.Traces);
        var metric = pipeline.FirstReceipt(TelemetrySignal.Metrics);
        var log = pipeline.FirstReceipt(TelemetrySignal.Logs);
        if (!stopped || !Accepted(trace) || !Accepted(metric) || !Accepted(log))
        {
            await error.WriteLineAsync($"OTLP smoke failed. Traces: {Label(trace)}; metrics: {Label(metric)}; logs: {Label(log)}.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync("OTLP receiver accepted the smoke trace, metric, and log.").ConfigureAwait(false);
        return 0;
    }

    private static bool Accepted(ExportReceipt? receipt) => receipt is ExportReceipt.Accepted or ExportReceipt.AcceptedWithWarning;

    private static string Label(ExportReceipt? receipt) => receipt?.ToString() ?? "NoReceipt";

    [LoggerMessage(1, LogLevel.Information, TelemetryNames.EventTemplate)]
    private static partial void WriteSmokeEvent(ILogger logger, string operation, string outcome);
}
