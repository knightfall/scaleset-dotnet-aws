using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    public async Task EventDiagnosticsAttributeOnlyAuthoritativeReadbackAndSuppressWireIdentifiers()
    {
        var token = TestContext.Current.CancellationToken;
        const string meterName = "Knightfall.ScaleSet.Aws.Providers.Fargate";
        const string receipt = "sensitive-receipt-value";
        var task = TaskRow(FirstTask, FirstAllocation);
        task["version"] = 8;
        using var aws = new FargateInspectionFixture
        {
            ReceiveMessage = _ => Messages((receipt, Event(7, "STOPPED", eventTime: Now.AddSeconds(-20)))),
            DescribeTasks = _ => TasksReply(task),
        };
        using var sqs = aws.CreateSqs();
        using var consumer = new FargateTaskEventConsumer(sqs, aws.Inspector, await ConfigurationAsync(token), QueueUrl, new EventClock());
        using var parent = new Activity("fargate-event-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var stopped = new ConcurrentQueue<Activity>();
        using var trace = new ActivityListener
        {
            ShouldListenTo = source => source.Name == meterName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (span.TraceId == parent.TraceId)
                {
                    stopped.Enqueue(span);
                }
            },
        };
        ActivitySource.AddActivityListener(trace);
        var ages = new ConcurrentQueue<(double Seconds, string? Profile)>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == "scaleset.aws.event.age")
            {
                Assert.Equal("s", instrument.Unit);
                listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var dimensions = tags.ToArray().ToDictionary();
            Assert.Equal("fargate", dimensions["provider"]);
            string? profile = dimensions.GetValueOrDefault("profile") as string;
            Assert.Equal(profile is null ? 1 : 2, dimensions.Count);
            ages.Enqueue((value, profile));
        });
        metrics.Start();

        await consumer.PollAsync(static (_, _, _) => Task.FromResult(true), token);
        await consumer.PollAsync(static (_, _, _) => throw new InvalidOperationException("Duplicate event invoked apply."), token);

        var processed = stopped.Where(activity => activity.OperationName == "scaleset.aws.fargate.sqs_process").ToArray();
        Assert.Collection(processed,
            observed =>
            {
                Assert.Equal(Profile, observed.GetTagItem("profile"));
                Assert.Equal("readback_accepted", observed.GetTagItem("reason"));
                Assert.Equal("success", observed.GetTagItem("outcome"));
            },
            duplicate =>
            {
                Assert.Null(duplicate.GetTagItem("profile"));
                Assert.Equal("duplicate_or_old", duplicate.GetTagItem("reason"));
            });
        var tagValues = stopped.SelectMany(activity => activity.Tags).Select(tag => tag.Value);
        Assert.DoesNotContain(tagValues, value => value is receipt or FirstTask or QueueUrl);
        Assert.Equal((20, Profile), ages.First());
        Assert.Equal((20, (string?)null), ages.Last());
        Assert.Equal(2, ages.Count);
        Assert.Equal([receipt, receipt], Acknowledgments(aws));
    }

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
                ? Messages(("newest", Event(7, "STOPPED").Replace(
                    "\"time\":null", "\"time\":\"2026-01-01T00:00:00Z\\ud800\"", StringComparison.Ordinal)),
                    ("older", Event(6, "RUNNING")), ("duplicate", Event(7, "STOPPED")))
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
    public async Task MalformedEnvelopesRemainForRedriveWhileValidIrrelevantHintsAreAcknowledged()
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

        int authorizations = 0;
        await consumer.PollAsync(static (_, _, _) => throw new InvalidOperationException("Unscoped hint supplied evidence."), _ =>
        {
            authorizations++;
            return Task.FromResult(true);
        }, token);

        Assert.DoesNotContain(aws.Requests, request => Operation(request) == "DescribeTasks");
        string[] receipts = ["foreign-definition", "foreign-cluster"];
        Assert.Equal(receipts, Acknowledgments(aws));
        Assert.Equal(receipts.Length, authorizations);
    }

    private static string Event(int version, string status, string account = Account, string cluster = Cluster,
        string definition = DefinitionArn, DateTimeOffset? eventTime = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["version"] = "0",
        ["detail-type"] = "ECS Task State Change",
        ["source"] = "aws.ecs",
        ["account"] = account,
        ["region"] = Region,
        ["time"] = eventTime,
        ["resources"] = new[] { FirstTask },
        ["detail"] = new { clusterArn = cluster, taskArn = FirstTask, taskDefinitionArn = definition, version, lastStatus = status },
    });

    private sealed class EventClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

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
