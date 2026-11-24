using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.Ec2InspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class Ec2StateChangeConsumerTests
{
    private const string QueueUrl = "https://aws.fixture.invalid/" + Account + "/ec2-observations";
    private const string InstanceArn = "arn:aws:ec2:" + Region + ":" + Account + ":instance/" + Instance1;

    [Fact]
    public async Task OutOfOrderStateChangeMessagesReadCurrentOwnedStateBeforeDeletingExactReceipts()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = _ => AwsWireTransport.Xml(InventoryPage(Instance(state: "terminated", code: 48))),
        };
        using var provider = cloud.CreateProvider(config);
        using var sqsWire = new AwsWireTransport(request => request.Target switch
        {
            "AmazonSQS.ReceiveMessage" => AwsWireTransport.Json(JsonSerializer.Serialize(new
            {
                Messages = new[]
                {
                    Message("terminated-receipt", Event("terminated")),
                    Message("stale-pending-receipt", Event("pending")),
                    Message("wrong-account-receipt", Event("running", "999999999999")),
                },
            })),
            "AmazonSQS.DeleteMessage" => AwsWireTransport.Json("{}"),
            _ => throw new InvalidOperationException("Unexpected SQS operation."),
        });
        using var sqs = new AmazonSQSClient(AwsWireTransport.Credentials, sqsWire.Configure(new AmazonSQSConfig()));
        var consumer = new Ec2StateChangeConsumer(config, sqs, QueueUrl);
        int refreshes = 0;

        int acknowledged = await consumer.ReceiveOnceAsync(async (hint, token) =>
        {
            Assert.Equal(refreshes, sqsWire.Requests.Count(item => item.Target == "AmazonSQS.DeleteMessage"));
            Assert.Equal(InstanceArn, hint.ResourceArn);
            var snapshot = await provider.ReadStateChangeAsync(hint, token);
            Assert.True(snapshot.IsComplete);
            Assert.Equal(Ec2InstanceState.Terminated, Assert.Single(snapshot.Instances).State);
            refreshes++;
            return true;
        }, ct);

        Assert.Equal(2, refreshes);
        Assert.Equal(2, acknowledged);
        Assert.Collection(sqsWire.Requests,
            request => Assert.Equal("AmazonSQS.ReceiveMessage", request.Target),
            request => AssertDelete(request, "terminated-receipt"),
            request => AssertDelete(request, "stale-pending-receipt"));
        var describes = cloud.Requests.Where(item => item["Action"] == "DescribeInstances").ToArray();
        Assert.Equal(2, describes.Length);
        Assert.All(describes, item =>
        {
            Assert.Equal("instance-id", item["Filter.4.Name"]);
            Assert.Equal(Instance1, item["Filter.4.Value.1"]);
        });
        Assert.DoesNotContain(cloud.Requests, item => item["Action"] is "TerminateInstances" or "RunInstances");
    }

    [Fact]
    public async Task FailedAuthoritativeRefreshLeavesTheReceiptForRedelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var wire = new AwsWireTransport(request => request.Target == "AmazonSQS.ReceiveMessage"
            ? AwsWireTransport.Json(JsonSerializer.Serialize(new { Messages = new[] { Message("hold", Event("running")) } }))
            : throw new InvalidOperationException("An incomplete refresh must not acknowledge the receipt."));
        using var sqs = new AmazonSQSClient(AwsWireTransport.Credentials, wire.Configure(new AmazonSQSConfig()));
        var consumer = new Ec2StateChangeConsumer(config, sqs, QueueUrl);
        int refreshed = 0;

        int acknowledged = await consumer.ReceiveOnceAsync((hint, _) =>
        {
            Assert.Equal(Instance1, hint.InstanceId);
            refreshed++;
            return Task.FromResult(false);
        }, ct);

        Assert.Equal(1, refreshed);
        Assert.Equal(0, acknowledged);
        Assert.Equal("AmazonSQS.ReceiveMessage", Assert.Single(wire.Requests).Target);
    }

    private static void AssertDelete(AwsWireRequest request, string expectedReceipt)
    {
        Assert.Equal("AmazonSQS.DeleteMessage", request.Target);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(QueueUrl, body.RootElement.GetProperty("QueueUrl").GetString());
        Assert.Equal(expectedReceipt, body.RootElement.GetProperty("ReceiptHandle").GetString());
    }

    private static object Message(string receipt, string body)
    {
#pragma warning disable CA5351 // SQS requires MD5 as its wire body checksum.
        string checksum = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));
#pragma warning restore CA5351
        return new { ReceiptHandle = receipt, MessageId = receipt + "-id", Body = body, MD5OfBody = checksum };
    }

    private static string Event(string state, string account = Account) => $$"""
        {
          "source": "aws.ec2",
          "detail-type": "EC2 Instance State-change Notification",
          "account": "{{account}}",
          "time": "2050-01-01T00:00:00Z",
          "region": "{{Region}}",
          "resources": [ "{{InstanceArn}}" ],
          "detail": { "instance-id": "{{Instance1}}", "state": "{{state}}" }
        }
        """;
}
