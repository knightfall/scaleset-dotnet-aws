using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Microsoft.AspNetCore.Http;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed record MixedRuntimeProfile(string Id, GitHubSimulator GitHub, string BootstrapRoleId);

internal sealed class MixedRuntimeFixture : IAsyncDisposable
{
    internal static readonly string[] ProfileIds = ["linux-ec2-docker", "linux-ec2-build", "linux-shell", "linux-ci-arm"];
    internal static IEnumerable<string> GitHubHosts => ProfileIds.SelectMany(profile => GitHubSimulatorAddress.ForProfile(profile).Hosts);
    private const string Trust = """
        {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ec2.amazonaws.com"},"Action":"sts:AssumeRole"}]}
        """;

    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("scaleset-mixed-");
    private readonly List<IDisposable> _seedFixtures = [];

    private MixedRuntimeFixture(MixedControllerCloud cloud, MiniStackTable table)
    {
        Cloud = cloud;
        Table = table;
        Store = new DynamoDbLifecycleStore(table.Client, table.Name, TimeProvider.System);
        ConfigurationPath = Path.Combine(_workspace.FullName, "runtime.json");
    }

    internal MixedControllerCloud Cloud { get; }
    internal MiniStackTable Table { get; }
    internal DynamoDbLifecycleStore Store { get; }
    internal RuntimeConfiguration Configuration { get; private set; } = null!;
    internal GitHubProtocolHost GitHub { get; private set; } = null!;
    internal Dictionary<string, MixedRuntimeProfile> Profiles { get; } = new(StringComparer.Ordinal);
    internal string ConfigurationPath { get; }
    internal string ControllerCluster { get; private set; } = null!;
    internal string FargateEvidence { get; private set; } = null!;
    private string ControllerDefinition { get; set; } = null!;
    private string ControllerSubnet { get; set; } = null!;

    internal static async Task<MixedRuntimeFixture> CreateAsync(string mode, CancellationToken token,
        bool sharedFargateCluster = false, long remainingFargateCpuUnits = 32_768, int? warmFargateMaximum = null)
    {
        var cloud = await MixedControllerCloud.StartAsync(token);
        MixedRuntimeFixture? fixture = null;
        try
        {
            fixture = new MixedRuntimeFixture(cloud, await MiniStackTable.CreateAsync(token));
            await fixture.SeedAsync(mode, sharedFargateCluster, remainingFargateCpuUnits, warmFargateMaximum, token);
            return fixture;
        }
        catch
        {
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
            else
            {
                await cloud.DisposeAsync();
            }
            throw;
        }
    }

