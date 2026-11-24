using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public enum Ec2LaunchAdvanceStatus
{
    OwnershipUnavailable = 0,
    NoPlacement = 1,
    AwaitingEvidence = 2,
    Finished = 3,
    Planned = 4,
    DispatchEvaluated = 5,
    StateChanged = 6,
    CanceledObsolete = 7,
}

public sealed record Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus Status, string? IntentId = null,
    LifecycleWriteOutcome? WriteOutcome = null, EffectDispatchStatus? DispatchStatus = null);

public sealed partial class DynamoDbEffectDispatcher
{
    /// <summary>Plans one placement or dispatches one existing durable launch. Reconciliation owns ambiguous sends.</summary>
    public async Task<Ec2LaunchAdvanceResult> AdvanceEc2LaunchAsync(
        ControllerLease expected, ControllerClockEvidence clockEvidence, LifecycleProfile profile, Guid allocationId,
        Func<int, IReadOnlySet<string>, CancellationToken, Task<Ec2LaunchEffectRequest?>> selectPlacement,
        IReconciliationEffectProvider provider, CancellationToken ownershipLost, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(clockEvidence);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(selectPlacement);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentOutOfRangeException.ThrowIfEqual(allocationId, Guid.Empty);
        if (expected.Mode != ControllerLeaseMode.Active || expected.Deployment != profile.Deployment || ownershipLost.IsCancellationRequested)
        {
            return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.OwnershipUnavailable);
        }

        using var owner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownershipLost);
        var token = owner.Token;
        var lease = await ReadCurrentLeaseAsync(expected, token).ConfigureAwait(false);
        if (lease is null || Window(lease, clockEvidence) is null)
        {
            return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.OwnershipUnavailable);
        }
        var lineage = await _store.ReadEc2LaunchLineageAsync(profile, allocationId, token).ConfigureAwait(false);
        EffectJournalSnapshot? previous = null;
        int ordinal = 0;
        if (lineage is not null)
        {
            previous = await _journal.ReadAsync(profile, lineage.IntentId, token).ConfigureAwait(false);
            if (previous is null || !DynamoDbLifecycleStore.MatchesEc2Lineage(lineage, previous.Entry.Envelope))
            {
                return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.StateChanged, lineage.IntentId);
            }
            if (previous.Entry.State == EffectState.Planned)
            {
                var dispatch = await DispatchPlannedAsync(lease, clockEvidence, profile, lineage.IntentId, provider,
                    ownershipLost, cancellationToken).ConfigureAwait(false);
                if (dispatch == EffectDispatchStatus.StalePlan)
                {
                    return await CancelObsoleteEc2LaunchAsync(lease, clockEvidence, previous, token).ConfigureAwait(false);
                }
                return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.DispatchEvaluated, lineage.IntentId, DispatchStatus: dispatch);
            }
            if (previous.Entry.State != EffectState.Settled)
            {
                return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.AwaitingEvidence, lineage.IntentId);
            }
            if (previous.Entry.Resolution is not { Kind: EffectResolutionKind.Rejected, Ec2LaunchRejection: not null })
            {
                return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.Finished, lineage.IntentId);
            }
            ordinal = lineage.AttemptOrdinal + 1;
        }

        var catalog = await _store.ReadCatalogProfileAsync(profile, token).ConfigureAwait(false);
        if (catalog is not { State: CatalogProfileState.Active or CatalogProfileState.Draining,
                Binding: { ProvisionerType: RunnerProvisionerType.Ec2, ScaleSetId: > 0 } } ||
            Window(lease, clockEvidence) is not { } planningWindow)
        {
            return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.StateChanged, lineage?.IntentId);
        }
        var tried = (lineage?.TriedAvailabilityZones ?? []).ToHashSet(StringComparer.Ordinal);
        var placement = await CallBoundedAsync(ct => selectPlacement(ordinal, tried, ct), planningWindow.Duration, token).ConfigureAwait(false);
        if (placement is null)
        {
            return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.NoPlacement, lineage?.IntentId);
        }
        if (placement.OwnershipTags is null || placement.AllocationId != allocationId ||
            placement.AttemptOrdinal != ordinal || tried.Contains(placement.AvailabilityZone))
        {
            return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.StateChanged, lineage?.IntentId);
        }
        lease = await ReadCurrentLeaseAsync(lease, token).ConfigureAwait(false);
        if (lease is null || Window(lease, clockEvidence) is null)
        {
            return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.OwnershipUnavailable, lineage?.IntentId);
        }
        var intent = EffectEnvelope.Create(profile, EffectIntentIdentity.ForEc2Launch(allocationId, ordinal), catalog.AppliedGeneration,
            placement, _clock.GetUtcNow(), null);
        Guid transition = Guid.NewGuid();
        var write = previous is null
            ? await _journal.PlanAsync(lease, intent, transition, token).ConfigureAwait(false)
            : await _journal.PlanNextEc2LaunchAsync(lease, previous, intent, transition, token).ConfigureAwait(false);
        return new Ec2LaunchAdvanceResult(write == LifecycleWriteOutcome.Committed ? Ec2LaunchAdvanceStatus.Planned :
            Ec2LaunchAdvanceStatus.StateChanged, intent.IntentId, write);
    }

    private async Task<Ec2LaunchAdvanceResult> CancelObsoleteEc2LaunchAsync(
        ControllerLease expected, ControllerClockEvidence clockEvidence, EffectJournalSnapshot planned, CancellationToken cancellationToken)
    {
        string intentId = planned.Entry.Envelope.IntentId;
        var lease = await ReadCurrentLeaseAsync(expected, cancellationToken).ConfigureAwait(false);
        if (lease is null || Window(lease, clockEvidence) is null)
        {
            return new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.OwnershipUnavailable, intentId, DispatchStatus: EffectDispatchStatus.StalePlan);
        }

        // The cancellation compares the Planned revision that dispatch evaluated. An executing or
        // uncertain attempt can only reach the existing provider readback path.
        var outcome = await CancelPlannedAsync(lease, planned, cancellationToken).ConfigureAwait(false);
        var status = outcome switch
        {
            LifecycleWriteOutcome.Committed => Ec2LaunchAdvanceStatus.CanceledObsolete,
            LifecycleWriteOutcome.LeaseRejected => Ec2LaunchAdvanceStatus.OwnershipUnavailable,
            _ => Ec2LaunchAdvanceStatus.StateChanged,
        };
        return new Ec2LaunchAdvanceResult(status, intentId, outcome, EffectDispatchStatus.StalePlan);
    }
}
