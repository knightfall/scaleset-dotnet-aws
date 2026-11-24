using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

public sealed class DynamoDbWireTests
{
    private const string Table = "runner-state";
    private const string TransactionToken = "3f5b06c9-5eb7-4a89-8e5a-f35ad54e6210";

    [Fact]
    public async Task TransactWriteItemsGeneratesFreshWireTokenForEachSdkInvocation()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json("{}"));
        using var client = DynamoClient(wire);
        var request = LeaseFencedTransaction();

        await client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken);
        await client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken);

        var sends = wire.Requests;
        Assert.Equal(2, sends.Count);
        Assert.True(Guid.TryParseExact(ReadTransactionToken(sends[0]), "D", out var first));
        Assert.True(Guid.TryParseExact(ReadTransactionToken(sends[1]), "D", out var second));
        Assert.NotEqual(first, second);
        Assert.Null(request.ClientRequestToken);
    }

    [Fact]
    public async Task TransactWriteItemsPreservesExplicitTokenAndFencedTransactionShape()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json("{}"));
        using var client = DynamoClient(wire);
        var request = LeaseFencedTransaction();
        request.ClientRequestToken = TransactionToken;

        await client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken);
        await client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken);

        var sends = wire.Requests;
        Assert.Equal(2, sends.Count);
        Assert.All(sends, send => Assert.Equal(TransactionToken, ReadTransactionToken(send)));
        using var json = ReadBody(sends[0], "TransactWriteItems");
        var actions = json.RootElement.GetProperty("TransactItems");
        Assert.Equal(2, actions.GetArrayLength());

        var fence = actions[0].GetProperty("ConditionCheck");
        Assert.Equal(Table, fence.GetProperty("TableName").GetString());
        Assert.Equal("ENV#test", StringAttribute(fence.GetProperty("Key"), "PK"));
        Assert.Equal("LEASE", StringAttribute(fence.GetProperty("Key"), "SK"));
        Assert.Equal("epoch = :epoch", fence.GetProperty("ConditionExpression").GetString());
        Assert.Equal("7", fence.GetProperty("ExpressionAttributeValues").GetProperty(":epoch").GetProperty("N").GetString());
        Assert.Equal("ALL_OLD", fence.GetProperty("ReturnValuesOnConditionCheckFailure").GetString());

        var put = actions[1].GetProperty("Put");
        Assert.Equal(Table, put.GetProperty("TableName").GetString());
        Assert.Equal("OP#attempt", StringAttribute(put.GetProperty("Item"), "SK"));
        Assert.Equal("attribute_not_exists(PK)", put.GetProperty("ConditionExpression").GetString());
    }

    [Fact]
    public async Task TransactionCanceledExceptionKeepsOrderedReasonsAndOldItem()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(
            """
            {
              "__type": "com.amazonaws.dynamodb.v20120810#TransactionCanceledException",
              "message": "Transaction cancelled",
              "CancellationReasons": [
                { "Code": "ConditionalCheckFailed", "Message": "Owner changed",
                  "Item": { "PK": { "S": "ENV#test" }, "SK": { "S": "LEASE" }, "epoch": { "N": "8" } } },
                { "Code": "None" }
              ]
            }
            """, HttpStatusCode.BadRequest));
        using var client = DynamoClient(wire);

        var error = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => client.TransactWriteItemsAsync(LeaseFencedTransaction(), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("TransactionCanceledException", error.ErrorCode);
        Assert.NotNull(error.CancellationReasons);
        Assert.Collection(error.CancellationReasons,
            condition =>
            {
                Assert.Equal("ConditionalCheckFailed", condition.Code);
                Assert.Equal("Owner changed", condition.Message);
                Assert.NotNull(condition.Item);
                Assert.Equal("LEASE", condition.Item["SK"].S);
                Assert.Equal("8", condition.Item["epoch"].N);
            },
            put =>
            {
                Assert.Equal("None", put.Code);
                Assert.Null(put.Item);
            });
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task TransactionCanceledExceptionCanOmitReasons()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(
            """{"__type":"TransactionCanceledException","message":"Transaction cancelled"}""",
            HttpStatusCode.BadRequest));
        using var client = DynamoClient(wire);

        var error = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => client.TransactWriteItemsAsync(LeaseFencedTransaction(), TestContext.Current.CancellationToken));

        Assert.Null(error.CancellationReasons);
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task MaxErrorRetryZeroStopsAfterRetryableServerError()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(
            """{"__type":"InternalServerError","message":"Outcome unknown"}""",
            HttpStatusCode.InternalServerError));
        using var client = DynamoClient(wire);
        var request = LeaseFencedTransaction();
        request.ClientRequestToken = TransactionToken;

        var error = await Assert.ThrowsAsync<InternalServerErrorException>(
            () => client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Equal(TransactionToken, ReadTransactionToken(Assert.Single(wire.Requests)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StaleConnectionRetryHasItsOwnBudget(int staleRetries)
    {
        var firstSend = true;
        using var wire = new AwsWireTransport(_ =>
        {
            if (firstSend)
            {
                firstSend = false;
                throw new HttpRequestException("Synthetic connection reset after send",
                    new SocketException((int)SocketError.ConnectionReset));
            }

            return AwsWireTransport.Json("{}");
        });
        var config = wire.Configure(new AmazonDynamoDBConfig());
        config.MaxStaleConnectionRetries = staleRetries;
        using var client = new AmazonDynamoDBClient(AwsWireTransport.Credentials, config);
        var request = LeaseFencedTransaction();
        request.ClientRequestToken = TransactionToken;

        var error = await Record.ExceptionAsync(
            () => client.TransactWriteItemsAsync(request, TestContext.Current.CancellationToken));

        var sends = wire.Requests;
        if (staleRetries == 0)
        {
            Assert.IsType<HttpRequestException>(error);
            Assert.Single(sends);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(2, sends.Count);
        }

        Assert.All(sends, send => Assert.Equal(TransactionToken, ReadTransactionToken(send)));
    }

    [Fact]
    public async Task ConditionalDeleteReturnsOfferPreImageOnlyOnSuccess()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(
            """{"Attributes":{"allocation":{"S":"attempt"},"ciphertext":{"B":"AQID"}}}"""));
        using var client = DynamoClient(wire);

        var response = await client.DeleteItemAsync(OfferDelete(), TestContext.Current.CancellationToken);

        Assert.NotNull(response.Attributes);
        Assert.Equal("attempt", response.Attributes["allocation"].S);
        Assert.Equal(new byte[] { 1, 2, 3 }, response.Attributes["ciphertext"].B.ToArray());

        using var json = ReadBody(Assert.Single(wire.Requests), "DeleteItem");
        var body = json.RootElement;
        Assert.Equal(Table, body.GetProperty("TableName").GetString());
        Assert.Equal("OFFER#role:instance", StringAttribute(body.GetProperty("Key"), "PK"));
        Assert.Equal("BOOTSTRAP", StringAttribute(body.GetProperty("Key"), "SK"));
        Assert.Equal("allocation = :allocation", body.GetProperty("ConditionExpression").GetString());
        Assert.Equal("attempt", StringAttribute(body.GetProperty("ExpressionAttributeValues"), ":allocation"));
        Assert.Equal("ALL_OLD", body.GetProperty("ReturnValues").GetString());
        Assert.False(body.TryGetProperty("ReturnValuesOnConditionCheckFailure", out _));
    }

    [Fact]
    public async Task ConditionalDeleteFailureIsTypedAndHasNoPreImage()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(
            """{"__type":"ConditionalCheckFailedException","message":"Offer already removed"}""",
            HttpStatusCode.BadRequest));
        using var client = DynamoClient(wire);

        var error = await Assert.ThrowsAsync<ConditionalCheckFailedException>(
            () => client.DeleteItemAsync(OfferDelete(), TestContext.Current.CancellationToken));

        Assert.Equal("ConditionalCheckFailedException", error.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Null(error.Item);
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task ConditionalDeleteDoesNotRetryAfterLosingTheConsumeResponse()
    {
        var attempts = 0;
        using var wire = new AwsWireTransport(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new HttpRequestException("Synthetic reset after consuming the offer",
                    new SocketException((int)SocketError.ConnectionReset));
            }

            return AwsWireTransport.Json(
                """{"__type":"ConditionalCheckFailedException","message":"Offer already removed"}""",
                HttpStatusCode.BadRequest);
        });
        using var client = DynamoClient(wire);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteItemAsync(OfferDelete(), TestContext.Current.CancellationToken));

        var reset = Assert.IsType<SocketException>(error.InnerException);
        Assert.Equal(SocketError.ConnectionReset, reset.SocketErrorCode);
        Assert.Equal(0, client.Config.MaxErrorRetry);
        Assert.Equal(0, client.Config.MaxStaleConnectionRetries);
        using var json = ReadBody(Assert.Single(wire.Requests), "DeleteItem");
        var body = json.RootElement;
        var key = body.GetProperty("Key");
        Assert.Equal(Table, body.GetProperty("TableName").GetString());
        Assert.Equal("OFFER#role:instance", StringAttribute(key, "PK"));
        Assert.Equal("BOOTSTRAP", StringAttribute(key, "SK"));
        Assert.Equal("ALL_OLD", body.GetProperty("ReturnValues").GetString());
    }

    [Fact]
    public async Task ConditionalPutKeepsExactHelloKeyAndCondition()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json("{}"));
        using var client = DynamoClient(wire);

        await client.PutItemAsync(new PutItemRequest
        {
            TableName = Table,
            Item = Key("HELLO#role:instance", "BOOTSTRAP"),
            ConditionExpression = "attribute_not_exists(PK)",
        }, TestContext.Current.CancellationToken);

        using var json = ReadBody(Assert.Single(wire.Requests), "PutItem");
        var body = json.RootElement;
        Assert.Equal(Table, body.GetProperty("TableName").GetString());
        Assert.Equal("HELLO#role:instance", StringAttribute(body.GetProperty("Item"), "PK"));
        Assert.Equal("BOOTSTRAP", StringAttribute(body.GetProperty("Item"), "SK"));
        Assert.Equal("attribute_not_exists(PK)", body.GetProperty("ConditionExpression").GetString());
    }

    [Fact]
    public async Task GetItemSendsConsistentReadAndReturnsExactLease()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(
            """{"Item":{"PK":{"S":"ENV#test"},"SK":{"S":"LEASE"},"epoch":{"N":"42"}}}"""));
        using var client = DynamoClient(wire);

        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = Table,
            Key = Key("ENV#test", "LEASE"),
            ConsistentRead = true,
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(response.Item);
        Assert.Equal("ENV#test", response.Item["PK"].S);
        Assert.Equal("LEASE", response.Item["SK"].S);
        Assert.Equal("42", response.Item["epoch"].N);
        using var json = ReadBody(Assert.Single(wire.Requests), "GetItem");
        var body = json.RootElement;
        var key = body.GetProperty("Key");
        Assert.Equal(Table, body.GetProperty("TableName").GetString());
        Assert.True(body.GetProperty("ConsistentRead").GetBoolean());
        Assert.Equal("ENV#test", StringAttribute(key, "PK"));
        Assert.Equal("LEASE", StringAttribute(key, "SK"));
    }

    [Fact]
    public async Task QueryKeepsCursorForEmptyPageAndSendsItOnTheNextRead()
    {
        using var wire = new AwsWireTransport(send =>
        {
            using var json = ReadBody(send, "Query");
            return json.RootElement.TryGetProperty("ExclusiveStartKey", out _)
                ? AwsWireTransport.Json(
                    """{"Count":1,"Items":[{"PK":{"S":"PROFILE#test#example"},"SK":{"S":"OP#next"}}]}""")
                : AwsWireTransport.Json(
                    """{"Count":0,"Items":[],"LastEvaluatedKey":{"PK":{"S":"PROFILE#test#example"},"SK":{"S":"OP#page"}}}""");
        });
        using var client = DynamoClient(wire);
        var request = new QueryRequest
        {
            TableName = Table,
            ConsistentRead = true,
            Limit = 32,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :prefix)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":pk"] = new() { S = "PROFILE#test#example" },
                [":prefix"] = new() { S = "OP#" },
            },
        };

        var first = await client.QueryAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(first.Items);
        Assert.Empty(first.Items);
        Assert.NotNull(first.LastEvaluatedKey);
        Assert.Equal("PROFILE#test#example", first.LastEvaluatedKey["PK"].S);
        Assert.Equal("OP#page", first.LastEvaluatedKey["SK"].S);

        request.ExclusiveStartKey = first.LastEvaluatedKey;
        var second = await client.QueryAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(second.Items);
        var item = Assert.Single(second.Items);
        Assert.Equal("PROFILE#test#example", item["PK"].S);
        Assert.Equal("OP#next", item["SK"].S);
        Assert.Null(second.LastEvaluatedKey);

        var sends = wire.Requests;
        Assert.Equal(2, sends.Count);
        Assert.All(sends, send =>
        {
            using var json = ReadBody(send, "Query");
            var body = json.RootElement;
            var attributes = body.GetProperty("ExpressionAttributeValues");
            Assert.Equal(Table, body.GetProperty("TableName").GetString());
            Assert.True(body.GetProperty("ConsistentRead").GetBoolean());
            Assert.Equal(32, body.GetProperty("Limit").GetInt32());
            Assert.Equal("PK = :pk AND begins_with(SK, :prefix)", body.GetProperty("KeyConditionExpression").GetString());
            Assert.Equal("PROFILE#test#example", StringAttribute(attributes, ":pk"));
            Assert.Equal("OP#", StringAttribute(attributes, ":prefix"));
        });
        using var next = ReadBody(sends[1], "Query");
        var cursor = next.RootElement.GetProperty("ExclusiveStartKey");
        Assert.Equal("PROFILE#test#example", StringAttribute(cursor, "PK"));
        Assert.Equal("OP#page", StringAttribute(cursor, "SK"));
    }

    private static AmazonDynamoDBClient DynamoClient(AwsWireTransport wire) =>
        new(AwsWireTransport.Credentials, wire.Configure(new AmazonDynamoDBConfig()));

    private static TransactWriteItemsRequest LeaseFencedTransaction() => new()
    {
        TransactItems =
        [
            new TransactWriteItem
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = Table,
                    Key = Key("ENV#test", "LEASE"),
                    ConditionExpression = "epoch = :epoch",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":epoch"] = new() { N = "7" },
                    },
                    ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                },
            },
            new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = Table,
                    Item = Key("PROFILE#test#example", "OP#attempt"),
                    ConditionExpression = "attribute_not_exists(PK)",
                },
            },
        ],
    };

    private static DeleteItemRequest OfferDelete() => new()
    {
        TableName = Table,
        Key = Key("OFFER#role:instance", "BOOTSTRAP"),
        ConditionExpression = "allocation = :allocation",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":allocation"] = new() { S = "attempt" },
        },
        ReturnValues = ReturnValue.ALL_OLD,
    };

    private static Dictionary<string, AttributeValue> Key(string partition, string sort) =>
        new(StringComparer.Ordinal)
        {
            ["PK"] = new() { S = partition },
            ["SK"] = new() { S = sort },
        };

    private static string? ReadTransactionToken(AwsWireRequest request)
    {
        using var json = ReadBody(request, "TransactWriteItems");
        return json.RootElement.GetProperty("ClientRequestToken").GetString();
    }

    private static JsonDocument ReadBody(AwsWireRequest request, string operation)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/", request.Uri.AbsolutePath);
        Assert.Equal($"DynamoDB_20120810.{operation}", request.Target);
        return JsonDocument.Parse(request.Body);
    }

    private static string? StringAttribute(JsonElement attributes, string name) =>
        attributes.GetProperty(name).GetProperty("S").GetString();
}
