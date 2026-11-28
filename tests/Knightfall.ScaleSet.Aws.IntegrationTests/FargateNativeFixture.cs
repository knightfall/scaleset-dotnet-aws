using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Xunit;
using EcsTask = Amazon.ECS.Model.Task;
using Task = System.Threading.Tasks.Task;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class FargateNativeFixture : IDisposable
{
    internal const string Account = "000000000000";
    internal const string Region = "us-east-1";
    internal const string Scope = "https://github.com/enterprises/engineering";
    internal const string ProfileId = "linux-shell";
    internal const string Connection = "engineering";
    internal const string Group = "No-Docker";
    internal const string Runner = "runner";
    private const string FirstController = "arn:aws:ecs:us-east-1:000000000000:task/engineering-dev-controller/00000000000000000000000000000001";
    internal const string SecondController = "arn:aws:ecs:us-east-1:000000000000:task/engineering-dev-controller/00000000000000000000000000000002";
    private const string Trust = """
        {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]}
        """;
    internal static readonly TimeSpan Reserve = TimeSpan.FromSeconds(2);
    internal static readonly EffectDispatcherTiming Timing = new(
        TimeSpan.FromSeconds(12), Reserve, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
    internal static readonly FargateContainerApproval Approval = new(
        "runner-fixture.invalid/job@sha256:" + new string('a', 64),
        ["/opt/scaleset/RunnerBootstrap"], ["fargate", "/opt/scaleset/bootstrap.json"]);

    private readonly Uri _endpoint;
    private readonly AmazonEC2Client _network;
    private readonly AmazonSecretsManagerClient _secrets;
    private readonly AmazonSecurityTokenServiceClient _sts;
    private readonly AmazonECSClient _nativeEcs;
    private readonly AmazonECSClient _ecs;
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly string _template;

    private FargateNativeFixture(Uri endpoint, MiniStackTable table, string template)
    {
        _endpoint = endpoint;
        _template = template;
        Table = table;
        Wire = new FargateNativeWire(endpoint);
        _network = new AmazonEC2Client(Admin(), Wire.Configure<AmazonEC2Config>());
        _ecs = new AmazonECSClient(Admin(), Wire.Configure<AmazonECSConfig>());
        _nativeEcs = new AmazonECSClient(Admin(), Native<AmazonECSConfig>(endpoint));
        Iam = new AmazonIdentityManagementServiceClient(Admin(), Wire.Configure<AmazonIdentityManagementServiceConfig>());
        _secrets = new AmazonSecretsManagerClient(Admin(), Native<AmazonSecretsManagerConfig>(endpoint));
        _sts = new AmazonSecurityTokenServiceClient(Admin(), Wire.Configure<AmazonSecurityTokenServiceConfig>());
        Clock = new FargateFixtureClock();
        Store = new DynamoDbLifecycleStore(table.Client, table.Name, Clock);
        Journal = new DynamoDbEffectJournal(Store, Clock);
        Dispatcher = new DynamoDbEffectDispatcher(Store, Clock, Timing);
        Inspector = new FargateProfileInspector(_ecs, Iam, new FargateNetworkReader(_network), Clock);
    }

    internal FargateNativeWire Wire { get; }
    internal FargateFixtureClock Clock { get; }
    internal MiniStackTable Table { get; }
    internal DynamoDbLifecycleStore Store { get; }
    internal DynamoDbEffectJournal Journal { get; }
    internal DynamoDbEffectDispatcher Dispatcher { get; }
    internal FargateProfileInspector Inspector { get; }
    internal AmazonIdentityManagementServiceClient Iam { get; }
    internal RuntimeConfiguration Configuration { get; private set; } = null!;
    internal LifecycleProfile Profile { get; private set; } = null!;
    internal Role TaskRole { get; private set; } = null!;
    internal string ClusterArn { get; private set; } = null!;
    internal string DefinitionArn { get; private set; } = null!;
    internal string Family => "t24-" + _suffix;
    private string SecretArn { get; set; } = null!;
    private string ExecutionRoleArn { get; set; } = null!;
    private string VpcId { get; set; } = null!;
    private string SubnetId { get; set; } = null!;
    private string Zone { get; set; } = null!;
    private string SecurityGroupId { get; set; } = null!;

    internal static async Task<FargateNativeFixture> CreateAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("SCALESET_MINISTACK_ENDPOINT"), UriKind.Absolute, out var endpoint) ||
            endpoint.Host != "127.0.0.1" || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.IsDefaultPort ||
            endpoint.AbsolutePath != "/" || !Uri.TryCreate(Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL"),
                UriKind.Absolute, out var localAws) || endpoint != localAws)
        {
            throw new InvalidOperationException("Use make check-integration for the pinned loopback MiniStack endpoint.");
        }

        string template = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken);
        var table = await MiniStackTable.CreateAsync(cancellationToken);
        var fixture = new FargateNativeFixture(endpoint, table, template);
        try
        {
            await fixture.SeedAsync(cancellationToken);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        TaskRole = (await Iam.CreateRoleAsync(new CreateRoleRequest
        {
            RoleName = Family + "-bootstrap",
            AssumeRolePolicyDocument = Trust,
        }, cancellationToken)).Role;
        ExecutionRoleArn = (await Iam.CreateRoleAsync(new CreateRoleRequest
        {
            RoleName = Family + "-execution",
            AssumeRolePolicyDocument = Trust,
        }, cancellationToken)).Role.Arn;
        ClusterArn = (await _ecs.CreateClusterAsync(new CreateClusterRequest { ClusterName = Family }, cancellationToken))
            .Cluster.ClusterArn;

        var vpc = await _network.CreateVpcAsync(new CreateVpcRequest { CidrBlock = "10.80.0.0/16" }, cancellationToken);
        VpcId = vpc.Vpc.VpcId;
        var subnet = await _network.CreateSubnetAsync(new CreateSubnetRequest
        {
            VpcId = VpcId,
            CidrBlock = "10.80.1.0/24",
            AvailabilityZone = "us-east-1a",
        }, cancellationToken);
        SubnetId = subnet.Subnet.SubnetId;
        Zone = subnet.Subnet.AvailabilityZone;
        var group = await _network.CreateSecurityGroupAsync(new CreateSecurityGroupRequest
        {
            VpcId = VpcId,
            GroupName = Family,
            Description = "T24 native runner network",
        }, cancellationToken);
        SecurityGroupId = group.GroupId;
        SecretArn = (await _secrets.CreateSecretAsync(new CreateSecretRequest
        {
            Name = Family,
            ClientRequestToken = _suffix,
            SecretString = JsonSerializer.Serialize(new { token = GitHubSimulator.FirstPat }),
        }, cancellationToken)).ARN;

        DefinitionArn = await RegisterDefinitionAsync(sidecar: false, privileged: false, cancellationToken);
        Configuration = ConfigurationFor(DefinitionArn);
        Profile = new LifecycleProfile(ControllerLeaseRecoveryCoordinator.DeploymentKey(Configuration), ProfileId);
    }

    internal async Task<string> RegisterDefinitionAsync(bool sidecar, bool privileged, CancellationToken cancellationToken)
    {
        var runner = new ContainerDefinition
        {
            Name = Runner,
            Image = Approval.Image,
            Essential = true,
            Privileged = privileged,
            ReadonlyRootFilesystem = true,
            User = "1001:1001",
            LinuxParameters = new LinuxParameters { Capabilities = new KernelCapabilities { Drop = ["ALL"] } },
            EntryPoint = [.. Approval.EntryPoint],
            Command = [.. Approval.Command],
        };
        var result = await _ecs.RegisterTaskDefinitionAsync(new RegisterTaskDefinitionRequest
        {
            Family = Family,
            NetworkMode = NetworkMode.Awsvpc,
            RequiresCompatibilities = ["FARGATE"],
            Cpu = "1024",
            Memory = "2048",
            TaskRoleArn = TaskRole.Arn,
            ExecutionRoleArn = ExecutionRoleArn,
            ContainerDefinitions = sidecar
                ? [runner, new ContainerDefinition { Name = "sidecar", Image = Approval.Image, Essential = false }]
                : [runner],
        }, cancellationToken);
        return result.TaskDefinition.TaskDefinitionArn;
    }

    internal RuntimeConfiguration ConfigurationFor(string definition, bool drainingWarmReserve = false)
    {
        var root = JsonNode.Parse(_template.Replace("111122223333", Account, StringComparison.Ordinal)
            .Replace("ap-southeast-2", Region, StringComparison.Ordinal))!.AsObject();
        var spec = root["spec"]!;
        var github = spec["github"]![Connection]!;
        github["scopeURL"] = Scope;
        github["auth"]!["type"] = "enterprise-pat";
        github["auth"]!["secretArn"] = SecretArn;
        spec["tracking"]!["table"] = new JsonObject { ["type"] = "import", ["name"] = Table.Name };
        var profile = spec["profiles"]![0]!;
        if (drainingWarmReserve)
        {
            profile["state"] = "draining";
            profile["capacity"]!["idleReserve"] = 1;
            profile["capacity"]!["maxRunners"] = 1;
        }
        var runner = profile["provisioner"]!;
        string cluster = runner["cluster"]!.GetValue<string>();
        spec["ecsClusters"]![cluster] = new JsonObject { ["type"] = "import", ["arn"] = ClusterArn };
        runner["taskDefinitionArn"] = definition;
        runner["bootstrapRoleArn"] = TaskRole.Arn;
        runner["executionRoleArn"] = ExecutionRoleArn;
        var network = spec["networks"]![runner["network"]!.GetValue<string>()]!;
        network["vpcId"] = VpcId;
        network["subnets"] = new JsonArray(new JsonObject { ["id"] = SubnetId, ["az"] = Zone });
        network["securityGroupIds"] = new JsonArray(JsonValue.Create(SecurityGroupId));
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    internal async Task<ControllerLease> StartOwnerAsync(CancellationToken token, string taskArn = FirstController,
        RuntimeConfiguration? rolloutConfiguration = null, long generation = 1)
    {
        var now = Clock.GetUtcNow();
        var acquired = await Store.TryAcquireLeaseAsync(Profile.Deployment, Guid.NewGuid().ToString("N"), taskArn,
            ControllerLease.CurrentWriterProtocol, now.AddSeconds(-1), now.AddHours(2), token);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquired.Outcome);
        var recovery = new ControllerLeaseRecoveryCoordinator(Store, new UnexpectedControllerRead(), Clock, Reserve);
        var result = await recovery.TryActivateAsync(Assert.IsType<ControllerLease>(acquired.Lease), Configuration,
            Clock.Proof(), [], token);
        Assert.Equal(LeaseActivationStatus.Activated, result.Status);
        var lease = Assert.IsType<ControllerLease>(result.Lease);
        for (int pass = 0; ; pass++)
        {
            var rollout = await Store.AdvanceCatalogRolloutAsync(lease.Fence, rolloutConfiguration ?? Configuration, generation, token);
            if (rollout.Status != CatalogRolloutStatus.Staging || pass == 3)
            {
                Assert.Equal(CatalogRolloutStatus.Applied, rollout.Status);
                break;
            }
        }
        Assert.Equal(EffectIndexState.Ready, (await Store.AdvanceEffectRebuildAsync(Profile, lease.Fence, token)).Head?.State);
        return lease;
    }

    internal async Task<FargateLaunchEffectRequest> PlanLaunchAsync(ControllerLease lease, CancellationToken token)
    {
        var preflight = await Inspector.ResolvePreflightAsync(Configuration, ProfileId, Approval, token);
        Assert.Equal(FargatePreflightStatus.Verified, preflight.Status);
        var resolved = Assert.IsType<FargateResolvedTask>(preflight.Task);
        var inventory = await Inspector.ReadInventoryAsync(Configuration, ProfileId, token);
        Assert.True(inventory.IsProfileComplete);
        var tracked = await Store.ReadExecutorInventoryAsync(Profile, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await Store.PublishExecutorInventoryAsync(Profile, lease.Fence, tracked.Revision,
            new ExecutorInventoryObservation(Clock.GetUtcNow(), IsComplete: true, BackendCapacityCeiling: 10, BackendLaunchHeadroom: 8), token));
        var request = FargateProfileInspector.CreateLaunchRequest(resolved, Guid.NewGuid(), [SubnetId]);
        var now = Clock.GetUtcNow();
        var envelope = EffectEnvelope.Create(Profile, Intent(request), 1, request, now, now.AddMinutes(50));
        Assert.Equal(LifecycleWriteOutcome.Committed, await Journal.PlanAsync(lease, envelope, Guid.NewGuid(), token));
        return request;
    }

    internal async Task BindScaleSetAsync(ControllerLease lease, long groupId, long scaleSetId, CancellationToken token)
    {
        var catalog = Assert.IsType<CatalogProfile>(await Store.ReadCatalogProfileAsync(Profile, token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await Store.BindCatalogScaleSetAsync(lease.Fence, catalog, groupId, scaleSetId, token));
    }

    internal Task<EffectDispatchStatus> DispatchAsync(ControllerLease lease, FargateLaunchEffectRequest launch,
        IFargateReplayEffectProvider provider, CancellationToken token) =>
        Dispatcher.DispatchPlannedAsync(lease, Clock.Proof(), Profile, Intent(launch), provider, Clock.GetUtcNow().AddMinutes(1),
            CancellationToken.None, token);

    internal async Task<SessionAWSCredentials> AssumeTaskAsync(string taskArn, CancellationToken token)
    {
        string id = taskArn[(taskArn.LastIndexOf('/') + 1)..];
        var session = await _sts.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = TaskRole.Arn,
            RoleSessionName = id,
            DurationSeconds = 900,
        }, token);
        Assert.Equal(TaskRole.RoleId + ":" + id, session.AssumedRoleUser.AssumedRoleId);
        return new SessionAWSCredentials(session.Credentials.AccessKeyId, session.Credentials.SecretAccessKey, session.Credentials.SessionToken);
    }

    internal AmazonSecurityTokenServiceClient TaskSts(SessionAWSCredentials credentials) =>
        new(credentials, Wire.Configure<AmazonSecurityTokenServiceConfig>());

    internal AmazonDynamoDBClient TaskDynamoDb(SessionAWSCredentials credentials) =>
        new(credentials, Wire.Configure<AmazonDynamoDBConfig>());

    internal async Task<EcsTask> ReadNativeTaskAsync(string taskArn, CancellationToken token)
    {
        var result = await _ecs.DescribeTasksAsync(new DescribeTasksRequest
        {
            Cluster = ClusterArn, Tasks = [taskArn], Include = ["TAGS"],
        }, token);
        Assert.Empty(result.Failures ?? []);
        return Assert.Single(result.Tasks);
    }

    internal async Task RecordProcessExitAsync(string taskArn, CancellationToken token)
    {
        // MiniStack's metadata-only task has no child. Advance its native lifecycle after the simulated worker exits.
        await _nativeEcs.StopTaskAsync(new StopTaskRequest
        {
            Cluster = ClusterArn, Task = Wire.NativeArn(taskArn),
            Reason = "T24 simulated essential process exited",
        }, token);
    }

    internal IFargateReplayEffectProvider Provider() => Inspector.CreateEffectProvider(Configuration, Profile, Approval);

    internal static string Intent(FargateLaunchEffectRequest launch) => EffectIntentIdentity.ForAllocation(launch)!;

    internal AmazonSecretsManagerClient CreateSecretsClient() => new(Admin(), Native<AmazonSecretsManagerConfig>(_endpoint));

    private static T Native<T>(Uri endpoint) where T : ClientConfig, new() => ControllerAwsClientConfiguration.Create<T>(Region, endpoint);

    private static BasicAWSCredentials Admin() => new(Account, "test-only-secret");

    public void Dispose()
    {
        _nativeEcs.Dispose();
        _ecs.Dispose();
        _network.Dispose();
        Iam.Dispose();
        _secrets.Dispose();
        _sts.Dispose();
        Wire.Dispose();
        Table.Dispose();
    }

    private sealed class UnexpectedControllerRead : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A first or cleanly released owner does not require a predecessor read.");
    }
}

internal sealed class FargateFixtureClock : TimeProvider
{
    private long _ticks = TimeProvider.System.GetUtcNow().AddMinutes(1).UtcTicks;

    internal DateTimeOffset Now
    {
        get => new(Volatile.Read(ref _ticks), TimeSpan.Zero);
        set => Volatile.Write(ref _ticks, value.UtcTicks);
    }

    public override DateTimeOffset GetUtcNow() => Now;

    internal ControllerClockEvidence Proof() => new(Now, Now.AddMinutes(3), TimeSpan.Zero);
}
