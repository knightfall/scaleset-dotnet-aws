using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed partial class ControllerProfileReconciler(
    RuntimeConfiguration configuration, RunnerProfile profile, LifecycleProfile key, long generation,
    IRunnerBackend backend, ControllerProfileSession session, ControllerLeaseOwnership ownership,
    DynamoDbLifecycleStore store, DynamoDbEffectDispatcher effects, ControllerJitCoordinator jit,
    GitHubJitHandoff handoff, ProfileRetirementCoordinator retirement, ProfileTelemetry telemetry,
    TimeProvider clock, EffectDispatcherTiming timing, ILogger<ControllerProfileReconciler> logger)
{
    private EffectCandidateCursor? _effectsCursor;
    private string? _handoffCursor;
    private bool _reportedStoppedEc2;

    internal LifecycleProfile Key { get; } = key;

    internal TelemetryProfile? TelemetryIdentity { get; } = TelemetryProfile.From(profile);

    internal ControllerProfileSession Session { get; } = session;

    internal async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        using var scope = TelemetryProfileScope.Enter(TelemetryIdentity);
        using var diagnostics = ReconciliationDiagnostics.Start(TelemetryIdentity, dryRun: false);
        if (ownership.ReadAuthority() is not { } authority || await store.ReadCatalogProfileAsync(Key, cancellationToken).ConfigureAwait(false) is not
                { } catalog || catalog.AppliedGeneration != generation)
        {
            diagnostics?.Complete("deferred", "ownership_unavailable");
            return false;
        }
        if (catalog.State == CatalogProfileState.Tombstoned)
        {
            telemetry.Retire(authority.Lease.Fence, Key.ProfileId);
            diagnostics?.Complete("success", "profile_retired");
            return true;
        }

        var progress = await effects.ReconcilePassAsync(authority.Lease, authority.Clock, Key, _effectsCursor,
            backend.ObservationEffects, recovery: null, cancellationToken).ConfigureAwait(false);
        _effectsCursor = progress.Continuation;
        if (progress.Outcome is LifecycleWriteOutcome.LeaseRejected or LifecycleWriteOutcome.RebuildRequired or LifecycleWriteOutcome.Indeterminate)
        {
            diagnostics?.Complete("deferred", "effects_unavailable");
            return false;
        }

        var cloud = await backend.ReadAsync(authority.Lease, cancellationToken).ConfigureAwait(false);
        var observed = await ObserveResourcesAsync(authority.Lease.Fence, cloud, cancellationToken).ConfigureAwait(false);
        if (ownership.ReadAuthority() is not { } current)
        {
            diagnostics?.Complete("deferred", "ownership_unavailable");
            return false;
        }
        var snapshot = await store.ReadExecutorInventoryAsync(Key, cancellationToken).ConfigureAwait(false);
        bool complete = cloud.IsComplete && observed.Complete && (snapshot.IsConsistent || snapshot.Revision is null) &&
            snapshot.Executors.All(executor => observed.Exact.ContainsKey(executor.Binding.AllocationId));
        var sample = RunnerCapacityProjection.Project(profile, snapshot, cloud.ObservedAt, observed.Unowned, complete,
            cloud.LaunchHeadroom, clock.GetUtcNow(), timing);
        var coverage = new ExecutorInventoryObservation(sample.ObservedAt, complete, observed.Unowned,
            sample.BackendCapacityCeiling, sample.BackendLaunchHeadroom);
        var published = await store.PublishExecutorInventoryAsync(Key, current.Lease.Fence, snapshot.Revision, coverage, cancellationToken)
            .ConfigureAwait(false);
        if (published != LifecycleWriteOutcome.Committed)
        {
            diagnostics?.Complete("deferred", "inventory_unconfirmed");
            return false;
        }
        if (complete)
        {
            ObserveStoppedEc2(observed);
        }

        GitHubScaleSetBinding? binding = null;
        if (catalog.State is CatalogProfileState.Active or CatalogProfileState.Draining)
        {
            binding = await Session.EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
        }
        var demand = await store.ReadCurrentSessionDemandAsync(Key, current.Lease.Fence, cancellationToken).ConfigureAwait(false);
        var plan = CapacityPlanner.Plan(profile, demand is null ? null : new DemandSample(demand.ObservedAt, demand.TotalAssignedJobs), sample, clock);
        var effectIndex = await store.ReadEffectIndexAsync(Key, cancellationToken).ConfigureAwait(false);
        bool exactIndex = effectIndex is { State: EffectIndexState.Ready } && effectIndex.OwnerEpoch == current.Lease.Epoch &&
            effectIndex.WriterProtocol == current.Lease.WriterProtocol;
        bool ready = IsReady(catalog.State, binding, plan, exactIndex, complete, Session.IsListenerAvailable);
        var now = clock.GetUtcNow();
        telemetry.SetOwnership(current.Lease, current.Clock, timing.WriteReserve);
        telemetry.SetListenerCooldown(current.Lease.Fence, Key.ProfileId, Session.ReadCredentialCooldown());
        var oldestCleanup = complete ? ReadOldestEligibleCleanup(snapshot, now) : null;
        telemetry.Observe(current.Lease.Fence, Key.ProfileId, new ProfileTelemetryObservation(plan, now,
            now + timing.ObservationFreshness, demand?.ObservedAt, sample.ObservedAt, cloud.DriftCount,
            exactIndex ? effectIndex!.UnfinishedCount : null, oldestCleanup, ready));

        var connection = binding is null ? null : await Session.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AdvanceExecutorsAsync(catalog, current, observed.Exact, connection, complete, cancellationToken).ConfigureAwait(false);
        if (ownership.ReadAuthority() is not { } latest)
        {
            diagnostics?.Complete("deferred", "ownership_unavailable");
            return false;
        }
        bool launchable = ready && catalog.State == CatalogProfileState.Active && progress.Outcome == LifecycleWriteOutcome.Committed;
        await backend.ReconcileLaunchesAsync(latest, catalog, launchable, launchable && plan.ProposedStarts > 0,
            ownership.OwnershipLost, cancellationToken).ConfigureAwait(false);

        if (catalog.State is CatalogProfileState.Draining or CatalogProfileState.ClosureHeld && ownership.ReadAuthority() is { } retiring)
        {
            var result = await retirement.AdvanceAsync(Key, retiring, cancellationToken).ConfigureAwait(false);
            if (result.Status is ProfileRetirementStatus.ClosureHeld or ProfileRetirementStatus.SessionRetirementRequired)
            {
                _ = await Session.RetireDrainingSessionAsync(retirement, cancellationToken).ConfigureAwait(false);
            }
            if (result.Status == ProfileRetirementStatus.Retired)
            {
                telemetry.Retire(retiring.Lease.Fence, Key.ProfileId);
                ready = true;
            }
        }
        diagnostics?.Complete(ready ? "success" : "deferred", ready ? "profile_current" : "profile_evidence_unavailable");
        return ready;
    }

    private DateTimeOffset? ReadOldestEligibleCleanup(ExecutorInventorySnapshot snapshot, DateTimeOffset now)
    {
        DateTimeOffset? oldest = null;
        foreach (var executor in snapshot.Executors)
        {
            var state = executor.State;
            if (state.Process is { State: ExecutorProcessState.Exited } process && state.Job is { Phase: ExecutorJobPhase.Completed } job &&
                ExecutorClassifier.Classify(state, now, timing.ObservationFreshness, timing.LogFlushGrace).CompletedCleanupCandidate)
            {
                var logDeadline = process.ObservedAt + timing.LogFlushGrace;
                var eligibleAt = logDeadline > job.ObservedAt ? logDeadline : job.ObservedAt;
                if (oldest is null || eligibleAt < oldest)
                {
                    oldest = eligibleAt;
                }
            }
        }
        return oldest;
    }

    private async Task<ObservedResources> ObserveResourcesAsync(
        DeploymentLeaseFence fence, RunnerBackendInventory cloud, CancellationToken cancellationToken)
    {
        var exact = new Dictionary<string, RunnerBackendResource>(StringComparer.Ordinal);
        bool complete = true;
        int unowned = 0;
        foreach (var resource in cloud.Resources.DistinctBy(resource => resource.ResourceArn))
        {
            if (resource.Binding is not { } expected || resource.Observation is not { } observation ||
                await store.ReadExecutorAsync(Key, expected.AllocationId, cancellationToken).ConfigureAwait(false) is not { } existing ||
                existing.Binding != expected)
            {
                if (resource.Observation?.State != ExecutorResourceState.IrreversiblyTerminated)
                {
                    unowned++;
                }
                continue;
            }

            var outcome = await store.ObserveResourceAsync(fence, expected, observation, cancellationToken).ConfigureAwait(false);
            if (outcome != LifecycleWriteOutcome.Committed || !exact.TryAdd(expected.AllocationId, resource))
            {
                complete = false;
                continue;
            }
            if (expected.Provider is Ec2ExecutorIdentity && existing.State.RunnerId is { } runnerId &&
                resource.Ec2StatusQualificationSha256 is { } qualification)
            {
                await ObserveEc2ProcessAsync(fence, expected, runnerId, qualification, cancellationToken).ConfigureAwait(false);
            }
        }
        return new ObservedResources(exact, unowned, complete);
    }

    private async Task ObserveEc2ProcessAsync(DeploymentLeaseFence fence, ExecutorBinding binding, long runnerId, string qualification,
        CancellationToken cancellationToken)
    {
        var current = await store.ReadJitHandoffAsync(Key, Guid.ParseExact(binding.AllocationId, "D"), cancellationToken).ConfigureAwait(false);
        if (current is { RunnerId: { } expected } && expected == runnerId && current.Identity.ResourceArn == binding.ResourceArn &&
            current.Identity.Incarnation == binding.Incarnation &&
            profile.Provisioner is Ec2ProvisionerConfiguration { StatusQualification: { } ec2 } &&
            current.Identity.AwsUserId.StartsWith(ec2.RoleId + ":", StringComparison.Ordinal) &&
            await store.ReadEc2BootstrapStatusAsync(current, qualification, cancellationToken).ConfigureAwait(false) is { } status)
        {
            var observation = new ExecutorProcessObservation(status.RunnerId,
                status.Exited ? ExecutorProcessState.Exited : ExecutorProcessState.Ready, status.ObservedAt, Guid.NewGuid());
            _ = await store.ObserveProcessAsync(fence, binding, observation, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AdvanceExecutorsAsync(CatalogProfile catalog, ActiveControllerAuthority authority,
        Dictionary<string, RunnerBackendResource> exact, GitHubScaleSetConnection? connection, bool inventoryComplete,
        CancellationToken cancellationToken)
    {
        foreach (var (allocation, resource) in exact)
        {
            if (ownership.ReadAuthority() is not { } current ||
                await store.ReadExecutorAsync(Key, allocation, cancellationToken).ConfigureAwait(false) is not { } executor ||
                executor.Binding != resource.Binding || executor.State.HasConflict)
            {
                continue;
            }
            if (inventoryComplete || executor.Binding.Provider is not FargateExecutorIdentity ||
                executor.State.Resource?.State == ExecutorResourceState.IrreversiblyTerminated)
            {
                await backend.CleanupAsync(current, catalog, executor, ownership.OwnershipLost, cancellationToken).ConfigureAwait(false);
            }
            if (connection is not null && executor.State.Resource is { State: ExecutorResourceState.Starting or ExecutorResourceState.Running } observed &&
                executor.State.Job is not { Phase: ExecutorJobPhase.Completed } && executor.State.AdmissionClosure is null &&
                resource.BootstrapCredentialArn is { } principal)
            {
                var result = await jit.AdvanceAsync(configuration, ownership, new BootstrapProviderObservation(executor.Binding,
                    principal, observed.ObservedAt), connection, cancellationToken).ConfigureAwait(false);
                if (result.Failure is { } failure)
                {
                    Session.RecordCredentialFailure(failure);
                    return;
                }
            }
        }

        if (connection is null)
        {
            return;
        }
        // Terminal executors leave the active index. Retained handoff rows can still require an exact GitHub removal after restart.
        var pending = await store.ReadOutstandingJitHandoffPageAsync(Key, _handoffCursor, cancellationToken).ConfigureAwait(false);
        foreach (var remaining in pending.Handoffs)
        {
            if (ownership.ReadAuthority() is not { } current || current.Lease.Fence != authority.Lease.Fence)
            {
                return;
            }
            var allocation = remaining.Identity.AllocationId;
            if (await store.ReadExecutorAsync(Key, allocation.ToString("D"), cancellationToken).ConfigureAwait(false) is
                { State: { HasConflict: false, Resource.State: ExecutorResourceState.IrreversiblyTerminated } })
            {
                _ = await handoff.RemoveTerminatedRunnerAsync(current, Key, allocation, connection, ownership.OwnershipLost,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        _handoffCursor = pending.Continuation;
    }

    private void ObserveStoppedEc2(ObservedResources observed)
    {
        bool stopped = observed.Exact.Values.Any(static resource => resource.IsStoppedEc2);
        if (stopped && !_reportedStoppedEc2)
        {
            try
            {
                using var logScope = logger.BeginScope(new KeyValuePair<string, object?>[]
                {
                    new("profile", profile.Id),
                    new("provider", "ec2"),
                    new("reason", "external_reclamation_required"),
                });
                LogRecoveryHold(logger, "reconcile", "held");
            }
            catch (Exception error) when (error is AggregateException or IOException or InvalidOperationException)
            {
                // A logging failure must not change reconciliation.
            }
        }
        _reportedStoppedEc2 = stopped;
    }

    [LoggerMessage(EventId = 902, Level = LogLevel.Warning, Message = TelemetryNames.EventTemplate)]
    private static partial void LogRecoveryHold(ILogger logger, string operation, string outcome);

    private static bool IsReady(CatalogProfileState state, GitHubScaleSetBinding? binding, CapacityPlan plan,
        bool exactIndex, bool complete, bool listenerAvailable) =>
        complete && exactIndex && (state == CatalogProfileState.ClosureHeld || (binding is not null && listenerAvailable &&
            (state == CatalogProfileState.Draining || (plan.DemandStatus == ObservationStatus.Fresh &&
                plan.InventoryStatus == ObservationStatus.Fresh && plan.BackendLaunchHeadroom.HasValue))));

    private sealed record ObservedResources(Dictionary<string, RunnerBackendResource> Exact, int Unowned, bool Complete);
}
