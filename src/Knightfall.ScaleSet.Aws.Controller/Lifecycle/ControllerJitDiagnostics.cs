using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.GitHub;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

internal static partial class ControllerJitDiagnostics
{
    private const string Name = "Knightfall.ScaleSet.Aws.Controller";
    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> HandoffDuration = Meter.CreateHistogram<double>("scaleset.aws.handoff.duration", "s");
    private static readonly Counter<long> HandoffOperations = Meter.CreateCounter<long>("scaleset.aws.handoff.operations", "{operation}");
    private static readonly Histogram<double> StartupDuration = Meter.CreateHistogram<double>("scaleset.aws.startup.duration", "s");
    private static readonly Counter<long> StartupObservations = Meter.CreateCounter<long>("scaleset.aws.startup.observations", "{operation}");

    internal sealed class Operation(bool startupAuthorization, CancellationToken cancellationToken) : IDisposable
    {
        private readonly TelemetryProfile? _profile = TelemetryProfileScope.Current;
        private readonly string _operation = startupAuthorization ? "authorize" : "advance";
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly Activity? _activity = Source.StartActivity(startupAuthorization
            ? "scaleset.aws.startup.authorize" : "scaleset.aws.handoff.advance");
        private string? _outcome;
        private string? _reason;

        internal void Complete(string outcome, string reason)
        {
            _outcome = outcome;
            _reason = reason;
        }

        internal void CompleteAdvance(GitHubJitStatus status, ILogger? logger)
        {
            var (outcome, reason, level) = status switch
            {
                GitHubJitStatus.Offered => ("success", "offer_confirmed", LogLevel.Information),
                GitHubJitStatus.RegisteredWithoutOffer => ("deferred", "registration_without_offer", LogLevel.Warning),
                GitHubJitStatus.WaitingForHello => ("deferred", "waiting_for_hello", LogLevel.None),
                GitHubJitStatus.OwnershipUnavailable => ("deferred", "ownership_unavailable", LogLevel.None),
                GitHubJitStatus.Held => ("deferred", "handoff_held", LogLevel.Information),
                GitHubJitStatus.Finished => ("deferred", "handoff_finished", LogLevel.None),
                _ => ("unknown", "recovery_required", LogLevel.Warning),
            };
            Complete(outcome, reason);
            if (logger is null || _profile is null || level == LogLevel.None)
            {
                return;
            }

            try
            {
                using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
                {
                    new("profile", _profile.Id),
                    new("provider", _profile.Provider),
                    new("reason", reason),
                });
                LogAdvance(logger, level, _operation, outcome);
            }
            catch (Exception error) when (error is AggregateException or IOException or InvalidOperationException)
            {
                // A logging failure must not change the durable handoff outcome.
            }
        }

        public void Dispose()
        {
            var outcome = _outcome ?? (cancellationToken.IsCancellationRequested ? "cancelled" : "unknown");
            var reason = _reason ?? "interrupted";
            _profile?.SetTags(_activity);
            _activity?.SetTag("operation", _operation);
            _activity?.SetTag("outcome", outcome);
            _activity?.SetTag("reason", reason);
            var tags = _profile?.Tags() ?? new TagList();
            tags.Add("operation", _operation);
            tags.Add("outcome", outcome);
            tags.Add("reason", reason);
            double elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
            if (startupAuthorization)
            {
                StartupDuration.Record(elapsed, tags);
                StartupObservations.Add(1, tags);
            }
            else
            {
                HandoffDuration.Record(elapsed, tags);
                HandoffOperations.Add(1, tags);
            }
            _activity?.Dispose();
        }
    }

    [LoggerMessage(EventId = 901, Message = TelemetryNames.EventTemplate)]
    private static partial void LogAdvance(ILogger logger, LogLevel level, string operation, string outcome);
}
