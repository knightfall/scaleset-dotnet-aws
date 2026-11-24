using System.Diagnostics;
using System.Globalization;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.GitHub;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

[Collection(nameof(TelemetryWireTests))]
public sealed class ControllerJitLoggingTests
{
    [Fact]
    public async Task HandoffLogsRetainOnlyScopedOutcomesAndCorrelateWithTheCurrentTrace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var configuration = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "mixed.json"), cancellationToken));
        var profile = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configuration.Profiles[0]));
        var settings = TelemetrySettings.Load(runtime: configuration, readEnvironment: static _ => null);
        var output = new CaptureLogs();
        await using var pipeline = TelemetryPipeline.Start(settings, TextWriter.Null, output);
        var logger = pipeline.Logging.CreateLogger<ControllerJitCoordinator>();
        using var trace = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start();

        Observe(GitHubJitStatus.Offered);
        using (TelemetryProfileScope.Enter(profile))
        using (logger.BeginScope(new KeyValuePair<string, object?>[] { new("runner_id", "synthetic-private-runner-id") }))
        {
            Observe(GitHubJitStatus.Offered);
            Observe(GitHubJitStatus.RegisteredWithoutOffer);
            Observe(GitHubJitStatus.Held);
            Observe(GitHubJitStatus.RecoveryRequired);
            Observe(GitHubJitStatus.WaitingForHello);
            Observe(GitHubJitStatus.OwnershipUnavailable);
            Observe(GitHubJitStatus.Finished);
        }

        Assert.True(await pipeline.StopAsync().WaitAsync(cancellationToken));
        Assert.Collection(output.Events,
            log => AssertLog(log, LogEventLevel.Information, "success", "offer_confirmed"),
            log => AssertLog(log, LogEventLevel.Warning, "deferred", "registration_without_offer"),
            log => AssertLog(log, LogEventLevel.Information, "deferred", "handoff_held"),
            log => AssertLog(log, LogEventLevel.Warning, "unknown", "recovery_required"));

        void Observe(GitHubJitStatus status)
        {
            using var operation = new ControllerJitDiagnostics.Operation(startupAuthorization: false, cancellationToken);
            operation.CompleteAdvance(status, logger);
        }

        void AssertLog(LogEvent log, LogEventLevel level, string outcome, string reason)
        {
            Assert.Equal(level, log.Level);
            Assert.Equal(trace.TraceId, log.TraceId);
            Assert.Equal(trace.SpanId, log.SpanId);
            Assert.Null(log.Exception);
            Assert.Equal(6, log.Properties.Count);
            Assert.Equal(TelemetryNames.Controller, TelemetryPrivacy.Scalar(log, "SourceContext"));
            Assert.Equal("advance", TelemetryPrivacy.Scalar(log, "operation"));
            Assert.Equal(outcome, TelemetryPrivacy.Scalar(log, "outcome"));
            Assert.Equal(reason, TelemetryPrivacy.Scalar(log, "reason"));
            Assert.Equal(profile.Id, TelemetryPrivacy.Scalar(log, "profile"));
            Assert.Equal(profile.Provider, TelemetryPrivacy.Scalar(log, "provider"));
        }
    }

    [Fact]
    public async Task BlockedConsoleAndDiagnosticsDoNotHoldTheJitResultOrOtlpIntake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var configuration = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "mixed.json"), cancellationToken));
        var profile = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configuration.Profiles[0]));
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, _ => ReceiverReply.StalledBody, cancellationToken);
        var settings = TelemetrySettings.Load(runtime: configuration, readEnvironment: name => name switch
        {
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" => receiver.Url + "/v1/logs",
            "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL" => "http/protobuf",
            "OTEL_EXPORTER_OTLP_LOGS_TIMEOUT" => "5000",
            _ => null,
        });
        await using var diagnostics = new BlockedDiagnostics();
        var console = new BlockedConsole();
        await using var pipeline = TelemetryPipeline.Start(settings, diagnostics, console);
        var logger = pipeline.Logging.CreateLogger<ControllerJitCoordinator>();
        var result = new GitHubJitResult(GitHubJitStatus.Offered, 42);

        try
        {
            var advancing = Task.Run(() => CompleteAdvance(result), cancellationToken);
            await console.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(result, await advancing.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken));
            await receiver.StalledBodyStarted.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            var first = Assert.Single(receiver.Requests);
            Assert.Equal("/v1/logs", first.Path);
            var log = new OtlpMessage(first.Payload()).Message(1).Message(2).Message(2);
            var attributes = log.StringAttributes(6);
            Assert.Equal("offer_confirmed", attributes["reason"]);
            Assert.Equal(profile.Id, attributes["profile"]);

            // Saturate the local queue while its writer is stuck. The resulting
            // rate-limited diagnostic also blocks in its own writer.
            await Task.Run(() =>
            {
                for (int index = 0; index <= ConsoleTelemetryQueue.Capacity; index++)
                {
                    CompleteAdvance(result);
                }
            }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            await diagnostics.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(result, await Task.Run(() => CompleteAdvance(result), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken));
            Assert.False(receiver.StalledBodyEnded.IsCompleted);

            var elapsed = Stopwatch.StartNew();
            Assert.False(await pipeline.StopAsync().WaitAsync(TimeSpan.FromSeconds(6), cancellationToken));
            Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5.25));
            await receiver.StalledBodyEnded.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            Assert.False(console.Disposed.Task.IsCompleted);
        }
        finally
        {
            console.Release();
            diagnostics.Release();
        }

        await console.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        Assert.Equal(1, console.Emitted);

        GitHubJitResult CompleteAdvance(GitHubJitResult completed)
        {
            using var scope = TelemetryProfileScope.Enter(profile);
            using var operation = new ControllerJitDiagnostics.Operation(startupAuthorization: false, cancellationToken);
            operation.CompleteAdvance(completed.Status, logger);
            return completed;
        }
    }

    private sealed class BlockedConsole : ILogEventSink, IDisposable
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _emitted;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Emitted => Volatile.Read(ref _emitted);

        public void Emit(LogEvent logEvent)
        {
            Interlocked.Increment(ref _emitted);
            Entered.TrySetResult();
            _released.Task.GetAwaiter().GetResult();
        }

        internal void Release() => _released.TrySetResult();

        public void Dispose() => Disposed.TrySetResult();
    }

    private sealed class BlockedDiagnostics() : StringWriter(CultureInfo.InvariantCulture)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            Entered.TrySetResult();
            _released.Task.GetAwaiter().GetResult();
        }

        internal void Release() => _released.TrySetResult();
    }

    private sealed class CaptureLogs : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
