using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Planning;

namespace Knightfall.ScaleSet.Aws.Core.DryRun;

public sealed record DryRunReport(
    string Environment,
    string RuntimeSha256,
    string DemandSource,
    DryRunInspection Inspection,
    ImmutableArray<DryRunProfileReport> Profiles,
    ImmutableArray<DryRunReplayFrame> Replay)
{
    public string ApiVersion { get; } = "scaleset-aws/v1alpha1";
    public string Kind { get; } = "RunnerDryRunReport";
}

public sealed record DryRunInspection(bool Requested, bool Performed, string? Reason);

public sealed record DryRunProfileReport(
    string ProfileId,
    RunnerProfileState State,
    RunnerProvisionerType Provisioner,
    DateTimeOffset? DemandObservedAt,
    DateTimeOffset? InventoryObservedAt,
    CapacityPlan Capacity)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ec2CleanupMode { get; init; }
}

public sealed record DryRunReplayFrame(DateTimeOffset ObservedAt, ImmutableArray<DryRunProfileReport> Profiles);

public sealed class DryRunEvidenceException(string message) : FormatException(message);
