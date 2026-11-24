namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal enum TelemetryProtocol
{
    Grpc = 0,
    HttpProtobuf = 1,
}

internal enum TelemetrySignal
{
    Traces = 0,
    Metrics = 1,
    Logs = 2,
}

internal static class TelemetrySignalExtensions
{
    internal static string HttpPath(this TelemetrySignal signal) => signal switch
    {
        TelemetrySignal.Traces => "v1/traces",
        TelemetrySignal.Metrics => "v1/metrics",
        TelemetrySignal.Logs => "v1/logs",
        _ => throw new ArgumentOutOfRangeException(nameof(signal)),
    };

    internal static string GrpcMethod(this TelemetrySignal signal) => signal switch
    {
        TelemetrySignal.Traces => "opentelemetry.proto.collector.trace.v1.TraceService/Export",
        TelemetrySignal.Metrics => "opentelemetry.proto.collector.metrics.v1.MetricsService/Export",
        TelemetrySignal.Logs => "opentelemetry.proto.collector.logs.v1.LogsService/Export",
        _ => throw new ArgumentOutOfRangeException(nameof(signal)),
    };
}

internal sealed class TelemetryEndpoint(TelemetrySignal signal, TelemetryProtocol protocol, Uri uri,
    TimeSpan timeout, IReadOnlyDictionary<string, string> headers)
{
    internal TelemetrySignal Signal { get; } = signal;
    internal TelemetryProtocol Protocol { get; } = protocol;
    internal Uri Uri { get; } = uri;
    internal TimeSpan Timeout { get; } = timeout;
    internal IReadOnlyDictionary<string, string> Headers { get; } = headers;
}
