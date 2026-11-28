using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

public sealed partial class TelemetryWireTests
{
    [Fact]
    public async Task ConsoleEmitsSanitizedJsonWithStructuredPropertiesAndCorrelation()
    {
        var token = TestContext.Current.CancellationToken;
        var configuration = RuntimeConfigurationLoader.Load(await MixedRuntimeAsync(token));
        var profile = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configuration.Profiles[0]));
        var settings = TelemetrySettings.Load(runtime: configuration, readEnvironment: static _ => null);
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        const string reference = "dc0fed00920ba342";
        string traceId;
        string spanId;
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            await using var pipeline = TelemetryPipeline.Start(settings, TextWriter.Null);
            var observer = pipeline.Logging.CreateLogger<CapturedEcsControllerTaskFenceObserver>();
            using (var trace = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start())
            using (observer.BeginScope(new KeyValuePair<string, object?>[]
            {
                new("profile", profile.Id), new("provider", profile.Provider), new("reason", "predecessor_capture_verified"),
                new("evidence_reference", reference), new(Secret, new[] { Secret }),
            }))
            {
                traceId = trace.TraceId.ToHexString();
                spanId = trace.SpanId.ToHexString();
                WriteEvent(observer, "reconcile", "success", new IOException(Secret));
            }
            var other = pipeline.Logging.CreateLogger(Controller + "." + Secret);
            WriteUnknownEvent(other, Secret, new IOException(Secret));
            Assert.True(await pipeline.StopAsync().WaitAsync(token));
        }
        finally
        {
            Console.SetOut(original);
        }

        string rendered = output.ToString();
        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        var lines = rendered.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var known = JsonDocument.Parse(lines[0]);
        var log = known.RootElement;
        Assert.Equal(TimeSpan.Zero, log.GetProperty("Timestamp").GetDateTimeOffset().Offset);
        Assert.Equal("Warning", log.GetProperty("Level").GetString());
        Assert.Equal(TelemetryNames.EventTemplate, log.GetProperty("MessageTemplate").GetString());
        Assert.Contains("reconcile", log.GetProperty("RenderedMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal(traceId, log.GetProperty("TraceId").GetString());
        Assert.Equal(spanId, log.GetProperty("SpanId").GetString());
        Assert.False(log.TryGetProperty("Exception", out _));
        var properties = log.GetProperty("Properties");
        Assert.Equal(7, properties.EnumerateObject().Count());
        Assert.Equal(Controller, properties.GetProperty("SourceContext").GetString());
        Assert.Equal("reconcile", properties.GetProperty("operation").GetString());
        Assert.Equal("success", properties.GetProperty("outcome").GetString());
        Assert.Equal("predecessor_capture_verified", properties.GetProperty("reason").GetString());
        Assert.Equal(profile.Id, properties.GetProperty("profile").GetString());
        Assert.Equal(profile.Provider, properties.GetProperty("provider").GetString());
        Assert.Equal(reference, properties.GetProperty("evidence_reference").GetString());

        using var unknown = JsonDocument.Parse(lines[1]);
        Assert.Equal("Controller diagnostic event", unknown.RootElement.GetProperty("RenderedMessage").GetString());
        Assert.False(unknown.RootElement.TryGetProperty("TraceId", out _));
        Assert.False(unknown.RootElement.TryGetProperty("SpanId", out _));
        Assert.False(unknown.RootElement.TryGetProperty("Exception", out _));
        Assert.Equal("SourceContext", Assert.Single(unknown.RootElement.GetProperty("Properties").EnumerateObject()).Name);
    }

    private static Task<byte[]> MixedRuntimeAsync(CancellationToken token) =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "mixed.json"), token);

    [LoggerMessage(2, LogLevel.Error, "Unexpected failure {payload}")]
    private static partial void WriteUnknownEvent(ILogger logger, string payload, Exception exception);
}
