using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

public enum Ec2PreflightStatus
{
    Verified = 0,
    TemplateUnavailable = 1,
    TemplateIdentityMismatch = 2,
    ImdsV2Required = 3,
    ImageIncompatible = 4,
    RootEncryptionUnverified = 5,
    BootstrapProtocolMismatch = 6,
    InstanceProfileMismatch = 7,
    BootstrapRoleMismatch = 8,
    NetworkIncompatible = 9,
    AwsUnavailable = 10,
    PlacementIncompatible = 11,
    AwsIdentityMismatch = 12,
    InstanceTypeIncompatible = 13,
    StatusQualificationMismatch = 14,
    EbsKeyUnqualified = 15,
}

public sealed record Ec2PreflightSubnet(string SubnetId, string AvailabilityZone, int? AvailableIpAddresses);

public sealed record Ec2ResolvedTemplate(
    string LaunchTemplateId,
    long VersionNumber,
    string ImageId,
    string InstanceProfileArn,
    string BootstrapRoleArn,
    string BootstrapRoleId,
    string InstanceType,
    Ec2Tenancy? Tenancy,
    ImmutableArray<Ec2PreflightSubnet> Subnets,
    ImmutableArray<string> SecurityGroupIds);

public sealed record Ec2PreflightResult(Ec2PreflightStatus Status, long? ObservedVersionNumber, Ec2ResolvedTemplate? Template)
{
    public bool IsReady => Status == Ec2PreflightStatus.Verified && Template is not null;
}

public enum Ec2InventoryStatus
{
    Complete = 0,
    Incomplete = 1,
    AwsUnavailable = 2,
    AwsIdentityMismatch = 3,
}

public enum Ec2InstanceState
{
    Unknown = 0,
    Pending = 1,
    Running = 2,
    ShuttingDown = 3,
    Terminated = 4,
}

public sealed record Ec2InventoryEntry(
    string InstanceId,
    string ResourceArn,
    Ec2InstanceState ReportedState,
    Guid? AllocationId,
    string? LaunchTemplateId,
    long? LaunchTemplateVersion,
    DateTimeOffset? OriginalLaunchTime,
    Guid? Incarnation,
    bool IsAuthoritative = true)
{
    public Ec2InstanceState State => IsAuthoritative && Incarnation.HasValue ? ReportedState : Ec2InstanceState.Unknown;

    public string? InstanceProfileArn { get; init; }
    public string? ImageId { get; init; }
    public bool HasPrimaryInterface { get; init; }

    /// <summary>Corroborates a retained executor when a terminal instance has lost its primary interface.</summary>
    public bool CorroboratesTerminal(ExecutorBinding binding, string account, string region, DateTimeOffset observedAt) =>
        IsAuthoritative && ReportedState == Ec2InstanceState.Terminated && binding is { Provider: Ec2ExecutorIdentity identity } &&
        ResourceArn == binding.ResourceArn && AllocationId?.ToString("D") == binding.AllocationId &&
        LaunchTemplateId == identity.LaunchTemplateId && LaunchTemplateVersion == identity.LaunchTemplateVersion &&
        identity.InstanceLaunchTime <= observedAt &&
        (OriginalLaunchTime == identity.InstanceLaunchTime || (OriginalLaunchTime is null && !HasPrimaryInterface)) &&
        binding.Incarnation == Ec2ExecutorIncarnation.Compute(account, region, InstanceId, identity.InstanceLaunchTime,
            identity.LaunchTemplateVersion);
}

public sealed record Ec2InventorySnapshot(
    Ec2InventoryStatus Status, DateTimeOffset ObservedAt, ImmutableArray<Ec2InventoryEntry> Instances)
{
    public bool IsComplete => Status == Ec2InventoryStatus.Complete;
}
