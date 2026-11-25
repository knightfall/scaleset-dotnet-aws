using System.Collections.Immutable;

namespace Knightfall.ScaleSet.Aws.Core.Planning;

public sealed record DemandSample(DateTimeOffset ObservedAt, int TotalAssignedJobs);

public sealed record InventorySample(
    DateTimeOffset ObservedAt,
    bool IsComplete,
    int? BackendCapacityCeiling,
    int? BackendLaunchHeadroom,
    ImmutableArray<RunnerSlot> Slots);

public sealed record RunnerSlot(
    string Id,
    RunnerSlotState State,
    bool OwnershipVerified,
    DateTimeOffset? StartedAt = null)
{
    public bool CoversIdleReserve { get; init; }
}

public enum RunnerSlotState
{
    Unknown = 0,
    Idle = 1,
    Busy = 2,
    Starting = 3,
}

public enum ObservationStatus
{
    Unknown = 0,
    Fresh = 1,
    Stale = 2,
    Incomplete = 3,
}

public sealed record CapacityPlan(
    ObservationStatus DemandStatus,
    ObservationStatus InventoryStatus,
    int EffectiveMax,
    int EffectiveIdleReserve,
    int? TotalAssignedJobs,
    int? EligibleServing,
    int? CrediblePending,
    int? OccupiedSlots,
    int? BusySlots,
    int? UncertainSlots,
    int? BackendLaunchHeadroom,
    int? AvailableLaunchSlots,
    int? Target,
    int? Deficit,
    int ProposedStarts,
    ImmutableArray<string> OrphanCandidates);
