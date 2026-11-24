using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    public async Task<bool> HasSessionOwnershipAsync(OwnedSessionFence fence, CancellationToken cancellationToken)
    {
        RequireSessionFence(fence);
        var profile = fence.Identity.Profile;
        var session = await ReadSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        if (session?.Fence != fence)
        {
            return false;
        }

        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Ready } || !MatchesLease(head, fence.Lease))
        {
            return false;
        }

        var lease = await ReadLeaseAsync(fence.Lease.Deployment, cancellationToken).ConfigureAwait(false);
        return lease is { Mode: ControllerLeaseMode.Active } && lease.Fence == fence.Lease &&
            lease.ValidUntil > ConservativeLatest(DateTimeOffset.MinValue);
    }

    /// <summary>Commits profile records under the exact current active session and index fences.</summary>
    /// <remarks>A logical transition can be read back or retried after uncertainty using its unchanged revision and payload.</remarks>
    public Task<LifecycleWriteOutcome> WriteSessionRecordsAsync(
        OwnedSessionFence fence,
        Guid transitionId,
        IReadOnlyList<LifecycleRecordWrite> writes,
        CancellationToken cancellationToken,
        IReadOnlyList<LifecycleRecordCondition>? readGuards = null) =>
        WriteSessionRecordsCoreAsync(fence, transitionId, writes, readGuards, null, cancellationToken);

    private async Task<LifecycleWriteOutcome> WriteSessionRecordsCoreAsync(
        OwnedSessionFence fence,
        Guid transitionId,
        IReadOnlyList<LifecycleRecordWrite> writes,
        IReadOnlyList<LifecycleRecordCondition>? readGuards,
        IReadOnlyList<TransactionAction>? executorActions,
        CancellationToken cancellationToken)
    {
        RequireSessionFence(fence);
        ArgumentNullException.ThrowIfNull(writes);
        if (transitionId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty logical transition is required.", nameof(transitionId));
        }
        if (writes.Count is 0 or > MaximumTransactionItems - 3)
        {
            throw new ArgumentOutOfRangeException(nameof(writes), "Use 1–97 session rows plus the three mandatory conditions.");
        }

        LifecycleRecordCondition? indexGuard = null;
        var profile = fence.Identity.Profile;
        var ownership = LeaseCondition(fence.Lease);
        var actions = new List<TransactionAction>
        {
            ownership,
            ActiveSessionCondition(fence),
        };

        if (readGuards is not null)
        {
            foreach (var guard in readGuards)
            {
                ArgumentNullException.ThrowIfNull(guard);
                RequireSessionRecordKey(fence, guard.Key, readOnly: true);
                if (guard.Key.Kind == LifecycleItemKind.Effects)
                {
                    if (indexGuard is not null)
                    {
                        throw new ArgumentException("A profile index can have only one revision condition.", nameof(readGuards));
                    }
                    indexGuard = guard;
                }
                else
                {
                    actions.Add(RecordRevisionCondition(guard));
                }
            }
        }

        actions.Add(EffectReadyCondition(profile, fence.Lease, indexGuard));
        foreach (var write in writes)
        {
            RequireSessionRecordKey(fence, write.Key, readOnly: false);
            actions.Add(BuildRecordWrite(fence.Lease.Deployment, transitionId, write));
        }
        if (writes.Any(write => write is { Key.Kind: LifecycleItemKind.Acquisition, ExpectedRevision: null }))
        {
            var catalog = await ReadCatalogProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            if (catalog is not null && (catalog.State != CatalogProfileState.Active ||
                catalog.Binding.ScopeIdentity != fence.Identity.ScopeIdentity || catalog.Binding.ScaleSetId != fence.Identity.ScaleSetId))
            {
                return LifecycleWriteOutcome.Conflict;
            }
            RequireCatalogSessionOpen(ownership, catalog);
            actions.Add(CatalogAllowsSessionOpenCondition(profile, catalog));
        }
        if (executorActions is not null)
        {
            actions.AddRange(executorActions);
        }

        if (actions.Count > MaximumTransactionItems)
        {
            return LifecycleWriteOutcome.Conflict;
        }
        var outcome = await WriteAsync(actions, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (outcome == LifecycleWriteOutcome.RebuildRequired && indexGuard is not null)
        {
            var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
            if (head is { State: EffectIndexState.Ready } && MatchesLease(head, fence.Lease))
            {
                return LifecycleWriteOutcome.Conflict;
            }
        }
        return outcome;
    }

    private TransactionAction ActiveSessionCondition(OwnedSessionFence fence)
    {
        var key = LifecycleKey.Session(fence.Identity.Profile);
        return new TransactionAction(key, new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = _tableName,
                Key = Key(key),
                ConditionExpression = "#deployment = :deployment AND #phase = :active AND #scope = :scope AND #set = :set AND #session = :session AND " +
                    "#owner = :owner AND #task = :task AND #epoch = :epoch AND #protocol = :protocol AND " +
                    "#incarnation = :incarnation AND #revision = :revision",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["#phase"] = "sessionPhase",
                    ["#deployment"] = "deployment",
                    ["#scope"] = "scopeIdentity",
                    ["#set"] = "scaleSetId",
                    ["#session"] = "sessionId",
                    ["#owner"] = "ownerId",
                    ["#task"] = "ownerTaskArn",
                    ["#epoch"] = "ownerEpoch",
                    ["#protocol"] = "writerProtocol",
                    ["#incarnation"] = "incarnation",
                    ["#revision"] = "revision",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":active"] = StringValue(SessionPhaseValue(SessionPhase.Active)),
                    [":deployment"] = StringValue(fence.Lease.Deployment),
                    [":scope"] = StringValue(fence.Identity.ScopeIdentity),
                    [":set"] = NumberValue(fence.Identity.ScaleSetId),
                    [":session"] = StringValue(fence.Identity.SessionId.ToString("D")),
                    [":owner"] = StringValue(fence.Lease.OwnerId),
                    [":task"] = StringValue(fence.Lease.OwnerTaskArn),
                    [":epoch"] = NumberValue(fence.Lease.Epoch),
                    [":protocol"] = NumberValue(fence.Lease.WriterProtocol),
                    [":incarnation"] = StringValue(fence.Incarnation.ToString("D")),
                    [":revision"] = NumberValue(fence.SessionRevision),
                },
            },
        }, LifecycleWriteOutcome.LeaseRejected);
    }

    private TransactionAction EffectReadyCondition(
        LifecycleProfile profile, DeploymentLeaseFence lease, LifecycleRecordCondition? revisionGuard = null,
        bool requireNoUnfinished = false)
    {
        var key = LifecycleKey.Effects(profile);
        var check = new ConditionCheck
        {
            TableName = _tableName,
            Key = Key(key),
            ConditionExpression = "#deployment = :deployment AND #state = :ready AND #epoch = :epoch AND #protocol = :protocol AND #count " +
                (requireNoUnfinished ? "=" : ">=") + " :zero",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["#state"] = "indexState",
                ["#deployment"] = "deployment",
                ["#epoch"] = "ownerEpoch",
                ["#protocol"] = "writerProtocol",
                ["#count"] = "unfinishedCount",
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":ready"] = StringValue("ready"),
                [":deployment"] = StringValue(lease.Deployment),
                [":epoch"] = NumberValue(lease.Epoch),
                [":protocol"] = NumberValue(lease.WriterProtocol),
                [":zero"] = NumberValue(0),
            },
        };
        if (revisionGuard is not null)
        {
            check.ExpressionAttributeNames["#revision"] = "revision";
            if (revisionGuard.ExpectedRevision is long expected)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expected);
                check.ConditionExpression += " AND #revision = :revision";
                check.ExpressionAttributeValues[":revision"] = NumberValue(expected);
            }
            else
            {
                check.ConditionExpression += " AND attribute_not_exists(#revision)";
            }
        }
        return new TransactionAction(key, new TransactWriteItem { ConditionCheck = check }, LifecycleWriteOutcome.RebuildRequired);
    }

    private TransactionAction RecordRevisionCondition(LifecycleRecordCondition guard)
    {
        var key = guard.Key;
        var check = new ConditionCheck
        {
            TableName = _tableName,
            Key = Key(key),
            ConditionExpression = "attribute_not_exists(PK)",
        };
        if (guard.ExpectedRevision is long revision)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
            check.ConditionExpression = "#revision = :revision AND #deployment = :deployment";
            check.ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["#revision"] = "revision",
                ["#deployment"] = "deployment",
            };
            check.ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":revision"] = NumberValue(revision),
                [":deployment"] = StringValue(key.Deployment!),
            };
            if (AllowsHistoryRetention(key.Kind))
            {
                check.ConditionExpression += " AND (attribute_not_exists(#expires) OR #expires > :now)";
                check.ExpressionAttributeNames["#expires"] = "expiresAt";
                check.ExpressionAttributeValues[":now"] = NumberValue(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
            }
        }

        return new TransactionAction(key, new TransactWriteItem { ConditionCheck = check });
    }

    private static void RequireSessionFence(OwnedSessionFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(fence.Identity);
        var identity = fence.Identity;
        RequireSessionBinding(new SessionBinding(identity.Profile, identity.ScopeIdentity, identity.ScaleSetId));
        RequireProfileLease(identity.Profile, fence.Lease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fence.SessionRevision);
        if (identity.SessionId == Guid.Empty || fence.Incarnation == Guid.Empty)
        {
            throw new ArgumentException("An exact session and ownership incarnation are required.", nameof(fence));
        }
    }

    private static void RequireSessionRecordKey(OwnedSessionFence fence, LifecycleKey key, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(key);
        var identity = fence.Identity;
        if (key.PartitionKey != identity.Profile.PartitionKey)
        {
            throw new ArgumentException("A session record must belong to its exact profile partition.", nameof(key));
        }

        bool valid = key.Kind switch
        {
            LifecycleItemKind.Checkpoint => key == LifecycleKey.Checkpoint(identity.Profile, identity.SessionId),
            LifecycleItemKind.Message => key.SortKey.StartsWith($"MSG#{identity.SessionId:D}#", StringComparison.Ordinal),
            LifecycleItemKind.Demand or LifecycleItemKind.Admission or LifecycleItemKind.Job or LifecycleItemKind.Acquisition => true,
            LifecycleItemKind.Effects or LifecycleItemKind.Runner or LifecycleItemKind.RunnerIdentity or LifecycleItemKind.RunnerInventory => readOnly,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("This record does not belong to the session transaction.", nameof(key));
        }
    }
}
