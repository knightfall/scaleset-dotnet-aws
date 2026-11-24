using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.Ec2InspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class Ec2PreflightTests
{
    [Theory]
    [InlineData("missing", Ec2PreflightStatus.TemplateUnavailable)]
    [InlineData("wrong-version", Ec2PreflightStatus.TemplateIdentityMismatch)]
    [InlineData("imds-v1", Ec2PreflightStatus.ImdsV2Required)]
    [InlineData("no-imds-endpoint", Ec2PreflightStatus.ImdsV2Required)]
    [InlineData("disabled-imds-endpoint", Ec2PreflightStatus.ImdsV2Required)]
    [InlineData("no-instance-type", Ec2PreflightStatus.InstanceTypeIncompatible)]
    [InlineData("unencrypted-root", Ec2PreflightStatus.RootEncryptionUnverified)]
    [InlineData("wrong-device", Ec2PreflightStatus.RootEncryptionUnverified)]
    [InlineData("wrong-protocol", Ec2PreflightStatus.BootstrapProtocolMismatch)]
    [InlineData("dynamic-ami", Ec2PreflightStatus.ImageIncompatible)]
    public async Task PinnedTemplateRequiresMatchingVerifiedEvidence(string fault, Ec2PreflightStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token, "12");
        using var cloud = new Ec2InspectionFixture
        {
            TemplateResponse = fault switch
            {
                "missing" => $"""
                    <DescribeLaunchTemplateVersionsResponse xmlns="{Namespace}"><requestId>missing</requestId>
                    <launchTemplateVersionSet/></DescribeLaunchTemplateVersionsResponse>
                    """,
                "wrong-version" => Template(version: 11),
                "imds-v1" => Template(tokens: "optional"),
                "no-imds-endpoint" => Template(endpoint: null),
                "disabled-imds-endpoint" => Template(endpoint: "disabled"),
                "no-instance-type" => Template(instanceType: null),
                "unencrypted-root" => Template(encrypted: false),
                "wrong-device" => Template(rootDevice: "/dev/sdf"),
                "wrong-protocol" => Template(protocol: "demo-invalid"),
                "dynamic-ami" => Template(imageId: "resolve:ssm:mutable-image-fixture"),
                _ => throw new InvalidOperationException("Unknown fixture variant."),
            },
        };

        var result = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);

        Assert.Equal(expected, result.Status);
        Assert.False(result.IsReady);
        Assert.Null(result.Template);
        var request = Assert.Single(cloud.Requests, request => request["Action"] == "DescribeLaunchTemplateVersions");
        Assert.Equal(TemplateId, request["LaunchTemplateId"]);
        Assert.Equal("12", request["LaunchTemplateVersion.1"]);
        Assert.Equal("false", request["ResolveAlias"]);
        if (fault == "dynamic-ami")
        {
            Assert.DoesNotContain(cloud.Requests, item => item["Action"] == "DescribeImages");
        }
    }

    [Theory]
    [InlineData("$Latest")]
    [InlineData("$Default")]
    public async Task AliasResolvesToNumericVersionAndRejectsBrokenDriftWithoutChangingInventory(string alias)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token, alias);
        using var cloud = new Ec2InspectionFixture
        {
            TemplateResponse = Template(byName: true),
            InventoryResponse = static _ => AwsWireTransport.Xml(InventoryPage(Instance())),
        };

        var result = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);
        var initially = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);
        var approved = Assert.IsType<Ec2ResolvedTemplate>(result.Template);
        Assert.True(result.IsReady);
        Assert.Equal(12, result.ObservedVersionNumber);
        Assert.Equal(12, approved.VersionNumber);
        Assert.Equal(ImageId, approved.ImageId);
        Assert.Equal(RoleArn, approved.BootstrapRoleArn);
        Assert.Equal(RoleId, approved.BootstrapRoleId);
        Assert.Equal(ProfileArn, approved.InstanceProfileArn);
        Assert.Equal("m7i.large", approved.InstanceType);
        Assert.Collection(approved.Subnets,
            item => Assert.Equal(new Ec2PreflightSubnet(Subnet1, Region + "a", 7), item),
            item => Assert.Equal(new Ec2PreflightSubnet(Subnet2, Region + "b", 0), item));
        Assert.Equal(Group, Assert.Single(approved.SecurityGroupIds));

        cloud.TemplateResponse = Template(version: 13, encrypted: false);
        var drift = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);
        var existing = await cloud.Inspector.ReadInventoryAsync(config, ProfileId, token);

        Assert.Equal(Ec2PreflightStatus.RootEncryptionUnverified, drift.Status);
        Assert.Equal(13, drift.ObservedVersionNumber);
        Assert.False(drift.IsReady);
        Assert.Null(drift.Template);
        Assert.True(existing.IsComplete);
        var runner = Assert.Single(existing.Instances);
        Assert.Equal(12, runner.LaunchTemplateVersion);
        Assert.Equal(Ec2InstanceState.Running, runner.State);
        Assert.Equal(Assert.Single(initially.Instances).Incarnation, runner.Incarnation);
        Assert.Equal(2, cloud.Requests.Count(request => request["Action"] == "DescribeLaunchTemplateVersions"));
        Assert.All(cloud.Requests.Where(request => request["Action"] == "DescribeLaunchTemplateVersions"), request =>
        {
            Assert.Equal(TemplateId, request["LaunchTemplateId"]);
            Assert.Equal(alias, request["LaunchTemplateVersion.1"]);
            Assert.Null(request["LaunchTemplateVersion.2"]);
        });
        Assert.Equal(Subnet1, Assert.Single(cloud.Requests, request => request["Action"] == "DescribeSubnets")["SubnetId.1"]);
        Assert.Equal(Group, Assert.Single(cloud.Requests, request => request["Action"] == "DescribeSecurityGroups")["GroupId.1"]);
        var routes = Assert.Single(cloud.Requests, request => request["Action"] == "DescribeRouteTables");
        Assert.Equal("vpc-id", routes["Filter.1.Name"]);
        Assert.Equal(Vpc, routes["Filter.1.Value.1"]);
        var instanceType = Assert.Single(cloud.Requests, request => request["Action"] == "DescribeInstanceTypes");
        Assert.Equal("m7i.large", instanceType["InstanceType.1"]);
        Assert.Equal(RoleName, Assert.Single(cloud.Requests, request => request["Action"] == "GetInstanceProfile")["InstanceProfileName"]);
        Assert.Equal(RoleName, Assert.Single(cloud.Requests, request => request["Action"] == "GetRole")["RoleName"]);
        Assert.All(cloud.WireRequests, request => Assert.Null(request.Target));
    }

    [Theory]
    [InlineData("profile-arn", Ec2PreflightStatus.InstanceProfileMismatch, false)]
    [InlineData("attached-role", Ec2PreflightStatus.BootstrapRoleMismatch, false)]
    [InlineData("recreated-role", Ec2PreflightStatus.BootstrapRoleMismatch, true)]
    public async Task IamRevalidatesExactProfileRoleAndStableRoleId(
        string fault, Ec2PreflightStatus expected, bool readsRole)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            ProfileResponse = fault switch
            {
                "profile-arn" => InstanceProfile(arn: "arn:aws:iam::111122223333:instance-profile/fake-path/" + RoleName),
                "attached-role" => InstanceProfile(roleArn: "arn:aws:iam::111122223333:role/fake-path/" + RoleName),
                _ => InstanceProfile(),
            },
            RoleResponse = fault == "recreated-role" ? Role("AROAEXAMPLE9876543210") : Role(),
        };

        var result = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);

        Assert.Equal(expected, result.Status);
        Assert.False(result.IsReady);
        Assert.Equal(readsRole, cloud.Requests.Any(request => request["Action"] == "GetRole"));
        Assert.DoesNotContain(cloud.Requests, request => request["Action"] == "DescribeSubnets");
    }

    [Theory]
    [InlineData("wrong-arch", Ec2PreflightStatus.ImageIncompatible)]
    [InlineData("wrong-os", Ec2PreflightStatus.ImageIncompatible)]
    [InlineData("public-subnet", Ec2PreflightStatus.NetworkIncompatible)]
    [InlineData("subnet-vpc", Ec2PreflightStatus.NetworkIncompatible)]
    [InlineData("subnet-zone", Ec2PreflightStatus.NetworkIncompatible)]
    [InlineData("group-vpc", Ec2PreflightStatus.NetworkIncompatible)]
    [InlineData("template-nic", Ec2PreflightStatus.NetworkIncompatible)]
    public async Task PreflightRejectsImageAndNetworkMismatches(string fault, Ec2PreflightStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            ImageResponse = fault switch
            {
                "wrong-arch" => Image(architecture: "arm64"),
                "wrong-os" => Image(platform: "<platform>windows</platform>"),
                _ => Image(),
            },
            SubnetResponse = fault switch
            {
                "public-subnet" => Subnets(firstPublic: "true"),
                "subnet-vpc" => Subnets(firstVpc: "vpc-0123456789abcdef9"),
                "subnet-zone" => Subnets(firstZone: Region + "b"),
                _ => Subnets(),
            },
            GroupsResponse = fault == "group-vpc" ? Groups("vpc-0123456789abcdef9") : Groups(),
            TemplateResponse = fault == "template-nic"
                ? Template(extra: "<networkInterfaceSet><item><deviceIndex>0</deviceIndex><associatePublicIpAddress>true</associatePublicIpAddress></item></networkInterfaceSet>")
                : Template(),
        };

        var result = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);

        Assert.Equal(expected, result.Status);
        Assert.False(result.IsReady);
        Assert.Null(result.Template);
    }

    [Theory]
    [InlineData("t4g.large", "t4g.large", "arm64", false, Ec2PreflightStatus.InstanceTypeIncompatible)]
    [InlineData("m7a.large", "m7i.large", "x86_64", false, Ec2PreflightStatus.InstanceTypeIncompatible)]
    [InlineData("m7a.large", "m7a.large", "x86_64", true, Ec2PreflightStatus.InstanceTypeIncompatible)]
    [InlineData("m7a.large", "m7a.large", "x86_64", false, Ec2PreflightStatus.Verified)]
    public async Task EffectiveInstanceTypeRequiresMatchingArchitectureAndExactResponse(
        string configuredType, string responseType, string architecture, bool incomplete, Ec2PreflightStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token, instanceType: configuredType);
        using var cloud = new Ec2InspectionFixture
        {
            InstanceTypeResponse = InstanceTypes(responseType, architecture, incomplete ? "<nextToken>extra</nextToken>" : ""),
        };

        var result = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);

        Assert.Equal(expected, result.Status);
        Assert.Equal(configuredType, Assert.Single(cloud.Requests, item => item["Action"] == "DescribeInstanceTypes")["InstanceType.1"]);
        Assert.Equal(expected == Ec2PreflightStatus.Verified ? configuredType : null, result.Template?.InstanceType);
    }

    [Theory]
    [InlineData("<destinationCidrBlock>203.0.113.0/24</destinationCidrBlock>", false)]
    [InlineData("<destinationIpv6CidrBlock>2001:db8::/32</destinationIpv6CidrBlock>", true)]
    public async Task RejectsAnActivePublicRouteInEitherEffectiveTable(string destination, bool useMain)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        string publicRoute = $"<item>{destination}<gatewayId>igw-0123456789abcdef0</gatewayId><state>active</state></item>";
        using var cloud = new Ec2InspectionFixture
        {
            RouteTableResponse = _ => AwsWireTransport.Xml(RouteTables(
                RouteTable("rtb-0123456789abcdef0", main: true, extraRoutes: useMain ? publicRoute : "") +
                RouteTable("rtb-0123456789abcdef1", subnet: Subnet2, extraRoutes: useMain ? "" : publicRoute))),
        };

        var result = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);

        Assert.Equal(Ec2PreflightStatus.NetworkIncompatible, result.Status);
        Assert.Null(result.Template);
    }

    [Theory]
    [InlineData("explicit", Ec2PreflightStatus.NetworkIncompatible)]
    [InlineData("transitional", Ec2PreflightStatus.NetworkIncompatible)]
    [InlineData("incomplete", Ec2PreflightStatus.NetworkIncompatible)]
    [InlineData("valid", Ec2PreflightStatus.Verified)]
    public async Task PreflightProvesExactEffectiveAssociationsAcrossPages(string variant, Ec2PreflightStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(token);
        using var cloud = new Ec2InspectionFixture
        {
            RouteTableResponse = query => AwsWireTransport.Xml(query["NextToken"] switch
            {
                null => RouteTables(RouteTable("rtb-0123456789abcdef0", main: true, associationState: null), "<nextToken>second</nextToken>"),
                "second" => RouteTables(RouteTable("rtb-0123456789abcdef1", subnet: Subnet2,
                    associationState: variant == "transitional" ? "associating" : "associated") +
                    (variant == "explicit" ? RouteTable("rtb-0123456789abcdef2", subnet: Subnet2) : ""),
                    variant == "incomplete" ? "<nextToken>second</nextToken>" : ""),
                _ => throw new InvalidOperationException("Unexpected route continuation."),
            }),
        };

        var result = await cloud.Inspector.ResolvePreflightAsync(config, ProfileId, token);

        Assert.Equal(expected, result.Status);
        Assert.Equal(2, cloud.Requests.Count(item => item["Action"] == "DescribeRouteTables"));
    }

    [Fact]
    public async Task DryRunWithoutInspectionAndWrongRegionMakeNoAwsRequests()
    {
        var token = TestContext.Current.CancellationToken;
        var disabled = await ConfigurationAsync(token, inspectAws: false);
        using var cloud = new Ec2InspectionFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => cloud.Inspector.ResolvePreflightAsync(disabled, ProfileId, token));
        await Assert.ThrowsAsync<ArgumentException>(() => cloud.Inspector.ReadInventoryAsync(disabled, ProfileId, token));
        Assert.Empty(cloud.WireRequests);

        var config = await ConfigurationAsync(token);
        using var wrongRegion = new Ec2InspectionFixture(ec2Region: "us-east-1");
        await Assert.ThrowsAsync<ArgumentException>(() => wrongRegion.Inspector.ReadInventoryAsync(config, ProfileId, token));
        Assert.Empty(wrongRegion.WireRequests);
    }
}
