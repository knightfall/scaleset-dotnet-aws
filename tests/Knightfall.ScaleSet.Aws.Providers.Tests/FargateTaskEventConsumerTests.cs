using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.FargateInspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class FargateTaskEventConsumerTests
{
    [Fact]
    public async Task OutOfOrderAndDuplicateHintsUseOneLatestVersionReadbackAndAcknowledgeTheBatch()
    {
        var token = TestContext.Current.CancellationToken;
        int poll = 0;
        var task = TaskRow(FirstTask, FirstAllocation);
        task["version"] = 8;
        using var aws = new FargateInspectionFixture
        {
            ReceiveMessage = _ => ++poll == 1
                ? Messages(("newest", Event(7, "STOPPED")), ("older", Event(6, "RUNNING")), ("duplicate", Event(7, "STOPPED")))
                : Messages(("late", Event(8, "STOPPED"))),
            DescribeTasks = _ => TasksReply(task),
        };
        using var sqs = aws.CreateSqs();
        using var consumer = new FargateTaskEventConsumer(sqs, aws.Inspector, await ConfigurationAsync(token), QueueUrl);
        var applied = new List<(ExecutorBinding Bind, ExecutorResourceObservation Resource)>();

        await consumer.PollAsync(Apply, token);
        await consumer.PollAsync(Apply, token);

        var (binding, resource) = Assert.Single(applied);
        Assert.Equal(FirstTask, binding.ResourceArn);
        Assert.Equal(FirstAllocation.ToString("D"), binding.AllocationId);
        Assert.Equal(new FargateExecutorIdentity(Cluster, DefinitionArn), binding.Provider);
        Assert.Equal(ExecutorResourceState.Running, resource.State);
        Assert.Equal(8L, resource.ProviderVersion);
        var describe = Body(Assert.Single(aws.Requests, request => Operation(request) == "DescribeTasks"));
        Assert.Equal(Cluster, describe.GetProperty("cluster").GetString());
        Assert.Equal(FirstTask, Assert.Single(describe.GetProperty("tasks").EnumerateArray()).GetString());
        Assert.Equal("TAGS", Assert.Single(describe.GetProperty("include").EnumerateArray()).GetString());
        string[] receipts = ["newest", "older", "duplicate", "late"];
        Assert.Equal(receipts, Acknowledgments(aws));
        var receive = Body(aws.Requests[0]);
        Assert.Equal(QueueUrl, receive.GetProperty("QueueUrl").GetString());
        Assert.Equal(10, receive.GetProperty("MaxNumberOfMessages").GetInt32());
        Assert.Equal(10, receive.GetProperty("WaitTimeSeconds").GetInt32());

        Task<bool> Apply(ExecutorBinding executor, ExecutorResourceObservation observed, CancellationToken cancellationToken)
        {
            Assert.Equal(token, cancellationToken);
            applied.Add((executor, observed));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task NewerEventWaitsForAuthoritativeVersionAndDurableAcceptanceBeforeAcknowledgment()
    {
        var token = TestContext.Current.CancellationToken;
        bool accepted = false;
        int applies = 0;
        var task = TaskRow(FirstTask, FirstAllocation, "RUNNING");
        task["version"] = 10;
        using var aws = new FargateInspectionFixture
        {
            ReceiveMessage = _ => Messages(("retry", Event(11, "STOPPED"))),
            DescribeTasks = _ => TasksReply(task),
        };
        using var sqs = aws.CreateSqs();
        using var consumer = new FargateTaskEventConsumer(sqs, aws.Inspector, await ConfigurationAsync(token), QueueUrl);

        await consumer.PollAsync(Apply, token);
        Assert.Equal(0, applies);
        Assert.Empty(Acknowledgments(aws));

        task["version"] = 11;
        task["lastStatus"] = "STOPPED";
        await consumer.PollAsync(Apply, token);
        Assert.Equal(1, applies);
        Assert.Empty(Acknowledgments(aws));

        accepted = true;
        await consumer.PollAsync(Apply, token);
        Assert.Equal(2, applies);
        Assert.Equal("retry", Assert.Single(Acknowledgments(aws)));

        Task<bool> Apply(ExecutorBinding executor, ExecutorResourceObservation resource, CancellationToken cancellationToken)
        {
            Assert.Equal(token, cancellationToken);
            Assert.Equal(new LifecycleProfile(Deployment, Profile), executor.Profile);
            Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, resource.State);
            Assert.Equal(11L, resource.ProviderVersion);
            applies++;
            return Task.FromResult(accepted);
        }
    }

    [Fact]
    public async Task OutOfScopeOrMalformedHintsAreDiscardedWithoutAwsTaskReads()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            ReceiveMessage = _ => Messages(("foreign-account", Event(2, "STOPPED", account: "999900001111")),
                ("foreign-definition", Event(2, "STOPPED", definition: DefinitionArn[..^1] + "8")),
                ("foreign-cluster", Event(2, "STOPPED", cluster: Cluster + "-other")), ("bad-json", "{")),
        };
        using var sqs = aws.CreateSqs();
        using var consumer = new FargateTaskEventConsumer(sqs, aws.Inspector, await ConfigurationAsync(token), QueueUrl);

        await consumer.PollAsync(static (_, _, _) => throw new InvalidOperationException("Unscoped hint supplied evidence."), token);

        Assert.DoesNotContain(aws.Requests, request => Operation(request) == "DescribeTasks");
        string[] receipts = ["foreign-account", "bad-json", "foreign-definition", "foreign-cluster"];
        Assert.Equal(receipts, Acknowledgments(aws));
    }

    private static string Event(int version, string status, string account = Account, string cluster = Cluster,
        string definition = DefinitionArn) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["version"] = "0",
        ["detail-type"] = "ECS Task State Change",
        ["source"] = "aws.ecs",
        ["account"] = account,
        ["region"] = Region,
        ["resources"] = new[] { FirstTask },
        ["detail"] = new { clusterArn = cluster, taskArn = FirstTask, taskDefinitionArn = definition, version, lastStatus = status },
    });

    private static string Messages(params (string Receipt, string Event)[] messages) => JsonSerializer.Serialize(new
    {
        Messages = messages.Select(message => new
        {
            MessageId = message.Receipt,
            ReceiptHandle = message.Receipt,
            Body = message.Event,
#pragma warning disable CA5351 // The SQS protocol's content checksum uses MD5.
            MD5OfBody = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(message.Event))),
#pragma warning restore CA5351
        }),
    });

    private static IEnumerable<string?> Acknowledgments(FargateInspectionFixture aws) => aws.Requests
        .Where(request => Operation(request) == "DeleteMessage")
        .Select(request =>
        {
            var body = Body(request);
            Assert.Equal(QueueUrl, body.GetProperty("QueueUrl").GetString());
            return body.GetProperty("ReceiptHandle").GetString();
        });
}
