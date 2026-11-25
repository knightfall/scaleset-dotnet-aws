using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

[CollectionDefinition(nameof(FargateEffectJournalSuite), DisableParallelization = true)]
public sealed class FargateEffectJournalSuite;

[Collection(nameof(FargateEffectJournalSuite))]
public sealed class FargateEffectJournalTests
{
    private const string Cluster = "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-shell";
    private const string Definition = "arn:aws:ecs:ap-southeast-2:111122223333:task-definition/linux-shell-runner:7";
    private const string TaskArn = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-shell/11111111111111111111111111111111";
    private const string TaskRole = "arn:aws:iam::111122223333:role/fargate-shell-bootstrap";
    private const string ExecutionRole = "arn:aws:iam::111122223333:role/fargate-shell-execution";
    private const string RunnerSubnet = "subnet-0123456789abcdef2";
    private const string NextOwnerTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const string BootstrapUser = "AROAFIXTUREHANDOFF001:11111111111111111111111111111111";
    private const long RunnerId = 17;
    private static readonly DateTimeOffset Now = MiniStackTable.Now;
    private static readonly TimeSpan Reserve = TimeSpan.FromSeconds(2);
    private static readonly EffectDispatcherTiming Timing = new(TimeSpan.FromSeconds(25), Reserve, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task QuotaExpiryAfterGuardedBeginRestoresTheUnsentPlanAndFreshQuotaPermitsIt()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var launch = Launch(fixture.Profile);
        var provider = new FixtureProvider(fixture.Clock);
        await PlanAsync(fixture, launch, token);

        var deadline = Now.AddSeconds(5);
        Assert.Equal(EffectDispatchStatus.Ineligible, await fixture.Dispatcher.DispatchPlannedAsync(fixture.Lease, Proof(fixture.Clock),
            fixture.Profile, launch.IntentId, provider, CancellationToken.None, token));
        var pause = table.Wire.PauseAfterNextSuccessfulTransactionWrite(LifecycleKey.Operation(fixture.Profile, launch.IntentId));
        var dispatch = DispatchBeforeAsync(deadline);
        try
        {
            await pause.Captured.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(EffectState.Executing, (await ReadAsync(fixture, launch, token)).Entry.State);
            fixture.Clock.Now = deadline.AddSeconds(-1); // The conservative upper clock equals expiry.
        }
        finally
        {
            pause.Resume();
        }
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await dispatch);
        Assert.Empty(provider.Sends);
        var held = await ReadAsync(fixture, launch, token);
        Assert.Equal(EffectState.Planned, held.Entry.State);
        Assert.Null(held.Entry.Attempt);
        Assert.True(held.Entry.Envelope.Matches(launch));
        Assert.Single((await ReservationsAsync(fixture, token)).Reservations);
        Assert.Equal(EffectDispatchStatus.NotPlanned, await ReplayAsync(fixture, fixture.Lease, launch, provider, token));

