using System.Text.RegularExpressions;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    public async Task<ControllerLease?> ReadLeaseAsync(string deployment, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.Lease(deployment), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        CheckNoExpiration(item);
        long epoch = Number(item, "epoch");
        string ownerId = Text(item, "ownerId");
        string ownerTask = Text(item, "ownerTaskArn");
        string? requiredTask = item.ContainsKey("requiredFencingTaskArn") ? Text(item, "requiredFencingTaskArn") : null;
        var mode = Text(item, "leaseMode") switch
        {
            "recovery-only" => ControllerLeaseMode.RecoveryOnly,
            "active" => ControllerLeaseMode.Active,
            "released" => ControllerLeaseMode.Released,
            _ => throw InvalidRecord(),
        };
        var previous = ReadPredecessor(item);
        if (epoch <= 0 || string.IsNullOrWhiteSpace(ownerId) || !IsTaskArn(ownerTask) ||
            (requiredTask is not null && (!IsTaskArn(requiredTask) || mode != ControllerLeaseMode.RecoveryOnly)) ||
            (epoch == 1 ? previous is not null : previous?.Epoch != epoch - 1))
        {
            throw InvalidRecord();
        }

        var lease = new ControllerLease(deployment, ownerId, ownerTask, epoch, ReadWriterProtocol(item, "writerProtocol"),
            OptionalTimestamp(item, "validUntil") ?? throw InvalidRecord(), Revision(item), GuidValue(item, "transitionId"),
            mode, previous, requiredTask);
        return ReadLeaseAcquisitionBounds(lease, item);
    }

    public async Task<ControllerLeaseChange> TryAcquireLeaseAsync(
        string deployment,
        string ownerId,
        string ownerTaskArn,
        int writerProtocol,
        DateTimeOffset earliestNow,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(writerProtocol);
        if (!IsTaskArn(ownerTaskArn))
        {
            throw new ArgumentException("An exact ECS task ARN is required.", nameof(ownerTaskArn));
        }

        using var telemetry = DynamoDbDiagnostics.StartLease("lease-acquire", cancellationToken);
        var existing = await ReadLeaseAsync(deployment, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        // Never use a time bound more permissive than the store's local observation.
        var expiredBy = AtMillisecond(earliestNow < now ? earliestNow : now);
        var deadline = AtMillisecond(validUntil);
        if (deadline <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(validUntil), "Lease validity must extend beyond the current time.");
        }
        if (existing is not null &&
            (existing.OwnerId == ownerId ||
             (existing.Mode != ControllerLeaseMode.Released && existing.ValidUntil > expiredBy)))
        {
            return telemetry.Complete(new ControllerLeaseChange(LifecycleWriteOutcome.LeaseRejected, existing));
        }

        ControllerLeasePredecessor? previous = null;
        string? requiredTask = null;
        DateTimeOffset? requiredAcquiredAt = null;
        if (existing is not null)
        {
            bool clean = existing.Mode == ControllerLeaseMode.Released;
            previous = new ControllerLeasePredecessor(existing.OwnerId, existing.OwnerTaskArn, existing.Epoch,
                existing.WriterProtocol, clean) { AcquiredAt = existing.AcquiredAt };
            // Recovery-only owners could not dispatch external effects. Preserve the last potentially
            // active writer if a recovery owner is replaced before that writer has been isolated.
            requiredTask = existing.Mode == ControllerLeaseMode.Active
                ? existing.OwnerTaskArn
                : existing.RequiredFencingTaskArn;
            requiredAcquiredAt = existing.Mode == ControllerLeaseMode.Active
                ? existing.AcquiredAt
                : existing.RequiredFencingTaskAcquiredAt;
        }

        var acquired = new ControllerLease(deployment, ownerId, ownerTaskArn, NextRevision(existing?.Epoch),
            writerProtocol, deadline, NextRevision(existing?.Revision), Guid.NewGuid(),
            ControllerLeaseMode.RecoveryOnly, previous, requiredTask)
        {
            AcquiredAt = AtMillisecond(now),
            RequiredFencingTaskAcquiredAt = requiredAcquiredAt,
        };
        return telemetry.Complete(await WriteLeaseChangeAsync(acquired, existing, null,
            existing?.Mode == ControllerLeaseMode.Released ? null : expiredBy, cancellationToken).ConfigureAwait(false));
    }

    public Task<ControllerLeaseChange> RenewLeaseAsync(
        ControllerLease expected,
        DateTimeOffset latestNow,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken) =>
        RenewLeaseAsync(expected, latestNow, validUntil, markWriteStarted: null, cancellationToken);

    internal async Task<ControllerLeaseChange> RenewLeaseAsync(
        ControllerLease expected,
        DateTimeOffset latestNow,
        DateTimeOffset validUntil,
        Action? markWriteStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var telemetry = DynamoDbDiagnostics.StartLease("lease-renew", cancellationToken);
        var deadline = AtMillisecond(validUntil);
        if (expected.Mode == ControllerLeaseMode.Released || deadline <= expected.ValidUntil || deadline <= latestNow)
        {
            return telemetry.Complete(new ControllerLeaseChange(LifecycleWriteOutcome.LeaseRejected, null));
        }

        if (await ReadLeaseAsync(expected.Deployment, cancellationToken).ConfigureAwait(false) != expected)
        {
            return telemetry.Complete(new ControllerLeaseChange(LifecycleWriteOutcome.Conflict, null));
        }
        var renewed = expected with { ValidUntil = deadline, Revision = NextRevision(expected.Revision), TransitionId = Guid.NewGuid() };
        cancellationToken.ThrowIfCancellationRequested();
        markWriteStarted?.Invoke();
        return telemetry.Complete(await WriteLeaseChangeAsync(renewed, expected, ConservativeLatest(latestNow), null, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Releases an active lease after the owner has quiesced all external sends.</summary>
    public Task<ControllerLeaseChange> ReleaseLeaseAsync(
        ControllerLease expected, DateTimeOffset latestNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Mode != ControllerLeaseMode.Active)
        {
            return Task.FromResult(new ControllerLeaseChange(LifecycleWriteOutcome.LeaseRejected, null));
        }

        return ChangeLeaseModeAsync(expected, ControllerLeaseMode.Released, latestNow, cancellationToken);
    }

    internal Task<ControllerLeaseChange> PromoteLeaseAsync(
        ControllerLease expected, DateTimeOffset latestNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Mode != ControllerLeaseMode.RecoveryOnly)
        {
            return Task.FromResult(new ControllerLeaseChange(LifecycleWriteOutcome.LeaseRejected, null));
        }

        return ChangeLeaseModeAsync(expected, ControllerLeaseMode.Active, latestNow, cancellationToken);
    }

    private Task<ControllerLeaseChange> ChangeLeaseModeAsync(
        ControllerLease expected, ControllerLeaseMode mode, DateTimeOffset latestNow, CancellationToken cancellationToken)
    {
        var changed = expected with
        {
            Mode = mode,
            RequiredFencingTaskArn = null,
            RequiredFencingTaskAcquiredAt = null,
            Revision = NextRevision(expected.Revision),
            TransitionId = Guid.NewGuid(),
        };
        return WriteLeaseChangeAsync(changed, expected, ConservativeLatest(latestNow), null, cancellationToken);
    }

    private async Task<ControllerLeaseChange> WriteLeaseChangeAsync(
        ControllerLease replacement,
        ControllerLease? expected,
        DateTimeOffset? validAfter,
        DateTimeOffset? expiredBy,
        CancellationToken cancellationToken)
    {
        var key = LifecycleKey.Lease(replacement.Deployment);
        var item = Record(key, replacement.Deployment, replacement.Revision, replacement.TransitionId);
        item.Add("ownerId", StringValue(replacement.OwnerId));
        item.Add("ownerTaskArn", StringValue(replacement.OwnerTaskArn));
        item.Add("epoch", NumberValue(replacement.Epoch));
        item.Add("writerProtocol", NumberValue(replacement.WriterProtocol));
        item.Add("validUntil", NumberValue(replacement.ValidUntil.ToUnixTimeMilliseconds()));
        item.Add("leaseMode", StringValue(LeaseModeValue(replacement.Mode)));
        if (replacement.RequiredFencingTaskArn is string requiredTask)
        {
            item.Add("requiredFencingTaskArn", StringValue(requiredTask));
        }
        if (replacement.PreviousOwner is { } previous)
        {
            item.Add("previousOwnerId", StringValue(previous.OwnerId));
            item.Add("previousOwnerTaskArn", StringValue(previous.OwnerTaskArn));
            item.Add("previousOwnerEpoch", NumberValue(previous.Epoch));
            item.Add("previousWriterProtocol", NumberValue(previous.WriterProtocol));
            item.Add("previousCleanRelease", new AttributeValue { BOOL = previous.CleanRelease });
        }
        if (replacement.AcquiredAt is { } acquiredAt)
        {
            var bounds = new LeaseAcquisitionBounds(replacement.OwnerId, replacement.OwnerTaskArn, replacement.Epoch,
                acquiredAt, replacement.PreviousOwner?.AcquiredAt, replacement.RequiredFencingTaskAcquiredAt);
            item.Add("acquisitionBounds", StringValue(JsonSerializer.Serialize(bounds)));
        }

        var action = CompareAndPut(key, item, expected?.Revision, replacement.Deployment);
        if (expected is not null)
        {
            var put = action.Write.Put;
            put.ConditionExpression += " AND #owner = :owner AND #task = :task AND #epoch = :epoch" +
                " AND #protocol = :protocol AND #mode = :mode AND #until = :until";
            put.ExpressionAttributeNames["#owner"] = "ownerId";
            put.ExpressionAttributeNames["#task"] = "ownerTaskArn";
            put.ExpressionAttributeNames["#epoch"] = "epoch";
            put.ExpressionAttributeNames["#protocol"] = "writerProtocol";
            put.ExpressionAttributeNames["#mode"] = "leaseMode";
            put.ExpressionAttributeNames["#until"] = "validUntil";
            put.ExpressionAttributeValues[":owner"] = StringValue(expected.OwnerId);
            put.ExpressionAttributeValues[":task"] = StringValue(expected.OwnerTaskArn);
            put.ExpressionAttributeValues[":epoch"] = NumberValue(expected.Epoch);
            put.ExpressionAttributeValues[":protocol"] = NumberValue(expected.WriterProtocol);
            put.ExpressionAttributeValues[":mode"] = StringValue(LeaseModeValue(expected.Mode));
            put.ExpressionAttributeValues[":until"] = NumberValue(expected.ValidUntil.ToUnixTimeMilliseconds());
            if (validAfter is { } after)
            {
                put.ConditionExpression += " AND #until > :clock";
                put.ExpressionAttributeValues[":clock"] = NumberValue(after.ToUnixTimeMilliseconds());
            }
            else if (expiredBy is { } before)
            {
                put.ConditionExpression += " AND #until <= :clock";
                put.ExpressionAttributeValues[":clock"] = NumberValue(before.ToUnixTimeMilliseconds());
            }
            List<string> assignments = [];
            int index = 0;
            foreach (var value in item)
            {
                if (value.Key is "PK" or "SK")
                {
                    continue;
                }
                string name = FormattableString.Invariant($"#leaseField{index}");
                string parameter = FormattableString.Invariant($":leaseValue{index++}");
                put.ExpressionAttributeNames.Add(name, value.Key);
                put.ExpressionAttributeValues.Add(parameter, value.Value);
                assignments.Add(name + " = " + parameter);
            }
            string updateExpression = "SET " + string.Join(", ", assignments);
            if (replacement.RequiredFencingTaskArn is null)
            {
                put.ExpressionAttributeNames.Add("#requiredTask", "requiredFencingTaskArn");
                updateExpression += " REMOVE #requiredTask";
            }
            action = new TransactionAction(key, new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = _tableName,
                    Key = Key(key),
                    ConditionExpression = put.ConditionExpression,
                    ExpressionAttributeNames = put.ExpressionAttributeNames,
                    ExpressionAttributeValues = put.ExpressionAttributeValues,
                    UpdateExpression = updateExpression,
                },
            });
        }

        var outcome = await WriteAsync([action], replacement.TransitionId, cancellationToken).ConfigureAwait(false);
        return new ControllerLeaseChange(outcome, outcome == LifecycleWriteOutcome.Committed ? replacement : null)
        {
            AttemptedTransitionId = replacement.TransitionId,
        };
    }

    private static ControllerLeasePredecessor? ReadPredecessor(Dictionary<string, AttributeValue> item)
    {
        if (!item.ContainsKey("previousOwnerTaskArn"))
        {
            if (item.ContainsKey("previousOwnerId") || item.ContainsKey("previousOwnerEpoch") ||
                item.ContainsKey("previousWriterProtocol") || item.ContainsKey("previousCleanRelease"))
            {
                throw InvalidRecord();
            }
            return null;
        }

        string task = Text(item, "previousOwnerTaskArn");
        string owner = Text(item, "previousOwnerId");
        long epoch = Number(item, "previousOwnerEpoch");
        if (!IsTaskArn(task) || string.IsNullOrWhiteSpace(owner) || epoch <= 0 ||
            !item.TryGetValue("previousCleanRelease", out var clean) || clean.BOOL is not bool cleanRelease)
        {
            throw InvalidRecord();
        }
        return new ControllerLeasePredecessor(owner, task, epoch, ReadWriterProtocol(item, "previousWriterProtocol"), cleanRelease);
    }

    private static int ReadWriterProtocol(Dictionary<string, AttributeValue> item, string name)
    {
        long protocol = Number(item, name);
        return protocol is > 0 and <= int.MaxValue ? (int)protocol : throw InvalidRecord();
    }

    private static ControllerLease ReadLeaseAcquisitionBounds(ControllerLease lease, Dictionary<string, AttributeValue> item)
    {
        if (!item.ContainsKey("acquisitionBounds"))
        {
            return lease;
        }
        LeaseAcquisitionBounds bounds;
        try
        {
            bounds = JsonSerializer.Deserialize<LeaseAcquisitionBounds>(Text(item, "acquisitionBounds"), JsonSerializerOptions.Strict)
                ?? throw InvalidRecord();
        }
        catch (JsonException)
        {
            return lease;
        }
        // A legacy writer can update the lease while leaving this additive field in place.
        if (bounds.Epoch != lease.Epoch || bounds.OwnerId != lease.OwnerId || bounds.OwnerTaskArn != lease.OwnerTaskArn)
        {
            return lease;
        }
        if (bounds.AcquiredAt <= DateTimeOffset.UnixEpoch || bounds.AcquiredAt >= lease.ValidUntil ||
            (lease.PreviousOwner is null && bounds.PreviousAcquiredAt is not null) ||
            (lease.RequiredFencingTaskArn is null && bounds.RequiredAcquiredAt is not null))
        {
            return lease;
        }
        return lease with
        {
            AcquiredAt = bounds.AcquiredAt,
            PreviousOwner = lease.PreviousOwner is { } prior ? prior with { AcquiredAt = bounds.PreviousAcquiredAt } : null,
            RequiredFencingTaskAcquiredAt = bounds.RequiredAcquiredAt,
        };
    }

    private sealed record LeaseAcquisitionBounds(string OwnerId, string OwnerTaskArn, long Epoch, DateTimeOffset AcquiredAt,
        DateTimeOffset? PreviousAcquiredAt, DateTimeOffset? RequiredAcquiredAt);

    private DateTimeOffset ConservativeLatest(DateTimeOffset latestNow)
    {
        var now = _timeProvider.GetUtcNow();
        var latest = latestNow > now ? latestNow : now;
        var milliseconds = AtMillisecond(latest);
        return milliseconds < latest ? milliseconds.AddMilliseconds(1) : milliseconds;
    }

    private static DateTimeOffset AtMillisecond(DateTimeOffset time) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

    private static bool IsTaskArn(string? value) => value is not null && TaskArnPattern().IsMatch(value);

    [GeneratedRegex(@"\Aarn:aws(?:-[a-z0-9-]+)?:ecs:[a-z0-9-]+:[0-9]{12}:task/[A-Za-z0-9_-]+/[a-f0-9]{32}\z")]
    private static partial Regex TaskArnPattern();

    private static string LeaseModeValue(ControllerLeaseMode mode) =>
        mode switch
        {
            ControllerLeaseMode.RecoveryOnly => "recovery-only",
            ControllerLeaseMode.Active => "active",
            ControllerLeaseMode.Released => "released",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
}
