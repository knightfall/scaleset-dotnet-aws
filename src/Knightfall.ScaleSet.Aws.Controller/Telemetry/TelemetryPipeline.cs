using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.OpenTelemetry;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

/// <summary>Owns telemetry resources for one controller process.</summary>
public sealed class TelemetryPipeline : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposalReserve = TimeSpan.FromMilliseconds(150);
    private static readonly Meter TransportMeter = new("Knightfall.ScaleSet.Aws.OtlpTransport");
    private static readonly TransportMeterFactory ExportMeters = new();
    private readonly CancellationTokenSource _transportShutdown = new();
    private readonly TelemetryExportState _exports;
    private readonly SafeTelemetryLogSink _sink;
    private readonly Serilog.Core.Logger _logger;
    private readonly ServiceProvider? _providers;
    private readonly TracerProvider? _traces;
    private readonly MeterProvider? _metrics;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly object _shutdownGate = new();
    private Task<bool>? _stopping;

    private TelemetryPipeline(TelemetrySettings settings, TextWriter diagnostics, bool smoke, ILogEventSink? console = null)
    {
        var privacy = new TelemetryPrivacy(settings.Profiles);
        _exports = new TelemetryExportState(diagnostics, _clock);
        _sink = new SafeTelemetryLogSink(privacy, _exports, console, _transportShutdown.Token);
        _logger = new LoggerConfiguration().MinimumLevel.Information().Enrich.FromLogContext().WriteTo.Sink(_sink).CreateLogger();
        Logging = new SerilogLoggerFactory(_logger, dispose: false);
        try
        {
            if (settings.Logs is { } logs)
            {
                _sink.SetRemote(CreateRemoteLogger(settings, logs));
            }
            if (settings.Traces is not null || settings.Metrics is not null)
            {
                _providers = CreateProviders(settings, privacy, smoke);
                _traces = _providers.GetService<TracerProvider>();
                _metrics = _providers.GetService<MeterProvider>();
                if (_traces is not null)
                {
                    _exports.ObserveTraceBacklog();
                }
            }
        }
        catch
        {
            _transportShutdown.Cancel();
            _providers?.Dispose();
            Logging.Dispose();
            _logger.Dispose();
            _exports.Dispose();
            _transportShutdown.Dispose();
            throw;
        }
    }

    public ILoggerFactory Logging { get; }

    public static TelemetryPipeline Start(TelemetrySettings settings, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TelemetryPipeline(settings, diagnostics ?? Console.Error, smoke: false);
    }

    internal static TelemetryPipeline Start(TelemetrySettings settings, TextWriter diagnostics, ILogEventSink console) =>
        new(settings, diagnostics, smoke: false, console);

    internal static TelemetryPipeline StartSmoke(TelemetrySettings settings, TextWriter diagnostics) =>
        new(settings, diagnostics, smoke: true);

    internal ExportReceipt? FirstReceipt(TelemetrySignal signal) => _exports.First(signal);

    public Task<bool> StopAsync()
    {
        lock (_shutdownGate)
        {
            return _stopping ??= StopCoreAsync();
        }
    }

    public async ValueTask DisposeAsync() => _ = await StopAsync().ConfigureAwait(false);

    private async Task<bool> StopCoreAsync()
    {
        long started = _clock.GetTimestamp();
        _sink.Stop();
        _transportShutdown.CancelAfter(ShutdownBudget - DisposalReserve);
        var cleanup = JoinShutdownAsync(started);
        try
        {
            var remaining = ShutdownBudget - _clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero && await cleanup.WaitAsync(remaining).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (TimeoutException)
        {
            // The transports already have a joint cancellation deadline.
            // Cleanup still observes library faults after this wait expires.
        }
        _exports.ReportShutdownFailure();
        return false;
    }

    private async Task<bool> JoinShutdownAsync(long started)
    {
        var exporters = StopExportersAsync(started);
        var console = _sink.ConsoleCompletion;
        try
        {
            await Task.WhenAll(exporters, console).ConfigureAwait(false);
            return exporters.Result && console.Result;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or
            OperationCanceledException or InvalidOperationException or AggregateException)
        {
            return false;
        }
        finally
        {
            _transportShutdown.Dispose();
        }
    }

    private async Task<bool> StopExportersAsync(long started)
    {
        // The SDK teardown APIs are synchronous. Start them together so each
        // uses the same remaining budget even when console output is blocked.
        var traces = Task.Run(() => _traces?.Shutdown(RemainingMilliseconds(started)) ?? true);
        var metrics = Task.Run(() => _metrics?.Shutdown(RemainingMilliseconds(started)) ?? true);
        var logs = Task.Run(async () =>
        {
            Logging.Dispose();
            await _logger.DisposeAsync().ConfigureAwait(false);
        });

        try
        {
            await Task.WhenAll(traces, metrics, logs).ConfigureAwait(false);
            return traces.Result && metrics.Result;
        }
        finally
        {
            try
            {
                if (_providers is not null)
                {
                    await _providers.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _exports.Dispose();
            }
        }
    }

    private int RemainingMilliseconds(long started) =>
        Math.Max(0, (int)(ShutdownBudget - _clock.GetElapsedTime(started)).TotalMilliseconds);

    private ServiceProvider CreateProviders(TelemetrySettings settings, TelemetryPrivacy privacy, bool smoke)
    {
        var services = new ServiceCollection();
        // The controller parses one settings snapshot. Give the SDK an empty
        // configuration so its option factories cannot reread ambient options.
        services.AddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
        var builder = services.AddOpenTelemetry().ConfigureResource(resource =>
            resource.Clear().AddAttributes(settings.ResourceAttributes));

        if (settings.Traces is { } traces)
        {
            builder.WithTracing(tracing =>
            {
                tracing.SetSampler(new ParentBasedSampler(smoke ? new AlwaysOnSampler() : new TraceIdRatioBasedSampler(settings.TraceRatio)))
                    .AddSource([.. TelemetryNames.Components, TelemetryNames.Client, TelemetryNames.Listener])
                    .AddProcessor(privacy);
                if (!smoke)
                {
                    tracing.AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = false;
                        options.FilterHttpRequestMessage = request => request.RequestUri is { Scheme: "https" } &&
                            (Activity.Current?.Source.Name == TelemetryNames.Client ||
                             Activity.Current?.Parent?.Source.Name == TelemetryNames.Client);
                    });
                }
                tracing.AddOtlpExporter(options =>
                {
                    ConfigureExporter(options, traces);
                    options.ExportProcessorType = ExportProcessorType.Batch;
                    options.BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
                    {
                        MaxQueueSize = 2_048,
                        MaxExportBatchSize = 128,
                        ScheduledDelayMilliseconds = 250,
                        ExporterTimeoutMilliseconds = (int)traces.Timeout.TotalMilliseconds,
                    };
                });
            });
        }

        if (settings.Metrics is { } metrics)
        {
            builder.WithMetrics(meter =>
            {
                meter.AddMeter([.. TelemetryNames.Components, TelemetryNames.Client])
                    .SetMaxMetricStreams(128)
                    .AddView(TelemetryViews.Configure);
                if (!smoke)
                {
                    meter.AddHttpClientInstrumentation().AddRuntimeInstrumentation();
                }
                meter.AddOtlpExporter((options, reader) =>
                {
                    ConfigureExporter(options, metrics);
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = settings.MetricIntervalMilliseconds;
                    reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = (int)metrics.Timeout.TotalMilliseconds;
                });
            });
        }
        return services.BuildServiceProvider();
    }

    private ILogEventSink CreateRemoteLogger(TelemetrySettings settings, TelemetryEndpoint logs) =>
        LoggerSinkConfiguration.CreateSink(configuration => configuration.OpenTelemetry(options =>
        {
            options.Endpoint = null;
            options.LogsEndpoint = logs.Protocol == TelemetryProtocol.Grpc
                ? logs.Uri.GetLeftPart(UriPartial.Authority) : logs.Uri.AbsoluteUri;
            options.Protocol = logs.Protocol == TelemetryProtocol.Grpc ? OtlpProtocol.Grpc : OtlpProtocol.HttpProtobuf;
            options.ResourceAttributes = new Dictionary<string, object>(settings.ResourceAttributes, StringComparer.Ordinal);
            options.IncludedData = IncludedData.TraceIdField | IncludedData.SpanIdField | IncludedData.SourceContextAttribute;
            options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
            options.HttpMessageHandler = new OtlpResponseHandler(logs, _exports.Report, ExportMeters, _sink.Release, _transportShutdown.Token);
            options.BatchingOptions.QueueLimit = SafeTelemetryLogSink.PendingLogs;
            options.BatchingOptions.BatchSizeLimit = SafeTelemetryLogSink.LogBatchSize;
            options.BatchingOptions.BufferingTimeLimit = TimeSpan.FromMilliseconds(250);
            options.BatchingOptions.RetryTimeLimit = TimeSpan.Zero;
            options.BatchingOptions.EagerlyEmitFirstEvent = false;
        }, ignoreEnvironment: true));

    private void ConfigureExporter(OtlpExporterOptions options, TelemetryEndpoint endpoint)
    {
        options.Endpoint = endpoint.Uri;
        // The pinned enum's warning applies to legacy framework targets.
#pragma warning disable CS0618
        options.Protocol = endpoint.Protocol == TelemetryProtocol.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
#pragma warning restore CS0618
        options.TimeoutMilliseconds = (int)endpoint.Timeout.TotalMilliseconds;
        options.Headers = string.Empty;
        options.MaxResponseSizeBytes = OtlpResponseHandler.MaximumResponseBytes;
        options.HttpClientFactory = () => new HttpClient(new OtlpResponseHandler(endpoint, _exports.Report, ExportMeters, releaseLogRecords: null, _transportShutdown.Token))
        {
            Timeout = endpoint.Timeout,
        };
    }

    private sealed class TransportMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => TransportMeter;

        public void Dispose()
        {
            // The unregistered meter is shared by the process.
        }
    }
}