        Assert.Equal(EffectDispatchStatus.Ineligible, await DispatchBeforeAsync(deadline));
        Assert.Empty(provider.Sends);
        Assert.Equal(held.TransitionId, (await ReadAsync(fixture, launch, token)).TransitionId);
        var freshDeadline = fixture.Clock.Now.AddSeconds(10);
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchBeforeAsync(freshDeadline));
        var dispatched = Assert.Single(provider.Sends);
        Assert.True(dispatched.Envelope.Matches(launch));
        Assert.Equal(freshDeadline, dispatched.Attempt.Deadline);

        Task<EffectDispatchStatus> DispatchBeforeAsync(DateTimeOffset quotaDeadline) => fixture.Dispatcher.DispatchPlannedAsync(
            fixture.Lease, Proof(fixture.Clock), fixture.Profile, launch.IntentId, provider, quotaDeadline, CancellationToken.None, token);
    }

    [Fact]
    public async Task PeriodicReconciliationReleasesOnlyAnExpiredPlannedLaunchReservation()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var expired = Launch(fixture.Profile);
        var later = Launch(fixture.Profile, earliest: Now.AddMinutes(1));
        var provider = new FixtureProvider(fixture.Clock);
        await PlanAsync(fixture, expired, token);
        await PlanAsync(fixture, later, token);
        fixture.Clock.Now = expired.ProviderReplayDeadline!.Value;

        var tooEarly = await PassAsync();
        Assert.Equal(LifecycleWriteOutcome.Committed, tooEarly.Outcome);
        Assert.Equal(0, tooEarly.Settled);
        Assert.Equal(2, (await ReservationsAsync(fixture, token)).Reservations.Length);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);

        var expiredPass = await PassAsync();
        Assert.Equal(LifecycleWriteOutcome.Committed, expiredPass.Outcome);
        Assert.Equal(1, expiredPass.Settled);
        Assert.Empty(provider.Sends);
        Assert.Empty(provider.Probes);
        Assert.Equal(EffectResolutionKind.CanceledBeforeSend, (await ReadAsync(fixture, expired, token)).Entry.Resolution?.Kind);
        var remaining = Assert.Single((await ReservationsAsync(fixture, token)).Reservations);
        Assert.Equal(Assert.IsType<FargateLaunchEffectRequest>(later.Request).AllocationId, remaining.AllocationId);

        fixture.Clock.Now = fixture.Lease.ValidUntil.AddSeconds(1);
        var acquired = await fixture.Store.TryAcquireLeaseAsync(fixture.Profile.Deployment, "next-owner", NextOwnerTask,
            fixture.Lease.WriterProtocol, fixture.Clock.Now, fixture.Clock.Now.AddMinutes(5), token);
        var next = Assert.IsType<ControllerLease>(acquired.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, next.Mode);
        var recovery = new EffectRecoveryAuthority(new ControllerLeaseRecoveryCoordinator(fixture.Store, new StoppedFenceObserver(),
            fixture.Clock, Reserve), []);
        EffectReconciliationPass? heldPass = null;
        for (int pass = 0; pass < 4 && heldPass is null or { Outcome: LifecycleWriteOutcome.RebuildRequired }; pass++)
        {
            heldPass = await fixture.Dispatcher.ReconcilePassAsync(next, Proof(fixture.Clock), fixture.Profile, null, provider, recovery, token);
        }
        Assert.Equal(LifecycleWriteOutcome.Committed, heldPass?.Outcome);
        Assert.Equal(0, heldPass?.Settled);
        Assert.Equal(EffectState.Planned, (await ReadAsync(fixture, later, token)).Entry.State);
        Assert.Empty(provider.Sends);
        Assert.Empty(provider.Probes);

        Task<EffectReconciliationPass> PassAsync() => fixture.Dispatcher.ReconcilePassAsync(fixture.Lease, Proof(fixture.Clock),
            fixture.Profile, null, provider, recovery: null, token);
    }

    [Fact]
    public async Task LostRunResponseSettlesFromExactStoppedReadbackBeforeRunnerExists()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var launch = Launch(fixture.Profile);
        var provider = new FixtureProvider(fixture.Clock);
        await PlanAsync(fixture, launch, token);
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, launch, provider, token));
        var attempt = (await ReadAsync(fixture, launch, token)).Entry.Attempt;
        var binding = Bind(launch);
        provider.Positive = envelope => Evidence(fixture.Clock, envelope, binding, ExecutorResourceState.IrreversiblyTerminated);
        table.Wire.DropNextSuccessfulTransactionResponse();

        Assert.Equal(EffectDispatchStatus.Settled, await ReplayAsync(fixture, fixture.Lease, launch, provider, token));
        Assert.Single(provider.Sends);
        Assert.Equal(attempt, (await ReadAsync(fixture, launch, token)).Entry.Attempt);
        var executor = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
        Assert.Equal(binding, executor.Binding);
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, executor.State.Resource?.State);
        Assert.Null(executor.State.RunnerId);
        Assert.Null(executor.State.Job);
        Assert.Null(executor.State.Process);
        Assert.Empty((await ReservationsAsync(fixture, token)).Reservations);
        Assert.True(await fixture.Dispatcher.HasNoUnfinishedEffectsAsync(fixture.Lease, fixture.Profile, token));
    }

    [Fact]
    public async Task CompleteEmptyReadbackReplaysExactLaunchAfterRenewalAndNeverAfterOriginalCutoff()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var launch = Launch(fixture.Profile);
        var provider = new FixtureProvider(fixture.Clock);
        await PlanAsync(fixture, launch, token);
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, launch, provider, token));
        var original = Assert.Single(provider.Sends);
        var first = await ReadAsync(fixture, launch, token);

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await ReplayAsync(fixture, fixture.Lease, launch, provider, token));
        Assert.Single(provider.Sends);
        fixture.Clock.Now = original.Attempt.Deadline.AddSeconds(4);
        var renewal = await fixture.Store.RenewLeaseAsync(fixture.Lease, fixture.Clock.Now, fixture.Lease.ValidUntil.AddMinutes(5), token);
        var renewed = Assert.IsType<ControllerLease>(renewal.Lease);
        Assert.Equal(fixture.Lease.Fence, renewed.Fence);
        Assert.NotEqual(fixture.Lease.Revision, renewed.Revision);

        provider.Complete = false;
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await ReplayAsync(fixture, renewed, launch, provider, token));
        Assert.Single(provider.Sends);
        Assert.Equal(first.TransitionId, (await ReadAsync(fixture, launch, token)).TransitionId);
        provider.Complete = true;
        var reconciled = await fixture.Dispatcher.ReconcilePassAsync(renewed, Proof(fixture.Clock), fixture.Profile, null, provider, null, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, reconciled.Outcome);
        Assert.Equal(1, reconciled.Examined);
        Assert.Equal(0, reconciled.Settled);
        Assert.Equal(2, provider.Sends.Count);
        var replay = provider.Sends[1];
        Assert.Equal(original.Envelope.CanonicalJson, replay.Envelope.CanonicalJson);
        Assert.Equal(Assert.IsType<FargateLaunchEffectRequest>(original.Envelope.Request).ClientToken,
            Assert.IsType<FargateLaunchEffectRequest>(replay.Envelope.Request).ClientToken);
        Assert.NotEqual(original.Attempt.Id, replay.Attempt.Id);
        Assert.Equal(original.Attempt.LeaseEpoch, replay.Attempt.LeaseEpoch);
        var replayed = await ReadAsync(fixture, launch, token);
        Assert.True(replayed.Entry.EarlierSendUncertain);
        Assert.True(replayed.Entry.LatestAuthorizedDeadline < launch.ProviderReplayDeadline);
        Assert.Single((await ReservationsAsync(fixture, token)).Reservations);

        fixture.Clock.Now = launch.ProviderReplayDeadline!.Value;
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await ReplayAsync(fixture, renewed, launch, provider, token));
        Assert.Equal(2, provider.Sends.Count);
        Assert.Equal(replayed.TransitionId, (await ReadAsync(fixture, launch, token)).TransitionId);
    }

    [Fact]
    public async Task NewLeaseEpochCannotReplayAndCompetingAllocationCannotClaimClusterToken()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token, TimeSpan.FromMinutes(5));
        var launch = Launch(fixture.Profile);
        var request = Assert.IsType<FargateLaunchEffectRequest>(launch.Request);
        await PlanAsync(fixture, launch, token);
        var collision = Launch(fixture.Profile, request.ClientToken);
        Assert.Equal(LifecycleWriteOutcome.Conflict, await fixture.Journal.PlanAsync(fixture.Lease, collision, Guid.NewGuid(), token));
        var planned = await ReadAsync(fixture, launch, token);
        var unguarded = await fixture.Journal.BeginAsync(fixture.Lease, planned, Guid.NewGuid(), Guid.NewGuid(), fixture.Clock.Now,
            fixture.Clock.Now.AddSeconds(15), Reserve, token);
        Assert.Null(unguarded.Permission);

        var provider = new FixtureProvider(fixture.Clock);
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, launch, provider, token));
        fixture.Clock.Now = fixture.Lease.ValidUntil.AddSeconds(1);
        var acquired = await fixture.Store.TryAcquireLeaseAsync(fixture.Profile.Deployment, "new-owner", NextOwnerTask,
            fixture.Lease.WriterProtocol, fixture.Clock.Now, fixture.Clock.Now.AddMinutes(5), token);
        var next = Assert.IsType<ControllerLease>(acquired.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, next.Mode);
        Assert.Equal(fixture.Lease.Epoch + 1, next.Epoch);

        Assert.Equal(EffectDispatchStatus.OwnershipUnavailable, await ReplayAsync(fixture, next, launch, provider, token));
        Assert.Equal(EffectDispatchStatus.OwnershipUnavailable, await ReplayAsync(fixture, fixture.Lease, launch, provider, token));
        Assert.Empty(provider.Probes);
        Assert.Single(provider.Sends);

        var binding = Bind(launch);
        provider.Positive = envelope => Evidence(fixture.Clock, envelope, binding, ExecutorResourceState.IrreversiblyTerminated);
        var recovery = new EffectRecoveryAuthority(new ControllerLeaseRecoveryCoordinator(fixture.Store, new StoppedFenceObserver(),
            fixture.Clock, Reserve), []);
        EffectReconciliationPass? reconciled = null;
        for (int pass = 0; pass < 4 && reconciled is null or { Outcome: LifecycleWriteOutcome.RebuildRequired }; pass++)
        {
            reconciled = await fixture.Dispatcher.ReconcilePassAsync(next, Proof(fixture.Clock), fixture.Profile, null, provider, recovery, token);
        }
        Assert.Equal(LifecycleWriteOutcome.Committed, reconciled?.Outcome);
        Assert.Equal(1, reconciled?.Settled);
        Assert.Empty(provider.Probes);
        Assert.Single(provider.Sends);
        var executor = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, executor.State.Resource?.State);
    }

    [Fact]
    public async Task PendingExecutorRetainsTheExactLaunchReservationUntilResourceDeath()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var launch = Launch(fixture.Profile);
        var request = Assert.IsType<FargateLaunchEffectRequest>(launch.Request);
        var binding = Bind(launch);
        await PlanAsync(fixture, launch, token);
        var plannedReservation = Assert.Single((await ReservationsAsync(fixture, token)).Reservations);
        Assert.Equal(request.AllocationId, plannedReservation.AllocationId);
        Assert.Equal(request.CpuUnits, plannedReservation.CpuUnits);
        Assert.Equal(fixture.Profile, plannedReservation.Profile);
        Assert.Equal(request.ClusterArn, plannedReservation.ClusterArn);
        Assert.Equal(request.TaskDefinitionArn, plannedReservation.TaskDefinitionArn);
        Assert.Null(plannedReservation.TaskArn);
        var provider = new FixtureProvider(fixture.Clock)
        {
            OnSend = envelope => Evidence(fixture.Clock, envelope, binding, ExecutorResourceState.Starting, providerVersion: 1),
        };

        Assert.Equal(EffectDispatchStatus.Settled, await DispatchAsync(fixture, launch, provider, token));
        Assert.True(await fixture.Dispatcher.HasNoUnfinishedEffectsAsync(fixture.Lease, fixture.Profile, token));
        var pendingReservation = Assert.Single((await ReservationsAsync(fixture, token)).Reservations);
        Assert.Equal(plannedReservation.AllocationId, pendingReservation.AllocationId);
        Assert.Equal(plannedReservation.CpuUnits, pendingReservation.CpuUnits);
        Assert.Equal(plannedReservation.EligibleSubnetIds, pendingReservation.EligibleSubnetIds);
        Assert.Equal(plannedReservation.Profile, pendingReservation.Profile);
        Assert.Equal(plannedReservation.ClusterArn, pendingReservation.ClusterArn);
        Assert.Equal(plannedReservation.TaskDefinitionArn, pendingReservation.TaskDefinitionArn);
        Assert.Equal(TaskArn, pendingReservation.TaskArn);

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(fixture.Lease.Fence, binding,
            new ExecutorResourceObservation(ExecutorResourceState.IrreversiblyTerminated, fixture.Clock.Now, Now, Guid.NewGuid(), ProviderVersion: 2), token));
        Assert.Empty((await ReservationsAsync(fixture, token)).Reservations);

        // A restarted consumer can see an old event and an equally stale DescribeTasks reply.
        var restartedStore = new DynamoDbLifecycleStore(table.Client, table.Name, fixture.Clock);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        Assert.Equal(LifecycleWriteOutcome.Committed, await restartedStore.ObserveResourceAsync(fixture.Lease.Fence, binding,
            new ExecutorResourceObservation(ExecutorResourceState.Running, fixture.Clock.Now, Now, Guid.NewGuid(), ProviderVersion: 1), token));
        var retained = Assert.IsType<ExecutorSnapshot>(await restartedStore.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, retained.State.Resource?.State);
        Assert.Equal(2, retained.State.Resource?.ProviderVersion);
        Assert.False(retained.State.HasConflict);
        Assert.Empty((await ReservationsAsync(fixture, token)).Reservations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopNeedsCompletedCleanupAuthorityOrAnAlreadyAppliedExactTerminalRead(bool completed, bool alreadyStopped)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var launch = Launch(fixture.Profile);
        var binding = Bind(launch);
        await SeedStopCandidateAsync(fixture, binding, completed, token);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var stop = Stop(fixture, binding);
        await PlanAsync(fixture, stop, token);
        var provider = new FixtureProvider(fixture.Clock)
        {
            AlreadyApplied = alreadyStopped ? Evidence(fixture.Clock, stop, binding, ExecutorResourceState.IrreversiblyTerminated) : null,
            OnSend = envelope => Evidence(fixture.Clock, envelope, binding, ExecutorResourceState.IrreversiblyTerminated),
        };

        Assert.Equal(completed ? EffectDispatchStatus.Settled : EffectDispatchStatus.Ineligible, await DispatchAsync(fixture, stop, provider, token));
        Assert.Equal(completed && !alreadyStopped ? 1 : 0, provider.Sends.Count);
        var executor = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
        Assert.Equal(completed ? ExecutorResourceState.IrreversiblyTerminated : ExecutorResourceState.Running, executor.State.Resource?.State);
        Assert.Equal(completed ? ExecutorJobPhase.Completed : ExecutorJobPhase.Started, executor.State.Job?.Phase);
        Assert.Equal(RunnerId, executor.State.RunnerId);
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("stale")]
    [InlineData("wrong-incarnation")]
    public async Task OldTerminalExecutorRequiresFreshExactStopReadback(string proofKind)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var binding = Bind(Launch(fixture.Profile));
        await SeedStopCandidateAsync(fixture, binding, completed: false, token);
        var terminalAt = Now.AddSeconds(1);
        fixture.Clock.Now = terminalAt;
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(fixture.Lease.Fence, binding,
            new ExecutorResourceObservation(ExecutorResourceState.IrreversiblyTerminated, terminalAt, null, Guid.NewGuid(), ProviderVersion: 5), token));
        var stop = Stop(fixture, binding);
        await PlanAsync(fixture, stop, token);

        fixture.Clock.Now = terminalAt + Timing.ObservationFreshness + TimeSpan.FromSeconds(1);
        var proof = Evidence(fixture.Clock, stop, binding, ExecutorResourceState.IrreversiblyTerminated, providerVersion: 6);
        proof = proofKind switch
        {
            "stale" => proof with { ResourceObservation = proof.ResourceObservation! with { ObservedAt = terminalAt } },
            "wrong-incarnation" => proof with { ExecutorBinding = binding with { Incarnation = Guid.NewGuid() } },
            _ => proof,
        };
        var provider = new FixtureProvider(fixture.Clock) { AlreadyApplied = proof };
        bool settles = proofKind == "fresh";

        Assert.Equal(settles ? EffectDispatchStatus.Settled : EffectDispatchStatus.Ineligible,
            await DispatchAsync(fixture, stop, provider, token));
        Assert.Empty(provider.Sends);
        var effect = await ReadAsync(fixture, stop, token);
        Assert.Equal(settles ? EffectState.Settled : EffectState.Planned, effect.Entry.State);
        Assert.Equal(settles ? proof : null, effect.Entry.Resolution);
        var executor = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, executor.State.Resource?.State);
        Assert.Equal(terminalAt, executor.State.Resource?.ObservedAt);
        Assert.Equal(settles ? 6 : 5, executor.State.Resource?.ProviderVersion);
        Assert.Equal(ExecutorJobPhase.Started, executor.State.Job?.Phase);
        Assert.Equal(RunnerId, executor.State.RunnerId);
        Assert.False(executor.State.HasConflict);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnusedAdmissionClosureAndJitStagingCannotBothCommit(bool closureWins)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        using var recipient = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, token);
        var binding = Bind(Launch(fixture.Profile));
        var executor = await SeedLiveAsync(fixture, binding, token);
        var (catalog, identity, runnerName, _) = await SeedHelloAsync(table, fixture, binding, recipient, token);
        var key = closureWins ? LifecycleKey.Handoff(fixture.Profile, identity.AllocationId) :
            LifecycleKey.Runner(fixture.Profile, binding.AllocationId);
        var pause = table.Wire.PauseNextTransactionWrite(key);
        var freshness = Timing.ObservationFreshness;

        Task<LifecycleWriteOutcome> Close() => fixture.Store.CloseFargateAdmissionAsync(fixture.Lease, catalog, executor, freshness, token);
        Task<JitHandoffChange> Stage() => fixture.Store.StageJitHandoffAsync(fixture.Lease, catalog, executor, identity, runnerName, token);
        if (closureWins)
        {
            var staging = Stage();
            try
            {
                await pause.Captured.WaitAsync(token);
                Assert.Equal(LifecycleWriteOutcome.Committed, await Close());
            }
            finally
            {
                pause.Resume();
            }
            Assert.Equal(LifecycleWriteOutcome.Conflict, (await staging).Outcome);
            Assert.Null(await fixture.Store.ReadJitHandoffAsync(fixture.Profile, identity.AllocationId, token));
            var closed = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
            Assert.Equal(FargateAdmissionClosure.Unused, closed.State.AdmissionClosure);
            Assert.Equal(LifecycleWriteOutcome.Conflict, (await fixture.Store.StageJitHandoffAsync(fixture.Lease, catalog, closed,
                identity, runnerName, token)).Outcome);
        }
        else
        {
            var closing = Close();
            try
            {
                await pause.Captured.WaitAsync(token);
                Assert.Equal(LifecycleWriteOutcome.Committed, (await Stage()).Outcome);
            }
            finally
            {
                pause.Resume();
            }
            Assert.Equal(LifecycleWriteOutcome.Conflict, await closing);
        }

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var stop = Stop(fixture, binding);
        await PlanAsync(fixture, stop, token);
        var provider = TerminalProvider(fixture.Clock, binding);
        Assert.Equal(closureWins ? EffectDispatchStatus.Settled : EffectDispatchStatus.Ineligible, await DispatchAsync(fixture, stop, provider, token));
        Assert.Equal(closureWins ? 1 : 0, provider.Sends.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task AcquisitionAfterBeginDiscardsUnsentStopAndCanRearmAfterItCloses()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var binding = Bind(Launch(fixture.Profile));
        var executor = await SeedLiveAsync(fixture, binding, token);
        var catalog = Assert.IsType<CatalogProfile>(await fixture.Store.ReadCatalogProfileAsync(fixture.Profile, token));
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await fixture.Store.CloseFargateAdmissionAsync(fixture.Lease, catalog, executor, Timing.ObservationFreshness, token));
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var stop = Stop(fixture, binding);
        await PlanAsync(fixture, stop, token);
        var provider = TerminalProvider(fixture.Clock, binding);
        var pause = table.Wire.PauseAfterNextSuccessfulTransactionWrite(LifecycleKey.Operation(fixture.Profile, stop.IntentId));
        var dispatch = DispatchAsync(fixture, stop, provider, token);
        try
        {
            await pause.Captured.WaitAsync(token);
            await WriteAdmissionBalanceAsync(fixture, 1, token);
        }
        finally
        {
            pause.Resume();
        }

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await dispatch);
        Assert.Empty(provider.Sends);
        var discarded = await ReadAsync(fixture, stop, token);
        Assert.Equal(EffectResolutionKind.AbandonedBeforeSend, discarded.Entry.Resolution?.Kind);
        Assert.Equal(EffectDispatchStatus.Ineligible, await DispatchAsync(fixture, stop, provider, token));
        Assert.Equal(discarded.TransitionId, (await ReadAsync(fixture, stop, token)).TransitionId);
        await WriteAdmissionBalanceAsync(fixture, 0, token);
        Assert.Equal(EffectDispatchStatus.Settled, await DispatchAsync(fixture, stop, provider, token));
        var sent = Assert.Single(provider.Sends);
        Assert.True(stop.Matches(sent.Envelope));
        Assert.NotEqual(discarded.Entry.Attempt?.Id, sent.Attempt.Id);
    }

    [Fact]
    public async Task StopWithConsumedAuthorizationAndAnAmbiguousResponseCannotRearm()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var fixture = await CreateFixtureAsync(table, token);
        var binding = Bind(Launch(fixture.Profile));
        var executor = await SeedLiveAsync(fixture, binding, token);
        var catalog = Assert.IsType<CatalogProfile>(await fixture.Store.ReadCatalogProfileAsync(fixture.Profile, token));
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await fixture.Store.CloseFargateAdmissionAsync(fixture.Lease, catalog, executor, Timing.ObservationFreshness, token));
        var stop = Stop(fixture, binding);
        await PlanAsync(fixture, stop, token);
        var provider = new FixtureProvider(fixture.Clock);

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, stop, provider, token));
        var executing = await ReadAsync(fixture, stop, token);
        Assert.Equal(EffectState.Executing, executing.Entry.State);
        Assert.Null(executing.Entry.Resolution);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, stop, provider, token));
        Assert.Equal(executing.TransitionId, (await ReadAsync(fixture, stop, token)).TransitionId);
        Assert.Equal(executing.Entry.Attempt, Assert.Single(provider.Sends).Attempt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FargateClosureRevokesOnlyAnExactUnconsumedOfferUnderCompleteInventory(bool consumed, bool loseRevocationReply)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        using var recipient = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, token);
        var binding = Bind(Launch(fixture.Profile));
        var unused = await SeedLiveAsync(fixture, binding, token);
        var (catalog, identity, runnerName, bootstrap) = await SeedHelloAsync(table, fixture, binding, recipient, token);
        var staged = Assert.IsType<JitHandoffSnapshot>((await fixture.Store.StageJitHandoffAsync(fixture.Lease, catalog, unused,
            identity, runnerName, token)).Handoff);
        var request = new GitHubJitGenerateEffectRequest(identity.ScopeIdentity, identity.ScaleSetId, identity.AllocationId,
            identity.ResourceArn, identity.Incarnation, identity.AwsUserId, staged.HelloSha256, staged.OfferId, staged.RunnerName);
        var generation = EffectEnvelope.Create(fixture.Profile, staged.IntentId, 1, request, fixture.Clock.Now, null);
        await PlanAsync(fixture, generation, token);
        var begun = await fixture.Journal.BeginJitAsync(fixture.Lease, catalog, unused, staged, await ReadAsync(fixture, generation, token),
            fixture.Clock.Now, fixture.Clock.Now.AddSeconds(20), Reserve, token);
        Assert.True(Assert.IsType<EffectSendAuthorization>(begun.Permission).TryConsume(out _));
        var attempt = await ReadAsync(fixture, generation, token);
        var offer = JitOfferCryptography.Seal(JitOfferBinding.Create(identity, staged.Hello.BootNonce, fixture.Clock.Now.AddMinutes(1)),
            staged.Hello, "synthetic-test-jit"u8, fixture.Clock);
        var offered = Assert.IsType<JitHandoffSnapshot>((await fixture.Store.PublishJitHandoffAsync(fixture.Lease, catalog, staged,
            attempt, RunnerId, offer, token)).Handoff);
        var registered = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
        var freshness = Timing.ObservationFreshness;

        var inventory = await fixture.Store.ReadExecutorInventoryAsync(fixture.Profile, token);
        var coverage = Assert.IsType<ExecutorInventoryObservation>(inventory.Coverage);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.PublishExecutorInventoryAsync(fixture.Profile,
            fixture.Lease.Fence, inventory.Revision, coverage with { IsComplete = false }, token));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await CloseAsync(fixture.Store));
        Assert.Equal(JitHandoffState.Offered, (await fixture.Store.ReadJitHandoffAsync(fixture.Profile, identity.AllocationId, token))?.State);
        var partial = await fixture.Store.ReadExecutorInventoryAsync(fixture.Profile, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.PublishExecutorInventoryAsync(fixture.Profile,
            fixture.Lease.Fence, partial.Revision, coverage, token));
        if (consumed)
        {
            Assert.Equal(BootstrapOfferStatus.Consumed, (await bootstrap.TryConsumeOfferAsync(recipient.Hello, token)).Status);
            Assert.Equal(LifecycleWriteOutcome.Conflict, (await fixture.Store.RevokeJitHandoffAsync(fixture.Lease, offered, token)).Outcome);
            Assert.Equal(LifecycleWriteOutcome.Conflict, await CloseAsync(fixture.Store));
        }
        else
        {
            await WriteAdmissionBalanceAsync(fixture, 1, token);
            Assert.Equal(LifecycleWriteOutcome.Conflict, await CloseAsync(fixture.Store));
            Assert.Equal(JitHandoffState.Offered, (await fixture.Store.ReadJitHandoffAsync(fixture.Profile, identity.AllocationId, token))?.State);
            await WriteAdmissionBalanceAsync(fixture, 0, token);
            if (loseRevocationReply)
            {
                table.Wire.DropNextSuccessfulTransactionResponse();
            }
            Assert.Equal(LifecycleWriteOutcome.Committed, await CloseAsync(fixture.Store));
            var restarted = new DynamoDbLifecycleStore(table.Client, table.Name, fixture.Clock);
            Assert.Equal(JitHandoffState.Revoked, (await restarted.ReadJitHandoffAsync(fixture.Profile, identity.AllocationId, token))?.State);
            Assert.Equal(LifecycleWriteOutcome.Committed, await CloseAsync(restarted));
            Assert.Equal(BootstrapOfferStatus.NotAvailable, (await bootstrap.TryConsumeOfferAsync(recipient.Hello, token)).Status);
        }

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var stop = Stop(fixture, binding);
        await PlanAsync(fixture, stop, token);
        var provider = TerminalProvider(fixture.Clock, binding);
        Assert.Equal(consumed ? EffectDispatchStatus.Ineligible : EffectDispatchStatus.Settled, await DispatchAsync(fixture, stop, provider, token));
        Assert.Equal(consumed ? 0 : 1, provider.Sends.Count);
        var current = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
        Assert.Equal(consumed ? null : FargateAdmissionClosure.OfferRevoked, current.State.AdmissionClosure);
        Assert.Equal(RunnerId, current.State.RunnerId);
        Assert.Null(current.State.Job);

        Task<LifecycleWriteOutcome> CloseAsync(DynamoDbLifecycleStore store) =>
            store.CloseFargateAdmissionAsync(fixture.Lease, catalog, registered, freshness, token);
    }

    private static async Task<Fixture> CreateFixtureAsync(MiniStackTable table, CancellationToken cancellationToken, TimeSpan? leaseDuration = null)
    {
        var configuration = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken));
        var profile = new LifecycleProfile(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), Assert.Single(configuration.Profiles).Id);
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var fence = table.Lease with { Deployment = profile.Deployment };
        await table.SetLeaseAsync(fence, Now + (leaseDuration ?? TimeSpan.FromHours(1)), cancellationToken);
        var lease = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(profile.Deployment, cancellationToken));
        for (int pass = 0; ; pass++)
        {
            var rollout = await store.AdvanceCatalogRolloutAsync(fence, configuration, 1, cancellationToken);
            if (rollout.Status != CatalogRolloutStatus.Staging || pass == 3)
            {
                Assert.Equal(CatalogRolloutStatus.Applied, rollout.Status);
                break;
            }
        }
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(fence, catalog, 7, 37, cancellationToken));
        Assert.Equal(EffectIndexState.Ready, (await store.AdvanceEffectRebuildAsync(profile, fence, cancellationToken)).Head?.State);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, fence, null,
            new ExecutorInventoryObservation(Now, IsComplete: true, BackendCapacityCeiling: 10, BackendLaunchHeadroom: 10), cancellationToken));
        return new Fixture(store, new DynamoDbEffectJournal(store, clock), new DynamoDbEffectDispatcher(store, clock, Timing), lease, profile, clock);
    }

    private static async Task SeedStopCandidateAsync(Fixture fixture, ExecutorBinding binding, bool completed, CancellationToken cancellationToken)
    {
        var fence = fixture.Lease.Fence;
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorAsync(fence, binding, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorRunnerAsync(fence, binding, RunnerId, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(fence, binding,
            new ExecutorResourceObservation(ExecutorResourceState.Running, Now, Now.AddMinutes(-1), Guid.NewGuid()), cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveProcessAsync(fence, binding,
            new ExecutorProcessObservation(RunnerId, completed ? ExecutorProcessState.Exited : ExecutorProcessState.Ready,
                Now.AddSeconds(-10), Guid.NewGuid()), cancellationToken));
        var catalog = Assert.IsType<CatalogProfile>(await fixture.Store.ReadCatalogProfileAsync(fixture.Profile, cancellationToken));
        var opened = await fixture.Store.BeginSessionOpenAsync(new SessionBinding(fixture.Profile, catalog.Binding.ScopeIdentity, 37), fence, null,
            "fargate-stop-fixture", Now, Now.AddMinutes(1), cancellationToken);
        var permit = Assert.IsType<SessionSendAuthorization>(opened.Permission);
        Assert.True(permit.TryConsume(out _));
        var id = Guid.NewGuid();
        var confirmed = await fixture.Store.ConfirmSessionOpenedAsync(permit, id, cancellationToken);
        var session = Assert.IsType<OwnedSessionFence>(confirmed.Session?.Fence);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.WriteSessionDeliveryRecordsAsync(session, Guid.NewGuid(),
            [new LifecycleRecordWrite(LifecycleKey.Message(fixture.Profile, id, 0), null, "{}")],
            [new ExecutorJobObservation(9, RunnerId, completed ? ExecutorJobPhase.Completed : ExecutorJobPhase.Started, id, 0, Now.AddSeconds(-11))],
            cancellationToken));
    }

    private static EffectEnvelope Launch(LifecycleProfile profile, Guid? clientToken = null, DateTimeOffset? earliest = null)
    {
        var allocation = Guid.NewGuid();
        var tags = new FargateLaunchOwnershipTags(RunnerOwnershipTags.ControllerValue, profile.Deployment, profile.ProfileId, allocation.ToString("D"), "v1");
        var request = new FargateLaunchEffectRequest(allocation, Cluster, Definition, [RunnerSubnet], ["sg-0123456789abcdef1"],
            TaskRole, ExecutionRole, "runner", clientToken ?? Guid.NewGuid(), tags, 1024);
        var start = earliest ?? Now;
        return EffectEnvelope.Create(profile, EffectIntentIdentity.ForAllocation(request)!, 1, request, start, start.AddMinutes(50));
    }

    private static EffectEnvelope Stop(Fixture fixture, ExecutorBinding binding)
    {
        var request = new FargateStopEffectRequest(Guid.Parse(binding.AllocationId), Cluster, TaskArn, Definition, binding.Incarnation);
        return EffectEnvelope.Create(fixture.Profile, EffectIntentIdentity.ForAllocation(request)!, 1, request, fixture.Clock.Now, null);
    }

    private static async Task<ExecutorSnapshot> SeedLiveAsync(Fixture fixture, ExecutorBinding binding, CancellationToken token)
    {
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorAsync(fixture.Lease.Fence, binding, token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(fixture.Lease.Fence, binding,
            new ExecutorResourceObservation(ExecutorResourceState.Running, Now, Now.AddMinutes(-1), Guid.NewGuid()), token));
        return Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, token));
    }

    private static async Task<(CatalogProfile Catalog, JitHandoffIdentity Identity, string RunnerName, DynamoDbBootstrapOfferClient Bootstrap)> SeedHelloAsync(
        MiniStackTable table, Fixture fixture, ExecutorBinding binding, JitRecipientKey recipient, CancellationToken token)
    {
        var catalog = Assert.IsType<CatalogProfile>(await fixture.Store.ReadCatalogProfileAsync(fixture.Profile, token));
        var identity = new JitHandoffIdentity(fixture.Profile.Deployment, fixture.Profile.ProfileId, catalog.Binding.ScopeIdentity, 37,
            Guid.Parse(binding.AllocationId), binding.ResourceArn, binding.Incarnation, BootstrapUser);
        var bootstrap = new DynamoDbBootstrapOfferClient(table.Client, table.Name, BootstrapUser, binding.ResourceArn, fixture.Clock);
        Assert.Equal(LifecycleWriteOutcome.Committed, await BrokerHelloFixture.RegisterAsync(table.Client,
            table.Name, BootstrapUser, recipient.Hello, fixture.Clock, token));
        return (catalog, identity, "ss-" + JitOfferBinding.GetOfferId(identity)[..48], bootstrap);
    }

    private static async Task WriteAdmissionBalanceAsync(Fixture fixture, int held, CancellationToken token)
    {
        var key = LifecycleKey.Admission(fixture.Profile);
        var current = await fixture.Store.ReadRecordAsync(key, token);
        string payload = FormattableString.Invariant($$"""{"heldCount":{{held}}}""");
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.WriteRecordsAsync(fixture.Lease.Fence, Guid.NewGuid(),
            [new LifecycleRecordWrite(key, current?.Revision, payload)], token));
    }

    private static FixtureProvider TerminalProvider(FixtureClock clock, ExecutorBinding binding) =>
        new(clock) { OnSend = envelope => Evidence(clock, envelope, binding, ExecutorResourceState.IrreversiblyTerminated) };

    private static ExecutorBinding Bind(EffectEnvelope launch)
    {
        var request = Assert.IsType<FargateLaunchEffectRequest>(launch.Request);
        return new ExecutorBinding(launch.Profile, request.AllocationId.ToString("D"), TaskArn,
            FargateExecutorIncarnation.Compute(TaskArn, Definition, request.AllocationId), new FargateExecutorIdentity(Cluster, Definition));
    }

    private static EffectEvidence Evidence(FixtureClock clock, EffectEnvelope envelope, ExecutorBinding binding, ExecutorResourceState state,
        long? providerVersion = null) =>
        new(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, clock.Now)
        {
            ExecutorBinding = binding,
            ResourceObservation = new ExecutorResourceObservation(state, clock.Now, state == ExecutorResourceState.Starting ? Now : null,
                Guid.NewGuid(), providerVersion),
        };

    private static ControllerClockEvidence Proof(FixtureClock clock) => new(clock.Now, clock.Now.AddMinutes(2), TimeSpan.FromSeconds(1));

    private static Task<EffectDispatchStatus> DispatchAsync(Fixture fixture, EffectEnvelope envelope, FixtureProvider provider,
        CancellationToken cancellationToken) => fixture.Dispatcher.DispatchPlannedAsync(fixture.Lease, Proof(fixture.Clock), envelope.Profile,
            envelope.IntentId, provider, envelope.Request is FargateLaunchEffectRequest ? fixture.Clock.Now.AddMinutes(1) : null,
            CancellationToken.None, cancellationToken);

    private static Task<EffectDispatchStatus> ReplayAsync(Fixture fixture, ControllerLease lease, EffectEnvelope envelope, FixtureProvider provider,
        CancellationToken cancellationToken) => fixture.Dispatcher.ReplayFargateLaunchAsync(lease, Proof(fixture.Clock), envelope.Profile,
            envelope.IntentId, provider, CancellationToken.None, cancellationToken);

    private static async Task PlanAsync(Fixture fixture, EffectEnvelope envelope, CancellationToken cancellationToken) =>
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await fixture.Journal.PlanAsync(fixture.Lease, envelope, Guid.NewGuid(), cancellationToken));

    private static async Task<EffectJournalSnapshot> ReadAsync(Fixture fixture, EffectEnvelope envelope, CancellationToken cancellationToken) =>
        Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken));

    private static async Task<TrackedFargateLaunchReservations> ReservationsAsync(Fixture fixture, CancellationToken cancellationToken) =>
        Assert.IsType<TrackedFargateLaunchReservations>(await fixture.Store.ReadTrackedFargateLaunchReservationsAsync(fixture.Profile,
            fixture.Lease.Fence, cancellationToken));

    private sealed record Fixture(DynamoDbLifecycleStore Store, DynamoDbEffectJournal Journal, DynamoDbEffectDispatcher Dispatcher,
        ControllerLease Lease, LifecycleProfile Profile, FixtureClock Clock);

    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = MiniStackTable.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StoppedFenceObserver : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
            Task.FromResult(ControllerTaskFenceStatus.Stopped);
    }

    private sealed class FixtureProvider(FixtureClock clock) : IFargateReplayEffectProvider
    {
        public List<(EffectEnvelope Envelope, EffectAttempt Attempt)> Sends { get; } = [];
        public List<EffectAttempt> Probes { get; } = [];
        public bool Complete { get; set; } = true;
        public Func<EffectEnvelope, EffectEvidence>? Positive { get; set; }
        public Func<EffectEnvelope, EffectEvidence>? OnSend { get; init; }
        public EffectEvidence? AlreadyApplied { get; init; }

        public Task<EffectDispatchPrecondition?> CheckAsync(EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult<EffectDispatchPrecondition?>(new(envelope.Sha256, envelope.Target, clock.Now, Eligible: true, AlreadyApplied));

        public Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            Sends.Add((envelope, attempt));
            return Task.FromResult(OnSend?.Invoke(envelope));
        }

        public Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken) =>
            Task.FromResult(Positive?.Invoke(envelope));

        public Task<FargateReplayObservation?> ObserveForReplayAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            Probes.Add(attempt);
            return Task.FromResult<FargateReplayObservation?>(new(envelope.Sha256, envelope.Target, clock.Now, Complete, Positive?.Invoke(envelope)));
        }
    }
}
