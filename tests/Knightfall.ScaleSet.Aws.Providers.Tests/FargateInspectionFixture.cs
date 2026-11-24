using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.IdentityManagement;
using Amazon.SQS;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

internal sealed class FargateInspectionFixture : IDisposable
{
    internal const string Account = "111122223333";
    internal const string Region = "ap-southeast-2";
    internal const string Deployment = "111122223333:ap-southeast-2:engineering-dev";
    internal const string Profile = "linux-shell";
    internal const string Cluster = "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-shell";
    internal const string DefinitionArn = "arn:aws:ecs:ap-southeast-2:111122223333:task-definition/linux-shell-runner:7";
    internal const string TaskRoleName = "fargate-shell-bootstrap";
    internal const string ExecutionRoleName = "fargate-shell-execution";
    internal const string TaskRoleId = "AROAEXAMPLETASK000001";
    internal const string ExecutionRoleId = "AROAEXAMPLEEXEC000001";
    internal const string Subnet1 = "subnet-0123456789abcdef2";
    internal const string Subnet2 = "subnet-0123456789abcdef3";
    internal const string Vpc = "vpc-0123456789abcdef0";
    internal const string SecurityGroup = "sg-0123456789abcdef1";
    internal const string QueueUrl = "https://sqs.ap-southeast-2.amazonaws.com/111122223333/runner-events";
    internal const string TaskPrefix = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-shell/";
    internal const string FirstTask = TaskPrefix + "0123456789abcdef0123456789abcdef";
    internal const string SecondTask = TaskPrefix + "0123456789abcdef0123456789abcdee";
    internal const string ThirdTask = TaskPrefix + "0123456789abcdef0123456789abcded";
    internal static readonly Guid FirstAllocation = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    internal static readonly DateTimeOffset Now = new(2050, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static readonly FargateContainerApproval Approval = new(
        "111122223333.dkr.ecr.ap-southeast-2.amazonaws.com/runner@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        ["/app/runner-bootstrap"], ["run"]);

    private readonly AwsWireTransport _wire;
    private readonly AmazonECSClient _ecs;
    private readonly AmazonIdentityManagementServiceClient _iam;
    private readonly AmazonEC2Client _network;
    private readonly FixtureClock _clock = new();

    internal FargateInspectionFixture(int maxErrorRetry = 0, int maxStaleConnectionRetries = 0)
    {
        _wire = new AwsWireTransport(Respond);
        var ecs = _wire.Configure(new AmazonECSConfig());
        ecs.AuthenticationRegion = Region;
        ecs.MaxErrorRetry = maxErrorRetry;
        ecs.MaxStaleConnectionRetries = maxStaleConnectionRetries;
        ecs.AllowAutoRedirect = false;
        _ecs = new AmazonECSClient(AwsWireTransport.Credentials, ecs);
        _iam = new AmazonIdentityManagementServiceClient(AwsWireTransport.Credentials, _wire.Configure(new AmazonIdentityManagementServiceConfig()));
        var network = _wire.Configure(new AmazonEC2Config());
        network.AuthenticationRegion = Region;
        _network = new AmazonEC2Client(AwsWireTransport.Credentials, network);
        Inspector = new FargateProfileInspector(_ecs, _iam, new FargateNetworkReader(_network), _clock);
    }

    internal FargateProfileInspector Inspector { get; }
    internal AmazonSQSClient CreateSqs() => new(AwsWireTransport.Credentials, _wire.Configure(new AmazonSQSConfig()));
    internal IReadOnlyList<AwsWireRequest> Requests => _wire.Requests;
    internal DateTimeOffset UtcNow
    {
        get => _clock.GetUtcNow();
        set => _clock.SetUtcNow(value);
    }
    internal Func<AwsWireRequest, CancellationToken, Task>? BeforeRespondAsync
    {
        set => _wire.BeforeRespondAsync = value;
    }
    internal JsonObject TaskDefinition { get; } = NewTaskDefinition();
    internal string ClusterResponse { get; set; } = ClusterReply();
    internal Func<JsonElement, string> Settings { get; set; } = static _ => SettingsReply();
    internal Func<JsonElement, string> ListTasks { get; set; } = static _ => """{"taskArns":[]}""";
    internal Func<JsonElement, string> DescribeTasks { get; set; } = static _ => """{"tasks":[]}""";
    internal Func<JsonElement, HttpResponseMessage> RunTask { get; set; } = static _ => Ecs("""{"tasks":[]}""");
    internal Func<JsonElement, HttpResponseMessage> StopTask { get; set; } = static _ => Ecs("{}");
    internal Func<JsonElement, string> ReceiveMessage { get; set; } = static _ => "{}";
    internal int? Ip1 { get; set; } = 3;
    internal int? Ip2 { get; set; } = 4;
    internal string SubnetVpc { get; set; } = Vpc;
    internal string GroupVpc { get; set; } = Vpc;
    internal bool WrongIamArn { get; set; }

    internal static async Task<RuntimeConfiguration> ConfigurationAsync(CancellationToken cancellationToken,
        bool fargate = true, bool dryRun = false, bool inspectAws = false, bool createRunnerCluster = false,
        string runnerClusterName = "engineering-shell")
    {
        string file = fargate ? "fargate-only.json" : "ec2-only-latest.json";
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, file), cancellationToken))!;
        if (dryRun)
        {
            document["spec"]!["mode"] = "dry-run";
            document["spec"]!["dryRun"]!["inspectAws"] = inspectAws;
        }
        if (createRunnerCluster)
        {
            document["spec"]!["ecsClusters"]!["runner-shell"] = new JsonObject { ["type"] = "create", ["name"] = runnerClusterName };
        }
        return RuntimeConfigurationLoader.Load(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    internal static string ClusterReply(string arn = Cluster, string state = "ACTIVE") =>
        $$"""{"clusters":[{"clusterArn":"{{arn}}","status":"{{state}}"}],"failures":[]}""";

    internal static string SettingsReply(string value = "enabled") =>
        $$"""{"settings":[{"name":"taskLongArnFormat","value":"{{value}}","principalArn":"arn:aws:iam::{{Account}}:root"}]}""";

    internal static JsonObject TaskRow(string arn, Guid? allocationId = null, string state = "RUNNING",
        string? attachedSubnet = Subnet1, bool enableExec = false, string? startedBy = null)
    {
        var tags = new JsonArray();
        if (allocationId is Guid id)
        {
            tags.Add(TaskTag(RunnerOwnershipTags.Controller, RunnerOwnershipTags.ControllerValue));
            tags.Add(TaskTag(RunnerOwnershipTags.Deployment, Deployment));
            tags.Add(TaskTag(RunnerOwnershipTags.Profile, Profile));
            tags.Add(TaskTag(RunnerOwnershipTags.Allocation, id.ToString("D")));
            tags.Add(TaskTag(RunnerOwnershipTags.BootstrapProtocol, "v1"));
        }
        var attachments = new JsonArray();
        if (attachedSubnet is not null)
        {
            attachments.Add(new JsonObject
            {
                ["type"] = "ElasticNetworkInterface",
                ["status"] = "ATTACHED",
                ["details"] = new JsonArray(Detail("subnetId", attachedSubnet), Detail("networkInterfaceId", "eni-0123456789abcdef0"),
                    Detail("privateIPv4Address", "10.0.1.25")),
            });
        }
        return new JsonObject
        {
            ["taskArn"] = arn,
            ["clusterArn"] = Cluster,
            ["taskDefinitionArn"] = DefinitionArn,
            ["lastStatus"] = state,
            ["desiredStatus"] = state == "STOPPED" ? "STOPPED" : "RUNNING",
            ["version"] = 1,
            ["launchType"] = "FARGATE",
            ["enableExecuteCommand"] = enableExec,
            ["startedBy"] = startedBy ?? (allocationId is Guid allocation ? FargateProfileInspector.StartedBy(allocation) : "some-service"),
            ["createdAt"] = Now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["attachments"] = attachments,
            ["tags"] = tags,
        };
    }

    internal static string ListReply(string[] arns, string? next = null) => JsonSerializer.Serialize(new { taskArns = arns, nextToken = next });

    internal static string TasksReply(params JsonObject[] tasks) => new JsonObject
    {
        ["tasks"] = new JsonArray([.. tasks.Select(task => task.DeepClone())]),
        ["failures"] = new JsonArray(),
    }.ToJsonString();

    internal static JsonElement Body(AwsWireRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.Clone();
    }

    internal static string Operation(AwsWireRequest request) => request.Target is { } target
        ? target[(target.LastIndexOf('.') + 1)..] : Query(request)["Action"]!;

    internal static NameValueCollection Query(AwsWireRequest request) => HttpUtility.ParseQueryString(request.Body);

    private HttpResponseMessage Respond(AwsWireRequest request) => Operation(request) switch
    {
        "DescribeClusters" => Ecs(ClusterResponse),
        "ListAccountSettings" => Ecs(Settings(Body(request))),
        "DescribeTaskDefinition" => Ecs(new JsonObject { ["taskDefinition"] = TaskDefinition.DeepClone() }.ToJsonString()),
        "ListTasks" => Ecs(ListTasks(Body(request))),
        "DescribeTasks" => Ecs(DescribeTasks(Body(request))),
        "RunTask" => RunTask(Body(request)),
        "StopTask" => StopTask(Body(request)),
        "ReceiveMessage" => AwsWireTransport.Json(ReceiveMessage(Body(request))),
        "DeleteMessage" => AwsWireTransport.Json("{}"),
        "GetRole" => AwsWireTransport.Xml(Role(Query(request)["RoleName"]!)),
        "DescribeSubnets" => AwsWireTransport.Xml(Subnets()),
        "DescribeSecurityGroups" => AwsWireTransport.Xml(Groups()),
        var operation => throw new InvalidOperationException("Unexpected AWS wire operation: " + operation),
    };

    private static HttpResponseMessage Ecs(string payload) => AwsWireTransport.Json(payload, mediaType: "application/x-amz-json-1.1");

    private static JsonObject NewTaskDefinition() => JsonNode.Parse($$"""
        {"taskDefinitionArn":"{{DefinitionArn}}","revision":7,"status":"ACTIVE","networkMode":"awsvpc",
        "requiresCompatibilities":["FARGATE"],"runtimePlatform":{"cpuArchitecture":"X86_64","operatingSystemFamily":"LINUX"},
        "cpu":"1024","memory":"2048","taskRoleArn":"arn:aws:iam::{{Account}}:role/{{TaskRoleName}}",
        "executionRoleArn":"arn:aws:iam::{{Account}}:role/{{ExecutionRoleName}}","containerDefinitions":[{
        "name":"runner","essential":true,"privileged":false,"readonlyRootFilesystem":true,"image":"{{Approval.Image}}","user":"1000",
        "entryPoint":["/app/runner-bootstrap"],"command":["run"]}]}
        """)!.AsObject();

    private string Role(string name)
    {
        string id = name == TaskRoleName ? TaskRoleId : ExecutionRoleId;
        string arn = $"arn:aws:iam::{Account}:role/{(WrongIamArn ? "wrong/" : "")}{name}";
        return $$"""
            <GetRoleResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/"><GetRoleResult><Role>
              <Path>/</Path><RoleName>{{name}}</RoleName><RoleId>{{id}}</RoleId><Arn>{{arn}}</Arn>
            </Role></GetRoleResult><ResponseMetadata><RequestId>role</RequestId></ResponseMetadata></GetRoleResponse>
            """;
    }

    private string Subnets() => $$"""
        <DescribeSubnetsResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/"><requestId>subnets</requestId><subnetSet>
          {{Subnet(Subnet1, "ap-southeast-2a", Ip1)}}{{Subnet(Subnet2, "ap-southeast-2b", Ip2)}}
        </subnetSet></DescribeSubnetsResponse>
        """;

    private string Subnet(string subnet, string az, int? count) => FormattableString.Invariant($$"""
        <item><subnetId>{{subnet}}</subnetId><state>available</state><vpcId>{{SubnetVpc}}</vpcId><availabilityZone>{{az}}</availabilityZone>
        {{(count.HasValue ? FormattableString.Invariant($"<availableIpAddressCount>{count}</availableIpAddressCount>") : "")}}</item>
        """);

    private string Groups() => $$"""
        <DescribeSecurityGroupsResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/"><requestId>groups</requestId><securityGroupInfo>
          <item><groupId>{{SecurityGroup}}</groupId><vpcId>{{GroupVpc}}</vpcId></item>
        </securityGroupInfo></DescribeSecurityGroupsResponse>
        """;

    private static JsonObject TaskTag(string key, string value) => new() { ["key"] = key, ["value"] = value };

    private static JsonObject Detail(string name, string value) => new() { ["name"] = name, ["value"] = value };

    public void Dispose()
    {
        _network.Dispose();
        _iam.Dispose();
        _ecs.Dispose();
        _wire.Dispose();
    }

    private sealed class FixtureClock : TimeProvider
    {
        private long _ticks = Now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _ticks), TimeSpan.Zero);

        internal void SetUtcNow(DateTimeOffset now) => Volatile.Write(ref _ticks, now.UtcTicks);
    }
}
