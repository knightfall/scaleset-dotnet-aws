namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record SessionBinding(LifecycleProfile Profile, string ScopeIdentity, long ScaleSetId);

public sealed record SessionIdentity(LifecycleProfile Profile, string ScopeIdentity, long ScaleSetId, Guid SessionId);

public sealed record OwnedSessionFence(
    SessionIdentity Identity, DeploymentLeaseFence Lease, Guid Incarnation, long SessionRevision);

public sealed record LifecycleRecordCondition(LifecycleKey Key, long? ExpectedRevision);

public enum SessionPhase
{
    OpenIntent = 0,
    Active = 1,
    Closing = 2,
    Retired = 3,
    Quarantined = 4,
}

public enum SessionCloseKind
{
    OwnedClient = 0,
    FencedRetirement = 1,
}

public sealed record SessionRecord(
    SessionBinding Binding,
    DeploymentLeaseFence Owner,
    Guid Incarnation,
    SessionPhase Phase,
    Guid? SessionId,
    string OwnerName,
    Guid AttemptId,
    DateTimeOffset AttemptStartedAt,
    DateTimeOffset AttemptDeadline,
    long Revision,
    Guid TransitionId,
    SessionCloseKind? CloseKind,
    Guid? PriorSessionId,
    string RequestSha256)
{
    public SessionIdentity? Identity => SessionId is { } id
        ? new SessionIdentity(Binding.Profile, Binding.ScopeIdentity, Binding.ScaleSetId, id)
        : null;

    public OwnedSessionFence? Fence => Phase == SessionPhase.Active && Identity is { } identity
        ? new OwnedSessionFence(identity, Owner, Incarnation, Revision)
        : null;
}
