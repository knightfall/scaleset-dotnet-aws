using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Telemetry;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

internal static class ControllerJitDiagnostics
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
}
