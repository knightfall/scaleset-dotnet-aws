using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    private const string OperationPrefix = "OP#";
    private const int DueShardCount = 8;

    public async Task<StoredEffect?> ReadEffectAsync(
        LifecycleProfile profile, string intentId, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.Operation(profile, intentId), cancellationToken).ConfigureAwait(false);
        return item is null ? null : DecodeEffect(profile, item);
    }

    public async Task<EffectIndexHead?> ReadEffectIndexAsync(
        LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.Effects(profile), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        CheckNoExpiration(item);
        var state = Text(item, "indexState") switch
        {
            "rebuilding" => EffectIndexState.Rebuilding,
            "ready" => EffectIndexState.Ready,
            _ => throw InvalidRecord(),
        };
        long? count = item.ContainsKey("unfinishedCount") ? Number(item, "unfinishedCount") : null;
        if ((state == EffectIndexState.Ready && count is not >= 0) ||
            (state == EffectIndexState.Rebuilding && count is not null))
        {
            throw InvalidRecord();
        }

        long epoch = Number(item, "ownerEpoch");
        long protocol = Number(item, "writerProtocol");
        if (epoch <= 0 || protocol is <= 0 or > int.MaxValue)
        {
            throw InvalidRecord();
        }
        return new EffectIndexHead(Revision(item), GuidValue(item, "indexGeneration"), epoch, (int)protocol, state, count);
    }

    /// <summary>Attempts a lifecycle effect update once for a fresh transition ID.</summary>
    /// <remarks>
    /// Use a strong effect read to reconcile the transition ID after an indeterminate result
    /// or cancellation during the request before making a new decision.
    /// </remarks>
    internal Task<LifecycleWriteOutcome> WriteEffectAsync(
        LifecycleProfile profile,
        DeploymentLeaseFence lease,
        Guid transitionId,
        EffectWrite write,
        CancellationToken cancellationToken) =>
        WriteEffectAsync(profile, lease, transitionId, write, LeaseAccess.Active, cancellationToken);

    internal Task<LifecycleWriteOutcome> WriteRecoveredEffectAsync(
        LifecycleProfile profile,
        DeploymentLeaseFence lease,
        Guid transitionId,
        EffectWrite write,
        CancellationToken cancellationToken) =>
        WriteEffectAsync(profile, lease, transitionId, write, LeaseAccess.Recovery, cancellationToken);

    internal Task<LifecycleWriteOutcome> WriteDispatchedEffectAsync(
        LifecycleProfile profile,
        DeploymentLeaseFence lease,
        Guid transitionId,
        EffectWrite write,
        EffectDispatchGuard guard,
        DateTimeOffset attemptDeadline,
        CancellationToken cancellationToken,
        EffectJournalSnapshot? replayOf = null) =>
        WriteEffectAsync(profile, lease, transitionId, write, LeaseAccess.Active, cancellationToken, guard, attemptDeadline,
            exactPrevious: replayOf);

    internal Task<LifecycleWriteOutcome> WriteExactEffectAsync(
        DeploymentLeaseFence lease, Guid transitionId, EffectJournalSnapshot expected, EffectJournalEntry replacement,
        DateTimeOffset? leaseValidThrough, CancellationToken cancellationToken) =>
        WriteEffectAsync(replacement.Envelope.Profile, lease, transitionId,
            new EffectWrite(replacement.Envelope.IntentId, expected.Revision, replacement.State, replacement.WritePayload()),
            LeaseAccess.Active, cancellationToken, leaseValidThrough: leaseValidThrough, exactPrevious: expected);

    private async Task<LifecycleWriteOutcome> WriteEffectAsync(
        LifecycleProfile profile,
        DeploymentLeaseFence lease,
        Guid transitionId,
        EffectWrite write,
        LeaseAccess leaseAccess,
        CancellationToken cancellationToken,
        EffectDispatchGuard? guard = null,
        DateTimeOffset? leaseValidThrough = null,
        EffectJournalSnapshot? exactPrevious = null,
        CatalogProfile? requiredCatalog = null,
        IReadOnlyList<TransactionAction>? accompanying = null,
        Guid? requestToken = null)
    {
        RequireProfileLease(profile, lease);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Payload);
        if (!Enum.IsDefined(write.State) || (write.State == EffectState.Settled && write.DueAt is not null))
        {
            throw new ArgumentException("Only an unfinished operation may have a due time.", nameof(write));
        }

        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Ready } || !MatchesLease(head, lease))
        {
            return LifecycleWriteOutcome.RebuildRequired;
        }

        var operationKey = LifecycleKey.Operation(profile, write.IntentId);
        var existing = await ReadEffectAsync(profile, write.IntentId, cancellationToken).ConfigureAwait(false);
        if (existing?.Revision != write.ExpectedRevision)
        {
            return LifecycleWriteOutcome.Conflict;
        }
        if (guard is not null && (head != guard.Index || existing?.State != (exactPrevious?.Entry.State ?? EffectState.Planned)))
        {
            return LifecycleWriteOutcome.Conflict;
        }
        if (leaseAccess == LeaseAccess.Recovery &&
            (write.State != EffectState.Settled || existing?.State is not (EffectState.Executing or EffectState.Uncertain)))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        bool wasUnfinished = existing?.IsUnfinished == true;
        bool isUnfinished = write.State != EffectState.Settled;
        long count = head.UnfinishedCount!.Value;
        if (wasUnfinished && count == 0)
        {
            if (await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false) != head)
            {
                return LifecycleWriteOutcome.Conflict;
            }
            throw InvalidRecord();
        }

        int delta = Convert.ToInt32(isUnfinished) - Convert.ToInt32(wasUnfinished);
        long newCount = checked(count + delta);
        var nextIndex = head with { Revision = NextRevision(head.Revision), UnfinishedCount = newCount };
        var operation = Record(operationKey, profile.Deployment, NextRevision(write.ExpectedRevision), transitionId);
        operation.Add("effectState", StringValue(EffectStateValue(write.State)));
        operation.Add("payload", StringValue(write.Payload));
        if (write.DueAt is { } due)
        {
            operation.Add("dueShard", StringValue(ShardKey(profile.Deployment, Shard(operationKey))));
            operation.Add("dueAtMillis", NumberValue(due.ToUnixTimeMilliseconds()));
        }

        var leaseCondition = LeaseCondition(lease, leaseAccess, leaseValidThrough);
        var writeOperation = CompareAndPut(operationKey, operation, write.ExpectedRevision, profile.Deployment);
        if (exactPrevious is not null)
        {
            var put = writeOperation.Write.Put;
            put.ConditionExpression += " AND transitionId = :previousTransition AND effectState = :previousState AND payload = :previousPayload";
            put.ExpressionAttributeValues[":previousTransition"] = StringValue(exactPrevious.TransitionId.ToString("D"));
            put.ExpressionAttributeValues[":previousState"] = StringValue(EffectStateValue(exactPrevious.Entry.State));
            put.ExpressionAttributeValues[":previousPayload"] = StringValue(exactPrevious.Entry.WritePayload());
        }
        var actions = new List<TransactionAction>
        {
            leaseCondition,
            writeOperation,
            WriteIndex(profile, nextIndex, head.Revision, transitionId),
        };
        if (guard is not null)
        {
            RequireAppliedCatalog(leaseCondition, guard.Catalog);
            actions.Add(CatalogProfileCondition(guard.Catalog));
            actions.Add(RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Runner(profile, guard.AllocationId), guard.AllocationRevision)));
            if (guard.Handoff is not null)
            {
                actions.AddRange(BuildHandoffBeginConditions(guard.Handoff));
            }
            if (guard.Removal is { } removal)
            {
                actions.Add(ExactJitHandoffCondition(removal));
                actions.Add(OfferAbsentCondition(removal));
            }
            if (guard.Ec2Lineage is { } lineage)
            {
                actions.Add(ExactEc2LineageCondition(lineage));
            }
        }
        if (requiredCatalog is not null)
        {
            RequireAppliedCatalog(leaseCondition, requiredCatalog);
            actions.Add(CatalogProfileCondition(requiredCatalog));
        }
        if (accompanying is not null)
        {
            actions.AddRange(accompanying);
        }
        if (delta > 0)
        {
            actions.Add(CreateMarker(profile, head.Generation, write.IntentId, transitionId));
        }
        else if (delta < 0)
        {
            actions.Add(DeleteMarker(profile, head.Generation, write.IntentId));
        }
        // Reusing a durable transition can require a fresh transaction bound to the current index revision.
        return await WriteAsync(actions, requestToken ?? (exactPrevious is null ? transitionId : Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActiveEffectPage?> ReadActiveOperationsAsync(
        LifecycleProfile profile,
        DeploymentLeaseFence lease,
        ActiveEffectCursor? continuation,
        CancellationToken cancellationToken)
    {
        RequireProfileLease(profile, lease);
        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Ready } || !MatchesLease(head, lease))
        {
            return null;
        }

        string prefix = LifecycleKey.ActiveOperationPrefix(head.Generation);
        long count = head.UnfinishedCount!.Value;
        if (continuation is { } cursor &&
            (cursor.Generation != head.Generation || cursor.IndexRevision != head.Revision ||
             cursor.SeenCount <= 0 || cursor.SeenCount > count ||
             !cursor.AfterSortKey.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return null;
        }

        var page = await ReadBasePageAsync(profile.PartitionKey, prefix, continuation?.AfterSortKey, cancellationToken).ConfigureAwait(false);
        var current = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (current != head)
        {
            return null;
        }

        long seen = (continuation?.SeenCount ?? 0) + page.Items.Count;
        if (seen < 0 || seen > count || (page.Continuation is null && seen != count))
        {
            return null;
        }

        var next = page.Continuation is { } after ? new ActiveEffectCursor(head.Generation, head.Revision, after, seen) : null;
        return new ActiveEffectPage(ReadMarkerIntents(profile, head.Generation, page), next);
    }

    internal async Task<EffectCandidatePage?> ReadEffectCandidatesAsync(
        LifecycleProfile profile,
        DeploymentLeaseFence lease,
        EffectCandidateCursor? continuation,
        CancellationToken cancellationToken)
    {
        RequireProfileLease(profile, lease);
        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Ready } || !MatchesLease(head, lease))
        {
            return null;
        }
        string prefix = LifecycleKey.ActiveOperationPrefix(head.Generation);
        if (continuation is { } cursor && (cursor.Generation != head.Generation ||
            !cursor.AfterSortKey.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return null;
        }
        var page = await ReadBasePageAsync(profile.PartitionKey, prefix, continuation?.AfterSortKey, cancellationToken).ConfigureAwait(false);
        var current = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (current is not { State: EffectIndexState.Ready } || current.Generation != head.Generation || !MatchesLease(current, lease))
        {
            return null;
        }

        // The marker page supplies work candidates only. A changing count cannot prove absence.
        var next = page.Continuation is { } after ? new EffectCandidateCursor(head.Generation, after) : null;
        return new EffectCandidatePage(ReadMarkerIntents(profile, head.Generation, page), next);
    }

    private static ImmutableArray<string> ReadMarkerIntents(LifecycleProfile profile, Guid generation, BasePage page)
    {
        var candidates = ImmutableArray.CreateBuilder<string>(page.Items.Count);
        foreach (var marker in page.Items)
        {
            string intent = Text(marker, "intentId");
            var markerKey = LifecycleKey.ActiveOperation(profile, generation, intent);
            RequireKey(marker, markerKey.PartitionKey, markerKey.SortKey);
            CheckNoExpiration(marker);
            if (GuidValue(marker, "indexGeneration") != generation || Text(marker, "deployment") != profile.Deployment)
            {
                throw InvalidRecord();
            }
            candidates.Add(intent);
        }
        return candidates.MoveToImmutable();
    }

    public async Task<DueEffectHintPage> ReadDueHintsAsync(
        string deployment,
        int shard,
        DateTimeOffset now,
        DueEffectHint? continuation,
        CancellationToken cancellationToken)
    {
        _ = LifecycleKey.Lease(deployment);
        if (shard is < 0 or >= DueShardCount)
        {
            throw new ArgumentOutOfRangeException(nameof(shard));
        }
        string shardKey = ShardKey(deployment, shard);
        if (continuation is { } cursor && cursor.Shard != shardKey)
        {
            throw new ArgumentException("The cursor does not belong to this due shard.", nameof(continuation));
        }

        var reply = await _client.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            IndexName = "due",
            Limit = HistoryPageSize,
            KeyConditionExpression = "dueShard = :shard AND dueAtMillis <= :now",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":shard"] = StringValue(shardKey),
                [":now"] = NumberValue(now.ToUnixTimeMilliseconds()),
            },
            ExclusiveStartKey = continuation is null ? null : DueCursor(continuation),
        }, cancellationToken).ConfigureAwait(false);

        var hints = (reply.Items ?? []).Select(item => DecodeHint(item, shardKey)).ToImmutableArray();
        var next = reply.LastEvaluatedKey is { Count: > 0 } key ? DecodeHint(key, shardKey) : null;
        return new DueEffectHintPage(hints, next);
    }

    public async Task<StoredEffect?> ResolveDueHintAsync(
        LifecycleProfile profile, DueEffectHint hint, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (hint.PartitionKey != profile.PartitionKey || !hint.SortKey.StartsWith(OperationPrefix, StringComparison.Ordinal) ||
            hint.SortKey.Length == OperationPrefix.Length)
        {
            return null;
        }

        string intentId = hint.SortKey[OperationPrefix.Length..];
        var current = await ReadEffectAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
        return current is { IsUnfinished: true, DueAt: { } due } && due <= now ? current : null;
    }

    private TransactionAction WriteIndex(
        LifecycleProfile profile, EffectIndexHead head, long? expectedRevision, Guid transitionId)
    {
        var key = LifecycleKey.Effects(profile);
        var item = Record(key, profile.Deployment, head.Revision, transitionId);
        item.Add("indexGeneration", StringValue(head.Generation.ToString("D")));
        item.Add("ownerEpoch", NumberValue(head.OwnerEpoch));
        item.Add("writerProtocol", NumberValue(head.WriterProtocol));
        item.Add("indexState", StringValue(head.State == EffectIndexState.Ready ? "ready" : "rebuilding"));
        if (head.UnfinishedCount is long count)
        {
            item.Add("unfinishedCount", NumberValue(count));
        }
        return CompareAndPut(key, item, expectedRevision, profile.Deployment);
    }

    private TransactionAction CreateMarker(LifecycleProfile profile, Guid generation, string intentId, Guid transitionId)
    {
        var key = LifecycleKey.ActiveOperation(profile, generation, intentId);
        var marker = Record(key, profile.Deployment, 1, transitionId);
        marker.Add("indexGeneration", StringValue(generation.ToString("D")));
        marker.Add("intentId", StringValue(intentId));
        return CompareAndPut(key, marker, null, profile.Deployment);
    }

    private TransactionAction DeleteMarker(LifecycleProfile profile, Guid generation, string intentId)
    {
        var key = LifecycleKey.ActiveOperation(profile, generation, intentId);
        var delete = new Delete
        {
            TableName = _tableName,
            Key = Key(key),
            ConditionExpression = "deployment = :deployment AND indexGeneration = :generation AND intentId = :intent",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":deployment"] = StringValue(profile.Deployment),
                [":generation"] = StringValue(generation.ToString("D")),
                [":intent"] = StringValue(intentId),
            },
        };
        return new TransactionAction(key, new TransactWriteItem { Delete = delete });
    }

    private static StoredEffect DecodeEffect(LifecycleProfile profile, Dictionary<string, AttributeValue> item)
    {
        string sort = Text(item, "SK");
        if (Text(item, "PK") != profile.PartitionKey || Text(item, "deployment") != profile.Deployment ||
            !sort.StartsWith(OperationPrefix, StringComparison.Ordinal) || sort.Length == OperationPrefix.Length)
        {
            throw InvalidRecord();
        }

        CheckNoExpiration(item);
        var state = Text(item, "effectState") switch
        {
            "planned" => EffectState.Planned,
            "executing" => EffectState.Executing,
            "uncertain" => EffectState.Uncertain,
            "settled" => EffectState.Settled,
            _ => throw InvalidRecord(),
        };
        var due = OptionalTimestamp(item, "dueAtMillis");
        if (due.HasValue != item.ContainsKey("dueShard") || (state == EffectState.Settled && due.HasValue))
        {
            throw InvalidRecord();
        }
        return new StoredEffect(sort[OperationPrefix.Length..], Revision(item), GuidValue(item, "transitionId"), state,
            Text(item, "payload"), due);
    }

    private static string EffectStateValue(EffectState state) =>
        state switch
        {
            EffectState.Planned => "planned",
            EffectState.Executing => "executing",
            EffectState.Uncertain => "uncertain",
            EffectState.Settled => "settled",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static bool MatchesLease(EffectIndexHead head, DeploymentLeaseFence lease) =>
        head.OwnerEpoch == lease.Epoch && head.WriterProtocol == lease.WriterProtocol;

    private static void RequireProfileLease(LifecycleProfile profile, DeploymentLeaseFence lease)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(lease);
        if (profile.Deployment != lease.Deployment)
        {
            throw new ArgumentException("A profile must belong to its lease deployment.", nameof(lease));
        }
    }

    private static void CheckNoExpiration(Dictionary<string, AttributeValue> item)
    {
        if (item.ContainsKey("expiresAt"))
        {
            throw InvalidRecord();
        }
    }

    private static DueEffectHint DecodeHint(Dictionary<string, AttributeValue> item, string shard)
    {
        if (Text(item, "dueShard") != shard)
        {
            throw InvalidRecord();
        }
        return new DueEffectHint(Text(item, "PK"), Text(item, "SK"), shard,
            DateTimeOffset.FromUnixTimeMilliseconds(Number(item, "dueAtMillis")));
    }

    private static Dictionary<string, AttributeValue> DueCursor(DueEffectHint cursor)
    {
        var key = Key(cursor.PartitionKey, cursor.SortKey);
        key.Add("dueShard", StringValue(cursor.Shard));
        key.Add("dueAtMillis", NumberValue(cursor.DueAt.ToUnixTimeMilliseconds()));
        return key;
    }

    private static string ShardKey(string deployment, int shard) =>
        deployment + "#" + shard.ToString(CultureInfo.InvariantCulture);

    private static int Shard(LifecycleKey key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.PartitionKey + "/" + key.SortKey));
        return hash[0] % DueShardCount;
    }
}
