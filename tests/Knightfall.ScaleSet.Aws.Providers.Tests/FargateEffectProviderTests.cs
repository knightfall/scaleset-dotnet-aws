using System.Net;
using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.FargateInspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class FargateEffectProviderTests
{
    private static readonly LifecycleProfile Scope = new(Deployment, Profile);
    private static readonly Guid ClientToken = Guid.Parse("fc3e788d-7341-4c2b-bf88-2442fed5589c");
    private static readonly string TaskRole = $"arn:aws:iam::{Account}:role/{TaskRoleName}";
    private static readonly string ExecutionRole = $"arn:aws:iam::{Account}:role/{ExecutionRoleName}";
    private static readonly CatalogBinding Catalog = new("scope", "group", "scale-set", ["shell"],
        RunnerProvisionerType.Fargate, DefinitionArn, TaskRole, "v1", null, null);
    private static readonly FargateLaunchOwnershipTags Ownership = new(RunnerOwnershipTags.ControllerValue, Deployment,
        Profile, FirstAllocation.ToString("D"), "v1");

    [Fact]
    public async Task PlanningUsesVerifiedSubnetChoicesAndAStableAllocationScopedToken()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var config = await ConfigurationAsync(token);
        var verified = Assert.IsType<FargateResolvedTask>((await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token)).Task);

        var first = FargateProfileInspector.CreateLaunchRequest(verified, FirstAllocation, [Subnet2, Subnet1]);
        var same = FargateProfileInspector.CreateLaunchRequest(verified, FirstAllocation, [Subnet1, Subnet2]);
        var other = FargateProfileInspector.CreateLaunchRequest(verified, Guid.NewGuid(), [Subnet1, Subnet2]);

        string[] ordered = [Subnet1, Subnet2];
        Assert.Equal(ordered, first.SubnetIds);
        Assert.Equal(first.ClientToken, same.ClientToken);
        Assert.NotEqual(Guid.Empty, first.ClientToken);
        Assert.NotEqual(first.ClientToken, other.ClientToken);
        Assert.Equal(DefinitionArn, first.TaskDefinitionArn);
        Assert.Equal(Ownership, first.OwnershipTags);
        Assert.Equal(1024, first.CpuUnits);
        Assert.Throws<ArgumentException>(() => FargateProfileInspector.CreateLaunchRequest(verified, FirstAllocation, [Subnet1, Subnet1]));
        Assert.Throws<ArgumentException>(() => FargateProfileInspector.CreateLaunchRequest(verified, FirstAllocation, ["subnet-0123456789abcdef9"]));
    }

    [Fact]
    public async Task ConfiguredRunnerHealthCheckHoldsInspectionAndDispatch()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var configuration = await ConfigurationAsync(token);
        var adapter = aws.Inspector.CreateEffectProvider(configuration, Scope, Approval);
        var eligible = Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(Launch(), Catalog, token));
        Assert.True(eligible.Eligible);

        aws.TaskDefinition["containerDefinitions"]![0]!["healthCheck"] =
            new JsonObject { ["command"] = new JsonArray("CMD", "/usr/bin/true") };

        var inspection = await aws.Inspector.ResolvePreflightAsync(configuration, Profile, Approval, token);
        Assert.Equal(FargatePreflightStatus.ContainerUnapproved, inspection.Status);
        Assert.Null(inspection.Task);
        var held = Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(Launch(), Catalog, token));
        Assert.False(held.Eligible);
        Assert.DoesNotContain(aws.Requests, request => Operation(request) == "RunTask");
    }

    [Fact]
    public async Task ExactRunTaskWireUsesOnlyCanonicalPinnedInputsAndReadsBackTheTaskWithTags()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            // Even a successful RunTask response can contain only a sparse task projection.
            RunTask = _ => AwsWireTransport.Json($$"""{"tasks":[{"taskArn":"{{FirstTask}}"}],"failures":[]}"""),
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation, "PENDING", attachedSubnet: null)),
        };
        var adapter = await AdapterAsync(aws, token);
        var launch = Launch();
        Assert.True(Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(launch, Catalog, token)).Eligible);

        var evidence = Assert.IsType<EffectEvidence>(await adapter.SendAsync(launch, Attempt(Now), token));

        var commands = aws.Requests.Where(request => Operation(request) is "RunTask" or "DescribeTasks").ToArray();
        string[] expectedCommands = ["RunTask", "DescribeTasks"];
        Assert.Equal(expectedCommands, commands.Select(Operation));
        var sent = Body(commands[0]);
        Assert.Equal(Cluster, sent.GetProperty("cluster").GetString());
        Assert.Equal(DefinitionArn, sent.GetProperty("taskDefinition").GetString());
        Assert.Equal("FARGATE", sent.GetProperty("launchType").GetString());
        Assert.Equal(1, sent.GetProperty("count").GetInt32());
        Assert.Equal("ss-00112233445566778899aabbccddeeff", sent.GetProperty("startedBy").GetString());
        Assert.Equal("fc3e788d73414c2bbf882442fed5589c", sent.GetProperty("clientToken").GetString());
        Assert.False(sent.GetProperty("enableExecuteCommand").GetBoolean());
        Assert.False(sent.TryGetProperty("overrides", out _));
        Assert.False(sent.TryGetProperty("capacityProviderStrategy", out _));
        var network = sent.GetProperty("networkConfiguration").GetProperty("awsvpcConfiguration");
        Assert.Equal("DISABLED", network.GetProperty("assignPublicIp").GetString());
        Assert.Equal(new[] { Subnet1, Subnet2 }, network.GetProperty("subnets").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(SecurityGroup, Assert.Single(network.GetProperty("securityGroups").EnumerateArray()).GetString());
        var tags = sent.GetProperty("tags").EnumerateArray().ToDictionary(tag => tag.GetProperty("key").GetString()!,
            tag => tag.GetProperty("value").GetString());
        Assert.Equal(5, tags.Count);
        Assert.Equal(RunnerOwnershipTags.ControllerValue, tags[RunnerOwnershipTags.Controller]);
        Assert.Equal(Deployment, tags[RunnerOwnershipTags.Deployment]);
        Assert.Equal(Profile, tags[RunnerOwnershipTags.Profile]);
        Assert.Equal(FirstAllocation.ToString("D"), tags[RunnerOwnershipTags.Allocation]);
        Assert.Equal("v1", tags[RunnerOwnershipTags.BootstrapProtocol]);
        AssertExactDescribe(commands[1]);
        AssertApplied(evidence, launch, ExecutorResourceState.Starting);
        Assert.Equal(1L, evidence.ResourceObservation!.ProviderVersion);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(-1L, false)]
    public async Task OptionalEcsVersionMayBeOmittedButNeverNegative(long? version, bool expectedTrusted)
    {
        var token = TestContext.Current.CancellationToken;
        var task = TaskRow(FirstTask, FirstAllocation);
        if (version.HasValue)
        {
            task["version"] = version.Value;
        }
        else
        {
            task.Remove("version");
        }
        using var aws = new FargateInspectionFixture
        {
            RunTask = _ => AwsWireTransport.Json($$"""{"tasks":[{"taskArn":"{{FirstTask}}"}]}"""),
            DescribeTasks = _ => TasksReply(task),
        };
        var adapter = await AdapterAsync(aws, token);

        var evidence = await adapter.SendAsync(Launch(), Attempt(Now), token);

        if (expectedTrusted)
        {
            var resource = Assert.IsType<ExecutorResourceObservation>(Assert.IsType<EffectEvidence>(evidence).ResourceObservation);
            Assert.Null(resource.ProviderVersion);
        }
        else
        {
            Assert.Null(evidence);
        }
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("busy")]
    [InlineData("empty")]
    [InlineData("partial")]
    public async Task AmbiguousRunTaskResponseReconcilesTheSingleExactAllocationWithNoWireRetry(string reply)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            RunTask = _ => reply switch
            {
                "busy" => AwsWireTransport.Json("""{"__type":"ServiceUnavailableException"}""", HttpStatusCode.ServiceUnavailable),
                "partial" => AwsWireTransport.Json($$"""
                    {"tasks":[{"taskArn":"{{SecondTask}}"}],"failures":[{"arn":"{{DefinitionArn}}","reason":"RESOURCE:ENI"}]}
                    """),
                _ => AwsWireTransport.Json("""{"tasks":[],"failures":[]}"""),
            },
            ListTasks = query => query.TryGetProperty("startedBy", out _) ? ListReply([FirstTask]) : ListReply([]),
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation)),
        };
        if (reply == "drop")
        {
            aws.BeforeRespondAsync = (request, _) => Operation(request) == "RunTask"
                ? Task.FromException(new HttpRequestException("RunTask response was lost.")) : Task.CompletedTask;
        }

        var adapter = await AdapterAsync(aws, token);
        var launch = Launch();
        var attempt = Attempt(Now);
        Assert.Null(await adapter.SendAsync(launch, attempt, token));
        AssertApplied(Assert.IsType<EffectEvidence>(await adapter.ObserveAsync(launch, attempt, token)), launch, ExecutorResourceState.Running);

        string[] expectedCommands = ["RunTask", "DescribeClusters", "ListTasks", "ListTasks", "DescribeTasks"];
        Assert.Equal(expectedCommands, aws.Requests.Select(Operation));
        var first = Body(aws.Requests[2]);
        Assert.Equal(FargateProfileInspector.StartedBy(FirstAllocation), first.GetProperty("startedBy").GetString());
        Assert.False(first.TryGetProperty("desiredStatus", out _));
        var stopped = Body(aws.Requests[3]);
        Assert.Equal("STOPPED", stopped.GetProperty("desiredStatus").GetString());
        Assert.False(stopped.TryGetProperty("startedBy", out _));
        AssertExactDescribe(aws.Requests[4]);
    }

    [Fact]
    public async Task ExactReplayBytesRemainIdenticalAcrossRenewalAndAreNeverSentAtCutoff()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var adapter = await AdapterAsync(aws, token);
        var original = Launch();
        var cutoff = original.ProviderReplayDeadline!.Value;
        Assert.Null(await adapter.SendAsync(original, Attempt(Now), token));
        aws.UtcNow = Now.AddMinutes(45);
        var restored = EffectEnvelope.Read(original.CanonicalJson, original.Sha256);
        var renewed = Attempt(aws.UtcNow, epoch: 2);
        var readback = Assert.IsType<FargateReplayObservation>(await adapter.ObserveForReplayAsync(restored, renewed, token));
        Assert.True(readback.IsComplete);
        Assert.Null(readback.Evidence);
        Assert.Null(await adapter.SendAsync(restored, renewed, token));
        aws.UtcNow = cutoff;
        Assert.Null(await adapter.SendAsync(restored, Attempt(cutoff, epoch: 3), token));
        aws.UtcNow = cutoff.AddSeconds(1);
        Assert.Null(await adapter.SendAsync(restored, Attempt(aws.UtcNow, epoch: 3), token));

        var mutations = aws.Requests.Where(request => Operation(request) == "RunTask").ToArray();
        Assert.Equal(2, mutations.Length);
        Assert.Equal(mutations[0].Body, mutations[1].Body);
    }

    [Fact]
    public async Task StoppedTaskIsReadBackBeforeAnyRunnerReadinessOrRunTaskResponse()
    {
        var token = TestContext.Current.CancellationToken;
        var task = TaskRow(FirstTask, FirstAllocation, "STOPPED", attachedSubnet: null);
        task.Remove("createdAt");
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query => query.TryGetProperty("desiredStatus", out var desired) && desired.GetString() == "STOPPED"
                ? ListReply([FirstTask]) : ListReply([]),
            DescribeTasks = _ => TasksReply(task),
        };
        var adapter = await AdapterAsync(aws, token);
        var launch = Launch();

        var evidence = Assert.IsType<EffectEvidence>(await adapter.ObserveAsync(launch, Attempt(Now), token));

        AssertApplied(evidence, launch, ExecutorResourceState.IrreversiblyTerminated);
        Assert.Null(evidence.ResourceObservation!.StartedAt);
        Assert.DoesNotContain(aws.Requests, request => Operation(request) is "RunTask" or "StopTask");
    }

    [Fact]
    public async Task HistoryUsesTheOriginalClusterAndSubnetButCannotSendAfterConfigurationChanges()
    {
        const string historicalSubnet = "subnet-0123456789abcdef9";
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query => query.TryGetProperty("startedBy", out _) ? ListReply([FirstTask]) : ListReply([]),
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation, attachedSubnet: historicalSubnet)),
        };
        var config = await ConfigurationAsync(token, createRunnerCluster: true, runnerClusterName: "engineering-shell-new");
        var adapter = aws.Inspector.CreateEffectProvider(config, Scope, Approval);
        var initial = Launch();
        var original = EffectEnvelope.Create(Scope, initial.IntentId, 1,
            (FargateLaunchEffectRequest)initial.Request with { SubnetIds = [historicalSubnet] },
            initial.EarliestPossibleFirstSend, initial.ProviderReplayDeadline);
        var attempt = Attempt(Now);

        Assert.False(Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(original, Catalog, token)).Eligible);
        Assert.Null(await adapter.SendAsync(original, attempt, token));
        AssertApplied(Assert.IsType<EffectEvidence>(await adapter.ObserveAsync(original, attempt, token)), original, ExecutorResourceState.Running);

        var clusterRequest = Body(aws.Requests[0]);
        Assert.Equal(Cluster, Assert.Single(clusterRequest.GetProperty("clusters").EnumerateArray()).GetString());
        Assert.All(aws.Requests.Skip(1), request => Assert.Equal(Cluster, Body(request).GetProperty("cluster").GetString()));
        Assert.DoesNotContain(aws.Requests, request => Operation(request) == "RunTask");
    }

    [Fact]
    public async Task HistoricalStopReadbackCanSettleOnlyTheOldExactStoppedTask()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation, "STOPPED", attachedSubnet: null)),
        };
        var config = await ConfigurationAsync(token, createRunnerCluster: true, runnerClusterName: "engineering-shell-new");
        var adapter = aws.Inspector.CreateEffectProvider(config, Scope, Approval);
        var original = Stop();
        var attempt = Attempt(Now);

        Assert.False(Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(original, Catalog, token)).Eligible);
        Assert.Null(await adapter.SendAsync(original, attempt, token));
        AssertApplied(Assert.IsType<EffectEvidence>(await adapter.ObserveAsync(original, attempt, token)), original,
            ExecutorResourceState.IrreversiblyTerminated);
        AssertExactDescribe(Assert.Single(aws.Requests));
    }

    [Fact]
    public async Task IncompleteDescribeNeverAuthorizesReplayFromAZeroCandidateRead()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query => query.TryGetProperty("startedBy", out _) ? ListReply([FirstTask]) : ListReply([]),
            DescribeTasks = _ => $$"""{"tasks":[],"failures":[{"arn":"{{FirstTask}}","reason":"MISSING"}]}""",
        };
        var adapter = await AdapterAsync(aws, token);

        var result = Assert.IsType<FargateReplayObservation>(await adapter.ObserveForReplayAsync(Launch(), Attempt(Now), token));

        Assert.False(result.IsComplete);
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-revision")]
    [InlineData("wrong-cluster")]
    [InlineData("wrong-started-by")]
    [InlineData("unknown-status")]
    [InlineData("different-task")]
    [InlineData("stopping")]
    [InlineData("desired-stopped")]
    public async Task StopPreconditionRejectsAnUnverifiedOrAlreadyStoppingResource(string defect)
    {
        var token = TestContext.Current.CancellationToken;
        var task = TaskRow(FirstTask, FirstAllocation);
        switch (defect)
        {
            case "wrong-revision":
                task["taskDefinitionArn"] = DefinitionArn[..^1] + "8";
                break;
            case "wrong-cluster":
                task["clusterArn"] = Cluster + "-other";
                break;
            case "wrong-started-by":
                task["startedBy"] = FargateProfileInspector.StartedBy(Guid.NewGuid());
                break;
            case "unknown-status":
                task["lastStatus"] = "UNKNOWN";
                break;
            case "different-task":
                task["taskArn"] = SecondTask;
                break;
            case "stopping":
                task["lastStatus"] = "STOPPING";
                break;
            case "desired-stopped":
                task["desiredStatus"] = "STOPPED";
                break;
        }
        using var aws = new FargateInspectionFixture { DescribeTasks = _ => defect == "missing" ? TasksReply() : TasksReply(task) };
        var adapter = await AdapterAsync(aws, token);

        var check = Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(Stop(), Catalog, token));

        Assert.False(check.Eligible);
        Assert.Null(check.AlreadyApplied);
        AssertExactDescribe(Assert.Single(aws.Requests));
    }

    [Fact]
    public async Task StopAcknowledgmentAndDesiredStateDoNotProveResourceDeath()
    {
        var token = TestContext.Current.CancellationToken;
        var lastStatus = "RUNNING";
        using var aws = new FargateInspectionFixture
        {
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation, lastStatus, lastStatus == "RUNNING" ? Subnet1 : null)),
            StopTask = _ =>
            {
                lastStatus = "DEACTIVATING";
                return AwsWireTransport.Json($$"""{"task":{"taskArn":"{{FirstTask}}","lastStatus":"STOPPED"} }""");
            },
        };
        var adapter = await AdapterAsync(aws, token);
        var envelope = Stop();
        var attempt = Attempt(Now);
        var check = Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(envelope, Catalog, token));
        Assert.True(check.Eligible);
        Assert.Null(check.AlreadyApplied);

        Assert.Null(await adapter.SendAsync(envelope, attempt, token));
        lastStatus = "STOPPED";
        var proof = Assert.IsType<EffectEvidence>(await adapter.ObserveAsync(envelope, attempt, token));

        AssertApplied(proof, envelope, ExecutorResourceState.IrreversiblyTerminated);
        string[] expectedCommands = ["DescribeTasks", "StopTask", "DescribeTasks", "DescribeTasks"];
        Assert.Equal(expectedCommands, aws.Requests.Select(Operation));
        var send = Body(aws.Requests[1]);
        Assert.Equal(Cluster, send.GetProperty("cluster").GetString());
        Assert.Equal(FirstTask, send.GetProperty("task").GetString());
    }

    [Fact]
    public async Task ExactAlreadyStoppedIsAnAuthoritativePreconditionWithoutAnyMutation()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation, "STOPPED", attachedSubnet: null)),
        };
        var adapter = await AdapterAsync(aws, token);
        var envelope = Stop();

        var check = Assert.IsType<EffectDispatchPrecondition>(await adapter.CheckAsync(envelope, Catalog, token));

        Assert.True(check.Eligible);
        AssertApplied(Assert.IsType<EffectEvidence>(check.AlreadyApplied), envelope, ExecutorResourceState.IrreversiblyTerminated);
        AssertExactDescribe(Assert.Single(aws.Requests));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task MutationAdapterRejectsBothAutomaticSdkRetryModes(int errors, int staleConnections)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture(errors, staleConnections);
        var config = await ConfigurationAsync(token);

        Assert.Throws<InvalidOperationException>(() => aws.Inspector.CreateEffectProvider(config, Scope, Approval));
        Assert.Empty(aws.Requests);
    }

    private static async Task<IFargateReplayEffectProvider> AdapterAsync(FargateInspectionFixture aws, CancellationToken token) =>
        aws.Inspector.CreateEffectProvider(await ConfigurationAsync(token), Scope, Approval);

    private static EffectEnvelope Launch() => EffectEnvelope.Create(Scope, "fargate-launch-" + FirstAllocation.ToString("D"), 1,
        new FargateLaunchEffectRequest(FirstAllocation, Cluster, DefinitionArn, [Subnet2, Subnet1], [SecurityGroup], TaskRole,
            ExecutionRole, "runner", ClientToken, Ownership, 1024), Now.AddMinutes(-2), Now.AddMinutes(48));

    private static EffectEnvelope Stop() => EffectEnvelope.Create(Scope, "fargate-stop-" + FirstAllocation.ToString("D"), 1,
        new FargateStopEffectRequest(FirstAllocation, Cluster, FirstTask, DefinitionArn,
            FargateExecutorIncarnation.Compute(FirstTask, DefinitionArn, FirstAllocation)), Now.AddMinutes(-2), null);

    private static EffectAttempt Attempt(DateTimeOffset at, long epoch = 1) => new(Guid.NewGuid(), epoch, at, at.AddSeconds(10));

    private static void AssertExactDescribe(AwsWireRequest request)
    {
        Assert.Equal("DescribeTasks", Operation(request));
        var describe = Body(request);
        Assert.Equal(Cluster, describe.GetProperty("cluster").GetString());
        Assert.Equal(FirstTask, Assert.Single(describe.GetProperty("tasks").EnumerateArray()).GetString());
        Assert.Equal("TAGS", Assert.Single(describe.GetProperty("include").EnumerateArray()).GetString());
    }

    private static void AssertApplied(EffectEvidence evidence, EffectEnvelope envelope, ExecutorResourceState state)
    {
        Assert.Equal(EffectResolutionKind.Applied, evidence.Kind);
        Assert.Equal(envelope.Sha256, evidence.EnvelopeSha256);
        Assert.Equal(envelope.Target, evidence.Target);
        var binding = Assert.IsType<ExecutorBinding>(evidence.ExecutorBinding);
        Assert.Equal(Scope, binding.Profile);
        Assert.Equal(FirstTask, binding.ResourceArn);
        Assert.Equal(FirstAllocation.ToString("D"), binding.AllocationId);
        Assert.Equal(Guid.Parse("399f3aad-0c23-fa25-f7c6-42b75438f741"), binding.Incarnation);
        Assert.Equal(new FargateExecutorIdentity(Cluster, DefinitionArn), binding.Provider);
        Assert.Equal(state, Assert.IsType<ExecutorResourceObservation>(evidence.ResourceObservation).State);
    }
}
