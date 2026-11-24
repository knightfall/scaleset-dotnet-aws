using System.Collections.Immutable;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    internal sealed record DrainPass(long Revision, long AppliedGeneration, DateTimeOffset? FirstClearAt);

    internal sealed record DrainProof(bool Clear, SessionRecord? Session, ImmutableArray<LifecycleRecordCondition> Guards,
        DateTimeOffset OldestEvidenceAt = default, DateTimeOffset OldestExternalAt = default);

    internal async Task<DrainPass?> ReadDrainPassAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.Drain(profile), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        CheckNoExpiration(item);
        long generation = Number(item, "appliedGeneration");
        if (generation <= 0)
        {
            throw InvalidRecord();
        }
        return new DrainPass(Revision(item), generation, OptionalTimestamp(item, "firstClearAt"));
    }

    internal Task<LifecycleWriteOutcome> WriteDrainPassAsync(
        DeploymentLeaseFence lease,
        CatalogProfile profile,
        DrainPass? expected,
        DateTimeOffset? firstClear,
        ImmutableArray<LifecycleRecordCondition> guards,
        CancellationToken cancellationToken)
    {
        RequireProfileLease(profile.Profile, lease);
        var key = LifecycleKey.Drain(profile.Profile);
        var transition = Guid.NewGuid();
        var item = Record(key, lease.Deployment, NextRevision(expected?.Revision), transition);
        item.Add("appliedGeneration", NumberValue(profile.AppliedGeneration));
        if (firstClear is { } observed)
        {
            item.Add("firstClearAt", NumberValue(ConservativeLatest(observed).ToUnixTimeMilliseconds()));
        }

        var leaseCondition = LeaseCondition(lease);
        RequireAppliedCatalog(leaseCondition, profile);
        var profileGuard = new LifecycleRecordCondition(LifecycleKey.Profile(profile.Profile), profile.Revision);
        var actions = new List<TransactionAction>
        {
            leaseCondition,
            RecordRevisionCondition(profileGuard),
            CompareAndPut(key, item, expected?.Revision, lease.Deployment),
        };
        actions.AddRange(guards.Select(RecordRevisionCondition));
        if (actions.Count > MaximumTransactionItems)
        {
            return Task.FromResult(LifecycleWriteOutcome.Conflict);
        }
        return WriteAsync(actions, transition, cancellationToken);
    }

    internal async Task<DrainProof> ReadDrainProofAsync(
        CatalogProfile catalog,
        DeploymentLeaseFence lease,
        ProfileRetirementTiming timing,
        ProfileHandoffObservation? handoffs,
        CancellationToken cancellationToken)
    {
        var profile = catalog.Profile;
        RequireProfileLease(profile, lease);
        var session = await ReadSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        if (session is not { Phase: SessionPhase.Active or SessionPhase.Retired, SessionId: Guid current } ||
            session.Binding.ScopeIdentity != catalog.Binding.ScopeIdentity || session.Binding.ScaleSetId != catalog.Binding.ScaleSetId ||
            handoffs is not { IsComplete: true, OutstandingOffers: 0, ReadGuards.IsDefaultOrEmpty: false })
        {
            return new DrainProof(false, session, []);
        }

        var demandKey = LifecycleKey.Demand(profile);
        var demand = await ReadRecordAsync(demandKey, cancellationToken).ConfigureAwait(false);
        var balanceKey = LifecycleKey.Admission(profile);
        var balance = await ReadRecordAsync(balanceKey, cancellationToken).ConfigureAwait(false);
        if (demand?.Payload is not { } demandPayload ||
            ReadDrainPayload<SessionDemandObservation>(demandPayload) is not { TotalAssignedJobs: 0 } currentDemand || currentDemand.SessionId != current ||
            !await HasNoReservationsAsync(profile, balance, cancellationToken).ConfigureAwait(false))
        {
            return new DrainProof(false, session, []);
        }

        var effects = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        var inventory = await ReadExecutorInventoryAsync(profile, cancellationToken).ConfigureAwait(false);
        if (effects is not { State: EffectIndexState.Ready, UnfinishedCount: 0 } || !MatchesLease(effects, lease) ||
            inventory is not { IsConsistent: true, Coverage: { IsComplete: true, UnownedOccupied: 0 } coverage, Revision: not null })
        {
            return new DrainProof(false, session, []);
        }

        long? checkpointRevision = await ReadClearCheckpointsAsync(profile, session, current, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        // After confirmed closure, no delivery can refresh this session's zero demand.
        bool requiresFreshDemand = catalog.State != CatalogProfileState.ClosureHeld || session.Phase != SessionPhase.Retired;
        if (!checkpointRevision.HasValue || !Fresh(handoffs.ObservedAt, now, timing.Freshness) ||
            !Fresh(coverage.ObservedAt, now, timing.Freshness) || currentDemand.ObservedAt == default || currentDemand.ObservedAt > now ||
            (requiresFreshDemand && !Fresh(currentDemand.ObservedAt, now, timing.Freshness)) ||
            inventory.Executors.Any(executor => ExecutorClassifier.Classify(executor.State, now, timing.Freshness, timing.LogFlushGrace).Occupied))
        {
            return new DrainProof(false, session, []);
        }

        LifecycleRecordCondition[] basis =
        [
            new(LifecycleKey.Session(profile), session.Revision),
            new(demandKey, demand.Revision),
            new(balanceKey, balance?.Revision),
            new(LifecycleKey.Effects(profile), effects.Revision),
            new(LifecycleKey.RunnerInventory(profile), inventory.Revision),
            new(LifecycleKey.Checkpoint(profile, current), checkpointRevision),
        ];
        var supplied = basis.Concat(handoffs.ReadGuards).GroupBy(guard => guard.Key).ToArray();
        if (supplied.Any(group => group.Key.PartitionKey != profile.PartitionKey || group.Key.Kind == LifecycleItemKind.Drain ||
            group.Select(guard => guard.ExpectedRevision).Distinct().Count() != 1))
        {
            return new DrainProof(false, session, []);
        }
        var external = coverage.ObservedAt < handoffs.ObservedAt ? coverage.ObservedAt : handoffs.ObservedAt;
        var oldest = requiresFreshDemand && currentDemand.ObservedAt < external ? currentDemand.ObservedAt : external;
        return new DrainProof(true, session, supplied.Select(group => group.First()).ToImmutableArray(), oldest, external);
    }

    private async Task<bool> HasNoReservationsAsync(LifecycleProfile profile, LifecycleRecord? balance, CancellationToken cancellationToken)
    {
        if (balance is null)
        {
            return !await HasAcquisitionHistoryAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        return balance.Payload is { } payload && ReadDrainPayload<DrainAdmissionBalance>(payload) is { HeldCount: 0 };
    }

    private async Task<long?> ReadClearCheckpointsAsync(LifecycleProfile profile, SessionRecord session, Guid current, CancellationToken cancellationToken)
    {
        const string prefix = "CHECKPOINT#";
        long? revision = null;
        string? after = null;
        do
        {
            var page = await ReadBasePageAsync(profile.PartitionKey, prefix, after, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                var sort = Text(item, "SK");
                RequireKey(item, profile.PartitionKey, sort);
                CheckNoExpiration(item);
                if (!sort.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(sort.AsSpan(prefix.Length), "D", out var id) ||
                    id == Guid.Empty || Text(item, "deployment") != profile.Deployment)
                {
                    throw InvalidRecord();
                }
                using var data = JsonDocument.Parse(Text(item, "payload"), new JsonDocumentOptions { AllowDuplicateProperties = false });
                if (!data.RootElement.TryGetProperty("Checkpoint", out var stored) ||
                    !stored.TryGetProperty("SchemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1 ||
                    !stored.TryGetProperty("Revision", out var storedRevision) || !storedRevision.TryGetInt64(out var rowRevision) ||
                    rowRevision < 2 || rowRevision != Revision(item) ||
                    !stored.TryGetProperty("TransitionId", out var transition) || !transition.TryGetGuid(out var transitionId) ||
                    transitionId != GuidValue(item, "transitionId") ||
                    !stored.TryGetProperty("InitialStatisticsCompleted", out var initial) || initial.ValueKind != JsonValueKind.True ||
                    !stored.TryGetProperty("SessionKey", out var identity) ||
                    !identity.TryGetProperty("SessionId", out var storedId) || !storedId.TryGetGuid(out var parsedId) || parsedId != id ||
                    !identity.TryGetProperty("Scope", out var scope) || scope.GetString() != session.Binding.ScopeIdentity ||
                    !identity.TryGetProperty("ScaleSetId", out var set) || !set.TryGetInt64(out var setId) || setId != session.Binding.ScaleSetId ||
                    !stored.TryGetProperty("Pending", out var pending) || pending.ValueKind != JsonValueKind.Null ||
                    !data.RootElement.TryGetProperty("AcknowledgmentSha256", out var digest) || digest.ValueKind != JsonValueKind.Null)
                {
                    return null;
                }
                if (id == current)
                {
                    revision = rowRevision;
                }
            }
            after = page.Continuation;
        }
        while (after is not null);
        return revision;
    }

    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now, TimeSpan allowedAge) =>
        observed <= now && now - observed <= allowedAge;

    private static T? ReadDrainPayload<T>(string payload) where T : class =>
        JsonSerializer.Deserialize<T>(payload, DrainPayloadOptions);

    private static readonly JsonSerializerOptions DrainPayloadOptions = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record DrainAdmissionBalance(int HeldCount);
}
