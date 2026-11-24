using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.GitHub;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class GitHubLifecycleIntegrationTests
{
    [Theory]
    [InlineData("confirmed")]
    [InlineData("drop")]
    [InlineData("bad request")]
    [InlineData("not found")]
    [InlineData("suppressed")]
    public async Task FreshClientReopensOnlyAfterTheFencedExactRetirementIsConfirmed(string scenario)
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        Guid original;
        using (var first = await rig.OpenLaneAsync(rig.Lease, token))
        {
            Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await first.StepAsync(token)).Step?.Status);
            original = Assert.IsType<Guid>(rig.Simulator.SessionId);
            Assert.Equal(0, rig.Simulator.Offer(743));
            Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await first.StepAsync(token)).Step?.Status);
        }
        var checkpointKey = LifecycleKey.Checkpoint(rig.Profile, original);
        var recorded = Assert.IsType<LifecycleRecord>(await rig.Store.ReadRecordAsync(checkpointKey, token));
        var successor = await rig.TakeOverAsync(token);
        switch (scenario)
        {
            case "drop":
                rig.Simulator.FailNext(SimulatorOperation.CloseSession, SimulatorFault.DropAfterCommit);
                break;
            case "bad request":
                rig.Simulator.FailNext(SimulatorOperation.CloseSession, SimulatorFault.BadRequest);
                break;
            case "not found":
                rig.Simulator.FailNext(SimulatorOperation.CloseSession, SimulatorFault.NotFound);
                break;
        }
        using var replacement = await rig.OpenLaneAsync(scenario == "suppressed" ? rig.Lease : successor, token);
        var step = await replacement.StepAsync(token);
        if (scenario == "confirmed")
        {
            Assert.Equal(ScaleSetListenerStepStatus.Initialized, step.Step?.Status);
            var session = Assert.IsType<SessionRecord>(await rig.Store.ReadSessionAsync(rig.Profile, token));
            Assert.NotEqual(original, session.SessionId);
            var checkpoint = new GitHubListenerCheckpointStore(rig.Store, Assert.IsType<OwnedSessionFence>(session.Fence));
            var fresh = Assert.IsType<ScaleSetListenerCheckpoint>(await checkpoint.ReadAsync(checkpoint.SessionKey, token));
            Assert.Null(fresh.LastAcknowledgedMessageId);
            Assert.True(fresh.InitialStatisticsCompleted);
            Assert.Equal(2, rig.Simulator.Requests.Count(IsSessionPost));
        }
        else
        {
            Assert.Equal(scenario == "suppressed" ? GitHubListenerLaneStatus.OwnershipUnavailable : GitHubListenerLaneStatus.Failed, step.Status);
            if (scenario != "suppressed")
            {
                Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, (await replacement.StepAsync(token)).Status);
                Assert.Equal(SessionPhase.Quarantined, (await rig.Store.ReadSessionAsync(rig.Profile, token))?.Phase);
            }
            Assert.Single(rig.Simulator.Requests, IsSessionPost);
        }
        Assert.Equal(recorded, await rig.Store.ReadRecordAsync(checkpointKey, token));
        var close = rig.Simulator.Requests.Where(request => request.Host == GitHubProtocolHost.ServiceHost && request.Method == "DELETE");
        if (scenario == "suppressed")
        {
            Assert.Empty(close);
        }
        else
        {
            Assert.Equal($"{GitHubSimulator.Sessions}/{original:D}", Assert.Single(close).Path);
        }
        Assert.Single(rig.Simulator.Requests, request => request.Host == GitHubProtocolHost.QueueHost && request.Method == "GET");
    }

    [Fact]
    public async Task LostSessionCreationResponsePreservesUnknownIdentityWithOnePost()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        rig.Simulator.FailNext(SimulatorOperation.OpenSession, SimulatorFault.DropAfterCommit);
        using (var lane = await rig.OpenLaneAsync(rig.Lease, token))
        {
            Assert.Equal(GitHubListenerLaneStatus.Failed, (await lane.StepAsync(token)).Status);
        }
        Assert.NotNull(rig.Simulator.SessionId);
        var successor = await rig.TakeOverAsync(token);
        using var replacement = await rig.OpenLaneAsync(successor, token);
        Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, (await replacement.StepAsync(token)).Status);
        var recorded = Assert.IsType<SessionRecord>(await rig.Store.ReadSessionAsync(rig.Profile, token));
        Assert.Equal(SessionPhase.Quarantined, recorded.Phase);
        Assert.Null(recorded.SessionId);
        Assert.Single(rig.Simulator.Requests, IsSessionPost);
        Assert.DoesNotContain(rig.Simulator.Requests, request => request.Method == "DELETE" || request.Host == GitHubProtocolHost.QueueHost);
    }

    [Fact]
    public async Task LostAcknowledgementKeepsExactPendingCheckpointWithoutASecondDelete()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        Guid original;
        using (var first = await rig.OpenLaneAsync(rig.Lease, token))
        {
            Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await first.StepAsync(token)).Step?.Status);
            original = Assert.IsType<Guid>(rig.Simulator.SessionId);
            Assert.Equal(0, rig.Simulator.Offer(743));
            rig.Simulator.FailNext(SimulatorOperation.Acknowledge, SimulatorFault.DropAfterCommit);
            Assert.True((await first.StepAsync(token)).Status is GitHubListenerLaneStatus.RecoveryRequired or GitHubListenerLaneStatus.Failed);
        }
        var session = Assert.IsType<SessionRecord>(await rig.Store.ReadSessionAsync(rig.Profile, token));
        var checkpoint = new GitHubListenerCheckpointStore(rig.Store, Assert.IsType<OwnedSessionFence>(session.Fence));
        var before = Assert.IsType<ScaleSetListenerCheckpoint>(await checkpoint.ReadAsync(checkpoint.SessionKey, token));
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, before.Pending?.Phase);
        Assert.Null(before.LastAcknowledgedMessageId);

        using var replacement = await rig.OpenLaneAsync(rig.Lease, token);
        Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, (await replacement.StepAsync(token)).Status);
        var after = Assert.IsType<ScaleSetListenerCheckpoint>(await checkpoint.ReadAsync(checkpoint.SessionKey, token));
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.TransitionId, after.TransitionId);
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, after.Pending?.Phase);
        Assert.Equal($"/messages/{original:D}/0", Assert.Single(rig.Simulator.Requests,
            request => request.Host == GitHubProtocolHost.QueueHost && request.Method == "DELETE").Path);
        Assert.Single(rig.Simulator.Requests, request => request.Host == GitHubProtocolHost.QueueHost && request.Method == "GET");
    }

    [Fact]
    public async Task ConstantAssignedDemandKeepsReservationAcrossRestartUntilExactCompletion()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        rig.Simulator.TotalAssignedJobs = 1;
        using (var first = await rig.OpenLaneAsync(rig.Lease, token))
        {
            Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await first.StepAsync(token)).Step?.Status);
            Assert.Equal(0, rig.Simulator.Offer(743, 744));
            Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await first.StepAsync(token)).Step?.Status);
        }
        Assert.Equal(1, await rig.ReadHeldAsync(token));
        Assert.NotNull(await rig.Store.ReadRecordAsync(LifecycleKey.Acquisition(rig.Profile, 743), token));
        Assert.Null(await rig.Store.ReadRecordAsync(LifecycleKey.Acquisition(rig.Profile, 744), token));

        var successor = await rig.TakeOverAsync(token);
        using var replacement = await rig.OpenLaneAsync(successor, token);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await replacement.StepAsync(token)).Step?.Status);
        rig.Simulator.Offer(744, 745);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await replacement.StepAsync(token)).Step?.Status);
        Assert.Equal(1, await rig.ReadHeldAsync(token));
        Assert.Single(rig.Simulator.Requests, IsAcquisition);

        rig.Simulator.Complete(743);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await replacement.StepAsync(token)).Step?.Status);
        Assert.Equal(0, await rig.ReadHeldAsync(token));
        rig.Simulator.Offer(744);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await replacement.StepAsync(token)).Step?.Status);
        Assert.Equal(1, await rig.ReadHeldAsync(token));
        Assert.NotNull(await rig.Store.ReadRecordAsync(LifecycleKey.Acquisition(rig.Profile, 744), token));
        Assert.Equal(2, rig.Simulator.Requests.Count(IsAcquisition));
    }

    private static bool IsAcquisition(SimulatorRequest request) => request.Method == "POST" &&
        request.Path.EndsWith("/acquirejobs", StringComparison.Ordinal);
}
