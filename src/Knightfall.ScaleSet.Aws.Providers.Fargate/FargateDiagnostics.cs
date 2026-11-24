using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Telemetry;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

internal static class FargateDiagnostics
{
    private const string Name = "Knightfall.ScaleSet.Aws.Providers.Fargate";
    private static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("scaleset.aws.provider.operation.duration", "s");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("scaleset.aws.provider.operations", "{operation}");
    private static readonly Histogram<double> EventAge = Meter.CreateHistogram<double>("scaleset.aws.event.age", "s");

    internal static OperationDiagnostics? Start(string operation, TelemetryProfile? profile, CancellationToken cancellationToken) =>
        OperationDiagnostics.Start(Activities, "fargate." + operation, operation, Duration, Operations, profile, "fargate", cancellationToken);

    internal static void RecordEventAge(TelemetryProfile? profile, DateTimeOffset? eventTime, DateTimeOffset now)
    {
        if (eventTime is { } timestamp && timestamp <= now && now - timestamp <= TimeSpan.FromDays(14))
        {
            var tags = profile?.Tags() ?? new TagList { { "provider", "fargate" } };
            EventAge.Record((now - timestamp).TotalSeconds, tags);
        }
    }
}
