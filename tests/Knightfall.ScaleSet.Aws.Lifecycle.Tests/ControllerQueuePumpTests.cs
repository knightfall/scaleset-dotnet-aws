using System.Globalization;
using System.Text.Json;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.IdentityManagement;
using Amazon.SQS;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Knightfall.ScaleSet.Aws.Providers.Tests;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.Ec2InspectionFixture;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class ControllerQueuePumpTests
{
    private const string Queue = "https://aws.fixture.invalid/111122223333/events";
    private const string OtherAllocation = "12345678-0000-0000-0000-000000000002";

    [Fact(Timeout = 10_000)]
    public async Task Ec2HintsRefreshOnlyTheExactDurableBindingAndDoNotProveTermination()
    {
        var token = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var table = await MiniStackTable.CreateAsync(token);
        var configuration = await ConfigurationAsync(token, active: true);
        var clock = new QueueClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        using var ownership = await StartOwnershipAsync(table, store, configuration, clock, token);
        var profile = new LifecycleProfile(Deployment, ProfileId);
        var valid = Binding(profile, Instance1, Allocation);
        var mismatched = Binding(profile, Instance2, OtherAllocation);
        var lease = Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority()).Lease;
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindExecutorAsync(lease.Fence, valid, token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindExecutorAsync(lease.Fence, mismatched, token));
        using var ec2 = new Ec2InspectionFixture(clock: clock)
        {
            InventoryResponse = query => AwsWireTransport.Xml(InventoryPage(Instance(query["Filter.4.Value.1"]!))),
        };
        using var provider = ec2.CreateProvider(configuration);
        var providers = new Dictionary<string, Ec2EffectProvider>(StringComparer.Ordinal) { [ProfileId] = provider };
        var receipts = new List<string>();
        int polls = 0;
        using var wire = new AwsWireTransport(request =>
        {
            if (request.Target?.EndsWith(".DeleteMessage", StringComparison.Ordinal) == true)
            {
                using var body = JsonDocument.Parse(request.Body);
                receipts.Add(body.RootElement.GetProperty("ReceiptHandle").GetString()!);
                return AwsWireTransport.Json("{}");
            }
            if (++polls == 1)
            {
                return AwsWireTransport.Json(JsonSerializer.Serialize(new
                {
                    Messages = new[]
                    {
                        new { ReceiptHandle = "unowned", Body = Event(Instance3) },
                        new { ReceiptHandle = "confirmed", Body = Event(Instance1) },
                        new { ReceiptHandle = "mismatch", Body = Event(Instance2) },
                    },
                }));
            }
            stop.Cancel();
            return AwsWireTransport.Json("{}");
        });
        using var sqs = new AmazonSQSClient(AwsWireTransport.Credentials, wire.Configure(new AmazonSQSConfig()));
        var pump = new ControllerQueuePump(configuration, sqs, store, providers, null, clock);

        await pump.RunEc2Async(Queue, ownership, stop.Token);

        Assert.Equal(["unowned", "confirmed"], receipts);
        Assert.Equal(ExecutorResourceState.Running, (await store.ReadExecutorAsync(profile, Allocation, token))?.State.Resource?.State);
        Assert.Null((await store.ReadExecutorAsync(profile, OtherAllocation, token))?.State.Resource);
        var targeted = ec2.Requests.Where(request => request["Action"] == "DescribeInstances");
        Assert.Equal([Instance1, Instance2], targeted.Select(request => request["Filter.4.Value.1"]));
    }

    [Fact(Timeout = 10_000)]
    public async Task FargateDoesNotAcknowledgeInvalidEventAfterDurableAuthorityChangesDuringReceive()
    {
        var token = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var table = await MiniStackTable.CreateAsync(token);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), token);
        var configuration = RuntimeConfigurationLoader.Load(bytes);
        var clock = new QueueClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        using var ownership = await StartOwnershipAsync(table, store, configuration, clock, token);
        var old = Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority()).Lease.Fence;
        using var wire = new AwsWireTransport(static request =>
        {
            Assert.EndsWith(".ReceiveMessage", request.Target);
            return AwsWireTransport.Json("""{"Messages":[{"ReceiptHandle":"invalid","Body":"not-an-event"}]}""");
        });
        wire.BeforeRespondAsync = async (_, ct) => await table.SetLeaseAsync(
            old with { Epoch = old.Epoch + 1, OwnerId = "successor-owner" }, clock.GetUtcNow().AddMinutes(5), ct);
        using var sqs = new AmazonSQSClient(AwsWireTransport.Credentials, wire.Configure(new AmazonSQSConfig()));
        using var ecs = new AmazonECSClient(AwsWireTransport.Credentials, wire.Configure(new AmazonECSConfig()));
        using var iam = new AmazonIdentityManagementServiceClient(AwsWireTransport.Credentials,
            wire.Configure(new AmazonIdentityManagementServiceConfig()));
        using var ec2 = new AmazonEC2Client(AwsWireTransport.Credentials, wire.Configure(new AmazonEC2Config()));
        var inspector = new FargateProfileInspector(ecs, iam, new FargateNetworkReader(ec2), clock);
        var pump = new ControllerQueuePump(configuration, sqs, store, new Dictionary<string, Ec2EffectProvider>(), inspector, clock);

        var running = pump.RunFargateAsync(Queue, ownership, stop.Token);
        try
        {
            await clock.RetryScheduled.WaitAsync(token);
            Assert.Equal(old, Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority()).Lease.Fence);
            Assert.Equal(old.Epoch + 1, (await store.ReadLeaseAsync(old.Deployment, token))?.Epoch);
            Assert.EndsWith(".ReceiveMessage", Assert.Single(wire.Requests).Target);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    private static async Task<ControllerLeaseOwnership> StartOwnershipAsync(MiniStackTable table, DynamoDbLifecycleStore store,
        RuntimeConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        string deployment = RunnerOwnershipTags.DeploymentIdentity(configuration);
        var now = clock.GetUtcNow();
        var window = TimeSpan.FromMinutes(5);
        await table.SetLeaseAsync(table.Lease with { Deployment = deployment }, now + window, cancellationToken);
        var lease = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(deployment, cancellationToken));
        return new ControllerLeaseOwnership(store, lease, new ControllerClockEvidence(now, now + window, TimeSpan.Zero),
            clock, window, TimeSpan.FromSeconds(2));
    }

    private static ExecutorBinding Binding(LifecycleProfile profile, string instance, string allocation)
    {
        var launch = DateTimeOffset.Parse(OriginalLaunch, CultureInfo.InvariantCulture);
        return new ExecutorBinding(profile, allocation, $"arn:aws:ec2:{Region}:{Account}:instance/{instance}",
            Ec2ExecutorIncarnation.Compute(Account, Region, instance, launch, 12), new Ec2ExecutorIdentity(launch, TemplateId, 12));
    }

    private static string Event(string instance) => $$"""
        {"source":"aws.ec2","detail-type":"EC2 Instance State-change Notification","account":"{{Account}}","region":"{{Region}}",
         "resources":["arn:aws:ec2:{{Region}}:{{Account}}:instance/{{instance}}"],"detail":{"instance-id":"{{instance}}","state":"terminated"} }
        """;

    private sealed class QueueClock : TimeProvider
    {
        private readonly TaskCompletionSource _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task RetryScheduled => _retry.Task;

        public override DateTimeOffset GetUtcNow() => MiniStackTable.Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == TimeSpan.FromSeconds(1))
            {
                _retry.TrySetResult();
            }
            return System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
