using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

public sealed class TelemetrySettingsTests
{
    [Fact]
    public void ResolvesEachSettingOnceAndKeepsExactPerSignalOverrides()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://collector.example/prefix/",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=Bearer%20fixture,x-instance=example",
            ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "2000",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "https://logs.example/custom/log",
            ["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = "x-tenant=one%2Ctwo",
            ["OTEL_EXPORTER_OTLP_METRICS_PROTOCOL"] = "grpc",
            ["OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"] = "4000",
            ["OTEL_SERVICE_NAME"] = "controller-fixture",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "deployment.environment.name=testing",
            ["OTEL_TRACES_SAMPLER_ARG"] = "0.25",
            ["OTEL_METRIC_EXPORT_INTERVAL"] = "20000",
        };
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var settings = TelemetrySettings.Load(readEnvironment: key =>
        {
            Assert.True(reads.Add(key), key);
            return values.GetValueOrDefault(key);
        });

        var traces = Assert.IsType<TelemetryEndpoint>(settings.Traces);
        Assert.Equal("https://collector.example/prefix/v1/traces", traces.Uri.AbsoluteUri);
        Assert.Equal(TelemetryProtocol.HttpProtobuf, traces.Protocol);
        Assert.Equal("Bearer fixture", traces.Headers["authorization"]);
        Assert.Equal(TimeSpan.FromSeconds(2), traces.Timeout);
        var logs = Assert.IsType<TelemetryEndpoint>(settings.Logs);
        Assert.Equal("https://logs.example/custom/log", logs.Uri.AbsoluteUri);
        Assert.Equal(new KeyValuePair<string, string>("x-tenant", "one,two"), Assert.Single(logs.Headers));
        var metrics = Assert.IsType<TelemetryEndpoint>(settings.Metrics);
        Assert.Equal(TelemetryProtocol.Grpc, metrics.Protocol);
        Assert.Equal("https://collector.example/prefix/opentelemetry.proto.collector.metrics.v1.MetricsService/Export", metrics.Uri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(4), metrics.Timeout);
        Assert.Equal(0.25, settings.TraceRatio);
        Assert.Equal(20_000, settings.MetricIntervalMilliseconds);
        Assert.Equal("controller-fixture", settings.ResourceAttributes["service.name"]);
        Assert.Equal("testing", settings.ResourceAttributes["deployment.environment.name"]);
        Assert.Equal(5, settings.ResourceAttributes.Count);
        Assert.Equal(settings.ResourceAttributes["service.instance.id"],
            TelemetrySettings.Load(readEnvironment: _ => null).ResourceAttributes["service.instance.id"]);
    }

    [Fact]
    public void UsesCliEndpointBeforeCommonAndRejectsPerSignalConflict()
    {
        var selected = TelemetrySettings.Load("http://localhost:8443/chosen", readEnvironment: key => key switch
        {
            "OTEL_EXPORTER_OTLP_ENDPOINT" => "https://unused.example/base",
            "OTEL_EXPORTER_OTLP_PROTOCOL" => "http/protobuf",
            _ => null,
        });
        Assert.Equal("http://localhost:8443/chosen/v1/logs", selected.Logs?.Uri.AbsoluteUri);

        var error = Assert.Throws<TelemetrySettingsException>(() => TelemetrySettings.Load("http://localhost:8443/chosen",
            readEnvironment: key => key == "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" ? "https://unused.example/token" : null));
        Assert.Equal("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", error.Setting);
        Assert.DoesNotContain("unused.example", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEndpointOrDisabledNeverCreatesRemoteSignalSettings()
    {
        var empty = TelemetrySettings.Load(readEnvironment: _ => null);
        Assert.False(empty.IsDisabled);
        Assert.Null(empty.Logs);
        Assert.Null(empty.Traces);
        Assert.Null(empty.Metrics);
        var disabled = TelemetrySettings.Load(readEnvironment: key => key switch
        {
            "OTEL_SDK_DISABLED" => "true",
            "OTEL_EXPORTER_OTLP_ENDPOINT" => "http://localhost:4317",
            _ => null,
        });
        Assert.True(disabled.IsDisabled);
        Assert.Null(disabled.Logs);
        Assert.Null(disabled.Traces);
        Assert.Null(disabled.Metrics);

        var partial = TelemetrySettings.Load(readEnvironment: key =>
            key == "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" ? "http://localhost:4317" : null);
        Assert.NotNull(partial.Logs);
        Assert.Null(partial.Traces);
        Assert.Null(partial.Metrics);
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://login:private-fixture@collector.example/")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://collector.example/?secret=private-fixture")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://collector.example/#private-fixture")]
    [InlineData("OTEL_EXPORTER_OTLP_PROTOCOL", "private-fixture")]
    [InlineData("OTEL_EXPORTER_OTLP_LOGS_TIMEOUT", "5001")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "authorization=private-fixture%0d%0a")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "authorization=private-fixture,Authorization=other")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "authorization=private-fixture%")]
    [InlineData("OTEL_RESOURCE_ATTRIBUTES", "service.name=private-fixture")]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "NaN")]
    [InlineData("OTEL_METRIC_EXPORT_INTERVAL", "500")]
    public void InvalidInputsReportOnlyTheSettingName(string name, string value)
    {
        var error = Assert.Throws<TelemetrySettingsException>(() =>
            TelemetrySettings.Load(readEnvironment: key => key == name ? value : null));
        Assert.Equal(name, error.Setting);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }
}
