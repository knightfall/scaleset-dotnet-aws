using System.Collections.Concurrent;
using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Fargate;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed class FargateRunnerBackend(
    RuntimeConfiguration configuration, RunnerProfile profile, LifecycleProfile key, FargateProfileInspector inspector,
    FargateRuntimeEvidence evidence, FargateQuotaReader quota, IReadOnlyCollection<LifecycleProfile> deploymentProfiles,
    ConcurrentDictionary<string, FargateInventorySnapshot> deploymentInventory,
    DynamoDbLifecycleStore store, DynamoDbEffectJournal journal, DynamoDbEffectDispatcher dispatcher,
    TimeProvider clock, EffectDispatcherTiming timing) : IRunnerBackend
{
    private static readonly TimeSpan DemandFreshness = TimeSpan.FromMinutes(10);
    private FargateResolvedTask? _launchTask;

    public IReconciliationEffectProvider Effects { get; } = evidence.ForProfile(profile.Id) is { } approval
        ? inspector.CreateEffectProvider(configuration, key, approval)
        : inspector.CreateEffectObserver(configuration, key);

    public IReconciliationEffectProvider ObservationEffects { get; } = new RunnerEffectReadback(inspector.CreateEffectObserver(configuration, key));

    public async Task<RunnerBackendInventory> ReadAsync(ControllerLease lease, CancellationToken cancellationToken)
    {
        _launchTask = null;
        deploymentInventory.TryGetValue(profile.Id, out var previous);
        FargateInventorySnapshot inventory;
        try
        {
            inventory = await inspector.ReadInventoryAsync(configuration, profile.Id, previous, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            deploymentInventory.TryRemove(profile.Id, out _);
            throw;
        }
        deploymentInventory[profile.Id] = inventory;
        var launchHeadroom = (int?)null;
        FargateResolvedTask? verified = null;
        if (evidence.ForProfile(profile.Id) is { } approval && inventory.IsProfileComplete)
        {
            var preflight = await inspector.ResolvePreflightAsync(configuration, profile.Id, approval, cancellationToken).ConfigureAwait(false);
            if (preflight is { IsReady: true, Task: { } approved })
            {
                verified = approved;
                var reservations = await ReadReservationsAsync(lease.Fence, cancellationToken).ConfigureAwait(false);
                var sample = await quota.ReadAsync(cancellationToken).ConfigureAwait(false);
                var headroom = inspector.EstimateHeadroom(approved, inventory, sample, reservations,
                    deploymentInventory.Values.ToArray());
                launchHeadroom = headroom.LaunchSlots;
                _launchTask = approved;
            }
        }

        var resources = inventory.Tasks.Select(task => ProjectResource(inventory, task, verified))
            .Where(static resource => resource is not null).Select(static resource => resource!).ToImmutableArray();
        return new RunnerBackendInventory(inventory.ObservedAt, inventory.IsProfileComplete, launchHeadroom, null, resources);
    }

    public async Task ReconcileLaunchesAsync(ActiveControllerAuthority authority, CatalogProfile catalog, bool allowPreparedLaunches,
        bool allowNewLaunch, CancellationToken ownershipLost, CancellationToken cancellationToken)
    {
        if (catalog.State != CatalogProfileState.Active || !allowPreparedLaunches || evidence.ForProfile(profile.Id) is null ||
            Effects is not IFargateReplayEffectProvider replay)
        {
            return;
        }

        ActiveEffectCursor? cursor = null;
        do
        {
            var page = await store.ReadActiveOperationsAsync(key, authority.Lease.Fence, cursor, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return;
            }
            foreach (string intentId in page.IntentIds)
            {
                var effect = await journal.ReadAsync(key, intentId, cancellationToken).ConfigureAwait(false);
                if (effect is null || effect.Entry.State == EffectState.Settled)
                {
                    return;
                }
                if (effect.Entry.Envelope.Request is not FargateLaunchEffectRequest)
                {
                    continue;
                }

                if (effect.Entry.State == EffectState.Planned)
                {
                    _ = await DispatchFirstLaunchAsync(authority, effect.Entry.Envelope, ownershipLost, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _ = await dispatcher.ReplayFargateLaunchAsync(authority.Lease, authority.Clock, key, intentId,
                        replay, ownershipLost, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            cursor = page.Continuation;
        }
        while (cursor is not null);

        if (!allowNewLaunch || _launchTask is not { } verified)
        {
            return;
        }
        string[] subnets = verified.Subnets.Where(subnet => subnet.AvailableIpAddresses is > 0)
            .Select(subnet => subnet.SubnetId).ToArray();
        if (subnets.Length == 0)
        {
            return;
        }
        var request = FargateProfileInspector.CreateLaunchRequest(verified, Guid.NewGuid(), subnets);
        var earliest = clock.GetUtcNow() - authority.Clock.MaximumAbsoluteSkew;
        var envelope = EffectEnvelope.Create(key, EffectIntentIdentity.ForAllocation(request)!, catalog.AppliedGeneration, request,
            earliest, earliest.AddMinutes(50));
        if (await journal.PlanAsync(authority.Lease, envelope, Guid.NewGuid(), cancellationToken).ConfigureAwait(false) == LifecycleWriteOutcome.Committed)
        {
            _ = await DispatchFirstLaunchAsync(authority, envelope, ownershipLost, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<EffectDispatchStatus> DispatchFirstLaunchAsync(ActiveControllerAuthority authority, EffectEnvelope envelope,
        CancellationToken ownershipLost, CancellationToken cancellationToken) =>
        dispatcher.DispatchFirstFargateLaunchAsync(authority.Lease, authority.Clock, key, envelope.IntentId, Effects,
            token => ReadFirstLaunchEvidenceAsync(authority.Lease.Fence, envelope, token), ownershipLost, cancellationToken);

    private async Task<FargateFirstLaunchEvidence?> ReadFirstLaunchEvidenceAsync(
        DeploymentLeaseFence lease, EffectEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Request is not FargateLaunchEffectRequest launch || _launchTask is not { } task ||
            !deploymentInventory.TryGetValue(profile.Id, out var inventory) ||
            await store.ReadFargateLaunchDemandAsync(key, lease, cancellationToken).ConfigureAwait(false) is not { } demand)
        {
            return null;
        }
        var snapshot = await store.ReadExecutorInventoryAsync(key, cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsConsistent || snapshot.Revision is not { } inventoryRevision ||
            snapshot.Coverage is not { IsComplete: true } coverage || coverage.ObservedAt != inventory.ObservedAt)
        {
            return null;
        }
        var readback = await inspector.ReadInventoryAsync(configuration, profile.Id, inventory, cancellationToken).ConfigureAwait(false);
        if (!readback.IsProfileComplete || readback.Scope != inventory.Scope ||
            !OccupiedTasks(inventory).ToHashSet().SetEquals(OccupiedTasks(readback)))
        {
            // The next pass can publish a changed provider inventory before deciding whether to launch.
            return null;
        }
        var reservations = await ReadReservationsAsync(lease, cancellationToken).ConfigureAwait(false);
        var sample = await quota.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (sample is null || reservations is null || coverage.ObservedAt > now ||
            now - coverage.ObservedAt >= timing.ObservationFreshness || demand.Demand.ObservedAt > now ||
            now - demand.Demand.ObservedAt >= DemandFreshness)
        {
            return null;
        }
        var headroom = inspector.EstimateReservedLaunchHeadroom(task, readback, sample, reservations,
            deploymentInventory.Values.ToArray(), launch).LaunchSlots;
        long unowned = (long)coverage.UnownedOccupied + reservations.Count(reservation =>
            reservation.Scope == task.Scope && reservation.AllocationId != launch.AllocationId && reservation.TaskArn is null);
        if (headroom is not > 0 || unowned >= profile.Capacity.MaxRunners)
        {
            return null;
        }
        var current = RunnerCapacityProjection.Project(profile, snapshot, coverage.ObservedAt, (int)unowned,
            complete: true, headroom, now, timing);
        var plan = CapacityPlanner.Plan(profile, new DemandSample(demand.Demand.ObservedAt, demand.Demand.TotalAssignedJobs), current, clock);
        if (plan.ProposedStarts == 0)
        {
            return null;
        }
        DateTimeOffset[] deadlines = [sample.ValidUntil, coverage.ObservedAt + timing.ObservationFreshness,
            demand.Demand.ObservedAt + DemandFreshness];
        return new FargateFirstLaunchEvidence(deadlines.Min(), demand.DemandRevision, demand.SessionRevision, inventoryRevision);
    }

    private static IEnumerable<(string Arn, Guid? Allocation, Guid? Incarnation, FargateTaskState State)> OccupiedTasks(
        FargateInventorySnapshot inventory) => inventory.Tasks
        .Where(task => !task.IsForeign && task.State != FargateTaskState.Stopped)
        .Select(task => (task.ResourceArn, task.AllocationId, task.Incarnation, task.State));

    public async Task CleanupAsync(ActiveControllerAuthority authority, CatalogProfile catalog, ExecutorSnapshot executor,
        CancellationToken ownershipLost, CancellationToken cancellationToken)
    {
        if (executor.Binding.Provider is not FargateExecutorIdentity identity || executor.State.HasConflict ||
            executor.State.Resource is not
                { State: ExecutorResourceState.Starting or ExecutorResourceState.Running or ExecutorResourceState.IrreversiblyTerminated })
        {
            return;
        }

        bool terminal = executor.State.Resource.State == ExecutorResourceState.IrreversiblyTerminated;
        if (!terminal && executor.State.AdmissionClosure is null)
        {
            var now = clock.GetUtcNow();
            var startupExpired = executor.State.StartedAt is { } start && now - start >= TimeSpan.FromSeconds(profile.Capacity.StartupTimeoutSeconds);
            if ((catalog.State != CatalogProfileState.Draining && !startupExpired) ||
                await store.CloseFargateAdmissionAsync(authority.Lease, catalog, executor, timing.ObservationFreshness, cancellationToken).ConfigureAwait(false) !=
                    LifecycleWriteOutcome.Committed)
            {
                return;
            }
        }

        var request = new FargateStopEffectRequest(Guid.ParseExact(executor.Binding.AllocationId, "D"), identity.ClusterArn,
            executor.Binding.ResourceArn, identity.TaskDefinitionArn, executor.Binding.Incarnation);
        string intentId = EffectIntentIdentity.ForAllocation(request)!;
        var existing = await journal.ReadAsync(key, intentId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            if (terminal)
            {
                return;
            }
            var envelope = EffectEnvelope.Create(key, intentId, catalog.AppliedGeneration, request, clock.GetUtcNow(), null);
            if (await journal.PlanAsync(authority.Lease, envelope, Guid.NewGuid(), cancellationToken).ConfigureAwait(false) != LifecycleWriteOutcome.Committed)
            {
                return;
            }
        }
        else if (existing.Entry.State != EffectState.Planned || existing.Entry.Envelope.Request != request)
        {
            return;
        }
        _ = await dispatcher.DispatchPlannedAsync(authority.Lease, authority.Clock, key, intentId,
            Effects, ownershipLost, cancellationToken).ConfigureAwait(false);
    }

    private RunnerBackendResource? ProjectResource(FargateInventorySnapshot inventory, FargateTaskObservation task,
        FargateResolvedTask? preflight)
    {
        if (task.IsForeign)
        {
            return null;
        }
        if (!inventory.IsProfileComplete || task.AllocationId is not Guid allocation ||
            task.Incarnation is not Guid incarnation || task.TaskDefinitionArn != inventory.Scope.TaskDefinitionArn)
        {
            return task.State == FargateTaskState.Stopped ? null : new RunnerBackendResource(task.ResourceArn, null, null, null);
        }

        var startedAt = task.CreatedAt <= inventory.ObservedAt ? task.CreatedAt : null;
        var state = task.State switch
        {
            FargateTaskState.Pending when startedAt.HasValue => ExecutorResourceState.Starting,
            FargateTaskState.Running => ExecutorResourceState.Running,
            FargateTaskState.Stopped => ExecutorResourceState.IrreversiblyTerminated,
            _ => ExecutorResourceState.Unknown,
        };
        var binding = new ExecutorBinding(key, allocation.ToString("D"), task.ResourceArn, incarnation,
            new FargateExecutorIdentity(inventory.Scope.ClusterArn, inventory.Scope.TaskDefinitionArn));
        var observation = new ExecutorResourceObservation(state, inventory.ObservedAt, startedAt, Guid.NewGuid(), task.ProviderVersion);
        return new RunnerBackendResource(task.ResourceArn, binding, observation, preflight?.TaskRoleArn);
    }

    private async Task<IReadOnlyCollection<FargateLaunchReservation>?> ReadReservationsAsync(
        DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        var all = new List<FargateLaunchReservation>();
        foreach (var deploymentProfile in deploymentProfiles)
        {
            var tracked = await store.ReadTrackedFargateLaunchReservationsAsync(deploymentProfile, lease, cancellationToken).ConfigureAwait(false);
            if (tracked is null)
            {
                return null;
            }
            all.AddRange(tracked.Reservations.Select(reservation => new FargateLaunchReservation(reservation.AllocationId,
                reservation.CpuUnits, reservation.EligibleSubnetIds, reservation.TaskArn)
            {
                Scope = new FargateProfileScope(configuration.Aws.Account, configuration.Aws.Region, reservation.Profile.Deployment,
                    reservation.Profile.ProfileId, reservation.ClusterArn, reservation.TaskDefinitionArn),
            }));
        }
        return all;
    }
}
