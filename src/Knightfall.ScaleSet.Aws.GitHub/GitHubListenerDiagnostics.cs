using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

internal static class GitHubListenerDiagnostics
{
    private const string Name = "Knightfall.ScaleSet.Aws.GitHub";
    private const string Prefix = "scaleset.aws.listener.";
    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(Prefix + "operations", "{operation}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(Prefix + "operation.duration", "s");

    internal static Observation Start(string operation, TimeProvider clock, CancellationToken cancellationToken = default) =>
        new(operation, clock, cancellationToken);

    internal static bool IsDeadline(Exception error, CancellationToken cancellationToken) =>
        error is ScaleSetMaxElapsedExceededException or TimeoutException ||
        (error is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    internal static string WriteOutcome(LifecycleWriteOutcome outcome) => outcome switch
    {
        LifecycleWriteOutcome.Committed => "committed",
        LifecycleWriteOutcome.Conflict => "conflict",
        LifecycleWriteOutcome.LeaseRejected => "fenced",
        LifecycleWriteOutcome.RebuildRequired => "deferred",
        _ => "unknown",
    };

    internal static void Count(string operation, string outcome, string? reason = null)
    {
        var tags = Tags(TelemetryProfileScope.Current, operation, outcome, reason);
        Operations.Add(1, tags);
    }

    private static TagList Tags(TelemetryProfile? profile, string operation, string outcome, string? reason)
    {
        var tags = profile?.Tags() ?? new TagList();
        tags.Add("operation", operation);
        tags.Add("outcome", outcome);
        if (reason is not null)
        {
            tags.Add("reason", reason);
        }
        return tags;
    }

    internal sealed class Observation : IDisposable
    {
        private readonly string _operation;
        private readonly TimeProvider _clock;
        private readonly long _start;
        private readonly CancellationToken _cancellationToken;
        private readonly TelemetryProfile? _profile;
        private readonly Activity? _activity;
        private string? _outcome;
        private string? _reason;

        internal Observation(string operation, TimeProvider clock, CancellationToken cancellationToken)
        {
            _operation = operation;
            _clock = clock;
            _start = clock.GetTimestamp();
            _cancellationToken = cancellationToken;
            _profile = TelemetryProfileScope.Current;
            _activity = Source.StartActivity(Prefix + operation);
            _profile?.SetTags(_activity);
            _activity?.SetTag("operation", operation);
        }

        internal void Complete(string outcome, string? reason = null)
        {
            _outcome = outcome;
            _reason = reason;
        }

        public void Dispose()
        {
            string outcome = _outcome ?? (_cancellationToken.IsCancellationRequested ? "cancelled" : "unknown");
            var tags = Tags(_profile, _operation, outcome, _reason);
            _activity?.SetTag("outcome", outcome);
            if (_reason is not null)
            {
                _activity?.SetTag("reason", _reason);
            }
            if (outcome == "failed")
            {
                _activity?.SetStatus(ActivityStatusCode.Error);
            }
            Operations.Add(1, tags);
            Duration.Record(_clock.GetElapsedTime(_start).TotalSeconds, tags);
            _activity?.Dispose();
        }
    }
}
