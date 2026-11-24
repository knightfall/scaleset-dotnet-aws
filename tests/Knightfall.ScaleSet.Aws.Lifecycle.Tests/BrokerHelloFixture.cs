using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

internal static class BrokerHelloFixture
{
    internal static async Task<LifecycleWriteOutcome> RegisterAsync(IAmazonDynamoDB broker, string tableName,
        string principal, JitHello hello, TimeProvider clock, CancellationToken cancellationToken)
    {
        JitOfferCryptography.ValidateHello(hello);
        var key = LifecycleKey.Hello(principal);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(hello, BootstrapJson.Options);
        try
        {
            await broker.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                ConditionExpression = "attribute_not_exists(PK)",
                Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    ["PK"] = new(key.PartitionKey), ["SK"] = new(key.SortKey),
                    ["awsUserId"] = new(principal), ["protocolVersion"] = new() { N = "1" },
                    ["helloSha256"] = new(Convert.ToHexStringLower(SHA256.HashData(payload))),
                    ["payload"] = new(Encoding.UTF8.GetString(payload)), ["helloIssuer"] = new("iam-url-v1"),
                    ["expiresAt"] = new() { N = clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
                },
            }, cancellationToken).ConfigureAwait(false);
            return LifecycleWriteOutcome.Committed;
        }
        catch (ConditionalCheckFailedException)
        {
            return LifecycleWriteOutcome.Conflict;
        }
    }
}
