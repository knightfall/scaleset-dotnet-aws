using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Planning;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

/// <summary>Explicitly approved image digest, bootstrap arguments and scratch paths for a runner container.</summary>
public sealed record FargateContainerApproval(
    string Image, ImmutableArray<string> EntryPoint, ImmutableArray<string> Command,
    ImmutableArray<string> ScratchMountPaths = default);

public sealed record FargateProfileScope(
    string Account, string Region, string Deployment, string ProfileId, string ClusterArn, string TaskDefinitionArn);

public enum FargatePreflightStatus
{
    Verified = 0,
    ClusterMismatch = 1,
    LongTaskArnsUnverified = 2,
    TaskDefinitionMismatch = 3,
    TaskPlatformMismatch = 4,
    InvalidTaskSize = 5,
    RoleMismatch = 6,
    ContainerUnapproved = 7,
    NetworkIncompatible = 8,
    AwsUnavailable = 9,
}

public sealed record FargateSubnet(string SubnetId, string AvailabilityZone, int? AvailableIpAddresses);

public sealed record FargateResolvedTask(
    FargateProfileScope Scope,
    DateTimeOffset ObservedAt,
    long TaskDefinitionRevision,
    int CpuUnits,
    int MemoryMiB,
    string RunnerContainer,
    string TaskRoleArn,
    string TaskRoleId,
    string ExecutionRoleArn,
    string ExecutionRoleId,
    ImmutableArray<FargateSubnet> Subnets,
    ImmutableArray<string> SecurityGroupIds,
    DateTimeOffset SubnetSampleStartedAt);

public sealed record FargatePreflightResult(FargatePreflightStatus Status, FargateResolvedTask? Task)
{
    public bool IsReady => Status == FargatePreflightStatus.Verified && Task is not null;
}

public enum FargateInventoryStatus
{
    Complete = 0,
    Incomplete = 1,
    AwsUnavailable = 2,
}

public enum FargateTaskState
{
    Unknown = 0,
    Pending = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
}

public sealed record FargateTaskObservation(
    string ResourceArn,
    string? TaskDefinitionArn,
    FargateTaskState State,
    DateTimeOffset? CreatedAt,
    Guid? AllocationId,
    Guid? Incarnation,
    ImmutableArray<string> AttachedSubnetIds,
    DateTimeOffset? AttachmentObservedAt,
    bool InConfiguredSubnet,
    bool? ExecuteCommandEnabled,
    long? ProviderVersion = null)
{
    public bool IsOwned => Incarnation.HasValue;

    public bool IsForeign { get; init; }

    public bool OccupiesProfile => !IsForeign && State != FargateTaskState.Stopped;
}

public sealed record FargateInventorySnapshot(
    FargateProfileScope Scope,
    FargateInventoryStatus Status,
    DateTimeOffset ObservedAt,
    ImmutableArray<FargateTaskObservation> Tasks,
    string? StartedByFilter = null)
{
    public bool IsComplete => Status == FargateInventoryStatus.Complete;

    public bool IsProfileComplete => IsComplete && StartedByFilter is null;

    public int? OwnedOccupancy => IsProfileComplete ? Tasks.Count(task => task.IsOwned && task.State != FargateTaskState.Stopped) : null;

    public int? ForeignSubnetOccupancy => IsProfileComplete
        ? Tasks.Count(task => !task.IsOwned && task.InConfiguredSubnet && task.State != FargateTaskState.Stopped) : null;

    public InventorySample ToPlanningInventory(FargateHeadroom headroom)
    {
        ArgumentNullException.ThrowIfNull(headroom);
        var slots = Tasks.Where(task => task.OccupiesProfile)
            .Select(task => new RunnerSlot(task.ResourceArn,
                task.IsOwned && task.State == FargateTaskState.Pending && task.CreatedAt.HasValue
                    ? RunnerSlotState.Starting : RunnerSlotState.Unknown, task.IsOwned, task.CreatedAt))
            .ToImmutableArray();
        int? launchHeadroom = IsProfileComplete ? headroom.LaunchSlots : null;
        int? ceiling = launchHeadroom is int launchSlots
            ? (int)Math.Min(int.MaxValue, (long)slots.Length + launchSlots) : null;
        return new InventorySample(ObservedAt, IsProfileComplete, ceiling, launchHeadroom, slots);
    }
}

/// <summary>The remaining on-demand CPU units after usage represented in the operator's sample.</summary>
public sealed record FargateQuotaEvidence(
    string Account, string Region, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt,
    [property: JsonRequired] long RemainingCpuUnits, [property: JsonRequired] long ReservedCpuUnits)
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(10);

    [JsonIgnore]
    public DateTimeOffset ValidUntil => ExpiresAt - ObservedAt < MaximumAge ? ExpiresAt : ObservedAt + MaximumAge;

    public bool IsCurrentFor(string account, string region, DateTimeOffset now) =>
        Account == account && Region == region && ObservedAt != default && ObservedAt <= now &&
        now - ObservedAt <= MaximumAge && ExpiresAt > now &&
        RemainingCpuUnits >= 0 && ReservedCpuUnits >= 0;
}

public sealed record FargateLaunchReservation(
    Guid AllocationId, int CpuUnits, ImmutableArray<string> EligibleSubnetIds, string? TaskArn = null)
{
    /// <summary>Exact durable launch scope. Without it, only the profile being planned can corroborate the reservation.</summary>
    public FargateProfileScope? Scope { get; init; }
}

public sealed record FargateHeadroom(int? IpSlots, int? QuotaSlots, int? LaunchSlots);
