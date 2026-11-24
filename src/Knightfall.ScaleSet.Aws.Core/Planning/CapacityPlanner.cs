using System.Collections.Immutable;
using System.Diagnostics;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;

namespace Knightfall.ScaleSet.Aws.Core.Planning;

public static class CapacityPlanner
{
    private static readonly TimeSpan ObservationFreshness = TimeSpan.FromMinutes(10);
    private static readonly ActivitySource Activities = new(ProfileTelemetry.MeterName);

    public static CapacityPlan Plan(
        RunnerProfile profile,
        DemandSample? demand,
        InventorySample? inventory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var activity = Activities.StartActivity("scaleset.aws.plan.capacity");
        if (activity is { IsAllDataRequested: true })
        {
            TelemetryProfile.From(profile)?.SetTags(activity);
            activity.SetTag("operation", "capacity_plan");
        }
        var capacity = profile.Capacity;
        if (capacity.MaxRunners <= 0 || capacity.IdleReserve < 0 ||
            capacity.IdleReserve > capacity.MaxRunners || capacity.StartupTimeoutSeconds <= 0)
        {
            throw new ArgumentException("The runner profile has invalid capacity limits.", nameof(profile));
        }

        if (demand is { TotalAssignedJobs: < 0 })
        {
            throw new ArgumentException("Assigned jobs cannot be negative.", nameof(demand));
        }

        var reserve = profile.State switch
        {
            RunnerProfileState.Active => capacity.IdleReserve,
            RunnerProfileState.Draining or RunnerProfileState.Disabled => 0,
            _ => throw new ArgumentException("The runner profile has an invalid state.", nameof(profile)),
        };
        var now = timeProvider.GetUtcNow();
        var demandStatus = GetObservationStatus(demand?.ObservedAt, now);
        var inventoryStatus = GetObservationStatus(inventory?.ObservedAt, now);
        if (inventoryStatus == ObservationStatus.Fresh && inventory is { IsComplete: false })
        {
            inventoryStatus = ObservationStatus.Incomplete;
        }

        var inventoryCurrent = inventoryStatus is ObservationStatus.Fresh or ObservationStatus.Incomplete;
        var inventoryComplete = inventoryStatus == ObservationStatus.Fresh;
        var slots = inventory is null
            ? null
            : ObserveSlots(inventory, now, TimeSpan.FromSeconds(capacity.StartupTimeoutSeconds));
        var effectiveMax = profile.State == RunnerProfileState.Disabled ? 0 : capacity.MaxRunners;
        if (inventoryCurrent && inventory?.BackendCapacityCeiling is int backendCeiling)
        {
            effectiveMax = Math.Min(effectiveMax, backendCeiling);
        }

        // Remaining backend headroom and an absolute backend capacity ceiling are separate bounds.
        var backendHeadroom = inventoryCurrent ? inventory?.BackendLaunchHeadroom : null;
        int? available = inventoryComplete && backendHeadroom is int headroom && slots is not null
            ? Math.Min(headroom, Math.Max(0, effectiveMax - slots.Occupied))
            : null;
        int? target = demandStatus == ObservationStatus.Fresh && demand is not null
            ? (int)Math.Min(effectiveMax, (long)demand.TotalAssignedJobs + reserve)
            : null;
        int? deficit = target is int knownTarget && inventoryComplete && slots is not null
            ? Math.Max(0, knownTarget - slots.Serving - slots.Pending)
            : null;

        var plan = new CapacityPlan(
            demandStatus,
            inventoryStatus,
            effectiveMax,
            reserve,
            demand?.TotalAssignedJobs,
            inventoryComplete ? slots?.Serving : null,
            inventoryComplete ? slots?.Pending : null,
            slots?.Occupied,
            slots?.Busy,
            slots?.Uncertain,
            backendHeadroom,
            available,
            target,
            deficit,
            deficit is int knownDeficit && available is int knownAvailability
                ? Math.Min(knownDeficit, knownAvailability)
                : 0,
            slots?.Orphans ?? []);
        if (activity is { IsAllDataRequested: true })
        {
            var (outcome, reason) = TelemetryOutcome(profile, plan);
            activity.SetTag("outcome", outcome).SetTag("reason", reason);
        }

        return plan;
    }

    internal static (string Outcome, string Reason) TelemetryOutcome(RunnerProfile profile, CapacityPlan plan)
    {
        if (profile.State != RunnerProfileState.Active)
        {
            return ("deferred", profile.State == RunnerProfileState.Draining ? "profile_draining" : "profile_disabled");
        }
        if (plan.DemandStatus != ObservationStatus.Fresh)
        {
            return ("unknown", "demand_unavailable");
        }
        if (plan.InventoryStatus != ObservationStatus.Fresh)
        {
            return ("unknown", "inventory_unavailable");
        }
        if (plan.Deficit is > 0)
        {
            return plan.ProposedStarts > 0 ? ("success", "capacity_requested") : ("deferred", "headroom_unavailable");
        }
        return ("success", "target_met");
    }

    internal static ObservationStatus GetObservationStatus(DateTimeOffset? observedAt, DateTimeOffset now)
    {
        if (observedAt is not { } observation)
        {
            return ObservationStatus.Unknown;
        }

        return observation <= now && now - observation <= ObservationFreshness
            ? ObservationStatus.Fresh
            : ObservationStatus.Stale;
    }

    private static SlotSummary ObserveSlots(
        InventorySample inventory,
        DateTimeOffset now,
        TimeSpan startupTimeout)
    {
        if (inventory.BackendCapacityCeiling is < 0 || inventory.BackendLaunchHeadroom is < 0)
        {
            throw new ArgumentException("Backend capacity limits cannot be negative.", nameof(inventory));
        }

        if (inventory.Slots.IsDefault)
        {
            throw new ArgumentException("An inventory sample needs an explicit list of slots.", nameof(inventory));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orphans = ImmutableArray.CreateBuilder<string>();
        var serving = 0;
        var busy = 0;
        var pending = 0;
        var uncertain = 0;
        foreach (var slot in inventory.Slots)
        {
            if (slot is null || string.IsNullOrWhiteSpace(slot.Id) || !ids.Add(slot.Id))
            {
                throw new ArgumentException("Inventory slots need unique nonblank identities.", nameof(inventory));
            }

            if (!Enum.IsDefined(slot.State) || slot.StartedAt > inventory.ObservedAt)
            {
                throw new ArgumentException("An inventory slot has an invalid state or start time.", nameof(inventory));
            }

            if (!slot.OwnershipVerified)
            {
                orphans.Add(slot.Id);
                uncertain++;
                continue;
            }

            switch (slot.State)
            {
                case RunnerSlotState.Busy:
                    busy++;
                    serving++;
                    break;
                case RunnerSlotState.Idle:
                    serving++;
                    break;
                case RunnerSlotState.Starting when slot.StartedAt is { } start &&
                    start <= now && now - start < startupTimeout:
                    pending++;
                    break;
                default:
                    uncertain++;
                    break;
            }
        }

        orphans.Sort(StringComparer.Ordinal);
        return new SlotSummary(inventory.Slots.Length, serving, busy, pending, uncertain, orphans.ToImmutable());
    }

    private sealed record SlotSummary(
        int Occupied,
        int Serving,
        int Busy,
        int Pending,
        int Uncertain,
        ImmutableArray<string> Orphans);
}
