using System.Text.Json;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

public sealed class TelemetrySettingsTests
{
    public static TheoryData<string, string?> EndpointPaths()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "otlp-endpoints.json")));
        var cases = new TheoryData<string, string?>();
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            cases.Add(fixture.GetProperty("endpoint").GetString()!, fixture.GetProperty("path").GetString());
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(EndpointPaths))]
    public void RuntimeUsesTheSameLiteralEndpointPathsAsCdk(string endpoint, string? expectedPath)
    {
        const string endpointKey = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
        TelemetrySettings Load() => TelemetrySettings.Load(readEnvironment: key => key switch
        {
            endpointKey => endpoint,
            "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL" => "http/protobuf",
            "OTEL_EXPORTER_OTLP_TRACES_HEADERS" => "authorization=fixture",
            _ => null,
        });
        if (expectedPath is null)
        {
            var error = Assert.Throws<TelemetrySettingsException>(() => Load());
            Assert.Equal(endpointKey, error.Setting);
            Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
            return;
        }

        var traces = Assert.IsType<TelemetryEndpoint>(Load().Traces);
        Assert.Equal(expectedPath, traces.Uri.AbsolutePath);
        Assert.Equal("fixture", traces.Headers["authorization"]);
    }

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
    [InlineData("TRACES", "grpc")]
    [InlineData("METRICS", "grpc")]
    [InlineData("LOGS", "grpc")]
    [InlineData("TRACES", "http/protobuf")]
    [InlineData("METRICS", "http/protobuf")]
    [InlineData("LOGS", "http/protobuf")]
    public void RemoteHttpRejectsInheritedAndDirectHeadersWithoutLoggingValues(string signal, string protocol)
    {
        const string endpoint = "http://collector.example.invalid";
        const string credential = "authorization=private-fixture";
        string endpointKey = $"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT";
        foreach (string headerKey in new[] { "OTEL_EXPORTER_OTLP_HEADERS", $"OTEL_EXPORTER_OTLP_{signal}_HEADERS" })
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://fallback.example.invalid",
                [endpointKey] = endpoint,
                [$"OTEL_EXPORTER_OTLP_{signal}_PROTOCOL"] = protocol,
                [headerKey] = credential,
            };
            var error = Assert.Throws<TelemetrySettingsException>(() => TelemetrySettings.Load(readEnvironment: values.GetValueOrDefault));
            Assert.Equal(endpointKey, error.Setting);
            Assert.DoesNotContain("private-fixture", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("https://collector.example.invalid")]
    [InlineData("http://localhost:4317")]
    [InlineData("http://127.0.0.2:4317")]
    [InlineData("http://[::1]:4317")]
    public void HttpsAndLoopbackPreserveHeaders(string endpoint)
    {
        var settings = TelemetrySettings.Load(endpoint, readEnvironment: key =>
            key == "OTEL_EXPORTER_OTLP_HEADERS" ? "authorization=fixture" : null);
        foreach (var signal in new[] { settings.Traces, settings.Metrics, settings.Logs })
        {
            Assert.Equal("fixture", Assert.IsType<TelemetryEndpoint>(signal).Headers["authorization"]);
        }
    }

    [Fact]
    public void CliOverrideAppliesTheHeaderTransportRuleAndEmptyOverridesClearInheritance()
    {
        const string endpoint = "http://collector.example.invalid";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=fixture",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://fallback.example.invalid",
        };
        var error = Assert.Throws<TelemetrySettingsException>(() => TelemetrySettings.Load(endpoint, readEnvironment: values.GetValueOrDefault));
        Assert.Equal("--otlp-endpoint", error.Setting);

        values["OTEL_EXPORTER_OTLP_TRACES_HEADERS"] = string.Empty;
        values["OTEL_EXPORTER_OTLP_METRICS_HEADERS"] = string.Empty;
        values["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = string.Empty;
        var settings = TelemetrySettings.Load(endpoint, readEnvironment: values.GetValueOrDefault);
        foreach (var signal in new[] { settings.Traces, settings.Metrics, settings.Logs })
        {
            Assert.Empty(Assert.IsType<TelemetryEndpoint>(signal).Headers);
        }
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
