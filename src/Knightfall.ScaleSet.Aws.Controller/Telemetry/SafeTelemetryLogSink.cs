using System.Globalization;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal sealed class SafeTelemetryLogSink(TelemetryPrivacy privacy, TelemetryExportState state, ILogEventSink? console) : ILogEventSink, IDisposable
{
    internal const int PendingLogs = 2_048;
    internal const int LogBatchSize = 128;
    private static readonly MessageTemplate OperationTemplate = new MessageTemplateParser().Parse(TelemetryNames.EventTemplate);
    private static readonly MessageTemplate OtherTemplate = new MessageTemplateParser().Parse("Controller diagnostic event");
    private readonly ILogEventSink _console = console ?? LoggerSinkConfiguration.CreateSink(configuration => configuration.Console(formatProvider: CultureInfo.InvariantCulture));
    private ILogEventSink? _remote;
    private int _pending;
    private int _stopped;

    internal void SetRemote(ILogEventSink remote) => _remote = remote;

    internal void Release(int records) => Interlocked.Add(ref _pending, -records);

    internal void Stop() => Volatile.Write(ref _stopped, 1);

    public void Emit(LogEvent logEvent)
    {
        if (Volatile.Read(ref _stopped) != 0 || TelemetryPrivacy.Scalar(logEvent, "SourceContext") is not { } category ||
            !TelemetryPrivacy.IsApprovedSource(category))
        {
            return;
        }
        var safeEvent = Sanitize(logEvent, category);
        _console.Emit(safeEvent);
        if (_remote is null)
        {
            return;
        }
        if (Interlocked.Increment(ref _pending) > PendingLogs)
        {
            Release(1);
            state.ReportDrop(TelemetrySignal.Logs);
            return;
        }
        _remote.Emit(safeEvent);
    }

    public void Dispose()
    {
        Stop();
        (_remote as IDisposable)?.Dispose();
        (_console as IDisposable)?.Dispose();
    }

    private LogEvent Sanitize(LogEvent log, string category)
    {
        var properties = new List<LogEventProperty>(6) { Property("SourceContext", SafeCategory(category)) };
        string? operation = TelemetryPrivacy.Scalar(log, "operation");
        string? outcome = TelemetryPrivacy.Scalar(log, "outcome");
        bool known = log.MessageTemplate.Text == TelemetryNames.EventTemplate && operation is not null && outcome is not null &&
            TelemetryNames.Operations.Contains(operation) && TelemetryNames.Outcomes.Contains(outcome);
        if (known)
        {
            properties.Add(Property("operation", operation!));
            properties.Add(Property("outcome", outcome!));
            if (TelemetryPrivacy.Scalar(log, "reason") is { } reason)
            {
                properties.Add(Property("reason", TelemetryNames.Reasons.Contains(reason) ? reason : "other"));
            }
        }

        string? provider = TelemetryPrivacy.Scalar(log, "provider");
        if (privacy.Profile(TelemetryPrivacy.Scalar(log, "profile"), provider) is { } profile)
        {
            properties.Add(Property("profile", profile));
            properties.Add(Property("provider", provider!));
        }
        return new LogEvent(log.Timestamp, log.Level, null, known ? OperationTemplate : OtherTemplate, properties,
            log.TraceId.GetValueOrDefault(), log.SpanId.GetValueOrDefault());
    }

    private static string SafeCategory(string category)
    {
        foreach (string component in TelemetryNames.Components)
        {
            if (category == component || category.StartsWith(component + ".", StringComparison.Ordinal))
            {
                return component;
            }
        }
        return category.StartsWith(TelemetryNames.Client, StringComparison.Ordinal) ? TelemetryNames.Client : TelemetryNames.Listener;
    }

    private static LogEventProperty Property(string key, string value) => new(key, new ScalarValue(value));
}
