using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.Ec2InspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class Ec2EffectProviderTests
{
    private static readonly Guid AllocationId = Guid.Parse(Allocation);
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse(OriginalLaunch, CultureInfo.InvariantCulture);
    private static readonly EffectAttempt Attempt = new(Guid.Parse("00000000-0000-0000-0000-000000000013"), 2, Now, Now.AddMinutes(1));
    private static readonly CatalogBinding Catalog = new("example:scope", "runner-group", "runner-set", ["ec2"], RunnerProvisionerType.Ec2,
        TemplateId, RoleArn, "v1", 1, 2);
    private const string InstanceArn = "arn:aws:ec2:" + Region + ":" + Account + ":instance/" + Instance1;

    [Fact]
    public async Task LaunchUsesPinnedCountOneAndRecoversExactBindingAfterDroppedResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var cloud = new Ec2InspectionFixture();
        using var provider = cloud.CreateProvider(config);
        var request = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 0, 0,
            new HashSet<string>(StringComparer.Ordinal), null, ct));
        var envelope = Envelope(config, request);
        bool serverCommitted = false;
        cloud.LaunchResponse = _ =>
        {
            serverCommitted = true;
            throw new HttpRequestException("fixture dropped the response", new SocketException((int)SocketError.ConnectionReset));
        };
        cloud.InventoryResponse = query => serverCommitted
            ? AwsWireTransport.Xml(query["NextToken"] is null
                ? InventoryPage(next: "second")
                : InventoryPage(LaunchRow(request)))
            : throw new InvalidOperationException("Readback started before the server commit.");

        var check = await provider.CheckAsync(envelope, Catalog, ct);
        Assert.True(check?.Eligible);
        Assert.Null(await provider.SendAsync(envelope, Attempt, ct));
        var applied = Assert.IsType<EffectEvidence>(await provider.ObserveAsync(envelope, Attempt, ct));

        Assert.Equal(EffectResolutionKind.Applied, applied.Kind);
        Assert.Null(applied.AttemptId);
        Assert.Equal(envelope.Sha256, applied.EnvelopeSha256);
        Assert.Equal(new Ec2ExecutorIdentity(StartedAt, TemplateId, 12), applied.ExecutorBinding?.Provider);
        Assert.Equal(InstanceArn, applied.ExecutorBinding?.ResourceArn);
        Assert.Equal(Allocation, applied.ExecutorBinding?.AllocationId);
        Assert.Equal(ExecutorResourceState.Running, applied.ResourceObservation?.State);
        Assert.Equal(StartedAt, applied.ResourceObservation?.StartedAt);

        var launch = Assert.Single(cloud.Requests, item => item["Action"] == "RunInstances");
        Assert.Equal("1", launch["MinCount"]);
        Assert.Equal("1", launch["MaxCount"]);
        Assert.Equal(request.ClientToken.ToString("D"), launch["ClientToken"]);
        Assert.Equal("12", launch["LaunchTemplate.Version"]);
        Assert.Equal(TemplateId, launch["LaunchTemplate.LaunchTemplateId"]);
        Assert.Equal("m7i.large", launch["InstanceType"]);
        Assert.Equal(ProfileArn, launch["IamInstanceProfile.Arn"]);
        Assert.Equal(Subnet1, launch["SubnetId"]);
        Assert.Equal(Region + "a", launch["Placement.AvailabilityZone"]);
        Assert.Equal(Group, launch["SecurityGroupId.1"]);
        Assert.Equal("instance", launch["TagSpecification.1.ResourceType"]);
        Assert.Equal("volume", launch["TagSpecification.2.ResourceType"]);
        for (int resource = 1; resource <= 2; resource++)
        {
            string prefix = $"TagSpecification.{resource}.Tag.";
            Assert.Equal(RunnerOwnershipTags.Controller, launch[prefix + "1.Key"]);
            Assert.Equal(RunnerOwnershipTags.ControllerValue, launch[prefix + "1.Value"]);
            Assert.Equal(Deployment, launch[prefix + "2.Value"]);
            Assert.Equal(ProfileId, launch[prefix + "3.Value"]);
            Assert.Equal(Allocation, launch[prefix + "4.Value"]);
            Assert.Equal("v1", launch[prefix + "5.Value"]);
            Assert.Null(launch[prefix + "6.Key"]);
        }
        var readback = cloud.Requests.Where(item => item["Action"] == "DescribeInstances").ToArray();
        Assert.Collection(readback, item => Assert.Null(item["NextToken"]), item => Assert.Equal("second", item["NextToken"]));
        Assert.All(readback, item =>
        {
            Assert.Equal("tag:" + RunnerOwnershipTags.Allocation, item["Filter.4.Name"]);
            Assert.Equal(Allocation, item["Filter.4.Value.1"]);
            Assert.Equal("client-token", item["Filter.5.Name"]);
            Assert.Equal(request.ClientToken.ToString("D"), item["Filter.5.Value.1"]);
        });
    }

    [Theory]
    [InlineData("InsufficientInstanceCapacity", HttpStatusCode.ServiceUnavailable, Ec2LaunchRejection.InsufficientInstanceCapacity)]
    [InlineData("InsufficientFreeAddressesInSubnet", HttpStatusCode.BadRequest, Ec2LaunchRejection.InsufficientFreeAddressesInSubnet)]
    public async Task DefinitiveZonalRejectionSuppliesAttemptEvidenceAndAlternateAzUsesFreshToken(
        string code, HttpStatusCode status, Ec2LaunchRejection reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var cloud = new Ec2InspectionFixture
        {
            LaunchResponse = _ => Error(code, status),
            SubnetResponse = Subnets(secondAvailable: "5"),
        };
        using var provider = cloud.CreateProvider(config);
        var prior = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 0, 0,
            new HashSet<string>(StringComparer.Ordinal), null, ct));

        var rejected = Assert.IsType<EffectEvidence>(await provider.SendAsync(Envelope(config, prior), Attempt, ct));
        var excluded = new HashSet<string>(StringComparer.Ordinal) { prior.AvailabilityZone };
        var alternate = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 1, 0, excluded, null, ct));
        var repeatedPlan = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 1, 0, excluded, null, ct));

        Assert.Equal(EffectResolutionKind.Rejected, rejected.Kind);
        Assert.Equal(Attempt.Id, rejected.AttemptId);
        Assert.Equal(reason, rejected.Ec2LaunchRejection);
        Assert.Equal(prior.AllocationId, alternate.AllocationId);
        Assert.Equal(Subnet1, prior.SubnetId);
        Assert.Equal(Subnet2, alternate.SubnetId);
        Assert.Equal(Region + "b", alternate.AvailabilityZone);
        Assert.Equal(1, alternate.AttemptOrdinal);
        Assert.NotEqual(prior.ClientToken, alternate.ClientToken);
        Assert.Equal(alternate.ClientToken, repeatedPlan.ClientToken);

        cloud.LaunchResponse = static _ => AwsWireTransport.Xml(LaunchResponse());
        cloud.InventoryResponse = _ => AwsWireTransport.Xml(InventoryPage(LaunchRow(alternate)));
        var second = Assert.IsType<EffectEvidence>(await provider.SendAsync(Envelope(config, alternate),
            Attempt with { Id = Guid.NewGuid() }, ct));
        Assert.Equal(EffectResolutionKind.Applied, second.Kind);
        Assert.Collection(cloud.Requests.Where(item => item["Action"] == "RunInstances"),
            wire =>
            {
                Assert.Equal(Subnet1, wire["SubnetId"]);
                Assert.Equal(prior.ClientToken.ToString("D"), wire["ClientToken"]);
            },
            wire =>
            {
                Assert.Equal(Subnet2, wire["SubnetId"]);
                Assert.Equal(alternate.ClientToken.ToString("D"), wire["ClientToken"]);
            });
    }

    [Theory]
    [InlineData("ServerInternal", HttpStatusCode.ServiceUnavailable)]
    [InlineData("IdempotentParameterMismatch", HttpStatusCode.BadRequest)]
    public async Task AmbiguousFailureAndEmptyReadbackNeverAuthorizeAlternate(string code, HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var cloud = new Ec2InspectionFixture { LaunchResponse = _ => Error(code, status) };
        using var provider = cloud.CreateProvider(config);
        var request = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 0, 0,
            new HashSet<string>(StringComparer.Ordinal), null, ct));
        var envelope = Envelope(config, request);

        Assert.Null(await provider.SendAsync(envelope, Attempt, ct));
        Assert.Null(await provider.ObserveAsync(envelope, Attempt, ct));
        Assert.Single(cloud.Requests, item => item["Action"] == "RunInstances");
    }

    [Fact]
    public async Task SuccessfulAcknowledgementCannotBindAContradictoryInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var cloud = new Ec2InspectionFixture { LaunchResponse = static _ => AwsWireTransport.Xml(LaunchResponse()) };
        using var provider = cloud.CreateProvider(config);
        var request = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 0, 0,
            new HashSet<string>(StringComparer.Ordinal), null, ct));
        cloud.InventoryResponse = _ => AwsWireTransport.Xml(InventoryPage(LaunchRow(request, Instance2)));

        Assert.Null(await provider.SendAsync(Envelope(config, request), Attempt, ct));

        Assert.Single(cloud.Requests, item => item["Action"] == "RunInstances");
        Assert.Single(cloud.Requests, item => item["Action"] == "DescribeInstances");
    }

    [Theory]
    [InlineData("round-robin", 1)]
    [InlineData("failure-weighted", 0)]
    public async Task PlacementUsesTheConfiguredStrategyOverHealthySubnets(string strategy, long sequence)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true, subnetStrategy: strategy);
        using var cloud = new Ec2InspectionFixture { SubnetResponse = Subnets(secondAvailable: "5") };
        using var provider = cloud.CreateProvider(config);
        var failures = new Dictionary<string, int>(StringComparer.Ordinal) { [Region + "a"] = 2 };

        var request = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 0, sequence,
            new HashSet<string>(StringComparer.Ordinal), failures, ct));

        Assert.Equal(Subnet2, request.SubnetId);
        Assert.Equal(Region + "b", request.AvailabilityZone);
        Assert.DoesNotContain(cloud.Requests, item => item["Action"] == "RunInstances");
    }

    [Fact]
    public async Task DryRunCannotConstructTheMutationProvider()
    {
        var config = await ConfigurationAsync(TestContext.Current.CancellationToken);
        using var cloud = new Ec2InspectionFixture();

        Assert.Throws<ArgumentException>(() => cloud.CreateProvider(config));
        Assert.Empty(cloud.WireRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchRejectsAliasDriftAndOnlyHealthyAliasEvidenceObsoletesThePlan(bool healthy)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var cloud = new Ec2InspectionFixture();
        using var provider = cloud.CreateProvider(config);
        var request = Assert.IsType<Ec2LaunchEffectRequest>(await provider.CreateLaunchRequestAsync(AllocationId, 0, 0,
            new HashSet<string>(StringComparer.Ordinal), null, ct));

        cloud.TemplateResponse = Template(version: 13, tokens: healthy ? "required" : "optional");
        var check = await provider.CheckAsync(Envelope(config, request), Catalog, ct);

        Assert.False(check?.Eligible);
        Assert.Equal(healthy, check?.DefinitivelyObsolete);
        Assert.DoesNotContain(cloud.Requests, item => item["Action"] == "RunInstances");
    }

    [Fact]
    public async Task TerminateSendsOneExactIdAndWaitsForTerminalReadback()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        string state = "running";
        int code = 16;
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = _ => AwsWireTransport.Xml(InventoryPage(Instance(state: state, code: code,
                originalLaunch: state == "terminated" ? null : OriginalLaunch))),
            TerminationResponse = _ =>
            {
                state = "shutting-down";
                code = 32;
                return AwsWireTransport.Xml($"""
                    <TerminateInstancesResponse xmlns="{Namespace}"><requestId>terminate-1</requestId>
                    <instancesSet><item><instanceId>{Instance1}</instanceId>
                    <currentState><code>32</code><name>shutting-down</name></currentState>
                    <previousState><code>16</code><name>running</name></previousState>
                    </item></instancesSet></TerminateInstancesResponse>
                    """);
            },
        };
        using var provider = cloud.CreateProvider(config);
        var request = new Ec2TerminateEffectRequest(AllocationId, InstanceArn, StartedAt, TemplateId, 12);
        var envelope = Envelope(config, request);
        Assert.True((await provider.CheckAsync(envelope, Catalog, ct))?.Eligible);

        Assert.Null(await provider.SendAsync(envelope, Attempt, ct));
        Assert.Collection(cloud.Requests,
            wire => Assert.Equal("GetCallerIdentity", wire["Action"]),
            wire => Assert.Equal("DescribeInstances", wire["Action"]),
            wire => Assert.Equal("TerminateInstances", wire["Action"]),
            wire => Assert.Equal("GetCallerIdentity", wire["Action"]),
            wire => Assert.Equal("DescribeInstances", wire["Action"]));
        Assert.Null(await provider.ObserveAsync(envelope, Attempt, ct));
        state = "terminated";
        code = 48;
        var evidence = Assert.IsType<EffectEvidence>(await provider.ObserveAsync(envelope, Attempt, ct));

        Assert.Equal(InstanceArn, evidence.ExecutorBinding?.ResourceArn);
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, evidence.ResourceObservation?.State);
        var terminate = Assert.Single(cloud.Requests, item => item["Action"] == "TerminateInstances");
        Assert.Equal(Instance1, terminate["InstanceId.1"]);
        Assert.Null(terminate["InstanceId.2"]);
    }

    [Theory]
    [InlineData("wrong-allocation")]
    [InlineData("wrong-incarnation")]
    [InlineData("missing")]
    [InlineData("already-shutting-down")]
    public async Task TerminationCheckRefusesUnknownOrIneligibleTargets(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = await ConfigurationAsync(ct, active: true);
        using var cloud = new Ec2InspectionFixture
        {
            InventoryResponse = _ => AwsWireTransport.Xml(InventoryPage(fault switch
            {
                "wrong-allocation" => Instance(allocation: "12345678-0000-0000-0000-000000000002"),
                "wrong-incarnation" => Instance(originalLaunch: "2049-12-31T01:00:00Z"),
                "already-shutting-down" => Instance(state: "shutting-down", code: 32),
                _ => "",
            })),
        };
        using var provider = cloud.CreateProvider(config);
        var envelope = Envelope(config, new Ec2TerminateEffectRequest(AllocationId, InstanceArn, StartedAt, TemplateId, 12));

        Assert.False((await provider.CheckAsync(envelope, Catalog, ct))?.Eligible);
        Assert.DoesNotContain(cloud.Requests, item => item["Action"] == "TerminateInstances");
    }

    private static EffectEnvelope Envelope(RuntimeConfiguration configuration, EffectEnvelopeRequest request) =>
        EffectEnvelope.Create(new LifecycleProfile(RunnerOwnershipTags.DeploymentIdentity(configuration), ProfileId),
            EffectIntentIdentity.ForAllocation(request)!, 1, request, Now, null);

    private static HttpResponseMessage Error(string code, HttpStatusCode status) => AwsWireTransport.Xml($"""
        <Response><Errors><Error><Code>{code}</Code><Message>synthetic failure</Message></Error></Errors>
        <RequestID>fixture-rejected</RequestID></Response>
        """, status);

    private static string LaunchResponse() => $"""
        <RunInstancesResponse xmlns="{Namespace}"><requestId>launch-1</requestId>
          <reservationId>r-0123456789abcdef0</reservationId><instancesSet>
          <item><instanceId>{Instance1}</instanceId><instanceState><code>0</code><name>pending</name></instanceState></item>
          </instancesSet></RunInstancesResponse>
        """;

    private static string LaunchRow(Ec2LaunchEffectRequest request, string instanceId = Instance1) => Instance(id: instanceId)
        .Replace("<instanceState>", $"""
            <clientToken>{request.ClientToken:D}</clientToken><subnetId>{request.SubnetId}</subnetId>
            <instanceType>{request.InstanceType}</instanceType><placement><availabilityZone>{request.AvailabilityZone}</availabilityZone></placement>
            <iamInstanceProfile><arn>{ProfileArn}</arn></iamInstanceProfile><instanceState>
            """, StringComparison.Ordinal)
        .Replace("</tagSet>", """
            <item><key>scaleset-aws:bootstrap-protocol</key><value>v1</value></item></tagSet>
            """, StringComparison.Ordinal);
}
