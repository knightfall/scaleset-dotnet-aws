using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

internal static class Ec2Diagnostics
{
    private const string Name = "Knightfall.ScaleSet.Aws.Providers.Ec2";
    private static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("scaleset.aws.provider.operation.duration", "s");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("scaleset.aws.provider.operations", "{operation}");
    private static readonly Counter<long> TemplateDrifts = Meter.CreateCounter<long>("scaleset.aws.ec2.template_drifts", "{operation}");
    private static readonly Counter<long> AzFailovers = Meter.CreateCounter<long>("scaleset.aws.ec2.az_failovers", "{operation}");
    private static readonly Histogram<double> EventAge = Meter.CreateHistogram<double>("scaleset.aws.event.age", "s");

    internal static OperationDiagnostics? Start(string operation, TelemetryProfile? profile, CancellationToken cancellationToken) =>
        OperationDiagnostics.Start(Activities, "ec2." + operation, operation, Duration, Operations, profile, "ec2", cancellationToken);

    internal static void RecordEffect(OperationDiagnostics? operation, EffectEvidence? evidence)
    {
        if (operation is null)
        {
            return;
        }
        if (evidence is { Kind: EffectResolutionKind.Rejected })
        {
            operation.Complete("rejected", evidence.Ec2LaunchRejection switch
            {
                Ec2LaunchRejection.InsufficientInstanceCapacity => "insufficient_instance_capacity",
                Ec2LaunchRejection.InsufficientFreeAddressesInSubnet => "insufficient_subnet_addresses",
                _ => "provider_rejected",
            });
        }
        else if (evidence is not null)
        {
            operation.Complete("success", "exact_observation");
        }
    }

    internal static void RecordTemplateDrift(TelemetryProfile? profile, string reason)
    {
        var tags = Tags(profile);
        tags.Add("reason", reason);
        TemplateDrifts.Add(1, tags);
    }

    internal static void RecordAzFailover(TelemetryProfile? profile) => AzFailovers.Add(1, Tags(profile));

    internal static void RecordEventAge(DateTimeOffset? eventTime, DateTimeOffset now)
    {
        if (eventTime is { } timestamp && timestamp <= now && now - timestamp <= TimeSpan.FromDays(14))
        {
            EventAge.Record((now - timestamp).TotalSeconds, Tags(null));
        }
    }

    private static TagList Tags(TelemetryProfile? profile) => profile?.Tags() ?? new TagList { { "provider", "ec2" } };
}
