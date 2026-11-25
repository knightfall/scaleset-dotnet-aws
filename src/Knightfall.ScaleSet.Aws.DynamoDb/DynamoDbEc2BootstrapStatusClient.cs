using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
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

    public Task<LifecycleWriteOutcome> PublishAsync(JitHello hello, JitEc2BootstrapStatus status, CancellationToken cancellationToken)
    {
        ValidateStatus(status);
        if (status.ObservedAt > _clock.GetUtcNow())
        {
            throw new ArgumentException("A locally observed status timestamp is required.", nameof(status));
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
        item.Add("payload", BootstrapItems.String(BootstrapItems.Serialize(status)));
        if (status.QualificationSha256 is { } qualification)
        {
            item.Add("qualificationSha256", BootstrapItems.String(qualification));
        }
        return BootstrapItems.PutAsync(_client, new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(PK) OR (resourceArn = :resource AND helloSha256 = :hello AND " +
                "offerId = :offer AND runnerId = :runner AND exited = :false AND #seq < :seq AND observedAt <= :observed)",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#seq"] = "sequence" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":resource"] = BootstrapItems.String(_resourceArn),
                [":hello"] = BootstrapItems.String(helloDigest),
                [":offer"] = BootstrapItems.String(status.OfferId),
                [":runner"] = BootstrapItems.Number(status.RunnerId),
                [":false"] = new AttributeValue { BOOL = false },
                [":seq"] = BootstrapItems.Number(status.Sequence),
                [":observed"] = BootstrapItems.Number(status.ObservedAt.ToUnixTimeMilliseconds()),
            },
        }, cancellationToken);
    }

    internal static void ValidateStatus(JitEc2BootstrapStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!JitHandoffSnapshot.IsLowercaseDigest(status.OfferId) || status.RunnerId <= 0 ||
            status.Sequence <= 0 || status.ObservedAt <= DateTimeOffset.UnixEpoch ||
            (status.QualificationSha256 is not null && !JitHandoffSnapshot.IsLowercaseDigest(status.QualificationSha256)))
        {
            throw new ArgumentException("An exact EC2 runner status is required.", nameof(status));
        }
    }
}
