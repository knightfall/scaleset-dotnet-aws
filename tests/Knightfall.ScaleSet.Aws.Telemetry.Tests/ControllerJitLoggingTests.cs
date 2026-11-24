using System.Diagnostics;
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

    private sealed class CaptureLogs : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
