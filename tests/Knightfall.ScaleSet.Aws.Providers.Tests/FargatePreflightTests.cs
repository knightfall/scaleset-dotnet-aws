using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.FargateInspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class FargatePreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiesOnlyTheExactRunnerClusterDefinitionRolesAndNetwork(bool createRunnerCluster)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            Settings = body => body.TryGetProperty("nextToken", out _) ? SettingsReply() :
                """{"settings":[],"nextToken":"opaque+a=1"}""",
        };
        var config = await ConfigurationAsync(token, dryRun: true, inspectAws: true, createRunnerCluster: createRunnerCluster);

        var result = await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token);
        Assert.Equal(FargatePreflightStatus.Verified, result.Status);
        Assert.True(result.IsReady);
        var verified = Assert.IsType<FargateResolvedTask>(result.Task);
        Assert.Equal(Cluster, verified.Scope.ClusterArn);
        Assert.Equal(DefinitionArn, verified.Scope.TaskDefinitionArn);
        Assert.Equal(7, verified.TaskDefinitionRevision);
        Assert.Equal(1024, verified.CpuUnits);
        Assert.Equal(2048, verified.MemoryMiB);
        Assert.Equal(TaskRoleId, verified.TaskRoleId);
        Assert.Equal(ExecutionRoleId, verified.ExecutionRoleId);
        Assert.Equal(new[] { new FargateSubnet(Subnet1, "ap-southeast-2a", 3), new FargateSubnet(Subnet2, "ap-southeast-2b", 4) },
            verified.Subnets);

        var requests = aws.Requests;
        string[] expectedOperations = ["DescribeClusters", "ListAccountSettings", "ListAccountSettings", "DescribeTaskDefinition", "GetRole",
            "GetRole", "DescribeSubnets", "DescribeSecurityGroups"];
        Assert.Equal(expectedOperations, requests.Select(Operation));
        Assert.Equal(Cluster, Assert.Single(Body(requests[0]).GetProperty("clusters").EnumerateArray()).GetString());
        Assert.Equal("taskLongArnFormat", Body(requests[1]).GetProperty("name").GetString());
        Assert.True(Body(requests[1]).GetProperty("effectiveSettings").GetBoolean());
        Assert.Equal("opaque+a=1", Body(requests[2]).GetProperty("nextToken").GetString());
        Assert.Equal(DefinitionArn, Body(requests[3]).GetProperty("taskDefinition").GetString());
        Assert.Equal(TaskRoleName, Query(requests[4])["RoleName"]);
        Assert.Equal(ExecutionRoleName, Query(requests[5])["RoleName"]);
        Assert.Equal(Subnet1, Query(requests[6])["SubnetId.1"]);
        Assert.Equal(Subnet2, Query(requests[6])["SubnetId.2"]);
        Assert.Equal(SecurityGroup, Query(requests[7])["GroupId.1"]);
    }

    [Theory]
    [InlineData("cluster", FargatePreflightStatus.ClusterMismatch)]
    [InlineData("inactive-cluster", FargatePreflightStatus.ClusterMismatch)]
    [InlineData("long-arn", FargatePreflightStatus.LongTaskArnsUnverified)]
    [InlineData("settings-loop", FargatePreflightStatus.LongTaskArnsUnverified)]
    [InlineData("revision", FargatePreflightStatus.TaskDefinitionMismatch)]
    [InlineData("definition-arn", FargatePreflightStatus.TaskDefinitionMismatch)]
    [InlineData("compatibility", FargatePreflightStatus.TaskPlatformMismatch)]
    [InlineData("network-mode", FargatePreflightStatus.TaskPlatformMismatch)]
    [InlineData("architecture", FargatePreflightStatus.TaskPlatformMismatch)]
    [InlineData("os", FargatePreflightStatus.TaskPlatformMismatch)]
    [InlineData("memory", FargatePreflightStatus.InvalidTaskSize)]
    [InlineData("task-role", FargatePreflightStatus.RoleMismatch)]
    [InlineData("execution-role", FargatePreflightStatus.RoleMismatch)]
    [InlineData("get-role", FargatePreflightStatus.RoleMismatch)]
    [InlineData("subnet-vpc", FargatePreflightStatus.NetworkIncompatible)]
    [InlineData("group-vpc", FargatePreflightStatus.NetworkIncompatible)]
    public async Task RejectsDriftAndIncompletePrerequisites(string fault, FargatePreflightStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var config = await ConfigurationAsync(token);
        switch (fault)
        {
            case "cluster": aws.ClusterResponse = ClusterReply(Cluster.Replace(Account, "999900001111", StringComparison.Ordinal)); break;
            case "inactive-cluster": aws.ClusterResponse = ClusterReply(state: "DRAINING"); break;
            case "long-arn": aws.Settings = _ => SettingsReply("disabled"); break;
            case "settings-loop": aws.Settings = _ => """{"settings":[],"nextToken":"same"}"""; break;
            case "revision": aws.TaskDefinition["revision"] = 8; break;
            case "definition-arn": aws.TaskDefinition["taskDefinitionArn"] = DefinitionArn[..^1] + "8"; break;
            case "compatibility": aws.TaskDefinition["requiresCompatibilities"] = new JsonArray("EC2"); break;
            case "network-mode": aws.TaskDefinition["networkMode"] = "bridge"; break;
            case "architecture": aws.TaskDefinition["runtimePlatform"]!["cpuArchitecture"] = "ARM64"; break;
            case "os": aws.TaskDefinition["runtimePlatform"]!["operatingSystemFamily"] = "WINDOWS_SERVER_2022_CORE"; break;
            case "memory": aws.TaskDefinition["memory"] = "1536"; break;
            case "task-role": aws.TaskDefinition["taskRoleArn"] = "arn:aws:iam::" + Account + ":role/wrong"; break;
            case "execution-role": aws.TaskDefinition["executionRoleArn"] = "arn:aws:iam::" + Account + ":role/wrong"; break;
            case "get-role": aws.WrongIamArn = true; break;
            case "subnet-vpc": aws.SubnetVpc = "vpc-11111111111111111"; break;
            case "group-vpc": aws.GroupVpc = "vpc-11111111111111111"; break;
        }

        var result = await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Task);
        if (fault == "settings-loop")
        {
            Assert.Equal(2, aws.Requests.Count(request => Operation(request) == "ListAccountSettings"));
        }
    }

    [Theory]
    [InlineData("sidecar")]
    [InlineData("privileged")]
    [InlineData("mount")]
    [InlineData("entrypoint")]
    [InlineData("command")]
    [InlineData("image")]
    [InlineData("nonessential")]
    [InlineData("restart")]
    [InlineData("writable-root")]
    [InlineData("environment")]
    [InlineData("secret-logs")]
    public async Task RejectsUnapprovedContainerRuntime(string fault)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var config = await ConfigurationAsync(token);
        var definitions = aws.TaskDefinition["containerDefinitions"]!.AsArray();
        var runner = definitions[0]!.AsObject();
        switch (fault)
        {
            case "sidecar": definitions.Add(new JsonObject { ["name"] = "extra", ["essential"] = false }); break;
            case "privileged": runner["privileged"] = true; break;
            case "mount": runner["mountPoints"] = JsonNode.Parse("""[{"sourceVolume":"socket","containerPath":"/var/run/docker.sock"}]"""); break;
            case "entrypoint": runner["entryPoint"] = new JsonArray("/bin/sh"); break;
            case "command": runner["command"] = new JsonArray("run", "--other"); break;
            case "image": runner["image"] = "docker.io/runner:latest"; break;
            case "nonessential": runner["essential"] = false; break;
            case "restart": runner["restartPolicy"] = new JsonObject { ["enabled"] = true }; break;
            case "writable-root": runner["readonlyRootFilesystem"] = false; break;
            case "environment": runner["environment"] = JsonNode.Parse("""[{"name":"TOKEN","value":"fixture-plaintext-token"}]"""); break;
            case "secret-logs": runner["logConfiguration"] = JsonNode.Parse("""{"logDriver":"splunk","options":{"splunk-token":"fixture-plaintext-token"}}"""); break;
        }

        var result = await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token);
        Assert.Equal(FargatePreflightStatus.ContainerUnapproved, result.Status);
        Assert.Null(result.Task);
        Assert.DoesNotContain("fixture-plaintext-token", result.ToString(), StringComparison.Ordinal);
        string[] expectedOperations = ["DescribeClusters", "ListAccountSettings", "DescribeTaskDefinition"];
        Assert.Equal(expectedOperations, aws.Requests.Select(Operation));
    }

    [Theory]
    [InlineData("ephemeral", FargatePreflightStatus.Verified)]
    [InlineData("unapproved-path", FargatePreflightStatus.ContainerUnapproved)]
    [InlineData("entrypoint-overlay", FargatePreflightStatus.ContainerUnapproved)]
    [InlineData("efs", FargatePreflightStatus.ContainerUnapproved)]
    [InlineData("host-path", FargatePreflightStatus.ContainerUnapproved)]
    [InlineData("provisioned-at-launch", FargatePreflightStatus.ContainerUnapproved)]
    public async Task ScratchMountsRequireApprovedPathsAndFargateManagedEphemeralVolumes(
        string shape, FargatePreflightStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var config = await ConfigurationAsync(token);
        string path = shape == "entrypoint-overlay" ? "/app" : "/scratch";
        var approval = shape == "unapproved-path" ? Approval : Approval with { ScratchMountPaths = [path] };
        var runner = aws.TaskDefinition["containerDefinitions"]![0]!;
        runner["mountPoints"] = JsonNode.Parse($$"""[{"sourceVolume":"scratch","containerPath":"{{path}}"}]""");
        aws.TaskDefinition["volumes"] = JsonNode.Parse(shape switch
        {
            "efs" => """[{"name":"scratch","efsVolumeConfiguration":{"fileSystemId":"fs-123"}}]""",
            "host-path" => """[{"name":"scratch","host":{"sourcePath":"/host/scratch"}}]""",
            "provisioned-at-launch" => """[{"name":"scratch","configuredAtLaunch":true}]""",
            _ => """[{"name":"scratch","host":{}}]""",
        });

        var result = await aws.Inspector.ResolvePreflightAsync(config, Profile, approval, token);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == FargatePreflightStatus.Verified, result.IsReady);
    }

    [Fact]
    public async Task HostOnlyEcsAndOfflineDryRunDoNotIssueFargateRunnerRequests()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var ec2 = await ConfigurationAsync(token, fargate: false);
        var offline = await ConfigurationAsync(token, dryRun: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => aws.Inspector.ResolvePreflightAsync(ec2, Assert.Single(ec2.Profiles).Id, Approval, token));
        await Assert.ThrowsAsync<ArgumentException>(() => aws.Inspector.ResolvePreflightAsync(offline, Profile, Approval, token));
        await Assert.ThrowsAsync<ArgumentException>(() => aws.Inspector.ReadInventoryAsync(offline, Profile, token));
        Assert.Empty(aws.Requests);
    }
}
