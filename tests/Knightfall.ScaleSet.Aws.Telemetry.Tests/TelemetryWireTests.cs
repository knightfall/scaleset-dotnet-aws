using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

[CollectionDefinition(nameof(TelemetryWireTests), DisableParallelization = true)]
public sealed class TelemetryProcessSuite;

[Collection(nameof(TelemetryWireTests))]
public sealed partial class TelemetryWireTests
{
    private const string Controller = "Knightfall.ScaleSet.Aws.Controller";
    private const string Secret = "fixture_secret_not_telemetry";
    private static readonly string[] ExpectedMetricTags = ["operation", "outcome", "profile", "provider"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmokeExportsThreeCorrelatedSignalsWithExactlyOneSharedResource(bool grpc)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc, respond: null, cancellationToken);
        var settings = Settings(receiver.Url + "/otlp-prefix/", grpc);
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);

        Assert.Equal(0, await TelemetrySmokeCommand.VerifyAsync(settings, output, errors).WaitAsync(cancellationToken));
        Assert.Contains("receiver accepted", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, errors.ToString());

        var requests = receiver.Requests;
        Assert.Equal(3, requests.Count);
        foreach (var request in requests)
        {
            Assert.Equal(grpc ? "HTTP/2" : "HTTP/1.1", request.Protocol);
            Assert.Equal(grpc ? "application/grpc" : "application/x-protobuf", request.MediaType);
            Assert.Equal("Bearer test-fixture", request.Authorization);
            Assert.StartsWith("/otlp-prefix/", request.Path, StringComparison.Ordinal);
        }

        var trace = Signal(requests, grpc, "traces", "trace", "Trace");
        var metric = Signal(requests, grpc, "metrics", "metrics", "Metrics");
        var log = Signal(requests, grpc, "logs", "logs", "Logs");
        Assert.Equal(settings.ResourceAttributes.Count, trace.Message(1).StringAttributes(1).Count);
        Assert.Equal(settings.ResourceAttributes.OrderBy(static item => item.Key, StringComparer.Ordinal),
            trace.Message(1).StringAttributes(1).Select(static item => new KeyValuePair<string, object>(item.Key, item.Value))
                .OrderBy(static item => item.Key, StringComparer.Ordinal));
        Assert.Equal(Attributes(trace), Attributes(metric));
        Assert.Equal(Attributes(trace), Attributes(log));

        var span = trace.Message(2).Message(2);
        Assert.Equal("scaleset.aws.telemetry.smoke", span.Text(5));
        Assert.Equal("smoke", span.StringAttributes(9)["operation"]);
        Assert.Equal(Controller, trace.Message(2).Message(1).Text(1));

        var sample = metric.Message(2).Message(2);
        Assert.Equal("scaleset.aws.telemetry.smoke", sample.Text(1));
        Assert.Equal("{sample}", sample.Text(3));
        Assert.Equal(1UL, sample.Message(7).Message(1).Number(6));
        Assert.Equal(Controller, metric.Message(2).Message(1).Text(1));
        var record = log.Message(2).Message(2);
        Assert.Equal(span.Bytes(1), record.Bytes(9));
        Assert.Equal(span.Bytes(2), record.Bytes(10));
        Assert.Contains("smoke", record.Message(5).Text(1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "traces")]
    [InlineData(false, "metrics")]
    [InlineData(false, "logs")]
    [InlineData(true, "traces")]
    [InlineData(true, "metrics")]
    [InlineData(true, "logs")]
    public async Task PartialResponseFailsSmokeForThatSignalWithoutResending(bool grpc, string signal)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc,
            request => IsSignal(request, signal) ? ReceiverReply.Partial : ReceiverReply.Accepted, cancellationToken);
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);

        Assert.Equal(1, await TelemetrySmokeCommand.VerifyAsync(Settings(receiver.Url, grpc), output, errors).WaitAsync(cancellationToken));
        Assert.Contains(signal + ": PartiallyRejected", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(3, receiver.Requests.Count);
        Assert.Single(receiver.Requests, request => IsSignal(request, signal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiverAuthenticationFailureIsReportedWithoutReturningItsBody(bool grpc)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc,
            request => IsSignal(request, "logs") ? ReceiverReply.Rejected : ReceiverReply.Accepted, cancellationToken);
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);

        Assert.Equal(1, await TelemetrySmokeCommand.VerifyAsync(Settings(receiver.Url, grpc), output, errors).WaitAsync(cancellationToken));
        Assert.Contains("logs: Rejected", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-collector-rejection", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-fixture", errors.ToString(), StringComparison.Ordinal);
        Assert.Single(receiver.Requests, request => IsSignal(request, "logs"));
    }

    [Fact]
    public async Task ReceiverWithHeadersAndStalledBodiesSharesOneShutdownBudget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, _ => ReceiverReply.StalledBody, cancellationToken);
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var stopwatch = Stopwatch.StartNew();

        Assert.Equal(1, await TelemetrySmokeCommand.VerifyAsync(Settings(receiver.Url, grpc: false, timeout: "5000"), output, errors)
            .WaitAsync(TimeSpan.FromSeconds(7), cancellationToken));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5.25));
        Assert.Equal(3, receiver.Requests.Count);
        Assert.Contains("Timeout", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task ConsoleOnlyDisabledAndLogsOnlyUseOnlyExplicitSignals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, respond: null, cancellationToken);
        foreach (bool disabled in new[] { false, true })
        {
            var settings = TelemetrySettings.Load(readEnvironment: key => key switch
            {
                "OTEL_SDK_DISABLED" when disabled => "true",
                "OTEL_EXPORTER_OTLP_ENDPOINT" when disabled => receiver.Url,
                _ => null,
            });
            await using var silent = TelemetryPipeline.Start(settings);
            WriteEvent(silent.Logging.CreateLogger(Controller), "smoke", "success");
            Assert.True(await silent.StopAsync().WaitAsync(cancellationToken));
        }
        Assert.Empty(receiver.Requests);

        var selected = TelemetrySettings.Load(readEnvironment: key => key switch
        {
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" => receiver.Url + "/my-logs",
            "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL" => "http/protobuf",
            _ => null,
        });
        await using var logs = TelemetryPipeline.Start(selected);
        WriteEvent(logs.Logging.CreateLogger(Controller), "smoke", "success");
        Assert.True(await logs.StopAsync().WaitAsync(cancellationToken));
        Assert.Equal("/my-logs", Assert.Single(receiver.Requests).Path);
    }

    [Fact]
    public async Task UntrustedInputIsScrubbedAndFourConfiguredProfilesKeepTheirMetricSeries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, respond: null, cancellationToken);
        var config = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "mixed.json"), cancellationToken));
        await using var pipeline = TelemetryPipeline.Start(Settings(receiver.Url, grpc: false, runtime: config));
        using var activitySource = new ActivitySource(Controller);
        using var meter = new Meter("Knightfall.ScaleSet.Aws.Core");
        var logger = pipeline.Logging.CreateLogger(Controller + "." + Secret);
        var operations = meter.CreateCounter<long>("scaleset.aws.provider.operations", "{operation}");
        var unknown = meter.CreateCounter<long>("scaleset.aws." + Secret, "{operation}");
        var profiles = config.Profiles.Select(profile => Assert.IsType<TelemetryProfile>(TelemetryProfile.From(profile))).ToArray();

        foreach (var profile in profiles)
        {
            using (var span = Assert.IsType<Activity>(activitySource.StartActivity("scaleset.aws.provider.operation")))
            {
                profile.SetTags(span);
                span.SetTag("operation", Secret).SetTag("job_id", Secret)
                    .SetTag("url.full", "https://example.invalid/" + Secret).SetStatus(ActivityStatusCode.Error, Secret);
                using var scope = logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["profile"] = profile.Id,
                    ["provider"] = profile.Provider,
                    ["reason"] = Secret,
                    ["nested"] = new { Authorization = Secret },
                });
                WriteEvent(logger, Secret, "success", new InvalidOperationException(Secret));
                WriteEvent(logger, "launch", "success");
            }
            for (int observation = 0; observation < 3_000; observation++)
            {
                var tags = profile.Tags();
                tags.Add("operation", "launch");
                tags.Add("outcome", "success");
                tags.Add("job_id", Secret + observation.ToString(CultureInfo.InvariantCulture));
                tags.Add("task_arn", Guid.NewGuid().ToString());
                operations.Add(1, tags);
            }
        }
        unknown.Add(1);
        Assert.True(await pipeline.StopAsync().WaitAsync(cancellationToken));

        var requests = receiver.Requests;
        Assert.All(requests, request => Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(request.Payload()), StringComparison.Ordinal));
        var metric = Signal(requests, grpc: false, "metrics", "metrics", "Metrics");
        var counter = Assert.Single(metric.Messages(2).SelectMany(static scope => scope.Messages(2)),
            static instrument => instrument.Text(1) == "scaleset.aws.provider.operations");
        var points = counter.Message(7).Messages(1).ToArray();
        Assert.Equal(4, points.Length);
        Assert.All(points, point =>
        {
            Assert.Equal(3_000UL, point.Number(6));
            Assert.Equal(ExpectedMetricTags, point.AttributeNames(7).Order(StringComparer.Ordinal));
        });
        Assert.Equal(profiles.Select(static profile => profile.Id).Order(StringComparer.Ordinal),
            points.Select(static point => point.StringAttributes(7)["profile"]).Order(StringComparer.Ordinal));
        Assert.Equal(config.EnvironmentName, metric.Message(1).StringAttributes(1)["scaleset.deployment.name"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullLogQueueDropsWithoutBlockingAndRecoversAfterTheReceiverResumes(bool grpc)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        int sent = 0;
        await using var receiver = await OtlpLoopback.StartAsync(grpc,
            _ => Interlocked.Increment(ref sent) == 1 ? ReceiverReply.StalledBody : ReceiverReply.Accepted, cancellationToken);
        var settings = TelemetrySettings.Load(readEnvironment: key => key switch
        {
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" => receiver.Url,
            "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL" => grpc ? "grpc" : "http/protobuf",
            "OTEL_EXPORTER_OTLP_LOGS_TIMEOUT" => "2000",
            _ => null,
        });
        await using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        await using var pipeline = TelemetryPipeline.Start(settings, TextWriter.Synchronized(diagnostics), new DiscardConsole());
        var logger = pipeline.Logging.CreateLogger(Controller);

        WriteEvent(logger, "smoke", "success");
        await receiver.FirstRequest.WaitAsync(cancellationToken);
        await Task.Run(() =>
        {
            for (int index = 0; index < SafeTelemetryLogSink.PendingLogs + 100; index++)
            {
                WriteEvent(logger, "smoke", "success");
            }
        }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(1.5), cancellationToken);

        Assert.True(await pipeline.StopAsync().WaitAsync(TimeSpan.FromSeconds(6), cancellationToken));
        Assert.Equal(ExportReceipt.Timeout, pipeline.FirstReceipt(TelemetrySignal.Logs));
        Assert.Contains("queue is full", diagnostics.ToString(), StringComparison.Ordinal);
        Assert.True(receiver.Requests.Count > 1);
        Assert.Equal(SafeTelemetryLogSink.PendingLogs, receiver.Requests.Sum(request =>
            new OtlpMessage(request.Payload()).Messages(1).Sum(resource => resource.Messages(2).Sum(scope => scope.Messages(2).Count()))));
    }

    [Fact]
    public async Task OfflineCliRoutesTelemetrySmokeWithNoCloudEnvironment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, respond: null, cancellationToken);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment.Clear();
        start.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        start.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1/wrong";
        // The test host shares ASP.NET Core assemblies. Exercise the standalone
        // controller with its real runtimeconfig, dependencies, and AppHost output.
        var target = new DirectoryInfo(AppContext.BaseDirectory);
        string controllerAssembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src",
            Controller, "bin", target.Parent!.Name, target.Name, Controller + ".dll"));
        start.ArgumentList.Add(controllerAssembly);
        start.ArgumentList.Add("telemetry-smoke");
        start.ArgumentList.Add("--otlp-endpoint");
        start.ArgumentList.Add(receiver.Url);
        using var process = Assert.IsType<Process>(Process.Start(start));
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        Assert.Equal(string.Empty, await errors);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("receiver accepted", await output, StringComparison.Ordinal);
        Assert.Equal(3, receiver.Requests.Count);
    }

    [Fact]
    public async Task FailedSinkTeardownReturnsFalseAndKeepsExceptionTextOutOfDiagnostics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var diagnostics = new SignalledDiagnostics();
        var settings = TelemetrySettings.Load(readEnvironment: _ => null);
        await using var pipeline = TelemetryPipeline.Start(settings, diagnostics, new ThrowingConsole());

        var stopping = pipeline.StopAsync();
        Assert.False(await stopping.WaitAsync(cancellationToken));
        Assert.Same(stopping, pipeline.StopAsync());
        await diagnostics.Written.Task.WaitAsync(cancellationToken);
        Assert.Equal("OTLP shutdown did not complete successfully." + Environment.NewLine, diagnostics.ToString());
    }

    private sealed class SignalledDiagnostics : StringWriter
    {
        internal SignalledDiagnostics() : base(CultureInfo.InvariantCulture)
        {
        }

        internal TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            Written.TrySetResult();
        }
    }

    private sealed class ThrowingConsole : ILogEventSink, IDisposable
    {
        public void Emit(LogEvent logEvent)
        {
        }

        public void Dispose() => throw new IOException(Secret);
    }

    private sealed class DiscardConsole : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
        }
    }

    private static OtlpMessage Signal(IReadOnlyList<OtlpCapture> requests, bool grpc, string signal, string package, string service)
    {
        string path = grpc ? $"/opentelemetry.proto.collector.{package}.v1.{service}Service/Export" : "/v1/" + signal;
        return new OtlpMessage(Assert.Single(requests, request => request.Path.EndsWith(path, StringComparison.Ordinal)).Payload()).Message(1);
    }

    private static bool IsSignal(OtlpCapture capture, string signal) => signal switch
    {
        "traces" => capture.Path.Contains(".trace.", StringComparison.Ordinal) || capture.Path.EndsWith("/traces", StringComparison.Ordinal),
        _ => capture.Path.Contains("." + signal + ".", StringComparison.Ordinal) || capture.Path.EndsWith("/" + signal, StringComparison.Ordinal),
    };

    private static KeyValuePair<string, string>[] Attributes(OtlpMessage signal) =>
        [.. signal.Message(1).StringAttributes(1).OrderBy(static item => item.Key, StringComparer.Ordinal)];

    private static TelemetrySettings Settings(string endpoint, bool grpc, string timeout = "3000", RuntimeConfiguration? runtime = null) =>
        TelemetrySettings.Load(runtime: runtime, readEnvironment: key => key switch
        {
            "OTEL_EXPORTER_OTLP_ENDPOINT" => endpoint,
            "OTEL_EXPORTER_OTLP_PROTOCOL" => grpc ? "grpc" : "http/protobuf",
            "OTEL_EXPORTER_OTLP_TIMEOUT" => timeout,
            "OTEL_EXPORTER_OTLP_HEADERS" => "authorization=Bearer%20test-fixture",
            "OTEL_SERVICE_NAME" => "controller-fixture",
            "OTEL_RESOURCE_ATTRIBUTES" => "deployment.environment.name=testing",
            "OTEL_TRACES_SAMPLER" => "parentbased_always_on",
            _ => null,
        });

    [LoggerMessage(1, LogLevel.Warning, "Telemetry operation {operation} finished with {outcome}")]
    private static partial void WriteEvent(ILogger logger, string operation, string outcome, Exception? exception = null);
}
