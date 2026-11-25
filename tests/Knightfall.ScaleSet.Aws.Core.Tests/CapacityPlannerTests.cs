using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class CapacityPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 2, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedTimeProvider(Now);

    [Theory]
    [InlineData(0, 0, 10, 10, 0, 0)]
    [InlineData(0, 2, 10, 10, 2, 2)]
    [InlineData(3, 2, 10, 10, 5, 5)]
    [InlineData(9, 2, 10, 10, 10, 10)]
    [InlineData(9, 2, 4, 10, 4, 4)]
    [InlineData(9, 2, 20, 20, 10, 10)]
    [InlineData(9, 2, 10, 3, 10, 3)]
    [InlineData(int.MaxValue, 2, 10, 10, 10, 10)]
    public void AppliesDemandReserveAndSeparateAbsoluteAndRemainingLimits(
        int assigned,
        int reserve,
        int backendCeiling,
        int backendHeadroom,
        int expectedTarget,
        int expectedStarts)
    {
        var inventory = new InventorySample(Now, true, backendCeiling, backendHeadroom, []);

        var plan = CapacityPlanner.Plan(Profile(reserve: reserve), new DemandSample(Now, assigned), inventory, Clock);

        Assert.Equal(ObservationStatus.Fresh, plan.DemandStatus);
        Assert.Equal(ObservationStatus.Fresh, plan.InventoryStatus);
        Assert.Equal(assigned, plan.TotalAssignedJobs);
        Assert.Equal(Math.Min(10, backendCeiling), plan.EffectiveMax);
        Assert.Equal(reserve, plan.EffectiveIdleReserve);
        Assert.Equal(0, plan.OccupiedSlots);
        Assert.Equal(0, plan.EligibleServing);
        Assert.Equal(0, plan.CrediblePending);
        Assert.Equal(expectedTarget, plan.Target);
        Assert.Equal(expectedTarget, plan.Deficit);
        Assert.Equal(backendHeadroom, plan.BackendLaunchHeadroom);
        Assert.Equal(expectedStarts, plan.ProposedStarts);
    }

    [Fact]
    public void VerifiedWarmAllocationOffsetsReserveWithoutCoveringAssignedDemand()
    {
        var profile = Assert.Single(RuntimeConfigurationLoader.Load(ConfigurationFixtures.Runtime(ConfigurationFixtures.Fargate)).Profiles);
        profile = profile with { Capacity = new RunnerCapacityConfiguration(1, 24, 600), State = RunnerProfileState.Active };
        var key = new LifecycleProfile("111122223333:ap-southeast-2:example", profile.Id);
        var binding = new ExecutorBinding(key, Guid.NewGuid().ToString("D"),
            "arn:aws:ecs:ap-southeast-2:111122223333:task/runners/0123456789abcdef0123456789abcdef",
            Guid.NewGuid(), new FargateExecutorIdentity("arn:aws:ecs:ap-southeast-2:111122223333:cluster/runners",
                "arn:aws:ecs:ap-southeast-2:111122223333:task-definition/runner:1"));
        var start = Now.AddMinutes(-1);
        var pending = new ExecutorState(binding,
            Resource: new ExecutorResourceObservation(ExecutorResourceState.Starting, Now, start, Guid.NewGuid()),
            StartedAt: start);
        var running = pending with { Resource = pending.Resource! with { State = ExecutorResourceState.Running } };
        var registered = running with { RunnerId = 123 };
        var restarted = JsonSerializer.Deserialize<ExecutorState>(JsonSerializer.SerializeToUtf8Bytes(registered));
        Assert.NotNull(restarted);

        Assert.Equal(0, Plan(pending).ProposedStarts);
        Assert.Equal(1, Plan(pending).CrediblePending);
        foreach (var executor in new[] { running, registered, restarted, restarted with { StartedAt = null } })
        {
            var reserved = Plan(executor);
            Assert.Equal(0, reserved.ProposedStarts);
            Assert.Equal(0, reserved.EligibleServing);
            Assert.Equal(0, reserved.CrediblePending);
            Assert.Equal(1, reserved.UncertainSlots);
        }
        var busy = registered with
        {
            Job = new ExecutorJobObservation(456, 123, ExecutorJobPhase.Started, Guid.NewGuid(), 7, Now),
        };
        Assert.Equal(1, Plan(busy, assigned: 1).EligibleServing);
        Assert.Equal(1, Plan(busy, assigned: 1).ProposedStarts);
        Assert.Equal(1, Plan(restarted, assigned: 1).ProposedStarts);
        Assert.Equal(0, Plan(restarted, assigned: 2, headroom: 0).ProposedStarts);

        var stopped = registered with { Resource = registered.Resource! with { State = ExecutorResourceState.IrreversiblyTerminated } };
        Assert.Equal(1, Plan(stopped).ProposedStarts);
        Assert.Equal(0, Plan(pending with { Binding = binding with { AllocationId = Guid.NewGuid().ToString("D") } }).ProposedStarts);
        var held = Plan(restarted with { HasConflict = true });
        Assert.Equal(0, held.EligibleServing);
        Assert.Equal(1, held.UncertainSlots);
        Assert.Equal(1, held.ProposedStarts);

        CapacityPlan Plan(ExecutorState state, int assigned = 0, int headroom = 23)
        {
            var classification = ExecutorClassifier.Classify(state, Now, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
            RunnerSlot[] slots = classification.Occupied
                ? [new(state.Binding.ResourceArn, classification.SlotState, true, state.StartedAt)
                    { CoversIdleReserve = classification.CoversIdleReserve }]
                : [];
            return CapacityPlanner.Plan(profile, new DemandSample(Now, assigned),
                new InventorySample(Now, true, null, headroom, [.. slots]), Clock);
        }
    }

    [Fact]
    public void ServingAndCrediblePendingReduceDeficitWhileAllObservedSlotsUseHeadroom()
    {
        var inventory = new InventorySample(Now, true, 6, 8,
        [
            new("owned-busy", RunnerSlotState.Busy, true),
            new("owned-idle", RunnerSlotState.Idle, true),
            new("owned-starting", RunnerSlotState.Starting, true, Now.AddMinutes(-1)),
            new("unknown", RunnerSlotState.Unknown, true),
        ]);

        var plan = CapacityPlanner.Plan(Profile(), new DemandSample(Now, 5), inventory, Clock);

        Assert.Equal(6, plan.EffectiveMax);
        Assert.Equal(6, plan.Target);
        Assert.Equal(2, plan.EligibleServing);
        Assert.Equal(1, plan.BusySlots);
        Assert.Equal(1, plan.CrediblePending);
        Assert.Equal(1, plan.UncertainSlots);
        Assert.Equal(4, plan.OccupiedSlots);
        Assert.Equal(3, plan.Deficit);
        Assert.Equal(8, plan.BackendLaunchHeadroom);
        Assert.Equal(2, plan.AvailableLaunchSlots);
        Assert.Equal(2, plan.ProposedStarts);
    }

    [Theory]
    [InlineData(RunnerProfileState.Draining, 3)]
    [InlineData(RunnerProfileState.Disabled, 0)]
    public void InactiveProfilesDoNotSpendWarmReserveOrDiscardOccupancy(
        RunnerProfileState state,
        int expectedMaximum)
    {
        var inventory = new InventorySample(Now, true, null, 10,
        [
            new("owned-unknown", RunnerSlotState.Unknown, true),
            new("busy", RunnerSlotState.Busy, true),
            new("foreign-02", RunnerSlotState.Idle, false),
            new("foreign-01", RunnerSlotState.Starting, false, Now),
        ]);

        var plan = CapacityPlanner.Plan(Profile(state, max: 3), new DemandSample(Now, 0), inventory, Clock);

        Assert.Equal(expectedMaximum, plan.EffectiveMax);
        Assert.Equal(0, plan.EffectiveIdleReserve);
        Assert.Equal(0, plan.Target);
        Assert.Equal(0, plan.Deficit);
        Assert.Equal(0, plan.ProposedStarts);
        Assert.Equal(1, plan.BusySlots);
        Assert.Equal(1, plan.EligibleServing);
        Assert.Equal(3, plan.UncertainSlots);
        Assert.Equal(4, plan.OccupiedSlots);
        Assert.Equal(["foreign-01", "foreign-02"], plan.OrphanCandidates);
    }

    [Fact]
    public void DrainingProfilesCanStillStartCapacityForAssignedJobs()
    {
        var inventory = new InventorySample(Now, true, null, 10, []);

        var plan = CapacityPlanner.Plan(
            Profile(RunnerProfileState.Draining), new DemandSample(Now, 2), inventory, Clock);

        Assert.Equal(0, plan.EffectiveIdleReserve);
        Assert.Equal(2, plan.Target);
        Assert.Equal(2, plan.Deficit);
        Assert.Equal(2, plan.ProposedStarts);
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 0)]
    public void StartupAtItsDeadlineLosesDemandCreditWithoutFreeingTheSlot(
        int ticksRelativeToDeadline,
        int expectedPending,
        int expectedDeficit)
    {
        var start = Now.AddSeconds(-600).AddTicks(ticksRelativeToDeadline);
        var inventory = new InventorySample(Now, true, null, 10, [new("starting", RunnerSlotState.Starting, true, start)]);

        var plan = CapacityPlanner.Plan(Profile(max: 1, reserve: 0), new DemandSample(Now, 2), inventory, Clock);

        Assert.Equal(1, plan.Target);
        Assert.Equal(expectedPending, plan.CrediblePending);
        Assert.Equal(expectedDeficit, plan.Deficit);
        Assert.Equal(1, plan.OccupiedSlots);
        Assert.Equal(1 - expectedPending, plan.UncertainSlots);
        Assert.Equal(0, plan.AvailableLaunchSlots);
        Assert.Equal(0, plan.ProposedStarts);
    }

    [Fact]
    public void StartingWithoutTimeOrOwnershipRemainsUncertainAndOccupied()
    {
        var inventory = new InventorySample(Now, true, null, 10,
        [
            new("without-time", RunnerSlotState.Starting, true),
            new("without-ownership", RunnerSlotState.Starting, false, Now),
        ]);

        var plan = CapacityPlanner.Plan(Profile(), new DemandSample(Now, 2), inventory, Clock);

        Assert.Equal(4, plan.Target);
        Assert.Equal(4, plan.Deficit);
        Assert.Equal(0, plan.CrediblePending);
        Assert.Equal(2, plan.OccupiedSlots);
        Assert.Equal(2, plan.UncertainSlots);
        Assert.Equal("without-ownership", Assert.Single(plan.OrphanCandidates));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void MissingBackendHeadroomRemainsDifferentFromVerifiedZero(int? headroom)
    {
        var inventory = new InventorySample(Now, true, null, headroom, []);

        var plan = CapacityPlanner.Plan(Profile(), new DemandSample(Now, 2), inventory, Clock);

        Assert.Equal(4, plan.Target);
        Assert.Equal(4, plan.Deficit);
        Assert.Equal(0, plan.OccupiedSlots);
        Assert.Equal(headroom, plan.BackendLaunchHeadroom);
        Assert.Equal(headroom, plan.AvailableLaunchSlots);
        Assert.Equal(0, plan.ProposedStarts);
    }

    [Theory]
    [InlineData(ObservationStatus.Unknown)]
    [InlineData(ObservationStatus.Incomplete)]
    [InlineData(ObservationStatus.Stale)]
    public void IncompleteOrMissingOccupancyPreventsAStartEvenWithKnownDemand(ObservationStatus status)
    {
        var inventory = status == ObservationStatus.Unknown
            ? null
            : new InventorySample(
                status == ObservationStatus.Stale ? Now.AddMinutes(-11) : Now,
                status == ObservationStatus.Stale,
                null,
                8,
                [new("known-busy", RunnerSlotState.Busy, true)]);

        var plan = CapacityPlanner.Plan(Profile(), new DemandSample(Now, 2), inventory, Clock);

        Assert.Equal(status, plan.InventoryStatus);
        Assert.Equal(4, plan.Target);
        Assert.Null(plan.EligibleServing);
        Assert.Null(plan.CrediblePending);
        Assert.Equal(inventory is null ? null : (int?)1, plan.OccupiedSlots);
        Assert.Equal(inventory is null ? null : (int?)1, plan.BusySlots);
        Assert.Null(plan.Deficit);
        Assert.Null(plan.AvailableLaunchSlots);
        Assert.Equal(0, plan.ProposedStarts);
    }

    [Theory]
    [InlineData(600, 0, ObservationStatus.Fresh, 6, 5)]
    [InlineData(600, 1, ObservationStatus.Stale, null, 0)]
    [InlineData(-1, 0, ObservationStatus.Stale, null, 0)]
    public void DemandIsUsableAtTenMinutesButNotOlderOrFutureDated(
        int ageSeconds,
        int extraTicks,
        ObservationStatus expectedStatus,
        int? expectedTarget,
        int expectedStarts)
    {
        var demand = new DemandSample(Now.AddSeconds(-ageSeconds).AddTicks(-extraTicks), 4);
        var inventory = new InventorySample(Now, true, null, 8, [new("busy", RunnerSlotState.Busy, true)]);

        var plan = CapacityPlanner.Plan(Profile(), demand, inventory, Clock);

        Assert.Equal(expectedStatus, plan.DemandStatus);
        Assert.Equal(4, plan.TotalAssignedJobs);
        Assert.Equal(expectedTarget, plan.Target);
        Assert.Equal(expectedTarget - 1, plan.Deficit);
        Assert.Equal(expectedStarts, plan.ProposedStarts);
        Assert.Equal(1, plan.BusySlots);
    }

    [Fact]
    public void AbsentDemandDoesNotEraseVerifiedInventoryOrOrphans()
    {
        var inventory = new InventorySample(Now, true, null, 7,
        [
            new("busy", RunnerSlotState.Busy, true),
            new("foreign", RunnerSlotState.Unknown, false),
        ]);

        var plan = CapacityPlanner.Plan(Profile(), null, inventory, Clock);

        Assert.Equal(ObservationStatus.Unknown, plan.DemandStatus);
        Assert.Null(plan.TotalAssignedJobs);
        Assert.Null(plan.Target);
        Assert.Null(plan.Deficit);
        Assert.Equal(0, plan.ProposedStarts);
        Assert.Equal(2, plan.OccupiedSlots);
        Assert.Equal(1, plan.BusySlots);
        Assert.Equal(7, plan.AvailableLaunchSlots);
        Assert.Equal("foreign", Assert.Single(plan.OrphanCandidates));
    }

    [Fact]
    public void RejectsInputThatCannotRepresentAValidObservation()
    {
        var profile = Profile();
        var badDemand = new DemandSample(Now, -1);
        Assert.Equal("demand", Assert.Throws<ArgumentException>(() => CapacityPlanner.Plan(profile, badDemand, null, Clock)).ParamName);

        InventorySample[] invalid =
        [
            new(Now, true, -1, null, []),
            new(Now, true, null, -1, []),
            new(Now, true, null, null, default),
            new(Now, true, null, null, [new("", RunnerSlotState.Busy, true)]),
            new(Now, true, null, null, [new("runner", RunnerSlotState.Busy, true), new("runner", RunnerSlotState.Idle, true)]),
            new(Now, true, null, null, [new("runner", (RunnerSlotState)123, true)]),
            new(Now, true, null, null, [new("runner", RunnerSlotState.Starting, true, Now.AddTicks(1))]),
        ];
        foreach (var observation in invalid)
        {
            var error = Assert.Throws<ArgumentException>(() => CapacityPlanner.Plan(profile, null, observation, Clock));
            Assert.Equal("inventory", error.ParamName);
        }
    }

    private static RunnerProfile Profile(RunnerProfileState state = RunnerProfileState.Active, int max = 10, int reserve = 2)
    {
        var json = ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Latest);
        var profile = Assert.Single(RuntimeConfigurationLoader.Load(json).Profiles);
        return profile with { State = state, Capacity = new RunnerCapacityConfiguration(reserve, max, 600) };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
