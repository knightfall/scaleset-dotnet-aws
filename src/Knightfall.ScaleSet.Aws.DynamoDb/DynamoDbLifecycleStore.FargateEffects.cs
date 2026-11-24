using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record TrackedFargateLaunchReservation(
    Guid AllocationId, int CpuUnits, ImmutableArray<string> EligibleSubnetIds, string? TaskArn);

public sealed record TrackedFargateLaunchReservations(
    ImmutableArray<TrackedFargateLaunchReservation> Reservations, long EffectIndexRevision, long ExecutorInventoryRevision);

public sealed partial class DynamoDbLifecycleStore
{
    internal async Task<LifecycleWriteOutcome> PlanTrackedFargateLaunchAsync(ControllerLease lease, EffectEnvelope envelope,
        FargateLaunchEffectRequest request, Guid transition, CancellationToken cancellationToken)
    {
        var profile = envelope.Profile;
        if (envelope.IntentId != EffectIntentIdentity.ForAllocation(request) ||
            await ReadCatalogProfileAsync(profile, cancellationToken).ConfigureAwait(false) is not { } catalog ||
            !MatchesFargateLaunchCatalog(catalog, envelope, request))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var previous = await ReadEffectAsync(profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
        var entry = EffectJournalEntry.Plan(envelope);
        if (previous is not null)
        {
            return previous.TransitionId == transition && previous.State == EffectState.Planned && previous.Payload == entry.WritePayload()
                ? LifecycleWriteOutcome.Committed : LifecycleWriteOutcome.Conflict;
        }

        string[] arn = request.ClusterArn.Split(':', 6);
        string cluster = arn[5]["cluster/".Length..];
        var claim = LifecycleKey.Claim($"ecs-run-task-token#{arn[4]}:{arn[3]}:{cluster}:{request.ClientToken:N}");
        var token = Record(claim, profile.Deployment, 1, transition);
        token.Add("payload", StringValue(envelope.Sha256));
        var outcome = await WriteEffectAsync(profile, lease.Fence, transition,
            new EffectWrite(envelope.IntentId, null, entry.State, entry.WritePayload()), LeaseAccess.Active, cancellationToken,
            requiredCatalog: catalog, accompanying:
            [
                RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Runner(profile, request.AllocationId.ToString("D")), null)),
                CompareAndPut(claim, token, null, profile.Deployment),
            ]).ConfigureAwait(false);
        if (outcome == LifecycleWriteOutcome.Indeterminate)
        {
            previous = await ReadEffectAsync(profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
            if (previous?.TransitionId == transition && previous.State == EffectState.Planned && previous.Payload == entry.WritePayload())
            {
                return LifecycleWriteOutcome.Committed;
            }
        }
        return outcome;
    }

    internal static bool MatchesFargateLaunchCatalog(CatalogProfile catalog, EffectEnvelope envelope, FargateLaunchEffectRequest request) =>
        catalog.Profile == envelope.Profile && catalog.AppliedGeneration == envelope.Generation &&
        catalog.State is CatalogProfileState.Active or CatalogProfileState.Draining &&
        catalog.Binding.ProvisionerType == RunnerProvisionerType.Fargate && catalog.Binding.ScaleSetId is > 0 &&
        catalog.Binding.ProviderDefinition == request.TaskDefinitionArn && catalog.Binding.BootstrapRoleArn == request.TaskRoleArn &&
        catalog.Binding.BootstrapProtocol == request.OwnershipTags?.BootstrapProtocol;

