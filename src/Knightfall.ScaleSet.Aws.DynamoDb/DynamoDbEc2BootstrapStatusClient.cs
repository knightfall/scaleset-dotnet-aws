using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record JitEc2BootstrapStatus(string OfferId, long RunnerId, long Sequence, bool Exited, DateTimeOffset ObservedAt)
{
    [JsonIgnore]
    public string? QualificationSha256 { get; init; }
}

/// <summary>Writes the exact EC2 STATUS row for an isolated bootstrap identity.</summary>
public sealed class DynamoDbEc2BootstrapStatusClient
{
    private const int MaximumStatusCharacters = 1024;
    private const int RequiredAttributeCount = 11;
    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly string _awsUserId;
    private readonly string _resourceArn;
    private readonly TimeProvider _clock;

    public DynamoDbEc2BootstrapStatusClient(IAmazonDynamoDB client, string tableName, string awsUserId, string resourceArn,
        TimeProvider timeProvider)
    {
        BootstrapItems.RequireClient(client, tableName);
        BootstrapItems.RequirePrincipal(awsUserId, resourceArn, ec2Only: true);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = client;
        _tableName = tableName;
        _awsUserId = awsUserId;
        _resourceArn = resourceArn;
        _clock = timeProvider;
    }

    public async Task<LifecycleWriteOutcome> PublishAsync(JitHello hello, JitEc2BootstrapStatus status, CancellationToken cancellationToken)
    {
        ValidateStatus(status);
        cancellationToken.ThrowIfCancellationRequested();
        if (status.ObservedAt > _clock.GetUtcNow())
        {
            return LifecycleWriteOutcome.Indeterminate;
        }
        var key = LifecycleKey.Status(_awsUserId);
        var item = BootstrapItems.Key(key);
        string helloDigest = BootstrapItems.HelloDigest(hello);
        item.Add("awsUserId", BootstrapItems.String(_awsUserId));
        item.Add("resourceArn", BootstrapItems.String(_resourceArn));
        item.Add("helloSha256", BootstrapItems.String(helloDigest));
        item.Add("offerId", BootstrapItems.String(status.OfferId));
        item.Add("runnerId", BootstrapItems.Number(status.RunnerId));
        item.Add("sequence", BootstrapItems.Number(status.Sequence));
        item.Add("observedAt", BootstrapItems.Number(status.ObservedAt.ToUnixTimeMilliseconds()));
        item.Add("exited", new AttributeValue { BOOL = status.Exited });
        string payload = BootstrapItems.Serialize(status);
        item.Add("payload", BootstrapItems.String(payload));
        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":resource"] = BootstrapItems.String(_resourceArn),
            [":principal"] = BootstrapItems.String(_awsUserId),
            [":hello"] = BootstrapItems.String(helloDigest),
            [":offer"] = BootstrapItems.String(status.OfferId),
            [":runner"] = BootstrapItems.Number(status.RunnerId),
            [":false"] = new AttributeValue { BOOL = false },
            [":seq"] = BootstrapItems.Number(status.Sequence),
            [":observed"] = BootstrapItems.Number(status.ObservedAt.ToUnixTimeMilliseconds()),
        };
        string qualificationCondition = "attribute_not_exists(qualificationSha256)";
        if (status.QualificationSha256 is { } qualification)
        {
            item.Add("qualificationSha256", BootstrapItems.String(qualification));
            values.Add(":qualification", BootstrapItems.String(qualification));
            qualificationCondition = "qualificationSha256 = :qualification";
        }

        try
        {
            var reply = await _client.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK) OR (resourceArn = :resource AND awsUserId = :principal " +
                    "AND helloSha256 = :hello AND offerId = :offer AND runnerId = :runner AND exited = :false " +
                    "AND #seq < :seq AND observedAt <= :observed AND " + qualificationCondition + ")",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#seq"] = "sequence" },
                ExpressionAttributeValues = values,
                ReturnValues = ReturnValue.NONE,
                ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
            }, cancellationToken).ConfigureAwait(false);
            return reply.HttpStatusCode == HttpStatusCode.OK ? LifecycleWriteOutcome.Committed : LifecycleWriteOutcome.Indeterminate;
        }
        catch (ConditionalCheckFailedException conflict)
        {
            // The returned item is the image checked by this conditional write. No table read grant is needed.
            return conflict.Item is { } prior && DecodeStatus(key, prior, _awsUserId, _resourceArn, helloDigest) == status &&
                BootstrapItems.Text(prior, "payload") == payload ? LifecycleWriteOutcome.Committed : LifecycleWriteOutcome.Conflict;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && BootstrapItems.IsIndeterminate(error))
        {
            return LifecycleWriteOutcome.Indeterminate;
        }
        catch (AmazonServiceException)
        {
            throw new IOException("The bootstrap status write was rejected.");
        }
    }

    internal static JitEc2BootstrapStatus? DecodeStatus(LifecycleKey key, Dictionary<string, AttributeValue> item,
        string awsUserId, string resourceArn, string helloDigest)
    {
        try
        {
            BootstrapItems.RequireKey(key, item);
            bool hasQualification = item.ContainsKey("qualificationSha256");
            string payload = BootstrapItems.Text(item, "payload");
            if (item.Count != RequiredAttributeCount + (hasQualification ? 1 : 0) || payload.Length > MaximumStatusCharacters)
            {
                return null;
            }
            var status = BootstrapItems.Deserialize<JitEc2BootstrapStatus>(payload) with
            {
                // Keep the payload readable by a controller rolled back to the original STATUS contract.
                QualificationSha256 = hasQualification ? BootstrapItems.Text(item, "qualificationSha256") : null,
            };
            ValidateStatus(status);
            return BootstrapItems.Text(item, "awsUserId") == awsUserId &&
                BootstrapItems.Text(item, "resourceArn") == resourceArn &&
                BootstrapItems.Text(item, "helloSha256") == helloDigest &&
                BootstrapItems.Text(item, "offerId") == status.OfferId &&
                BootstrapItems.Integer(item, "runnerId") == status.RunnerId &&
                BootstrapItems.Integer(item, "sequence") == status.Sequence &&
                BootstrapItems.Integer(item, "observedAt") == status.ObservedAt.ToUnixTimeMilliseconds() &&
                item.TryGetValue("exited", out var exited) && exited is { IsBOOLSet: true } && exited.BOOL == status.Exited
                ? status : null;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    internal static void ValidateStatus(JitEc2BootstrapStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!JitHandoffSnapshot.IsLowercaseDigest(status.OfferId) || status.RunnerId <= 0 ||
            status.Sequence != (status.Exited ? 2 : 1) || status.ObservedAt <= DateTimeOffset.UnixEpoch ||
            (status.QualificationSha256 is not null && !JitHandoffSnapshot.IsLowercaseDigest(status.QualificationSha256)))
        {
            throw new ArgumentException("An exact EC2 runner status is required.", nameof(status));
        }
    }
}
