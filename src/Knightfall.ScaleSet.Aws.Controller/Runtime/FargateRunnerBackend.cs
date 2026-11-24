using System.Collections.Concurrent;
using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Fargate;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed class FargateRunnerBackend(
    RuntimeConfiguration configuration, RunnerProfile profile, LifecycleProfile key, FargateProfileInspector inspector,
    FargateRuntimeEvidence evidence, IReadOnlyCollection<LifecycleProfile> deploymentProfiles,
    ConcurrentDictionary<string, FargateInventorySnapshot> deploymentInventory,
    DynamoDbLifecycleStore store, DynamoDbEffectJournal journal, DynamoDbEffectDispatcher dispatcher,
    TimeProvider clock, TimeSpan observationFreshness) : IRunnerBackend
{
    private FargateResolvedTask? _launchTask;

    public IReconciliationEffectProvider Effects { get; } = evidence.ForProfile(profile.Id) is { } approval
        ? inspector.CreateEffectProvider(configuration, key, approval)
        : inspector.CreateEffectObserver(configuration, key);

    public IReconciliationEffectProvider ObservationEffects { get; } = new RunnerEffectReadback(inspector.CreateEffectObserver(configuration, key));

    public async Task<RunnerBackendInventory> ReadAsync(ControllerLease lease, CancellationToken cancellationToken)
    {
        _launchTask = null;
        var inventory = await inspector.ReadInventoryAsync(configuration, profile.Id, cancellationToken).ConfigureAwait(false);
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
                var headroom = inspector.EstimateHeadroom(approved, inventory, evidence.Quota, reservations,
                    deploymentInventory.Values.ToArray());
                launchHeadroom = headroom.LaunchSlots;
                if (launchHeadroom > 0)
                {
                    _launchTask = approved;
                }
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
                    _ = await dispatcher.DispatchPlannedAsync(authority.Lease, authority.Clock, key, intentId,
                        Effects, ownershipLost, cancellationToken).ConfigureAwait(false);
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
            _ = await dispatcher.DispatchPlannedAsync(authority.Lease, authority.Clock, key, envelope.IntentId,
                Effects, ownershipLost, cancellationToken).ConfigureAwait(false);
        }
    }

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
                await store.CloseFargateAdmissionAsync(authority.Lease, executor, observationFreshness, cancellationToken).ConfigureAwait(false) !=
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
                reservation.CpuUnits, reservation.EligibleSubnetIds, reservation.TaskArn)));
        }
        return all;
    }
}
