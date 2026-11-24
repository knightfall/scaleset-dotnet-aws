using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Knightfall.ScaleSet.Aws.Core.Telemetry;

public static class ReconciliationDiagnostics
{
    private static readonly ActivitySource Activities = new(ProfileTelemetry.MeterName);
    private static readonly Meter Meter = new(ProfileTelemetry.MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("scaleset.aws.reconcile.duration", "s");

    public static OperationDiagnostics? Start(TelemetryProfile? profile, bool dryRun) =>
        OperationDiagnostics.Start(Activities, dryRun ? "reconcile.dry_run" : "reconcile.profile",
            dryRun ? "dry_run" : "reconcile", Duration, profile: profile);
}
