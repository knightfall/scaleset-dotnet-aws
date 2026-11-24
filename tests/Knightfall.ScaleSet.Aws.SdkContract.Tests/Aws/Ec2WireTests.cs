using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Web;
using Amazon.EC2;
using Amazon.EC2.Model;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

public sealed class Ec2WireTests
{
    private const string InstanceId = "i-0123456789abcdef0";
    private const string LaunchTemplateId = "lt-0123456789abcdef0";
    private const string SubnetId = "subnet-0123456789abcdef0";
    private const string SecurityGroupId = "sg-0123456789abcdef0";
    private const string ImageId = "ami-0123456789abcdef0";
    private const string Token = "ec2-allocation-token";
    private const string RunResult = """
        <RunInstancesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
          <requestId>run-1</requestId><reservationId>r-0123456789abcdef0</reservationId>
          <instancesSet><item><instanceId>i-0123456789abcdef0</instanceId>
          <instanceState><code>0</code><name>pending</name></instanceState></item></instancesSet>
        </RunInstancesResponse>
        """;

    [Fact]
    public async Task RunInstancesSerializesTheExactSingleInstanceIntent()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml(RunResult));
        using var client = CreateClient(wire);

        var response = await client.RunInstancesAsync(CreateLaunchRequest(Token), TestContext.Current.CancellationToken);

        var instance = Assert.Single(response.Reservation.Instances);
        Assert.Equal(InstanceId, instance.InstanceId);
        Assert.Equal(InstanceStateName.Pending, instance.State.Name);
        var form = GetParameters(Assert.Single(wire.Requests), "RunInstances");
        Assert.Equal("1", form["MinCount"]);
        Assert.Equal("1", form["MaxCount"]);
        Assert.Equal(Token, form["ClientToken"]);
        Assert.Equal(LaunchTemplateId, form["LaunchTemplate.LaunchTemplateId"]);
        Assert.Equal("12", form["LaunchTemplate.Version"]);
        Assert.Equal(SubnetId, form["SubnetId"]);
        Assert.Equal(SecurityGroupId, form["SecurityGroupId.1"]);
        Assert.Equal("instance", form["TagSpecification.1.ResourceType"]);
        Assert.Equal("volume", form["TagSpecification.2.ResourceType"]);
        Assert.Equal("Allocation", form["TagSpecification.1.Tag.1.Key"]);
        Assert.Equal("profile-a", form["TagSpecification.1.Tag.1.Value"]);
        Assert.Equal("Allocation", form["TagSpecification.2.Tag.1.Key"]);
        Assert.Equal("profile-a", form["TagSpecification.2.Tag.1.Value"]);
        Assert.Null(form["TagSpecification.3.ResourceType"]);
    }

    [Fact]
    public async Task RunInstancesGeneratesANewWireTokenOnSeparateInvocationsWithoutAToken()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml(RunResult));
        using var client = CreateClient(wire);
        var launch = CreateLaunchRequest(null);
        var token = TestContext.Current.CancellationToken;

        await client.RunInstancesAsync(launch, token);
        await client.RunInstancesAsync(launch, token);

        Assert.Null(launch.ClientToken);
        Assert.Equal(2, wire.Requests.Count);
        string? first = GetParameters(wire.Requests[0], "RunInstances")["ClientToken"];
        string? second = GetParameters(wire.Requests[1], "RunInstances")["ClientToken"];
        Assert.True(Guid.TryParse(first, out var firstToken));
        Assert.True(Guid.TryParse(second, out var secondToken));
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public async Task TerminateInstancesTargetsOneInstanceAndPreservesIntermediateState()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <TerminateInstancesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
              <requestId>terminate-1</requestId><instancesSet><item>
                <instanceId>i-0123456789abcdef0</instanceId>
                <currentState><code>32</code><name>shutting-down</name></currentState>
                <previousState><code>16</code><name>running</name></previousState>
              </item></instancesSet>
            </TerminateInstancesResponse>
            """));
        using var client = CreateClient(wire);

        var response = await client.TerminateInstancesAsync(
            new TerminateInstancesRequest { InstanceIds = [InstanceId] },
            TestContext.Current.CancellationToken);

        var stateChange = Assert.Single(response.TerminatingInstances);
        Assert.Equal(InstanceId, stateChange.InstanceId);
        Assert.Equal(InstanceStateName.Running, stateChange.PreviousState.Name);
        Assert.Equal(InstanceStateName.ShuttingDown, stateChange.CurrentState.Name);
        var form = GetParameters(Assert.Single(wire.Requests), "TerminateInstances");
        Assert.Equal(InstanceId, form["InstanceId.1"]);
        Assert.Null(form["InstanceId.2"]);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("$Latest")]
    [InlineData("$Default")]
    public async Task DescribeLaunchTemplateVersionsPreservesSelectorAndReturnsNumericVersion(string selector)
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <DescribeLaunchTemplateVersionsResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
              <requestId>template-1</requestId><nextToken>templates-page-2</nextToken>
              <launchTemplateVersionSet><item>
                <launchTemplateId>lt-0123456789abcdef0</launchTemplateId><versionNumber>12</versionNumber>
                <launchTemplateData><imageId>ami-0123456789abcdef0</imageId>
                  <iamInstanceProfile><arn>arn:aws:iam::123456789012:instance-profile/runner</arn></iamInstanceProfile>
                  <metadataOptions><httpTokens>required</httpTokens></metadataOptions>
                  <blockDeviceMappingSet><item><deviceName>/dev/xvda</deviceName>
                    <ebs><encrypted>true</encrypted></ebs></item></blockDeviceMappingSet>
                </launchTemplateData>
              </item></launchTemplateVersionSet>
            </DescribeLaunchTemplateVersionsResponse>
            """));
        using var client = CreateClient(wire);

        var result = await client.DescribeLaunchTemplateVersionsAsync(
            new DescribeLaunchTemplateVersionsRequest
            {
                LaunchTemplateId = LaunchTemplateId,
                Versions = [selector],
                NextToken = "templates-page-1",
            },
            TestContext.Current.CancellationToken);

        var version = Assert.Single(result.LaunchTemplateVersions);
        Assert.Equal(LaunchTemplateId, version.LaunchTemplateId);
        Assert.Equal(12L, version.VersionNumber);
        Assert.Equal(ImageId, version.LaunchTemplateData.ImageId);
        Assert.Equal("arn:aws:iam::123456789012:instance-profile/runner", version.LaunchTemplateData.IamInstanceProfile.Arn);
        Assert.Equal(LaunchTemplateHttpTokensState.Required, version.LaunchTemplateData.MetadataOptions.HttpTokens);
        Assert.True(Assert.Single(version.LaunchTemplateData.BlockDeviceMappings).Ebs.Encrypted);
        Assert.Equal("templates-page-2", result.NextToken);
        var form = GetParameters(Assert.Single(wire.Requests), "DescribeLaunchTemplateVersions");
        Assert.Equal(LaunchTemplateId, form["LaunchTemplateId"]);
        Assert.Equal(selector, form["LaunchTemplateVersion.1"]);
        Assert.Equal("templates-page-1", form["NextToken"]);
    }

    [Fact]
    public async Task DescribeInstancesSerializesOwnershipAndTokenFiltersAndPreservesPagination()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <DescribeInstancesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
              <requestId>instances-1</requestId><nextToken>instances-page-2</nextToken>
              <reservationSet><item><instancesSet><item>
                <instanceId>i-0123456789abcdef0</instanceId><clientToken>ec2-allocation-token</clientToken>
                <subnetId>subnet-0123456789abcdef0</subnetId>
                <instanceState><code>48</code><name>terminated</name></instanceState>
                <tagSet><item><key>Allocation</key><value>profile-a</value></item></tagSet>
              </item></instancesSet></item></reservationSet>
            </DescribeInstancesResponse>
            """));
        using var client = CreateClient(wire);

        var response = await client.DescribeInstancesAsync(
            new DescribeInstancesRequest
            {
                Filters =
                [
                    new Filter { Name = "tag:Allocation", Values = ["profile-a"] },
                    new Filter { Name = "client-token", Values = [Token] },
                ],
                NextToken = "instances-page-1",
            },
            TestContext.Current.CancellationToken);

        var instance = Assert.Single(Assert.Single(response.Reservations).Instances);
        Assert.Equal(InstanceId, instance.InstanceId);
        Assert.Equal(Token, instance.ClientToken);
        Assert.Equal(SubnetId, instance.SubnetId);
        Assert.Equal(InstanceStateName.Terminated, instance.State.Name);
        var tag = Assert.Single(instance.Tags);
        Assert.Equal("Allocation", tag.Key);
        Assert.Equal("profile-a", tag.Value);
        Assert.Equal("instances-page-2", response.NextToken);
        var form = GetParameters(Assert.Single(wire.Requests), "DescribeInstances");
        Assert.Equal("tag:Allocation", form["Filter.1.Name"]);
        Assert.Equal("profile-a", form["Filter.1.Value.1"]);
        Assert.Equal("client-token", form["Filter.2.Name"]);
        Assert.Equal(Token, form["Filter.2.Value.1"]);
        Assert.Equal("instances-page-1", form["NextToken"]);
    }

    [Fact]
    public async Task DescribeImagesUsesExactIdAndReadsArchitectureAndRootDevice()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <DescribeImagesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
              <requestId>images-1</requestId><imagesSet><item>
                <imageId>ami-0123456789abcdef0</imageId><architecture>arm64</architecture>
                <rootDeviceName>/dev/xvda</rootDeviceName>
                <imageState>available</imageState>
              </item></imagesSet>
            </DescribeImagesResponse>
            """));
        using var client = CreateClient(wire);

        var response = await client.DescribeImagesAsync(
            new DescribeImagesRequest { ImageIds = [ImageId] },
            TestContext.Current.CancellationToken);

        var image = Assert.Single(response.Images);
        Assert.Equal(ImageId, image.ImageId);
        Assert.Equal("arm64", image.Architecture.Value);
        Assert.Equal("/dev/xvda", image.RootDeviceName);
        Assert.Equal("available", image.State.Value);
        var form = GetParameters(Assert.Single(wire.Requests), "DescribeImages");
        Assert.Equal(ImageId, form["ImageId.1"]);
        Assert.Null(form["ImageId.2"]);
    }

    [Fact]
    public async Task DescribeSubnetsUsesExactIdAndReadsVpcZoneAndAddressCount()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <DescribeSubnetsResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
              <requestId>subnets-1</requestId><subnetSet><item>
                <subnetId>subnet-0123456789abcdef0</subnetId><vpcId>vpc-0123456789abcdef0</vpcId>
                <availabilityZone>us-east-1a</availabilityZone><availableIpAddressCount>7</availableIpAddressCount>
                <mapPublicIpOnLaunch>false</mapPublicIpOnLaunch>
              </item></subnetSet>
            </DescribeSubnetsResponse>
            """));
        using var client = CreateClient(wire);

        var response = await client.DescribeSubnetsAsync(
            new DescribeSubnetsRequest { SubnetIds = [SubnetId] },
            TestContext.Current.CancellationToken);

        var subnet = Assert.Single(response.Subnets);
        Assert.Equal(SubnetId, subnet.SubnetId);
        Assert.Equal("vpc-0123456789abcdef0", subnet.VpcId);
        Assert.Equal("us-east-1a", subnet.AvailabilityZone);
        Assert.Equal(7, subnet.AvailableIpAddressCount);
        Assert.False(subnet.MapPublicIpOnLaunch);
        var form = GetParameters(Assert.Single(wire.Requests), "DescribeSubnets");
        Assert.Equal(SubnetId, form["SubnetId.1"]);
    }

    [Fact]
    public async Task DescribeSecurityGroupsUsesExactIdAndReadsVpcMembership()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <DescribeSecurityGroupsResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
              <requestId>groups-1</requestId><securityGroupInfo><item>
                <groupId>sg-0123456789abcdef0</groupId><vpcId>vpc-0123456789abcdef0</vpcId>
              </item></securityGroupInfo>
            </DescribeSecurityGroupsResponse>
            """));
        using var client = CreateClient(wire);

        var response = await client.DescribeSecurityGroupsAsync(
            new DescribeSecurityGroupsRequest { GroupIds = [SecurityGroupId] },
            TestContext.Current.CancellationToken);

        var group = Assert.Single(response.SecurityGroups);
        Assert.Equal(SecurityGroupId, group.GroupId);
        Assert.Equal("vpc-0123456789abcdef0", group.VpcId);
        var form = GetParameters(Assert.Single(wire.Requests), "DescribeSecurityGroups");
        Assert.Equal(SecurityGroupId, form["GroupId.1"]);
    }

    [Fact]
    public async Task CreateTagsSerializesTheExactResourceAndValue()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <CreateTagsResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
              <requestId>tags-1</requestId><return>true</return>
            </CreateTagsResponse>
            """));
        using var client = CreateClient(wire);

        var response = await client.CreateTagsAsync(CreateTagRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        var form = GetParameters(Assert.Single(wire.Requests), "CreateTags");
        Assert.Equal(InstanceId, form["ResourceId.1"]);
        Assert.Null(form["ResourceId.2"]);
        Assert.Equal("Allocation", form["Tag.1.Key"]);
        Assert.Equal("profile + A", form["Tag.1.Value"]);
    }

    [Theory]
    [InlineData("RunInstances")]
    [InlineData("TerminateInstances")]
    [InlineData("CreateTags")]
    public async Task JournalOwnedWritesSendOneAttemptOnServiceUnavailable(string operation)
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <Response><Errors><Error><Code>ServiceUnavailable</Code>
              <Message>Injected HTTP 503</Message></Error></Errors><RequestID>unavailable-1</RequestID></Response>
            """, HttpStatusCode.ServiceUnavailable));
        using var client = CreateClient(wire);
        var token = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<AmazonEC2Exception>(() => operation switch
        {
            "RunInstances" => client.RunInstancesAsync(CreateLaunchRequest(Token), token),
            "TerminateInstances" => client.TerminateInstancesAsync(new TerminateInstancesRequest { InstanceIds = [InstanceId] }, token),
            "CreateTags" => client.CreateTagsAsync(CreateTagRequest(), token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("ServiceUnavailable", exception.ErrorCode);
        GetParameters(Assert.Single(wire.Requests), operation);
    }

    [Fact]
    public async Task RunInstancesExposesDefinitiveCapacityRejection()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml("""
            <Response><Errors><Error><Code>InsufficientInstanceCapacity</Code>
              <Message>Injected capacity rejection</Message></Error></Errors><RequestID>capacity-1</RequestID></Response>
            """, HttpStatusCode.BadRequest));
        using var client = CreateClient(wire);

        var exception = await Assert.ThrowsAsync<AmazonEC2Exception>(
            () => client.RunInstancesAsync(CreateLaunchRequest(Token), TestContext.Current.CancellationToken));

        Assert.Equal("InsufficientInstanceCapacity", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(Token, GetParameters(Assert.Single(wire.Requests), "RunInstances")["ClientToken"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StaleConnectionRetryBudgetIsIndependentOfStandardRetryBudget(int staleConnectionRetries)
    {
        int attempts = 0;
        using var wire = new AwsWireTransport(_ =>
        {
            if (++attempts == 1)
            {
                throw new HttpRequestException("Injected connection reset", new SocketException((int)SocketError.ConnectionReset));
            }

            return AwsWireTransport.Xml(RunResult);
        });
        var config = wire.Configure(new AmazonEC2Config());
        config.MaxStaleConnectionRetries = staleConnectionRetries;
        using var client = new AmazonEC2Client(AwsWireTransport.Credentials, config);

        var exception = await Record.ExceptionAsync(
            () => client.RunInstancesAsync(CreateLaunchRequest(Token), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Config.MaxErrorRetry);
        if (staleConnectionRetries == 0)
        {
            Assert.NotNull(exception);
            Assert.IsType<SocketException>(exception.GetBaseException());
        }
        else
        {
            Assert.Null(exception);
        }

        Assert.Equal(staleConnectionRetries + 1, wire.Requests.Count);
        Assert.All(wire.Requests, request => Assert.Equal(Token, GetParameters(request, "RunInstances")["ClientToken"]));
    }

    private static AmazonEC2Client CreateClient(AwsWireTransport wire)
    {
        return new AmazonEC2Client(AwsWireTransport.Credentials, wire.Configure(new AmazonEC2Config()));
    }

    private static RunInstancesRequest CreateLaunchRequest(string? token)
    {
        return new RunInstancesRequest
        {
            ClientToken = token,
            MinCount = 1,
            MaxCount = 1,
            LaunchTemplate = new LaunchTemplateSpecification { LaunchTemplateId = LaunchTemplateId, Version = "12" },
            SubnetId = SubnetId,
            SecurityGroupIds = [SecurityGroupId],
            TagSpecifications =
            [
                new TagSpecification { ResourceType = ResourceType.Instance, Tags = [new Tag("Allocation", "profile-a")] },
                new TagSpecification { ResourceType = ResourceType.Volume, Tags = [new Tag("Allocation", "profile-a")] },
            ],
        };
    }

    private static CreateTagsRequest CreateTagRequest()
    {
        return new CreateTagsRequest { Resources = [InstanceId], Tags = [new Tag("Allocation", "profile + A")] };
    }

    private static NameValueCollection GetParameters(AwsWireRequest request, string operation)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        var form = HttpUtility.ParseQueryString(request.Body);
        Assert.Equal(operation, form["Action"]);
        Assert.Equal("2016-11-15", form["Version"]);
        return form;
    }
}
