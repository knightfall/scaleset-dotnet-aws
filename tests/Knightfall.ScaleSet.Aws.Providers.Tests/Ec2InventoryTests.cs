using System.Globalization;
using System.Net;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.Ec2InspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class Ec2InventoryTests
{
    [Fact]
    public async Task ReadsEveryOwnedPageAndDistinguishesShutdownFromTermination()
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = static query => query["NextToken"] switch
            {
                null => AwsWireTransport.Xml(InventoryPage(
                    Instance(state: "pending", code: 0, version: "10") +
                    Instance(Instance2, allocation: "12345678-0000-0000-0000-000000000002"), next: "second")),
                "second" => AwsWireTransport.Xml(InventoryPage(
                    Instance(Instance3, "shutting-down", 32, allocation: "12345678-0000-0000-0000-000000000003") +
                    Instance(Instance4, "terminated", 48, allocation: "12345678-0000-0000-0000-000000000004"))),
                _ => throw new InvalidOperationException("Unexpected inventory continuation."),
            },
        };

        var result = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.True(result.IsComplete);
        Assert.Equal(Now, result.ObservedAt);
        Assert.Equal(new[] { Ec2InstanceState.Pending, Ec2InstanceState.Running, Ec2InstanceState.ShuttingDown, Ec2InstanceState.Terminated },
            result.Instances.Select(instance => instance.State));
        Assert.Equal(10, result.Instances[0].LaunchTemplateVersion);
        Assert.Equal(Guid.Parse(Allocation), result.Instances[0].AllocationId);
        Assert.All(result.Instances, instance =>
        {
            Assert.Equal($"arn:aws:ec2:{Region}:{Account}:instance/{instance.InstanceId}", instance.ResourceArn);
            Assert.NotNull(instance.Incarnation);
        });
        Assert.Equal("GetCallerIdentity", cloud.Requests[0]["Action"]);
        var pages = cloud.Requests.Where(request => request["Action"] == "DescribeInstances").ToArray();
        Assert.Collection(pages, first => Assert.Null(first["NextToken"]), later => Assert.Equal("second", later["NextToken"]));
        Assert.All(pages, request =>
        {
            Assert.Equal("tag:scaleset-aws:controller", request["Filter.1.Name"]);
            Assert.Equal("scaleset-dotnet-aws", request["Filter.1.Value.1"]);
            Assert.Equal("tag:scaleset-aws:deployment", request["Filter.2.Name"]);
            Assert.Equal(Deployment, request["Filter.2.Value.1"]);
            Assert.Equal("tag:scaleset-aws:profile", request["Filter.3.Name"]);
            Assert.Equal(ProfileId, request["Filter.3.Value.1"]);
            Assert.Null(request["Filter.4.Name"]);
        });
        Assert.All(cloud.WireRequests, request => Assert.Null(request.Target));
    }

    [Fact]
    public async Task MultipleInstancesClaimingOneAllocationMakeTheInventoryInconclusive()
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = static _ => AwsWireTransport.Xml(InventoryPage(Instance() + Instance(Instance2))),
        };
        var result = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.False(result.IsComplete);
        Assert.Equal(2, result.Instances.Length);
        Assert.All(result.Instances, instance => Assert.Equal(Ec2InstanceState.Unknown, instance.State));
    }

    [Fact]
    public async Task OriginalPrimaryAttachmentTimeKeepsTheSameIncarnationAfterStopStart()
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture();
        cloud.InventoryResponse = static _ => AwsWireTransport.Xml(InventoryPage(Instance(state: "stopped", code: 80)));
        var stopped = Assert.Single((await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token)).Instances);
        cloud.InventoryResponse = static _ => AwsWireTransport.Xml(InventoryPage(Instance(lastLaunch: "2049-12-31T22:00:00Z")));
        var resumed = Assert.Single((await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token)).Instances);

        Assert.Equal(Ec2InstanceState.Unknown, stopped.State);
        Assert.Equal(Ec2InstanceState.Running, resumed.State);
        Assert.Equal(DateTimeOffset.Parse(OriginalLaunch, CultureInfo.InvariantCulture), stopped.OriginalLaunchTime);
        Assert.Equal(stopped.OriginalLaunchTime, resumed.OriginalLaunchTime);
        Assert.NotNull(stopped.Incarnation);
        Assert.Equal(stopped.Incarnation, resumed.Incarnation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteOrRepeatedPageDoesNotProveLifecycleState(bool repeatToken)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = query =>
            {
                if (query["NextToken"] is null)
                {
                    return AwsWireTransport.Xml(InventoryPage(Instance(state: "terminated", code: 48), next: "retry"));
                }
                return repeatToken ? AwsWireTransport.Xml(InventoryPage(next: "retry")) :
                    AwsWireTransport.Xml("<Response><Errors><Error><Code>Unavailable</Code></Error></Errors></Response>",
                        HttpStatusCode.ServiceUnavailable);
            },
        };

        var result = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.Equal(Ec2InventoryStatus.Incomplete, result.Status);
        Assert.False(result.IsComplete);
        var runner = Assert.Single(result.Instances);
        Assert.Equal(Ec2InstanceState.Terminated, runner.ReportedState);
        Assert.Equal(Ec2InstanceState.Unknown, runner.State);
        Assert.False(runner.IsAuthoritative);
        Assert.Equal(3, cloud.Requests.Length);
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-profile")]
    [InlineData("invalid-id")]
    public async Task ResponseRowsRequireExactScopeAndCanonicalInstanceId(string fault)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = _ => AwsWireTransport.Xml(fault switch
            {
                "wrong-owner" => InventoryPage(Instance(), owner: "000000000000"),
                "wrong-profile" => InventoryPage(Instance(profile: "another-profile")),
                "invalid-id" => InventoryPage(Instance(id: "invalid-id")),
                _ => throw new InvalidOperationException("Unknown ownership variant."),
            }),
        };

        var result = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.Equal(Ec2InventoryStatus.Incomplete, result.Status);
        Assert.Empty(result.Instances);
    }

    [Fact]
    public async Task TerminalRowWithoutPrimaryAttachmentRemainsUnknownAndKeepsNativeProvenance()
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = static _ =>
                AwsWireTransport.Xml(InventoryPage(Instance(state: "terminated", code: 48, originalLaunch: null))),
        };

        var result = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.True(result.IsComplete);
        var candidate = Assert.Single(result.Instances);
        Assert.Equal(Ec2InstanceState.Terminated, candidate.ReportedState);
        Assert.Equal(Ec2InstanceState.Unknown, candidate.State);
        Assert.Null(candidate.OriginalLaunchTime);
        Assert.Null(candidate.Incarnation);
        Assert.Equal(12, candidate.LaunchTemplateVersion);
        Assert.Equal(TemplateId, candidate.LaunchTemplateId);
        Assert.NotNull(candidate.AllocationId);
    }

    [Theory]
    [InlineData("999999999999", "aws", "999999999999")]
    [InlineData(Account, "aws-us-gov", Account)]
    [InlineData(Account, "aws", "999999999999")]
    public async Task WrongCallerCannotProveKnownZeroOrAReadyTemplate(string account, string partition, string arnAccount)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture { IdentityResponse = CallerIdentity(account, partition, arnAccount) };

        var inventory = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);
        var template = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);

        Assert.Equal(Ec2InventoryStatus.AwsIdentityMismatch, inventory.Status);
        Assert.False(inventory.IsComplete);
        Assert.Empty(inventory.Instances);
        Assert.Equal(Ec2PreflightStatus.AwsIdentityMismatch, template.Status);
        Assert.False(template.IsReady);
        Assert.Collection(cloud.Requests,
            request => Assert.Equal("GetCallerIdentity", request["Action"]),
            request => Assert.Equal("GetCallerIdentity", request["Action"]));
    }

    [Fact]
    public async Task UnavailableCallerIdentityLeavesInventoryUnknownWithoutAnEc2Request()
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            IdentityStatus = HttpStatusCode.ServiceUnavailable,
            IdentityResponse = "<ErrorResponse><Error><Code>ServiceUnavailable</Code><Message>synthetic</Message></Error></ErrorResponse>",
        };

        var result = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.Equal(Ec2InventoryStatus.AwsUnavailable, result.Status);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Instances);
        Assert.Equal("GetCallerIdentity", Assert.Single(cloud.Requests)["Action"]);
    }

    [Fact]
    public async Task EmptySuccessfulInventoryIsKnownZeroWhileUnavailableInventoryIsUnknown()
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture();
        var preflight = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);
        var empty = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);
        cloud.InventoryResponse = static _ => AwsWireTransport.Xml(
            "<Response><Errors><Error><Code>Unavailable</Code></Error></Errors></Response>", HttpStatusCode.ServiceUnavailable);
        var unavailable = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.True(preflight.IsReady);
        Assert.True(empty.IsComplete);
        Assert.Empty(empty.Instances);
        Assert.Equal(Ec2InventoryStatus.AwsUnavailable, unavailable.Status);
        Assert.False(unavailable.IsComplete);
        Assert.Empty(unavailable.Instances);
    }
}
