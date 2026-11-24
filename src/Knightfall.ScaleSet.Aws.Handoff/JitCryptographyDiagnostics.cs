using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Knightfall.ScaleSet.Aws.Handoff;

internal static class JitCryptographyDiagnostics
{
    private const string Name = "Knightfall.ScaleSet.Aws.Handoff";
    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("scaleset.aws.handoff.duration", "s");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("scaleset.aws.handoff.operations", "{operation}");

    internal sealed class Operation(string name, TimeProvider clock) : IDisposable
    {
        private readonly Activity? _activity = Source.StartActivity("scaleset.aws.handoff." + name);
        private readonly long _start = clock.GetTimestamp();
        private string _outcome = "rejected";

        internal void Complete() => _outcome = "success";

        internal void MarkExpired() => _outcome = "expired";

        public void Dispose()
        {
            _activity?.SetTag("operation", name);
            _activity?.SetTag("outcome", _outcome);
            var tags = new TagList { { "operation", name }, { "outcome", _outcome } };
            Duration.Record(clock.GetElapsedTime(_start).TotalSeconds, tags);
            Operations.Add(1, tags);
            _activity?.Dispose();
        }
    }
}
