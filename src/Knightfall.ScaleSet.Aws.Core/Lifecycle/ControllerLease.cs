namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public enum ControllerLeaseMode
{
    RecoveryOnly = 0,
    Active = 1,
    Released = 2,
}

public sealed record ControllerLeasePredecessor(
    string OwnerId,
    string OwnerTaskArn,
    long Epoch,
    int WriterProtocol,
    bool CleanRelease)
{
    public DateTimeOffset? AcquiredAt { get; init; }
}

public sealed record ControllerLease(
    string Deployment,
    string OwnerId,
    string OwnerTaskArn,
    long Epoch,
    int WriterProtocol,
    DateTimeOffset ValidUntil,
    long Revision,
    Guid TransitionId,
    ControllerLeaseMode Mode,
    ControllerLeasePredecessor? PreviousOwner,
    string? RequiredFencingTaskArn)
{
    public const int CurrentWriterProtocol = 1;

    public DateTimeOffset? AcquiredAt { get; init; }

    public DateTimeOffset? RequiredFencingTaskAcquiredAt { get; init; }

    public DeploymentLeaseFence Fence => new(Deployment, OwnerId, Epoch, WriterProtocol, OwnerTaskArn);
}

public sealed record ControllerLeaseChange(LifecycleWriteOutcome Outcome, ControllerLease? Lease)
{
    public Guid? AttemptedTransitionId { get; init; }
}
