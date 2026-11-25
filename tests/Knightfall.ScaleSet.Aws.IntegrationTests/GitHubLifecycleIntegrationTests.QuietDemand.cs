using System.Globalization;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Microsoft.AspNetCore.Http;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class GitHubLifecycleIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietActiveDemandRefreshRestoresReserveAndAcceptsLaterJobOnSameSession(bool pollFails)
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        rig.Simulator.CurrentStatisticsTime = () => rig.Clock.Now;
        var lease = await ExtendQuietLeaseAsync(rig, token);
        var binding = await QuietBindingAsync(rig, token);
        var definition = Definition(rig.Configuration, lease, rig.Clock, binding, await rig.ConnectAsync(token), capacity: 2) with
        {
            ReadCapacity = cancellation => rig.Store.ReadListenerCapacityAsync(rig.Profile, 2,
                TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), cancellation),
        };
        using var lane = new GitHubListenerSupervisor(rig.Store, rig.Clock, ListenerTiming).Register(definition);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var session = Assert.IsType<Guid>(rig.Simulator.SessionId);
        rig.Simulator.Complete(900);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        var before = Assert.IsType<SessionDemandObservation>(await rig.Store.ReadCurrentSessionDemandAsync(rig.Profile, lease.Fence, token));

        rig.Clock.Now += TimeSpan.FromMinutes(11);
        await PublishQuietInventoryAsync(rig, lease, token);
        var profile = Assert.Single(rig.Configuration.Profiles);
        Assert.Equal(1, profile.Capacity.IdleReserve);
        var inventory = new InventorySample(rig.Clock.Now, true, 2, 2, []);
        Assert.Equal(0, Plan(before));
        Assert.Equal(0, (await definition.ReadCapacity(token)).Maximum);

        if (pollFails)
        {
            rig.Simulator.FailNext(SimulatorOperation.Poll, SimulatorFault.BadRequest);
        }
        var quiet = await lane.StepAsync(token);
        if (pollFails)
        {
            Assert.Equal(GitHubListenerLaneStatus.Failed, quiet.Status);
        }
        else
        {
            Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, quiet.Step?.Status);
        }
        var refreshed = Assert.IsType<SessionDemandObservation>(await rig.Store.ReadCurrentSessionDemandAsync(rig.Profile, lease.Fence, token));
        Assert.Equal((session, before.MessageId, rig.Clock.Now, 0),
            (refreshed.SessionId, refreshed.MessageId, refreshed.ObservedAt, refreshed.TotalAssignedJobs));
        Assert.Equal(1, Plan(refreshed));
        Assert.Single(rig.Simulator.Requests, IsQuietStatisticsRead);
        Assert.Equal("2", LastPollCapacity(rig.Simulator));

        rig.Simulator.Offer(901);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        Assert.Equal("2", LastPollCapacity(rig.Simulator));
        Assert.Equal([901L], rig.Simulator.AcquiredRequests);
        Assert.Equal(session, rig.Simulator.SessionId);
        Assert.Single(rig.Simulator.Requests, IsSessionPost);

        int Plan(SessionDemandObservation demand) =>
            CapacityPlanner.Plan(profile, new DemandSample(demand.ObservedAt, demand.TotalAssignedJobs), inventory, rig.Clock).ProposedStarts;
    }

    [Theory]
    [InlineData("missing statistics")]
    [InlineData("missing count")]
    [InlineData("duplicate count")]
    [InlineData("negative")]
    [InlineData("contradictory")]
    [InlineData("wrong set")]
    [InlineData("cached")]
    [InlineData("stale date")]
    [InlineData("unavailable")]
    [InlineData("oversized")]
    public async Task QuietDemandHoldsStaleAdmissionOnUnusableStatistics(string response)
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        var lease = await ExtendQuietLeaseAsync(rig, token);
        using var lane = await rig.OpenLaneAsync(lease, token);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var before = await rig.Store.ReadRecordAsync(LifecycleKey.Demand(rig.Profile), token);
        var binding = await QuietBindingAsync(rig, token);
        rig.Clock.Now += TimeSpan.FromMinutes(11);
        rig.Simulator.ReadExactSetResponse = context => QuietStatisticsReply(context, binding, rig.Clock.Now, response);

        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await lane.StepAsync(token)).Step?.Status);
        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await lane.StepAsync(token)).Step?.Status);
        Assert.Single(rig.Simulator.Requests, IsQuietStatisticsRead);
        Assert.Equal(before, await rig.Store.ReadRecordAsync(LifecycleKey.Demand(rig.Profile), token));
        Assert.Equal("0", LastPollCapacity(rig.Simulator));
        Assert.DoesNotContain(rig.Simulator.Requests, IsAcquisition);
    }

    [Fact]
    public async Task QuietDemandFromAnOlderReadCannotOverwriteANewerSessionObservation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        rig.Simulator.CurrentStatisticsTime = () => rig.Clock.Now;
        var lease = await ExtendQuietLeaseAsync(rig, token);
        using var lane = await rig.OpenLaneAsync(lease, token);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var session = Assert.IsType<SessionRecord>(await rig.Store.ReadSessionAsync(rig.Profile, token));
        var fence = Assert.IsType<OwnedSessionFence>(session.Fence);
        var key = LifecycleKey.Demand(rig.Profile);
        var prior = Assert.IsType<LifecycleRecord>(await rig.Store.ReadRecordAsync(key, token));
        rig.Clock.Now += TimeSpan.FromMinutes(11);
        var pause = rig.Table.Wire.PauseNextTransactionWrite(key);
        var quiet = lane.StepAsync(token);
        try
        {
            await pause.Captured.WaitAsync(TimeSpan.FromSeconds(10), token);
            rig.Clock.Now += TimeSpan.FromSeconds(1);
            var newer = new SessionDemandObservation(fence.Identity.SessionId, rig.Clock.Now, 41, 1);
            var write = new LifecycleRecordWrite(key, prior.Revision, JsonSerializer.Serialize(newer, JsonSerializerOptions.Web));
            Assert.Equal(LifecycleWriteOutcome.Committed,
                await rig.Store.WriteSessionRecordsAsync(fence, Guid.NewGuid(), [write], token));
            Assert.Equal(newer, await rig.Store.ReadCurrentSessionDemandAsync(rig.Profile, lease.Fence, token));
        }
        finally
        {
            pause.Resume();
        }

        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await quiet).Step?.Status);
        var surviving = Assert.IsType<SessionDemandObservation>(await rig.Store.ReadCurrentSessionDemandAsync(rig.Profile, lease.Fence, token));
        Assert.Equal((41L, 1), (surviving.MessageId, surviving.TotalAssignedJobs));
    }

    [Fact]
    public async Task QuietDemandDoesNotRelabelAPredecessorSessionObservation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        rig.Simulator.CurrentStatisticsTime = () => rig.Clock.Now;
        using var oldLane = await rig.OpenLaneAsync(rig.Lease, token);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await oldLane.StepAsync(token)).Step?.Status);
        var predecessor = Assert.IsType<SessionDemandObservation>(await rig.Store.ReadCurrentSessionDemandAsync(rig.Profile, rig.Lease.Fence, token));
        var lease = await rig.TakeOverAsync(token);
        using var lane = await rig.OpenLaneAsync(lease, token);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var session = Assert.IsType<SessionRecord>(await rig.Store.ReadSessionAsync(rig.Profile, token));
        var fence = Assert.IsType<OwnedSessionFence>(session.Fence);
        var key = LifecycleKey.Demand(rig.Profile);
        var before = Assert.IsType<LifecycleRecord>(await rig.Store.ReadRecordAsync(key, token));
        var write = new LifecycleRecordWrite(key, before.Revision, JsonSerializer.Serialize(predecessor, JsonSerializerOptions.Web));
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await rig.Store.WriteSessionRecordsAsync(fence, Guid.NewGuid(), [write], token));

        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await lane.StepAsync(token)).Step?.Status);
        Assert.DoesNotContain(rig.Simulator.Requests, IsQuietStatisticsRead);
        Assert.Null(await rig.Store.ReadCurrentSessionDemandAsync(rig.Profile, lease.Fence, token));
        Assert.Equal("0", LastPollCapacity(rig.Simulator));
    }

    [Fact]
    public async Task QuietDrainingDemandRefreshEnablesBothClearPassesAndExactSessionRetirement()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        var draining = await DrainingConfigurationAsync(rig.Configuration, token);
        var successor = await AcquireHandoffSuccessorAsync(rig, rig.Store, token);
        var ownership = new ControllerLeaseRecoveryCoordinator(rig.Store, new StoppedController(), rig.Clock, WriteReserve);
        var activated = await ownership.TryActivateAsync(successor, draining, ClockEvidence(rig.Clock), [], token);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        var lease = Assert.IsType<ControllerLease>(activated.Lease);
        await ApplyDrainingGenerationAsync(rig, draining, lease, token);
        var binding = await QuietBindingAsync(rig, token);
        var definition = Definition(draining, lease, rig.Clock, binding, await rig.ConnectAsync(token), capacity: 0) with
        {
            ReadOwnership = _ => Task.FromResult<GitHubRegistrationLease?>(new GitHubRegistrationLease(lease, 2, ClockEvidence(rig.Clock))),
        };
        using var lane = new GitHubListenerSupervisor(rig.Store, rig.Clock, ListenerTiming).Register(definition);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var sessionId = Assert.IsType<Guid>(rig.Simulator.SessionId);

        var timing = new ProfileRetirementTiming(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), WriteReserve);
        var retirement = new ProfileRetirementCoordinator(rig.Store, rig.Clock, timing,
            async (key, cancellation) => await rig.Store.ReadProfileHandoffObservationAsync(key, cancellation));
        rig.Clock.Now += timing.Freshness + TimeSpan.FromSeconds(1);
        await PublishQuietInventoryAsync(rig, lease, token);
        Assert.Equal(ProfileRetirementStatus.ReconciliationPending, (await AdvanceAsync()).Status);
        rig.Simulator.CurrentStatisticsTime = () => rig.Clock.Now;
        rig.Simulator.TotalAssignedJobs = 1;
        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await lane.StepAsync(token)).Step?.Status);
        Assert.Equal(1, (await rig.Store.ReadCurrentSessionDemandAsync(rig.Profile, lease.Fence, token))?.TotalAssignedJobs);
        Assert.Equal(ProfileRetirementStatus.ReconciliationPending, (await AdvanceAsync()).Status);

        rig.Simulator.TotalAssignedJobs = 0;
        rig.Clock.Now += TimeSpan.FromMinutes(1);
        await PublishQuietInventoryAsync(rig, lease, token);
        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await lane.StepAsync(token)).Step?.Status);
        Assert.Equal(ProfileRetirementStatus.FirstClearRecorded, (await AdvanceAsync()).Status);
        Assert.Equal(ProfileRetirementStatus.WaitingForSecondPass, (await AdvanceAsync()).Status);
        rig.Clock.Now += timing.ClearSeparation + TimeSpan.FromSeconds(3);
        await PublishQuietInventoryAsync(rig, lease, token);
        Assert.Equal(ProfileRetirementStatus.ClosureHeld, (await AdvanceAsync()).Status);
        var close = await AdvanceAsync();
        Assert.Equal(ProfileRetirementStatus.SessionRetirementRequired, close.Status);
        Assert.Equal(sessionId, close.Session?.SessionId);
        Assert.Equal(GitHubListenerLaneStatus.Stopped, (await lane.RetireDrainingSessionAsync(retirement, token)).Status);
        Assert.Equal(ProfileRetirementStatus.Retired, (await AdvanceAsync()).Status);
        Assert.Equal(SessionPhase.Retired, (await rig.Store.ReadSessionAsync(rig.Profile, token))?.Phase);
        Assert.Single(rig.Simulator.Requests, request =>
            request.Method == "DELETE" && request.Path == $"{GitHubSimulator.Sessions}/{sessionId:D}");
        Assert.Equal(2, rig.Simulator.Requests.Count(IsQuietStatisticsRead));
        Assert.DoesNotContain(rig.Simulator.Requests, IsAcquisition);

        Task<ProfileRetirementResult> AdvanceAsync() =>
            retirement.AdvanceAsync(rig.Profile, new ActiveControllerAuthority(lease, ClockEvidence(rig.Clock)), token);
    }

    private static IResult QuietStatisticsReply(HttpContext context, GitHubScaleSetBinding binding, DateTimeOffset now, string response)
    {
        context.Response.Headers.Date = (response == "stale date" ? now.AddSeconds(-30) : now).ToString("R", CultureInfo.InvariantCulture);
        if (response == "cached")
        {
            context.Response.Headers.Age = "21";
        }
        if (response == "unavailable")
        {
            return Results.StatusCode(503);
        }
        if (response == "oversized")
        {
            return Results.Content(new string(' ', 65_537), "application/json");
        }

        string fields = response switch
        {
            "missing statistics" => "",
            "missing count" => ""","statistics":{"totalRunningJobs":0}""",
            "duplicate count" => ""","statistics":{"totalAssignedJobs":1,"totalAssignedJobs":0}""",
            "negative" => ""","statistics":{"totalAssignedJobs":-1}""",
            "contradictory" => ""","statistics":{"totalAssignedJobs":0,"totalRunningJobs":1}""",
            _ => ""","statistics":{"totalAssignedJobs":0,"totalRunningJobs":0}""",
        };
        long setId = response == "wrong set" ? binding.ScaleSetId + 1 : binding.ScaleSetId;
        string name = JsonSerializer.Serialize(binding.ScaleSet);
        string labels = JsonSerializer.Serialize(binding.Labels.Select(label => new { name = label }));
        return Results.Content(FormattableString.Invariant($$"""
            {"id":{{setId}},"name":{{name}},"runnerGroupId":{{binding.RunnerGroupId}},"labels":{{labels}}{{fields}}}
            """), "application/json");
    }

    private static async Task<ControllerLease> ExtendQuietLeaseAsync(SessionRig rig, CancellationToken token)
    {
        var renewed = await rig.Store.RenewLeaseAsync(rig.Lease, rig.Clock.Now, rig.Clock.Now.AddMinutes(30), token);
        Assert.Equal(LifecycleWriteOutcome.Committed, renewed.Outcome);
        return Assert.IsType<ControllerLease>(renewed.Lease);
    }

    private static async Task<GitHubScaleSetBinding> QuietBindingAsync(SessionRig rig, CancellationToken token)
    {
        var catalog = await rig.Store.ReadCatalogProfileAsync(rig.Profile, token);
        var binding = Assert.IsType<CatalogProfile>(catalog).Binding;
        return new GitHubScaleSetBinding(rig.Configuration.GitHub[ConnectionId].ScopeUrl, binding.RunnerGroup,
            Assert.IsType<long>(binding.RunnerGroupId), binding.ScaleSet, Assert.IsType<long>(binding.ScaleSetId), binding.Labels);
    }

    private static async Task PublishQuietInventoryAsync(SessionRig rig, ControllerLease lease, CancellationToken token)
    {
        var inventory = await rig.Store.ReadExecutorInventoryAsync(rig.Profile, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.PublishExecutorInventoryAsync(rig.Profile, lease.Fence,
            inventory.Revision, new ExecutorInventoryObservation(rig.Clock.Now, IsComplete: true, BackendCapacityCeiling: 2), token));
    }

    private static bool IsQuietStatisticsRead(SimulatorRequest request) =>
        request.Method == "GET" && request.Host == GitHubProtocolHost.ServiceHost && request.Path == GitHubSimulator.ExactSet;

    private static string LastPollCapacity(GitHubSimulator simulator) => simulator.Requests.Last(request =>
        request.Method == "GET" && request.Host == GitHubProtocolHost.QueueHost).Capacity;
}
