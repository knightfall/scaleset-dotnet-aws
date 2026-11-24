using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed class MiniStackIamAuthorizationTests
{
    [Fact]
    public async Task Ec2SessionsFromOneRoleCanWriteAndConsumeOnlyTheirOwnKeys()
    {
        var token = TestContext.Current.CancellationToken;
        using var cloud = await MiniStackIamFixture.CreateAsync(token);
        var role = await cloud.EnsureRunnerRoleAsync("fixture-ec2-iam-authorization", includeStatus: true, token);
        const string firstInstance = "i-0123456789abcdef0";
        const string secondInstance = "i-0123456789abcdef1";
        using var first = await cloud.AssumeRunnerAsync(role, firstInstance, token);
        using var second = await cloud.AssumeRunnerAsync(role, secondInstance, token);
        Assert.Equal($"{role.RoleId}:{firstInstance}", first.AwsUserId);
        Assert.Equal($"{role.RoleId}:{secondInstance}", second.AwsUserId);
        Assert.EndsWith("/" + firstInstance, first.Arn, StringComparison.Ordinal);
        Assert.EndsWith("/" + secondInstance, second.Arn, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>("shortInstanceOrTaskId",
            () => cloud.AssumeRunnerAsync(role, "i-0123456789abcdef0:*", token));

        await cloud.Controller.DynamoDb.PutItemAsync(cloud.TableName, Item(LifecycleKey.Offer(first.AwsUserId), "first offer"), token);
        await cloud.Controller.DynamoDb.PutItemAsync(cloud.TableName, Item(LifecycleKey.Offer(second.AwsUserId), "second offer"), token);

        await AssertScopeAsync(cloud, first, second, token);
        await AssertScopeAsync(cloud, second, first, token);

        await AssertOwnOfferAsync(cloud, first, "first offer", token);
        await AssertOwnOfferAsync(cloud, second, "second offer", token);
        Assert.Empty((await cloud.Controller.DynamoDb.GetItemAsync(cloud.TableName, Key(LifecycleKey.Offer(first.AwsUserId)),
            consistentRead: true, token)).Item ?? []);
        Assert.Empty((await cloud.Controller.DynamoDb.GetItemAsync(cloud.TableName, Key(LifecycleKey.Offer(second.AwsUserId)),
            consistentRead: true, token)).Item ?? []);
    }

    [Fact]
    public async Task FargateSessionLacksStatusWritesAndItsPolicyNamesOnlyTheFixtureTable()
    {
        var token = TestContext.Current.CancellationToken;
        using var cloud = await MiniStackIamFixture.CreateAsync(token);
        using var other = await MiniStackIamFixture.CreateAsync(token);
        var role = await cloud.EnsureRunnerRoleAsync("fixture-fargate-iam-authorization", includeStatus: false, token);
        const string taskId = "0123456789abcdef0123456789abcdef";
        using var runner = await cloud.AssumeRunnerAsync(role, taskId, token);
        Assert.Equal($"{role.RoleId}:{taskId}", runner.AwsUserId);
        var hello = LifecycleKey.Hello(runner.AwsUserId);
        await runner.Clients.DynamoDb.PutItemAsync(cloud.TableName, Item(hello, "hello"), token);
        await AssertDeniedAsync(() => runner.Clients.DynamoDb.PutItemAsync(other.TableName, Item(hello, "wrong table"), token));
        await AssertDeniedAsync(() => runner.Clients.DynamoDb.PutItemAsync(cloud.TableName,
            Item(LifecycleKey.Status(runner.AwsUserId), "unsupported status"), token));
    }

    private static async Task AssertScopeAsync(MiniStackIamFixture cloud, MiniStackIamSession owner, MiniStackIamSession peer,
        CancellationToken cancellationToken)
    {
        var db = owner.Clients.DynamoDb;
        var hello = LifecycleKey.Hello(owner.AwsUserId);
        var status = LifecycleKey.Status(owner.AwsUserId);
        await db.PutItemAsync(cloud.TableName, Item(hello, "hello"), cancellationToken);
        await db.PutItemAsync(cloud.TableName, Item(status, "status"), cancellationToken);
        await AssertDeniedAsync(() => db.PutItemAsync(cloud.TableName,
            Item(LifecycleKey.Hello(peer.AwsUserId), "wrong hello"), cancellationToken));
        await AssertDeniedAsync(() => db.PutItemAsync(cloud.TableName,
            Item(LifecycleKey.Status(peer.AwsUserId), "wrong status"), cancellationToken));
        await AssertDeniedAsync(() => db.DeleteItemAsync(DeleteOffer(cloud.TableName, peer.AwsUserId), cancellationToken));
        await AssertDeniedAsync(() => db.GetItemAsync(cloud.TableName, Key(LifecycleKey.Offer(owner.AwsUserId)),
            consistentRead: true, cancellationToken));
        Assert.Equal("hello", (await cloud.Controller.DynamoDb.GetItemAsync(cloud.TableName, Key(hello),
            consistentRead: true, cancellationToken)).Item["marker"].S);
        Assert.Equal("status", (await cloud.Controller.DynamoDb.GetItemAsync(cloud.TableName, Key(status),
            consistentRead: true, cancellationToken)).Item["marker"].S);
    }

    private static async Task AssertOwnOfferAsync(MiniStackIamFixture cloud, MiniStackIamSession owner, string expected,
        CancellationToken cancellationToken)
    {
        var request = DeleteOffer(cloud.TableName, owner.AwsUserId);
        var consumed = await owner.Clients.DynamoDb.DeleteItemAsync(request, cancellationToken);
        Assert.Equal(expected, consumed.Attributes["marker"].S);
        await Assert.ThrowsAsync<ConditionalCheckFailedException>(
            () => owner.Clients.DynamoDb.DeleteItemAsync(request, cancellationToken));
    }

    private static async Task AssertDeniedAsync(Func<Task> request)
    {
        var error = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(request);
        Assert.Equal("AccessDeniedException", error.ErrorCode);
    }

    private static DeleteItemRequest DeleteOffer(string tableName, string principal) => new()
    {
        TableName = tableName,
        Key = Key(LifecycleKey.Offer(principal)),
        ConditionExpression = "attribute_exists(PK)",
        ReturnValues = "ALL_OLD",
    };

    private static Dictionary<string, AttributeValue> Item(LifecycleKey key, string marker) => new(Key(key), StringComparer.Ordinal)
    {
        ["marker"] = new(marker),
    };

    private static Dictionary<string, AttributeValue> Key(LifecycleKey key) => new(StringComparer.Ordinal)
    {
        ["PK"] = new(key.PartitionKey),
        ["SK"] = new(key.SortKey),
    };
}
