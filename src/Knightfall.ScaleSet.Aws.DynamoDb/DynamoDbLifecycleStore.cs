using System.Globalization;
using System.Net;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    private const int MaximumTransactionItems = 100;
    private const int HistoryPageSize = 32;
    private const int HistoryPagesPerPass = 2;

    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly TimeProvider _timeProvider;

    public DynamoDbLifecycleStore(IAmazonDynamoDB client, string tableName, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (client.Config.MaxErrorRetry != 0 || client.Config.MaxStaleConnectionRetries != 0)
        {
            throw new ArgumentException("Lifecycle transactions require an AWS client with both retry budgets disabled.", nameof(client));
        }

        _client = client;
        _tableName = tableName;
        _timeProvider = timeProvider;
    }

    public async Task<LifecycleRecord?> ReadRecordAsync(LifecycleKey key, CancellationToken cancellationToken)
    {
        RequireOrdinaryKey(key);
        var item = await ReadItemAsync(key, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var expiresAt = OptionalTimestamp(item, "expiresAt", seconds: true);
        if (expiresAt is not null && !AllowsHistoryRetention(key.Kind))
        {
            throw InvalidRecord();
        }
        string? payload = expiresAt <= _timeProvider.GetUtcNow() ? null : Text(item, "payload");
        return new LifecycleRecord(key, Revision(item), GuidValue(item, "transitionId"), payload, expiresAt);
    }

    /// <summary>Attempts a record transaction once for a fresh transition ID.</summary>
    /// <remarks>
    /// After an indeterminate result, or cancellation during the request, strongly read the affected rows and
    /// reconcile their transition IDs before making a new decision. A retry would use a new lease deadline test
    /// and would change the request bound to the original token.
    /// </remarks>
    public async Task<LifecycleWriteOutcome> WriteRecordsAsync(
        DeploymentLeaseFence lease,
        Guid transitionId,
        IReadOnlyList<LifecycleRecordWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0 || writes.Count >= MaximumTransactionItems)
        {
            throw new ArgumentOutOfRangeException(nameof(writes), "A transaction allows 1–99 records plus the lease check.");
        }

        var actions = new List<TransactionAction>(writes.Count + 1) { LeaseCondition(lease) };
        foreach (var write in writes)
        {
            RequireOrdinaryKey(write.Key);
            if (write.Key.Deployment is string deployment && deployment != lease.Deployment)
            {
                throw new ArgumentException("A write must belong to its lease deployment.", nameof(writes));
            }
            ArgumentNullException.ThrowIfNull(write.Payload);
            var item = Record(write.Key, lease.Deployment, NextRevision(write.ExpectedRevision), transitionId);
            item.Add("payload", StringValue(write.Payload));
            if (write.Key.Kind == LifecycleItemKind.Profile)
            {
                item.Add(CatalogProfileIdentity, StringValue(write.Key.SortKey[CatalogProfilePrefix.Length..]));
            }

            if (write.HistoryRetention is { } retention)
            {
                if (!AllowsHistoryRetention(write.Key.Kind) || retention.Retention <= TimeSpan.Zero ||
                    retention.SettledAt > _timeProvider.GetUtcNow())
                {
                    throw new ArgumentException("Only settled message, job, or acquisition history may have an expiration.", nameof(writes));
                }

                var expiresAt = retention.SettledAt + retention.Retention;
                long seconds = expiresAt.ToUnixTimeSeconds();
                if (DateTimeOffset.FromUnixTimeSeconds(seconds) < expiresAt)
                {
                    seconds++;
                }
                item.Add("expiresAt", NumberValue(seconds));
            }

            long? expirationCheck = AllowsHistoryRetention(write.Key.Kind) ? _timeProvider.GetUtcNow().ToUnixTimeSeconds() : null;
            actions.Add(CompareAndPut(write.Key, item, write.ExpectedRevision, lease.Deployment, expirationCheck));
        }

        return await WriteAsync(actions, transitionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, AttributeValue>?> ReadItemAsync(LifecycleKey key, CancellationToken cancellationToken)
    {
        var reply = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = Key(key),
            ConsistentRead = true,
        }, cancellationToken).ConfigureAwait(false);

        if (reply.Item is not { Count: > 0 } item)
        {
            return null;
        }

        RequireKey(item, key.PartitionKey, key.SortKey);
        if (key.Deployment is string deployment && Text(item, "deployment") != deployment)
        {
            throw InvalidRecord();
        }
        return item;
    }

    private async Task<BasePage> ReadBasePageAsync(
        string partition,
        string prefix,
        string? after,
        CancellationToken cancellationToken)
    {
        if (after?.StartsWith(prefix, StringComparison.Ordinal) == false)
        {
            throw new ArgumentException("The cursor does not belong to this record prefix.", nameof(after));
        }
        var reply = await _client.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            ConsistentRead = true,
            Limit = HistoryPageSize,
            ScanIndexForward = true,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :prefix)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":pk"] = StringValue(partition),
                [":prefix"] = StringValue(prefix),
            },
            ExclusiveStartKey = after is null ? null : Key(partition, after),
        }, cancellationToken).ConfigureAwait(false);

        if (reply.Items is { Count: > HistoryPageSize })
        {
            throw InvalidRecord();
        }

        string? next = null;
        if (reply.LastEvaluatedKey is { Count: > 0 } cursor)
        {
            next = Text(cursor, "SK");
            RequireKey(cursor, partition, next);
            if (!next.StartsWith(prefix, StringComparison.Ordinal) || next == after)
            {
                throw InvalidRecord();
            }
        }

        return new BasePage(reply.Items ?? [], next);
    }

    private TransactionAction LeaseCondition(DeploymentLeaseFence lease, LeaseAccess access = LeaseAccess.Active)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.OwnerId);
        if (!IsTaskArn(lease.OwnerTaskArn))
        {
            throw new ArgumentException("An exact ECS task ARN is required.", nameof(lease));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lease.Epoch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lease.WriterProtocol);

        var key = LifecycleKey.Lease(lease.Deployment);
        var check = new ConditionCheck
        {
            TableName = _tableName,
            Key = Key(key),
            ConditionExpression = "#owner = :owner AND #task = :task AND #epoch = :epoch AND #protocol = :protocol AND #until > :now",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["#owner"] = "ownerId",
                ["#task"] = "ownerTaskArn",
                ["#epoch"] = "epoch",
                ["#protocol"] = "writerProtocol",
                ["#until"] = "validUntil",
                ["#mode"] = "leaseMode",
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":owner"] = StringValue(lease.OwnerId),
                [":task"] = StringValue(lease.OwnerTaskArn),
                [":epoch"] = NumberValue(lease.Epoch),
                [":protocol"] = NumberValue(lease.WriterProtocol),
                [":now"] = NumberValue(ConservativeLatest(DateTimeOffset.MinValue).ToUnixTimeMilliseconds()),
            },
        };
        if (access == LeaseAccess.ActiveOrRecovery)
        {
            check.ConditionExpression += " AND (#mode = :active OR #mode = :recovery)";
            check.ExpressionAttributeValues[":active"] = StringValue(LeaseModeValue(ControllerLeaseMode.Active));
            check.ExpressionAttributeValues[":recovery"] = StringValue(LeaseModeValue(ControllerLeaseMode.RecoveryOnly));
        }
        else
        {
            check.ConditionExpression += " AND #mode = :mode";
            var mode = access == LeaseAccess.Active ? ControllerLeaseMode.Active : ControllerLeaseMode.RecoveryOnly;
            check.ExpressionAttributeValues[":mode"] = StringValue(LeaseModeValue(mode));
        }
        return new TransactionAction(key, new TransactWriteItem { ConditionCheck = check });
    }

    private TransactionAction CompareAndPut(
        LifecycleKey key,
        Dictionary<string, AttributeValue> item,
        long? expectedRevision,
        string deployment,
        long? expirationCheck = null)
    {
        var put = new Put { TableName = _tableName, Item = item, ConditionExpression = "attribute_not_exists(PK)" };
        if (expectedRevision is long revision)
        {
            put.ConditionExpression = "#revision = :revision AND #deployment = :deployment";
            put.ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["#revision"] = "revision",
                ["#deployment"] = "deployment",
            };
            put.ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":revision"] = NumberValue(revision),
                [":deployment"] = StringValue(deployment),
            };
            if (expirationCheck is long now)
            {
                put.ConditionExpression += " AND (attribute_not_exists(#expires) OR #expires > :expiryNow)";
                put.ExpressionAttributeNames["#expires"] = "expiresAt";
                put.ExpressionAttributeValues[":expiryNow"] = NumberValue(now);
            }
        }
        return new TransactionAction(key, new TransactWriteItem { Put = put });
    }

    private async Task<LifecycleWriteOutcome> WriteAsync(
        IReadOnlyList<TransactionAction> actions,
        Guid transitionId,
        CancellationToken cancellationToken)
    {
        if (transitionId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty transaction token is required.", nameof(transitionId));
        }

        var keys = new HashSet<(string Partition, string Sort)>();
        if (actions.Count is 0 or > MaximumTransactionItems)
        {
            throw new ArgumentException("Invalid DynamoDB transaction item count.", nameof(actions));
        }
        foreach (var action in actions)
        {
            if (!keys.Add((action.Key.PartitionKey, action.Key.SortKey)))
            {
                throw new ArgumentException("Each DynamoDB transaction action must target a distinct key.", nameof(actions));
            }
        }

        try
        {
            await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                ClientRequestToken = transitionId.ToString("D"),
                TransactItems = actions.Select(action => action.Write).ToList(),
            }, cancellationToken).ConfigureAwait(false);
            return LifecycleWriteOutcome.Committed;
        }
        catch (TransactionCanceledException error)
        {
            if (error.CancellationReasons is not { } reasons || reasons.Count != actions.Count)
            {
                return LifecycleWriteOutcome.Indeterminate;
            }
            if (actions[0].Write.ConditionCheck is not null && actions[0].Key.Kind == LifecycleItemKind.Lease &&
                reasons[0].Code == "ConditionalCheckFailed")
            {
                return LifecycleWriteOutcome.LeaseRejected;
            }
            if (reasons.Any(reason => reason.Code is "ConditionalCheckFailed" or "TransactionConflict") &&
                reasons.All(reason => reason.Code is "None" or "ConditionalCheckFailed" or "TransactionConflict"))
            {
                return LifecycleWriteOutcome.Conflict;
            }
            return LifecycleWriteOutcome.Indeterminate;
        }
        catch (AmazonDynamoDBException error) when (error.StatusCode >= HttpStatusCode.InternalServerError ||
                                                   error.ErrorCode is "TransactionInProgressException" or "IdempotentParameterMismatchException")
        {
            return LifecycleWriteOutcome.Indeterminate;
        }
        catch (HttpRequestException)
        {
            return LifecycleWriteOutcome.Indeterminate;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LifecycleWriteOutcome.Indeterminate;
        }
    }

    private static void RequireOrdinaryKey(LifecycleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Kind is LifecycleItemKind.Lease or LifecycleItemKind.Operation or LifecycleItemKind.ActiveOperation or
            LifecycleItemKind.Effects or LifecycleItemKind.EffectRebuild or LifecycleItemKind.Hello or LifecycleItemKind.Offer or
            LifecycleItemKind.Status)
        {
            throw new ArgumentException("This lifecycle key requires its dedicated protocol.", nameof(key));
        }
    }

    private static bool AllowsHistoryRetention(LifecycleItemKind kind) =>
        kind is LifecycleItemKind.Message or LifecycleItemKind.Job or LifecycleItemKind.Acquisition;

    private static long NextRevision(long? expected)
    {
        if (expected is long value)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            return checked(value + 1);
        }
        return 1;
    }

    private static Dictionary<string, AttributeValue> Record(LifecycleKey key, string deployment, long revision, Guid transitionId)
    {
        var item = Key(key);
        item.Add("deployment", StringValue(deployment));
        item.Add("revision", NumberValue(revision));
        item.Add("transitionId", StringValue(transitionId.ToString("D")));
        return item;
    }

    private static Dictionary<string, AttributeValue> Key(LifecycleKey key) => Key(key.PartitionKey, key.SortKey);

    private static Dictionary<string, AttributeValue> Key(string partition, string sort) =>
        new(StringComparer.Ordinal) { ["PK"] = StringValue(partition), ["SK"] = StringValue(sort) };

    private static AttributeValue StringValue(string value) => new() { S = value };

    private static AttributeValue NumberValue(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static void RequireKey(Dictionary<string, AttributeValue> item, string partition, string sort)
    {
        if (Text(item, "PK") != partition || Text(item, "SK") != sort)
        {
            throw InvalidRecord();
        }
    }

    private static string Text(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.S is string text ? text : throw InvalidRecord();

    private static long Number(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && long.TryParse(value.N, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
            ? number
            : throw InvalidRecord();

    private static long Revision(Dictionary<string, AttributeValue> item)
    {
        long revision = Number(item, "revision");
        return revision > 0 ? revision : throw InvalidRecord();
    }

    private static Guid GuidValue(Dictionary<string, AttributeValue> item, string name) =>
        Guid.TryParseExact(Text(item, name), "D", out Guid id) && id != Guid.Empty ? id : throw InvalidRecord();

    private static DateTimeOffset? OptionalTimestamp(Dictionary<string, AttributeValue> item, string name, bool seconds = false)
    {
        if (!item.ContainsKey(name))
        {
            return null;
        }
        long value = Number(item, name);
        return seconds ? DateTimeOffset.FromUnixTimeSeconds(value) : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    private static InvalidDataException InvalidRecord() => new("Invalid durable lifecycle record or continuation.");

    private sealed record TransactionAction(LifecycleKey Key, TransactWriteItem Write);

    private sealed record BasePage(List<Dictionary<string, AttributeValue>> Items, string? Continuation);

    private enum LeaseAccess
    {
        Active = 0,
        Recovery = 1,
        ActiveOrRecovery = 2,
    }
}
