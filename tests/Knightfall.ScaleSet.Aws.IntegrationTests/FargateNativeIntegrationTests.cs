using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed class FargateNativeIntegrationTests
{
    [Fact(Timeout = 35_000)]
    public async Task NativePreflightRejectsBadDefinitionsAndSeparatesUnknownAndZeroHeadroom()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = await FargateNativeFixture.CreateAsync(token);
        var exact = await PreflightAsync(aws, aws.DefinitionArn, token);
        Assert.Equal(FargatePreflightStatus.Verified, exact.Status);
        var task = Assert.IsType<FargateResolvedTask>(exact.Task);
        Assert.Equal(aws.TaskRole.RoleId, task.TaskRoleId);
        Assert.Equal(aws.DefinitionArn, task.Scope.TaskDefinitionArn);
        Assert.Equal(aws.ClusterArn, task.Scope.ClusterArn);
        var inventory = await aws.Inspector.ReadInventoryAsync(aws.Configuration, FargateNativeFixture.ProfileId, token);
        Assert.True(inventory.IsProfileComplete);
        Assert.Empty(inventory.Tasks);
        int ipCount = Assert.IsType<int>(Assert.Single(task.Subnets).AvailableIpAddresses);
        Assert.True(ipCount > 0);

        var now = aws.Clock.Now;
        var quota = new FargateQuotaEvidence(FargateNativeFixture.Account, FargateNativeFixture.Region,
            now, now.AddMinutes(1), task.CpuUnits * 2, 0);
        Assert.Equal(new FargateHeadroom(ipCount, 2, 2), aws.Inspector.EstimateHeadroom(task, inventory, quota, []));
        Assert.Equal(new FargateHeadroom(ipCount, null, null), aws.Inspector.EstimateHeadroom(task, inventory, null, []));
        Assert.Equal(new FargateHeadroom(ipCount, null, null),
            aws.Inspector.EstimateHeadroom(task, inventory, quota with { ExpiresAt = now }, []));
        Assert.Equal(new FargateHeadroom(ipCount, 0, 0),
            aws.Inspector.EstimateHeadroom(task, inventory, quota with { RemainingCpuUnits = 0 }, []));

        aws.Wire.SubnetEvidence = FargateSubnetEvidence.Missing;
        var unknown = Assert.IsType<FargateResolvedTask>((await PreflightAsync(aws, aws.DefinitionArn, token)).Task);
        Assert.Null(Assert.Single(unknown.Subnets).AvailableIpAddresses);
        Assert.Equal(new FargateHeadroom(null, 2, null), aws.Inspector.EstimateHeadroom(unknown, inventory, quota, []));
        aws.Wire.SubnetEvidence = FargateSubnetEvidence.Zero;
        var zero = Assert.IsType<FargateResolvedTask>((await PreflightAsync(aws, aws.DefinitionArn, token)).Task);
        Assert.Equal(0, Assert.Single(zero.Subnets).AvailableIpAddresses);
        Assert.Equal(new FargateHeadroom(0, 2, 0), aws.Inspector.EstimateHeadroom(zero, inventory, quota, []));
        aws.Wire.SubnetEvidence = FargateSubnetEvidence.Native;

        var sidecar = await aws.RegisterDefinitionAsync(sidecar: true, privileged: false, token);
        var privileged = await aws.RegisterDefinitionAsync(sidecar: false, privileged: true, token);
        Assert.Equal(FargatePreflightStatus.ContainerUnapproved, (await PreflightAsync(aws, sidecar, token)).Status);
        Assert.Equal(FargatePreflightStatus.ContainerUnapproved, (await PreflightAsync(aws, privileged, token)).Status);
        aws.Wire.OmitLongArnSetting = true;
        Assert.Equal(FargatePreflightStatus.LongTaskArnsUnverified, (await PreflightAsync(aws, aws.DefinitionArn, token)).Status);
        Assert.DoesNotContain(aws.Wire.Requests, request => request.Operation == "RunTask");
    }

    [Theory(Timeout = 45_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NativeRunTaskLossUsesReadbackOrOneIdenticalReplayAndNeverSendsAfterCutoff(bool hideFirstReadback, bool expired)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = await FargateNativeFixture.CreateAsync(token);
        var lease = await aws.StartOwnerAsync(token);
        await aws.BindScaleSetAsync(lease, 7, GitHubSimulator.SetId, token);
        var launch = await aws.PlanLaunchAsync(lease, token);
        var provider = aws.Provider();
        aws.Wire.DropNextRunResponse();

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await aws.DispatchAsync(lease, launch, provider, token));
        var initial = Assert.IsType<EffectJournalSnapshot>(await aws.Journal.ReadAsync(aws.Profile, FargateNativeFixture.Intent(launch), token));
        var original = Assert.IsType<EffectAttempt>(initial.Entry.Attempt);
        Assert.Null(await aws.Store.ReadExecutorAsync(aws.Profile, launch.AllocationId.ToString("D"), token));
        Assert.Single(aws.Wire.Requests, request => request.Operation == "RunTask");
        Assert.Equal(1, aws.Wire.NativeLaunches);

        aws.Clock.Now = expired
            ? initial.Entry.Envelope.ProviderReplayDeadline!.Value
            : original.Deadline.AddSeconds(2);
        if (hideFirstReadback || expired)
        {
            aws.Wire.HideNextStartedBy(launch.StartedBy);
        }
        var observed = await aws.Dispatcher.ReplayFargateLaunchAsync(lease, aws.Clock.Proof(), aws.Profile,
            FargateNativeFixture.Intent(launch), provider, CancellationToken.None, token);
        var recovered = Assert.IsType<EffectJournalSnapshot>(
            await aws.Journal.ReadAsync(aws.Profile, FargateNativeFixture.Intent(launch), token));
        var run = aws.Wire.Requests.Where(request => request.Operation == "RunTask").ToArray();
        Assert.Equal(1, aws.Wire.NativeLaunches);
        Assert.Contains(aws.Wire.Requests, request => request.Operation == "ListTasks" &&
            HasExactStartedBy(request.Body, launch.StartedBy));

        using var sent = JsonDocument.Parse(run[0].Body);
        var json = sent.RootElement;
        Assert.Equal(launch.ClientToken.ToString("N"), json.GetProperty("clientToken").GetString());
        Assert.Equal(launch.StartedBy, json.GetProperty("startedBy").GetString());
        Assert.Equal(aws.ClusterArn, json.GetProperty("cluster").GetString());
        Assert.Equal(aws.DefinitionArn, json.GetProperty("taskDefinition").GetString());
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.TryGetProperty("overrides", out _));

        if (expired)
        {
            Assert.Equal(EffectDispatchStatus.AwaitingEvidence, observed);
            Assert.Single(run);
            Assert.Equal(initial.TransitionId, recovered.TransitionId);
            Assert.Null(recovered.Entry.Resolution);
            Assert.Null(await aws.Store.ReadExecutorAsync(aws.Profile, launch.AllocationId.ToString("D"), token));
            return;
        }

        Assert.Equal(EffectDispatchStatus.Settled, observed);
        Assert.Equal(initial.Entry.Envelope.CanonicalJson, recovered.Entry.Envelope.CanonicalJson);
        Assert.Equal(EffectResolutionKind.Applied, recovered.Entry.Resolution?.Kind);
        if (hideFirstReadback)
        {
            Assert.Equal(2, run.Length);
            Assert.Equal(run[0].Body, run[1].Body);
            Assert.NotEqual(original.Id, recovered.Entry.Attempt?.Id);
            Assert.Equal(original.LeaseEpoch, recovered.Entry.Attempt?.LeaseEpoch);
        }
        else
        {
            Assert.Single(run);
            Assert.Equal(original, recovered.Entry.Attempt);
        }
        var executor = Assert.IsType<ExecutorSnapshot>(await aws.Store.ReadExecutorAsync(aws.Profile, launch.AllocationId.ToString("D"), token));
        Assert.Equal(ExecutorResourceState.Running, executor.State.Resource?.State);
        var native = await aws.ReadNativeTaskAsync(executor.Binding.ResourceArn, token);
        Assert.Equal(executor.Binding.ResourceArn, native.TaskArn);
        Assert.Equal("RUNNING", native.LastStatus);
    }

    [Fact(Timeout = 35_000)]
    public async Task PeriodicReconciliationCancelsExpiredPlanWhileItsSubnetsHaveZeroHeadroom()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = await FargateNativeFixture.CreateAsync(token);
        var lease = await aws.StartOwnerAsync(token);
        await aws.BindScaleSetAsync(lease, 7, GitHubSimulator.SetId, token);
        var launch = await aws.PlanLaunchAsync(lease, token);
        string intentId = FargateNativeFixture.Intent(launch);
        var planned = Assert.IsType<EffectJournalSnapshot>(await aws.Journal.ReadAsync(aws.Profile, intentId, token));
        aws.Clock.Now = planned.Entry.Envelope.ProviderReplayDeadline!.Value.AddSeconds(2);
        aws.Wire.SubnetEvidence = FargateSubnetEvidence.Zero;

        var inventory = await aws.Inspector.ReadInventoryAsync(aws.Configuration, FargateNativeFixture.ProfileId, token);
        var task = Assert.IsType<FargateResolvedTask>((await PreflightAsync(aws, aws.DefinitionArn, token)).Task);
        var before = Assert.IsType<TrackedFargateLaunchReservations>(
            await aws.Store.ReadTrackedFargateLaunchReservationsAsync(aws.Profile, lease.Fence, token));
        Assert.Equal(launch.AllocationId, Assert.Single(before.Reservations).AllocationId);
        var now = aws.Clock.Now;
        var quota = new FargateQuotaEvidence(FargateNativeFixture.Account, FargateNativeFixture.Region,
            now, now.AddMinutes(1), task.CpuUnits * 2, 0);
        Assert.Equal(0, aws.Inspector.EstimateHeadroom(task, inventory, quota, []).LaunchSlots);

        var observer = aws.Inspector.CreateEffectObserver(aws.Configuration, aws.Profile);
        var pass = await aws.Dispatcher.ReconcilePassAsync(lease, aws.Clock.Proof(), aws.Profile, null, observer, recovery: null, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, pass.Outcome);
        Assert.Equal(1, pass.Settled);
        var settled = Assert.IsType<EffectJournalSnapshot>(await aws.Journal.ReadAsync(aws.Profile, intentId, token));
        Assert.Equal(EffectResolutionKind.CanceledBeforeSend, settled.Entry.Resolution?.Kind);
        Assert.Null(settled.Entry.Attempt);
        Assert.True(settled.Entry.Envelope.Matches(planned.Entry.Envelope));
        var after = Assert.IsType<TrackedFargateLaunchReservations>(
            await aws.Store.ReadTrackedFargateLaunchReservationsAsync(aws.Profile, lease.Fence, token));
        Assert.Empty(after.Reservations);
        Assert.DoesNotContain(aws.Wire.Requests, request => request.Operation == "RunTask");
    }

    private static bool HasExactStartedBy(string body, string expected)
    {
        using var request = JsonDocument.Parse(body);
        var json = request.RootElement;
        return json.TryGetProperty("startedBy", out var found) && found.GetString() == expected &&
            !json.TryGetProperty("desiredStatus", out _) && !json.TryGetProperty("family", out _);
    }

    private static Task<FargatePreflightResult> PreflightAsync(FargateNativeFixture aws, string definition, CancellationToken token) =>
        aws.Inspector.ResolvePreflightAsync(aws.ConfigurationFor(definition), FargateNativeFixture.ProfileId, FargateNativeFixture.Approval, token);
}
