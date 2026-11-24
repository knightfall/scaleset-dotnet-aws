using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Core.Catalog;

public enum CatalogProfileState
{
    Active = 0,
    Draining = 1,
    ClosureHeld = 2,
    Tombstoned = 3,
}

public sealed record CatalogBinding(
    string ScopeIdentity,
    string RunnerGroup,
    string ScaleSet,
    ImmutableArray<string> Labels,
    RunnerProvisionerType ProvisionerType,
    string ProviderDefinition,
    string BootstrapRoleArn,
    string BootstrapProtocol,
    long? RunnerGroupId,
    long? ScaleSetId);

public sealed record CatalogProfile(
    LifecycleProfile Profile,
    CatalogBinding Binding,
    CatalogProfileState State,
    long AppliedGeneration,
    long Revision,
    Guid TransitionId,
    DateTimeOffset? ClosureHeldAt,
    DateTimeOffset? RetiredAt);

public sealed record CatalogRolloutPending(
    long Generation, string Digest, string PlanDigest, int NextAction, int ActionCount, bool Aborting);

public sealed record CatalogRolloutHead(
    string Deployment,
    long Revision,
    long AppliedGeneration,
    string? AppliedDigest,
    long AppliedEpoch,
    CatalogRolloutPending? Pending);

public enum CatalogRolloutStatus
{
    Applied = 0,
    Staging = 1,
    StaleGeneration = 2,
    LeaseRejected = 3,
    BindingConflict = 4,
    RetirementRequired = 5,
    PendingConflict = 6,
    Indeterminate = 7,
    Aborted = 8,
}

public sealed record CatalogRolloutChange(CatalogRolloutStatus Status, CatalogRolloutHead? Head);