    internal async Task<LifecycleWriteOutcome> WriteFargateResourceSettlementAsync(ControllerLease lease,
        EffectJournalSnapshot previous, EffectJournalEntry settled, Guid transition, CancellationToken cancellationToken)
    {
        var profile = settled.Envelope.Profile;
        if (settled.Resolution is not { ExecutorBinding: { } binding, ResourceObservation: { } observation } ||
            observation.ObservedAt > _timeProvider.GetUtcNow())
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var executor = await ReadExecutorAsync(profile, binding.AllocationId, cancellationToken).ConfigureAwait(false);
        var claimKey = LifecycleKey.Claim("aws-resource#" + binding.ResourceArn);
        var claim = await ReadRecordAsync(claimKey, cancellationToken).ConfigureAwait(false);
        var actions = new List<TransactionAction>();
        if (settled.Envelope.Request is FargateLaunchEffectRequest && executor is null && claim is null)
        {
            actions.Add(ExecutorWrite(claimKey, binding, null, transition, profile.Deployment));
        }
        else if (executor?.Binding != binding || claim is null || ParseExecutorRecord<ExecutorBinding>(claim) != binding)
        {
            return LifecycleWriteOutcome.Conflict;
        }
        else
        {
            actions.Add(ExactExecutorClaimCondition(claimKey, claim, profile.Deployment));
        }

        var inventory = await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        var state = ExecutorObservations.ObserveResource(executor?.State ?? new ExecutorState(binding), observation, _timeProvider.GetUtcNow());
        if (observation.State == ExecutorResourceState.IrreversiblyTerminated && state.Resource?.State != ExecutorResourceState.IrreversiblyTerminated)
        {
            return LifecycleWriteOutcome.Conflict;
        }
        int change = AddExecutorActions(actions, executor, state, transition);
        actions.Add(WriteExecutorInventoryHead(profile, inventory, change, transition));
        var access = lease.Mode == ControllerLeaseMode.RecoveryOnly ? LeaseAccess.Recovery : LeaseAccess.Active;
        var outcome = await WriteEffectAsync(profile, lease.Fence, transition,
            new EffectWrite(settled.Envelope.IntentId, previous.Revision, settled.State, settled.WritePayload()), access, cancellationToken,
            exactPrevious: previous, accompanying: actions).ConfigureAwait(false);
        if (outcome == LifecycleWriteOutcome.Indeterminate)
        {
            var receipt = await ReadEffectAsync(profile, settled.Envelope.IntentId, cancellationToken).ConfigureAwait(false);
            if (receipt?.TransitionId == transition && receipt.State == EffectState.Settled && receipt.Payload == settled.WritePayload())
            {
                return LifecycleWriteOutcome.Committed;
            }
        }
        return outcome;
    }

    /// <summary>Reads unfinished launches and live executors that can still consume previously sampled Fargate headroom.</summary>
    public async Task<TrackedFargateLaunchReservations?> ReadTrackedFargateLaunchReservationsAsync(LifecycleProfile profile,
        DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        RequireProfileLease(profile, lease);
        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        var inventory = await ReadExecutorInventoryAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Ready } || !MatchesLease(head, lease) ||
            !inventory.IsConsistent || inventory.Revision is not { } inventoryRevision)
        {
            return null;
        }

        var reservations = new Dictionary<Guid, TrackedFargateLaunchReservation>();
        if (!await ReadUnfinishedFargateLaunchReservationsAsync(profile, lease, reservations, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        foreach (var executor in inventory.Executors)
        {
            if (executor.Binding.Provider is not FargateExecutorIdentity || !Guid.TryParseExact(executor.Binding.AllocationId, "D", out var allocation))
            {
                return null;
            }

            var raw = await ReadEffectAsync(profile, "fargate-launch-" + executor.Binding.AllocationId, cancellationToken).ConfigureAwait(false);
            if (raw is not { State: EffectState.Settled } || EffectJournalEntry.Read(profile, raw) is not
                { Envelope.Request: FargateLaunchEffectRequest { CpuUnits: { } units } request,
                    Resolution: { Kind: EffectResolutionKind.Applied, ExecutorBinding: { } binding } } ||
                binding != executor.Binding || request.AllocationId != allocation ||
                !reservations.TryAdd(allocation, new TrackedFargateLaunchReservation(allocation, units, request.SubnetIds, binding.ResourceArn)))
            {
                return null;
            }
        }

        if (await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false) != head ||
            (await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false))?.Record.Revision != inventoryRevision)
        {
            return null;
        }
        return new TrackedFargateLaunchReservations([.. reservations.Values.OrderBy(item => item.AllocationId)], head.Revision, inventoryRevision);
    }

    private async Task<bool> ReadUnfinishedFargateLaunchReservationsAsync(LifecycleProfile profile, DeploymentLeaseFence lease,
        Dictionary<Guid, TrackedFargateLaunchReservation> reservations, CancellationToken cancellationToken)
    {
        ActiveEffectCursor? cursor = null;
        do
        {
            var page = await ReadActiveOperationsAsync(profile, lease, cursor, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return false;
            }
            foreach (string intentId in page.IntentIds)
            {
                var raw = await ReadEffectAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
                if (raw is not { IsUnfinished: true })
                {
                    return false;
                }
                if (EffectJournalEntry.Read(profile, raw).Envelope.Request is FargateLaunchEffectRequest launch &&
                    (launch.CpuUnits is not { } units || !reservations.TryAdd(launch.AllocationId,
                        new TrackedFargateLaunchReservation(launch.AllocationId, units, launch.SubnetIds, null))))
                {
                    return false;
                }
            }
            cursor = page.Continuation;
        }
        while (cursor is not null);

        return true;
    }
}
