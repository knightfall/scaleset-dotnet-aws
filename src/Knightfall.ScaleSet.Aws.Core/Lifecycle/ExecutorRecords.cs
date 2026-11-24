using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Planning;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record ExecutorBinding(
    LifecycleProfile Profile, string AllocationId, string ResourceArn, Guid Incarnation, ExecutorProviderIdentity Provider)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Profile);
        if (!Guid.TryParseExact(AllocationId, "D", out var allocation) || allocation == Guid.Empty ||
            AllocationId != allocation.ToString("D") || Incarnation == Guid.Empty)
        {
            throw new ArgumentException("An exact allocation ID and executor incarnation are required.");
        }

        // Validate the provider identity against the targeted cleanup contract.
        EffectEnvelopeRequest identity = Provider switch
        {
            Ec2ExecutorIdentity ec2 => new Ec2TerminateEffectRequest(allocation, ResourceArn, ec2.InstanceLaunchTime,
                ec2.LaunchTemplateId, ec2.LaunchTemplateVersion),
            FargateExecutorIdentity fargate => new FargateStopEffectRequest(allocation, fargate.ClusterArn,
                ResourceArn, fargate.TaskDefinitionArn),
            _ => throw new ArgumentException("An exact executor provider identity is required."),
        };
        _ = identity.Normalize();
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Ec2ExecutorIdentity), "ec2")]
[JsonDerivedType(typeof(FargateExecutorIdentity), "fargate")]
public abstract record ExecutorProviderIdentity
{
    private protected ExecutorProviderIdentity()
    {
    }
}

public sealed record Ec2ExecutorIdentity(DateTimeOffset InstanceLaunchTime, string LaunchTemplateId, long LaunchTemplateVersion)
    : ExecutorProviderIdentity;

public sealed record FargateExecutorIdentity(string ClusterArn, string TaskDefinitionArn) : ExecutorProviderIdentity;

public enum ExecutorResourceState
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    IrreversiblyTerminated = 3,
}

public enum ExecutorProcessState
{
    Unknown = 0,
    Ready = 1,
    Exited = 2,
}

public enum ExecutorJobPhase
{
    Assigned = 0,
    Started = 1,
    Completed = 2,
}

public sealed record ExecutorResourceObservation(
    ExecutorResourceState State, DateTimeOffset ObservedAt, DateTimeOffset? StartedAt, Guid EvidenceId);

public sealed record ExecutorProcessObservation(
    long RunnerId, ExecutorProcessState State, DateTimeOffset ObservedAt, Guid EvidenceId);

public sealed record ExecutorJobObservation(
    long RequestId, long RunnerId, ExecutorJobPhase Phase, Guid SessionId, long MessageId, DateTimeOffset ObservedAt,
    bool HasConflict = false);

public sealed record ExecutorState(
    ExecutorBinding Binding,
    long? RunnerId = null,
    ExecutorResourceObservation? Resource = null,
    ExecutorProcessObservation? Process = null,
    ExecutorJobObservation? Job = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? LastResourcePresentAt = null,
    DateTimeOffset? LastProcessReadyAt = null,
    bool HasConflict = false);

public sealed record ExecutorSnapshot(ExecutorState State, long Revision, Guid TransitionId)
{
    public ExecutorBinding Binding => State.Binding;
}

public sealed record ExecutorInventoryObservation(
    DateTimeOffset ObservedAt, bool IsComplete, int UnownedOccupied = 0,
    int? BackendCapacityCeiling = null, int? BackendLaunchHeadroom = null);

public sealed record ExecutorInventorySnapshot(
    long? Revision, ExecutorInventoryObservation? Coverage, ImmutableArray<ExecutorSnapshot> Executors, bool IsConsistent);

public sealed record ExecutorClassification(
    bool Occupied, bool Recovering, bool CompletedCleanupCandidate, RunnerSlotState SlotState);

/// <summary>Current-session demand with the original queue observation identity.</summary>
public sealed record SessionDemandObservation(Guid SessionId, DateTimeOffset ObservedAt, long? MessageId, int TotalAssignedJobs);