    private async Task SeedAsync(string mode, bool sharedFargateCluster, long remainingFargateCpuUnits, int? warmFargateMaximum,
        CancellationToken token)
    {
        string raw = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mixed-ec2-fargate.json"), token);
        var root = JsonNode.Parse(raw.Replace("111122223333", MixedControllerCloud.Account, StringComparison.Ordinal)
            .Replace("ap-southeast-2", MixedControllerCloud.Region, StringComparison.Ordinal))!;
        var spec = root["spec"]!;
        string deployment = "t25-" + Guid.NewGuid().ToString("N");
        root["metadata"]!["name"] = deployment;
        spec["tracking"]!["table"] = new JsonObject { ["type"] = "import", ["name"] = Table.Name };

        var connections = new JsonObject();
        var networks = new JsonObject();
        var clusters = new JsonObject();
        spec["github"] = connections;
        spec["networks"] = networks;
        spec["ecsClusters"] = clusters;

        var host = await FargateNativeFixture.CreateAsync(token);
        _seedFixtures.Add(host);
        var hostProvisioner = Assert.IsType<FargateProvisionerConfiguration>(host.Configuration.Profiles[0].Provisioner);
        var hostNetwork = host.Configuration.Networks[hostProvisioner.Network];
        using var ecs = new AmazonECSClient(MixedControllerCloud.Credentials, Native<AmazonECSConfig>());
        ControllerCluster = (await ecs.CreateClusterAsync(new CreateClusterRequest
        {
            ClusterName = deployment + "-controller",
        }, token)).Cluster.ClusterArn;
        ControllerDefinition = host.DefinitionArn;
        ControllerSubnet = Assert.Single(hostNetwork.Subnets).Id;
        networks["controller-private"] = Network(hostNetwork);
        clusters["control-plane"] = new JsonObject { ["type"] = "import", ["arn"] = ControllerCluster };

        using var iam = new AmazonIdentityManagementServiceClient(MixedControllerCloud.Credentials, Native<AmazonIdentityManagementServiceConfig>());
        using var ec2 = new AmazonEC2Client(MixedControllerCloud.Credentials, Native<AmazonEC2Config>());
        var profiles = spec["profiles"]!.AsArray();
        var selected = new List<JsonNode>();
        var approvals = new Dictionary<string, FargateContainerApproval>(StringComparer.Ordinal);
        for (int index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index]!;
            string kind = profile["provisioner"]!["type"]!.GetValue<string>();
            if (mode != "mixed" && mode != kind)
            {
                continue;
            }

            string id = profile["id"]!.GetValue<string>();
            string networkName = "network-" + id;
            string roleId;
            var provisioner = profile["provisioner"]!;
            provisioner["network"] = networkName;
            profile["platform"]!["arch"] = "x64";
            int? warmMaximum = kind == "fargate" ? warmFargateMaximum : null;
            profile["capacity"]!["idleReserve"] = warmMaximum is null ? 0 : 1;
            profile["capacity"]!["maxRunners"] = warmMaximum ?? 1;

            if (kind == "ec2")
            {
                var role = (await iam.CreateRoleAsync(new CreateRoleRequest
                {
                    RoleName = DirectEc2Fixture.RoleName(), AssumeRolePolicyDocument = Trust,
                }, token)).Role;
                var source = await DirectEc2Fixture.CreateAsync(Cloud.NativeEndpoint, MixedControllerCloud.Credentials, ec2, iam,
                    MixedControllerCloud.Account, MixedControllerCloud.Region, role.Arn, token);
                _seedFixtures.Add(source);
                networks[networkName] = Network(source.Network);
                provisioner["launchTemplateId"] = source.TemplateId;
                provisioner["launchTemplateVersion"] = new JsonObject { ["type"] = "pinned", ["version"] = "1" };
                provisioner["instanceProfileArn"] = source.InstanceProfileArn;
                provisioner["bootstrapRoleArn"] = role.Arn;
                roleId = role.RoleId;
            }
            else
            {
                var source = await FargateNativeFixture.CreateAsync(token);
                _seedFixtures.Add(source);
                var fargateProfile = Assert.IsType<FargateProvisionerConfiguration>(source.Configuration.Profiles[0].Provisioner);
                if (sharedFargateCluster)
                {
                    provisioner["cluster"] = "control-plane";
                }
                else
                {
                    string cluster = provisioner["cluster"]!.GetValue<string>();
                    clusters[cluster] = new JsonObject { ["type"] = "import", ["arn"] = source.ClusterArn };
                }
                networks[networkName] = Network(source.Configuration.Networks[fargateProfile.Network]);
                provisioner["taskDefinitionArn"] = source.DefinitionArn;
                provisioner["bootstrapRoleArn"] = fargateProfile.BootstrapRoleArn;
                provisioner["executionRoleArn"] = fargateProfile.ExecutionRoleArn;
                roleId = source.TaskRole.RoleId;
                approvals.Add(id, FargateNativeFixture.Approval with { ScratchMountPaths = [] });
            }

            // These secrets and HTTPS tenants are isolated traffic within the test process.
            connections[id] = new JsonObject
            {
                ["scopeURL"] = $"https://{id}.ghe.com/enterprises/engineering",
                ["auth"] = new JsonObject
                {
                    ["type"] = "enterprise-pat", ["secretArn"] = host.Configuration.GitHub[FargateNativeFixture.Connection].SecretArn,
                },
            };
            var github = profile["github"]!.AsObject();
            github["connection"] = id;
            github["registration"] = "existing";
            github["scaleSetId"] = GitHubSimulator.SetId.ToString(CultureInfo.InvariantCulture);
            var simulator = new GitHubSimulator(github["runnerGroup"]!.GetValue<string>(), GitHubSimulatorAddress.ForProfile(id));
            simulator.SeedScaleSet(github["scaleSet"]!.GetValue<string>(), github["labels"]!.AsArray().Select(label => label!.GetValue<string>()));
            Profiles.Add(id, new MixedRuntimeProfile(id, simulator, roleId));
            selected.Add(profile);
        }
        profiles.Clear();
        foreach (var profile in selected)
        {
            profiles.Add(profile);
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        await File.WriteAllBytesAsync(ConfigurationPath, bytes, token);
        Configuration = RuntimeConfigurationLoader.Load(bytes);

        var now = TimeProvider.System.GetUtcNow();
        FargateEvidence = JsonSerializer.Serialize(new
        {
            profiles = approvals,
            quota = new FargateQuotaEvidence(MixedControllerCloud.Account, MixedControllerCloud.Region,
                now, now.AddMinutes(10), remainingFargateCpuUnits, 0),
        }, JsonSerializerOptions.Web);
        GitHub = await GitHubProtocolHost.StartAsync(Profiles.Values.Select(value => value.GitHub).ToArray(), token, PacePollsAsync);
    }

    internal Task<string> StartControllerTaskAsync(CancellationToken token) =>
        Cloud.StartControllerTaskAsync(ControllerCluster, ControllerDefinition, ControllerSubnet, token);

    internal Task<string> StartUnrelatedServiceTaskAsync(CancellationToken token) =>
        Cloud.StartControllerTaskAsync(ControllerCluster, ControllerDefinition, ControllerSubnet, token, "service:unrelated-fixture");

    internal void EnableRefreshableQuota()
    {
        var input = JsonNode.Parse(FargateEvidence)!.AsObject();
        input["quotaSource"] = new JsonObject
        {
            ["bucket"] = MixedControllerCloud.QuotaBucket,
            ["key"] = MixedControllerCloud.QuotaKey,
        };
        FargateEvidence = input.ToJsonString();
    }

    private T Native<T>() where T : ClientConfig, new() =>
        ControllerAwsClientConfiguration.Create<T>(MixedControllerCloud.Region, Cloud.NativeEndpoint);

    private static JsonObject Network(Knightfall.ScaleSet.Aws.Core.Configuration.NetworkConfiguration network) => new()
    {
        ["vpcId"] = network.VpcId,
        ["subnets"] = new JsonArray(network.Subnets.Select(subnet => (JsonNode)new JsonObject
            { ["id"] = subnet.Id, ["az"] = subnet.AvailabilityZone }).ToArray()),
        ["securityGroupIds"] = new JsonArray(network.SecurityGroupIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
    };

    private static async Task PacePollsAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.StartsWithSegments("/messages"))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(125), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Abrupt child termination closes the loopback transport.
            }
        }
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (GitHub is not null)
        {
            await GitHub.DisposeAsync();
        }
        await Cloud.DisposeAsync();
        foreach (var seed in _seedFixtures)
        {
            seed.Dispose();
        }
        Table.Dispose();
        _workspace.Delete(recursive: true);
    }
}
