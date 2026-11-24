using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

internal static class RunnerBootstrapDiagnostics
{
    private const string Name = "Knightfall.ScaleSet.Aws.RunnerBootstrap";
    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> HandoffDuration = Meter.CreateHistogram<double>("scaleset.aws.handoff.duration", "s");
    private static readonly Counter<long> HandoffOperations = Meter.CreateCounter<long>("scaleset.aws.handoff.operations", "{operation}");
    private static readonly Histogram<double> StartupDuration = Meter.CreateHistogram<double>("scaleset.aws.startup.duration", "s");
    private static readonly Counter<long> StartupObservations = Meter.CreateCounter<long>("scaleset.aws.startup.observations", "{operation}");

    internal static void RecordStartup(TimeSpan duration, RunnerProvisionerType? provider, string outcome, string reason)
    {
        const string operation = "runner-listen";
        using var activity = Source.StartActivity("scaleset.aws.startup.runner-listen");
        var tags = Tags(activity, provider, operation, outcome, reason);
        StartupDuration.Record(duration.TotalSeconds, tags);
        StartupObservations.Add(1, tags);
    }

    private static TagList Tags(Activity? activity, RunnerProvisionerType? provider, string operation, string outcome, string reason)
    {
        var tags = new TagList { { "operation", operation }, { "outcome", outcome }, { "reason", reason } };
        string? name = provider switch
        {
            RunnerProvisionerType.Ec2 => "ec2",
            RunnerProvisionerType.Fargate => "fargate",
            _ => null,
        };
        if (name is not null)
        {
            tags.Add("provider", name);
        }
        if (activity is not null)
        {
            foreach (var tag in tags)
            {
                activity.SetTag(tag.Key, tag.Value);
            }
        }
        return tags;
    }

    internal sealed class Handoff(TimeProvider clock, RunnerProvisionerType? provider, CancellationToken cancellationToken) : IDisposable
    {
        private readonly Activity? _activity = Source.StartActivity("scaleset.aws.handoff.bootstrap-consume");
        private readonly long _startedAt = clock.GetTimestamp();
        private bool _reported;

        internal RunnerBootstrapOutcome Complete(RunnerBootstrapOutcome result, string outcome, string reason)
        {
            Record(outcome, reason);
            return result;
        }

        internal void Authenticated() => Record("success", "offer_authenticated");

        public void Dispose() => Record(cancellationToken.IsCancellationRequested ? "cancelled" : "unknown", "interrupted");

        private void Record(string outcome, string reason)
        {
            if (_reported)
            {
                return;
            }
            _reported = true;
            var tags = Tags(_activity, provider, "bootstrap-consume", outcome, reason);
            HandoffDuration.Record(clock.GetElapsedTime(_startedAt).TotalSeconds, tags);
            HandoffOperations.Add(1, tags);
            _activity?.Dispose();
        }
    }
}
