using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    internal Task<LifecycleWriteOutcome> TryHoldCatalogClosureAsync(
        DeploymentLeaseFence lease, CatalogProfile expected, IReadOnlyList<LifecycleRecordCondition> readGuards,
        CancellationToken cancellationToken) =>
        ChangeCatalogRetirementAsync(lease, expected, readGuards, CatalogProfileState.ClosureHeld, cancellationToken);

    internal Task<LifecycleWriteOutcome> TryTombstoneAsync(
        DeploymentLeaseFence lease, CatalogProfile expected, IReadOnlyList<LifecycleRecordCondition> readGuards,
        CancellationToken cancellationToken) =>
        ChangeCatalogRetirementAsync(lease, expected, readGuards, CatalogProfileState.Tombstoned, cancellationToken);

    private async Task<LifecycleWriteOutcome> ChangeCatalogRetirementAsync(
        DeploymentLeaseFence lease,
        CatalogProfile expected,
        IReadOnlyList<LifecycleRecordCondition> readGuards,
        CatalogProfileState nextState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(readGuards);
        RequireProfileLease(expected.Profile, lease);
        var required = nextState == CatalogProfileState.ClosureHeld ? CatalogProfileState.Draining : CatalogProfileState.ClosureHeld;
        var current = await ReadCatalogProfileAsync(expected.Profile, cancellationToken).ConfigureAwait(false);
        if (expected.State != required || !SameProfile(current, expected))
        {
            return LifecycleWriteOutcome.Conflict;
        }
        var row = await ReadExactCatalogRowAsync(expected, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var guards = new Dictionary<LifecycleItemKind, LifecycleRecordCondition>();
        var checks = new List<TransactionAction>();
        foreach (var guard in readGuards)
        {
            if (guard.Key.PartitionKey != expected.Profile.PartitionKey)
            {
                throw new ArgumentException("Retirement conditions must belong to the exact profile.", nameof(readGuards));
            }

            if (guard.Key.Kind is LifecycleItemKind.Effects or LifecycleItemKind.Session)
            {
                if (!guards.TryAdd(guard.Key.Kind, guard))
                {
                    throw new ArgumentException("Retirement conditions must be unique.", nameof(readGuards));
                }
            }
            else if (guard.Key.Kind is LifecycleItemKind.Demand or LifecycleItemKind.Admission or LifecycleItemKind.Checkpoint or
                     LifecycleItemKind.RunnerInventory or LifecycleItemKind.Drain or LifecycleItemKind.Runner or
                     LifecycleItemKind.RunnerIdentity or LifecycleItemKind.Job or LifecycleItemKind.Acquisition)
            {
                checks.Add(RecordRevisionCondition(guard));
            }
            else
            {
                throw new ArgumentException("Retirement conditions must identify a verified profile evidence row.", nameof(readGuards));
            }
        }

        if (nextState == CatalogProfileState.Tombstoned)
        {
            var session = await ReadSessionAsync(expected.Profile, cancellationToken).ConfigureAwait(false);
            if (session is { Phase: not SessionPhase.Retired } ||
                (guards.TryGetValue(LifecycleItemKind.Session, out var sessionGuard) && sessionGuard.ExpectedRevision != session?.Revision))
            {
                return LifecycleWriteOutcome.Conflict;
            }
            guards[LifecycleItemKind.Session] = new LifecycleRecordCondition(LifecycleKey.Session(expected.Profile), session?.Revision);
        }
        if (guards.TryGetValue(LifecycleItemKind.Session, out var exactSession))
        {
            checks.Add(RecordRevisionCondition(exactSession));
        }

        var leaseCondition = LeaseCondition(lease);
        RequireAppliedCatalog(leaseCondition, expected);
        var time = AtMillisecond(_timeProvider.GetUtcNow());
        var changed = nextState == CatalogProfileState.ClosureHeld
            ? new CatalogProfileData(expected.Binding, nextState, time)
            : new CatalogProfileData(expected.Binding, nextState, expected.ClosureHeldAt, time);
        if (!ValidProfileData(changed))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var transition = Guid.NewGuid();
        checks.Add(leaseCondition);
        checks.Add(EffectReadyCondition(expected.Profile, lease, guards.GetValueOrDefault(LifecycleItemKind.Effects), requireNoUnfinished: true));
        checks.Add(WriteCatalogRow(expected.Profile, row, changed, null, transition));
        if (checks.Count > MaximumTransactionItems)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var result = await WriteAsync(checks, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (result == LifecycleWriteOutcome.Indeterminate &&
            await ReadCatalogProfileAsync(expected.Profile, cancellationToken).ConfigureAwait(false) is { TransitionId: var observed } &&
            observed == transition)
        {
            return LifecycleWriteOutcome.Committed;
        }
        return result;
    }
}
