using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class ControllerLifecycleTests
{
    private const string SecondTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const string ThirdTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000003";
    private const string FourthTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000004";
    private const string ProfileId = "linux-shell";
    private const string Scope = "https://github.com/enterprises/engineering";
    private static readonly string[] CurrentStage = ["AWSCURRENT"];
    private static readonly TimeSpan Reserve = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly ProfileRetirementTiming RetirementTiming = new(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(10), Reserve);

    [Fact]
    public async Task TwoClearPassesSurviveTakeoverAndPreventRotationUntilTombstone()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var document = await ConfigurationDocumentAsync(draining: true, cancellation);
        document["spec"]!["tracking"]!["table"] = new JsonObject { ["type"] = "import", ["name"] = table.Name };
        var profiles = Assert.IsType<JsonArray>(document["spec"]!["profiles"]);
        var retained = profiles[0]!.DeepClone();
        retained["id"] = "retained-linux";
        retained["state"] = "active";
        retained["github"]!["scaleSet"] = "retained-linux";
        retained["github"]!["labels"] = new JsonArray("retained-linux");
        profiles.Add(retained);
        var configuration = RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(document));
        var candidateDocument = document.DeepClone();
        candidateDocument["spec"]!["profiles"]![0]!["state"] = "disabled";
        var candidate = RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(candidateDocument));
        Assert.Equal(RetirementPreflightStatus.CatalogUnavailable,
            (await store.VerifyRetirementAsync(configuration, 1, candidate, 2, cancellation)).Status);
        var first = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        await ApplyCatalogAsync(store, first, configuration, cancellation);
        var premature = await store.VerifyRetirementAsync(configuration, 1, candidate, 2, cancellation);
        Assert.Equal(RetirementPreflightStatus.RetirementRequired, premature.Status);
        Assert.Equal([ProfileId], premature.Profiles);
        Assert.Equal(RetirementPreflightStatus.Verified,
            (await store.VerifyRetirementAsync(configuration, 1, configuration, 2, cancellation)).Status);

        var clusterChange = document.DeepClone();
        clusterChange["spec"]!["ecsClusters"]!["replacement"] = new JsonObject { ["type"] = "create", ["name"] = "replacement-cluster" };
        clusterChange["spec"]!["profiles"]![1]!["provisioner"]!["cluster"] = "replacement";
        var changedCluster = RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(clusterChange));
        var blockedCluster = await store.VerifyRetirementAsync(configuration, 1, changedCluster, 2, cancellation);
        Assert.Equal(RetirementPreflightStatus.RetirementRequired, blockedCluster.Status);
        Assert.Equal(["retained-linux"], blockedCluster.Profiles);

        var roleChange = document.DeepClone();
        var changedProvisioner = roleChange["spec"]!["profiles"]![1]!["provisioner"]!;
        changedProvisioner["bootstrapRoleArn"] = changedProvisioner["bootstrapRoleArn"]!.GetValue<string>() + "-replacement";
        var changedRole = RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(roleChange));
        var blockedRole = await store.VerifyRetirementAsync(configuration, 1, changedRole, 2, cancellation);
        Assert.Equal(RetirementPreflightStatus.RetirementRequired, blockedRole.Status);
        Assert.Equal(["retained-linux"], blockedRole.Profiles);
        var profile = new LifecycleProfile(first.Deployment, ProfileId);
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(CatalogProfileState.Draining, catalog.State);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(first.Fence, catalog, 42, 84, cancellation));
        var binding = new SessionBinding(profile, catalog.Binding.ScopeIdentity, 84);
        var opened = await OpenAsync(store, first, binding, clock, cancellation);
        var fence = Assert.IsType<OwnedSessionFence>(opened.Fence);
        var identity = fence.Identity;
        var demand = new LifecycleRecordWrite(LifecycleKey.Demand(profile), null,
            JsonSerializer.Serialize(new { sessionId = identity.SessionId, observedAt = clock.Now, messageId = (long?)null, totalAssignedJobs = 0 }));
        var checkpoint = new GitHubListenerCheckpointStore(store, fence);
        var initial = new ScaleSetListenerCheckpoint(checkpoint.SessionKey, ScaleSetListenerCheckpoint.CurrentSchemaVersion, 1,
            Guid.NewGuid(), false, null, new(new(checkpoint.SessionKey, ScaleSetListenerDeliveryKind.InitialStatistics, clock.Now,
                initialStatistics: new RunnerScaleSetStatistic()), ScaleSetListenerPendingPhase.Received));
        Assert.Equal(ScaleSetListenerCheckpointWriteResult.Committed, await checkpoint.WriteAsync(checkpoint.SessionKey, null, initial, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteSessionRecordsAsync(fence, Guid.NewGuid(), [demand], cancellation));
        Assert.Equal(ScaleSetListenerCheckpointWriteResult.Committed, await checkpoint.WriteAsync(checkpoint.SessionKey, 1,
            initial with { Revision = 2, TransitionId = Guid.NewGuid(), InitialStatisticsCompleted = true, Pending = null }, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, first.Fence, null,
            new ExecutorInventoryObservation(clock.Now, IsComplete: true), cancellation));

        int offers = 0;
        async Task<ProfileHandoffObservation?> Handoffs(LifecycleProfile selected, CancellationToken token)
        {
            var witness = await store.ReadExecutorInventoryAsync(selected, token);
            return new ProfileHandoffObservation(clock.Now, true, offers,
                [new LifecycleRecordCondition(LifecycleKey.RunnerInventory(selected), witness.Revision)]);
        }
        var coordinator = new ProfileRetirementCoordinator(store, clock, RetirementTiming, Handoffs);
        Assert.Equal(ProfileRetirementStatus.FirstClearRecorded,
            (await coordinator.AdvanceAsync(profile, Authority(first, clock), cancellation)).Status);
        Assert.Equal(ProfileRetirementStatus.WaitingForSecondPass,
            (await coordinator.AdvanceAsync(profile, Authority(first, clock), cancellation)).Status);

        clock.Now = first.ValidUntil.AddSeconds(2);
        var successor = await AcquireAsync(store, configuration, clock, SecondTask, cancellation);
        await ApplyCatalogAsync(store, successor, configuration, cancellation);
        var restarted = new ProfileRetirementCoordinator(store, clock, RetirementTiming, Handoffs);
        Assert.Equal(ProfileRetirementStatus.WaitingForSecondPass,
            (await restarted.AdvanceAsync(profile, Authority(successor, clock), cancellation)).Status);
        var priorCoverage = await store.ReadExecutorInventoryAsync(profile, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, successor.Fence, priorCoverage.Revision,
            new ExecutorInventoryObservation(clock.Now, IsComplete: true), cancellation));
        Assert.Equal(ProfileRetirementStatus.ClosureHeld,
            (await restarted.AdvanceAsync(profile, Authority(successor, clock), cancellation)).Status);
        offers = 1;
        Assert.Equal(ProfileRetirementStatus.ReconciliationPending,
            (await restarted.AdvanceAsync(profile, Authority(successor, clock), cancellation)).Status);
        offers = 0;
        var required = await new ProfileRetirementCoordinator(store, clock, RetirementTiming, Handoffs)
            .AdvanceAsync(profile, Authority(successor, clock), cancellation);
        Assert.Equal(new ProfileRetirementResult(ProfileRetirementStatus.SessionRetirementRequired, identity), required);

        var unguarded = await store.BeginSessionCloseAsync(opened, successor.Fence, SessionCloseKind.FencedRetirement,
            clock.Now.AddSeconds(1), clock.Now.AddSeconds(30), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, unguarded.Outcome);
        Assert.Null(unguarded.Permission);

        clock.Now += RetirementTiming.Freshness + TimeSpan.FromSeconds(1);
        var closingOwner = await AcquireAsync(store, configuration, clock, ThirdTask, cancellation, SecondTask);
        await ApplyCatalogAsync(store, closingOwner, configuration, cancellation);
        restarted = new ProfileRetirementCoordinator(store, clock, RetirementTiming, Handoffs);
        var staleProof = await restarted.BeginSessionRetirementAsync(profile, Authority(closingOwner, clock), opened,
            TimeSpan.FromSeconds(30), cancellation);
        Assert.Null(staleProof.Permission);
        var precloseCoverage = await store.ReadExecutorInventoryAsync(profile, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, closingOwner.Fence, precloseCoverage.Revision,
            new ExecutorInventoryObservation(clock.Now, IsComplete: true), cancellation));
        Assert.Equal(new ProfileRetirementResult(ProfileRetirementStatus.SessionRetirementRequired, identity),
            await restarted.AdvanceAsync(profile, Authority(closingOwner, clock), cancellation));

        offers = 1;
        var denied = await restarted.BeginSessionRetirementAsync(profile, Authority(closingOwner, clock), opened, TimeSpan.FromSeconds(30), cancellation);
        Assert.Null(denied.Permission);
        offers = 0;
        string retirePath = $"{GitHubWire.ServicePath}/_apis/runtime/runnerscalesets/84/sessions/{identity.SessionId:D}";
        using var wire = new GitHubWire((request, _) =>
        {
            Assert.Equal((HttpMethod.Delete, retirePath), (request.Method, request.Uri.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        var remote = await ConnectAsync(configuration, clock, http, cancellation);
        var routing = Assert.Single(configuration.Profiles, item => item.Id == ProfileId).GitHub;
        var laneInput = new GitHubListenerProfile(configuration, ProfileId, new GitHubScaleSetBinding(Scope, routing.RunnerGroup,
            42, routing.ScaleSet, 84, routing.Labels), remote, closingOwner.Fence,
            _ => Task.FromResult<GitHubRegistrationLease?>(new GitHubRegistrationLease(closingOwner, 1, ClockProof(clock))),
            _ => Task.FromResult(new ListenerCapacity(0, 0)), (_, _) => Task.FromResult(true), CancellationToken.None);
        var supervisor = new GitHubListenerSupervisor(store, clock, new GitHubListenerTiming(Reserve, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15)));
        using var retirementLane = supervisor.Register(laneInput);
        Assert.Equal(GitHubListenerLaneStatus.Stopped, (await retirementLane.RetireDrainingSessionAsync(restarted, cancellation)).Status);
        var retiredSession = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(profile, cancellation));
        Assert.Equal(SessionPhase.Retired, retiredSession.Phase);
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == retirePath);

        table.Wire.Clear();
        var rotation = await store.BeginSessionOpenAsync(binding, closingOwner.Fence, retiredSession, "replacement-fixture",
            clock.Now.AddSeconds(1), clock.Now.AddSeconds(30), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, rotation.Outcome);
        Assert.Null(rotation.Permission);
        Assert.DoesNotContain(table.Wire.Requests, request => request.Operation == "TransactWriteItems");

        // Restart after the retired session's final demand observation has expired.
        clock.Now += RetirementTiming.Freshness + TimeSpan.FromSeconds(1);
        var closer = await AcquireAsync(store, configuration, clock, FourthTask, cancellation, ThirdTask);
        await ApplyCatalogAsync(store, closer, configuration, cancellation);
        var resumed = new ProfileRetirementCoordinator(store, clock, RetirementTiming, Handoffs);
        Assert.Equal(ProfileRetirementStatus.ReconciliationPending,
            (await resumed.AdvanceAsync(profile, Authority(closer, clock), cancellation)).Status);
        var staleCoverage = await store.ReadExecutorInventoryAsync(profile, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, closer.Fence, staleCoverage.Revision,
            new ExecutorInventoryObservation(clock.Now, IsComplete: true), cancellation));

        offers = 1;
        Assert.Equal(ProfileRetirementStatus.ReconciliationPending,
            (await resumed.AdvanceAsync(profile, Authority(closer, clock), cancellation)).Status);
        offers = 0;
        Assert.Equal(ProfileRetirementStatus.Retired,
            (await resumed.AdvanceAsync(profile, Authority(closer, clock), cancellation)).Status);
        var tombstone = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(CatalogProfileState.Tombstoned, tombstone.State);
        Assert.NotNull(tombstone.RetiredAt);
        Assert.Equal(84, tombstone.Binding.ScaleSetId);
        Assert.NotNull(await checkpoint.ReadAsync(checkpoint.SessionKey, cancellation));
        Assert.Equal(ProfileRetirementStatus.Retired,
            (await coordinator.AdvanceAsync(profile, Authority(closer, clock), cancellation)).Status);

        table.Wire.Clear();
        var verified = await store.VerifyRetirementAsync(configuration, 1, candidate, 2, cancellation);
        Assert.Equal(RetirementPreflightStatus.Verified, verified.Status);
        Assert.Empty(verified.Profiles);
        Assert.NotEmpty(table.Wire.Requests);
        Assert.All(table.Wire.Requests, request =>
        {
            Assert.True(request.Operation is "GetItem" or "Query");
            Assert.True(request.Request.GetProperty("ConsistentRead").GetBoolean());
        });
        Assert.Equal(RetirementPreflightStatus.CatalogUnavailable,
            (await store.VerifyRetirementAsync(configuration, 2, candidate, 3, cancellation)).Status);
        Assert.Equal(RetirementPreflightStatus.BindingMismatch,
            (await store.VerifyRetirementAsync(configuration, 1, candidate, 1, cancellation)).Status);

        var dryRunDocument = document.DeepClone();
        dryRunDocument["spec"]!["mode"] = "dry-run";
        var dryRun = RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(dryRunDocument));
        var retainedGrants = await store.VerifyRetirementAsync(configuration, 1, dryRun, 2, cancellation);
        Assert.Equal(RetirementPreflightStatus.RetirementRequired, retainedGrants.Status);
        Assert.Equal(["retained-linux"], retainedGrants.Profiles);
    }

    [Fact]
    public async Task SharedHeartbeatReadbackPreservesTheFixedFenceAndSignalsLossAtTakeover()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(draining: false, cancellation);
        var first = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        using var ownership = new ControllerLeaseOwnership(store, first, ClockProof(clock), clock, LeaseDuration, Reserve);
        clock.Now = clock.Now.AddSeconds(20);
        table.Wire.DropNextSuccessfulTransactionResponse();
        Assert.Equal(ControllerHeartbeatStatus.RetryRequired, await ownership.RenewAsync(ClockProof(clock), cancellation));
        Assert.Equal(first, ownership.ReadAuthority()?.Lease);
        var durable = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(first.Deployment, cancellation));
        Assert.Equal(first.Fence, durable.Fence);
        Assert.Equal(first.Revision + 1, durable.Revision);
        Assert.Equal(ControllerHeartbeatStatus.Current, await ownership.RenewAsync(ClockProof(clock), cancellation));
        Assert.Equal(durable, ownership.ReadAuthority()?.Lease);
        Assert.False(ownership.OwnershipLost.IsCancellationRequested);

        clock.Now = durable.ValidUntil.AddSeconds(2);
        _ = await AcquireAsync(store, configuration, clock, SecondTask, cancellation);
        Assert.Equal(ControllerHeartbeatStatus.OwnershipLost, await ownership.RenewAsync(ClockProof(clock), cancellation));
        Assert.True(ownership.OwnershipLost.IsCancellationRequested);
        Assert.Null(ownership.ReadAuthority());
    }

    [Theory]
    [InlineData("ExpiredTokenException", true)]
    [InlineData("AccessDeniedException", false)]
    public async Task HeartbeatOnlyRetriesRecoverableStorageErrorsWithinTheExistingFence(string errorCode, bool retry)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(draining: false, cancellation);
        var lease = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        using var ownership = new ControllerLeaseOwnership(store, lease, ClockProof(clock), clock, LeaseDuration, Reserve);
        clock.Now = clock.Now.AddSeconds(20);
        table.Wire.Clear();
        int reads = 0;
        Task<ControllerClockEvidence?> ReadClock(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++reads == 1)
            {
                table.Wire.FailNextGetItem(LifecycleKey.Lease(lease.Deployment), HttpStatusCode.Forbidden, errorCode);
            }
            else
            {
                Assert.Equal(lease, ownership.ReadAuthority()?.Lease);
                clock.Now = lease.ValidUntil - Reserve;
            }
            return Task.FromResult<ControllerClockEvidence?>(ClockProof(clock));
        }

        var heartbeat = ownership.RunHeartbeatsAsync(TimeSpan.FromSeconds(1), ReadClock, cancellation);
        if (retry)
        {
            await heartbeat;
        }
        else
        {
            _ = await Assert.ThrowsAnyAsync<AmazonServiceException>(() => heartbeat);
        }

        Assert.True(ownership.OwnershipLost.IsCancellationRequested);
        Assert.Null(ownership.ReadAuthority());
        Assert.Equal(retry ? 2 : 1, reads);
        Assert.DoesNotContain(table.Wire.Requests, request => request.Operation == "TransactWriteItems");
        Assert.Equal(lease, await store.ReadLeaseAsync(lease.Deployment, cancellation));
    }

    [Fact]
    public async Task HeartbeatDeadlineAfterCommitEndsLocalAuthorityWithoutAnotherWrite()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock { HoldTimers = true };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(draining: false, cancellation);
        var lease = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        using var ownership = new ControllerLeaseOwnership(store, lease, ClockProof(clock), clock, LeaseDuration, Reserve);
        clock.Now = clock.Now.AddSeconds(20);
        table.Wire.Clear();
        var paused = table.Wire.PauseAfterNextSuccessfulTransactionWrite(LifecycleKey.Lease(lease.Deployment));
        try
        {
            var heartbeat = ownership.RunHeartbeatsAsync(TimeSpan.FromSeconds(1),
                _ => Task.FromResult<ControllerClockEvidence?>(ClockProof(clock)), cancellation);
            await paused.Captured.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            Assert.Equal(lease, ownership.ReadAuthority()?.Lease);
            Assert.IsType<Action>(clock.FireTimer)();
            await heartbeat.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        }
        finally
        {
            paused.Resume();
        }

        Assert.True(ownership.OwnershipLost.IsCancellationRequested);
        Assert.Null(ownership.ReadAuthority());
        Assert.Single(table.Wire.Requests, request => request.Operation == "TransactWriteItems");
        var durable = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(lease.Deployment, cancellation));
        Assert.Equal(lease.Fence, durable.Fence);
        Assert.Equal(ControllerLeaseMode.Active, durable.Mode);
        Assert.Equal(lease.Revision + 1, durable.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootShutdownJoinsBeforeCancelingOnlyUnstartedEffectsAndReleasesWhenClear(bool ambiguous)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(draining: false, cancellation);
        var lease = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var profile = new LifecycleProfile(lease.Deployment, ProfileId);
        using var ownership = new ControllerLeaseOwnership(store, lease, ClockProof(clock), clock, LeaseDuration, Reserve);
        var journal = new DynamoDbEffectJournal(store, clock);
        var envelope = EffectEnvelope.Create(profile, "close-session-fixture", 1,
            new GitHubSessionRetireEffectRequest("https://example.ghe.com/engineering/tools", 84, Guid.NewGuid()), clock.Now, null);
        Assert.Equal(LifecycleWriteOutcome.Committed, await journal.PlanAsync(lease, envelope, Guid.NewGuid(), cancellation));
        var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        if (ambiguous)
        {
            var begin = await journal.BeginAsync(lease, planned, Guid.NewGuid(), Guid.NewGuid(), clock.Now.AddSeconds(1),
                clock.Now.AddSeconds(30), Reserve, cancellation);
            Assert.NotNull(begin.Permission);
        }
        var stages = new List<string>();
        var work = new ShutdownWork(stages, async (stage, token) =>
        {
            Assert.Equal(cancellation, token);
            Assert.Equal(ControllerLeaseMode.Active, (await store.ReadLeaseAsync(lease.Deployment, token))?.Mode);
            var snapshot = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, token));
            if (stage == "close")
            {
                Assert.Equal(ambiguous ? EffectState.Executing : EffectState.Settled, snapshot.Entry.State);
                if (!ambiguous)
                {
                    Assert.Equal(EffectResolutionKind.CanceledBeforeSend, snapshot.Entry.Resolution?.Kind);
                    table.Wire.DropNextSuccessfulTransactionResponse();
                }
            }
            else
            {
                Assert.Equal(ambiguous ? EffectState.Executing : EffectState.Planned, snapshot.Entry.State);
            }
        });
        var dispatcher = new DynamoDbEffectDispatcher(store, clock,
            new EffectDispatcherTiming(TimeSpan.FromSeconds(25), Reserve, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)));
        var shutdown = new ControllerShutdownCoordinator(store, dispatcher);

        var status = await shutdown.StopAsync(ownership, [], work, cancellation);
        Assert.Equal(["admissions", "polls", "join", "close"], stages);
        Assert.Equal(ambiguous ? ControllerShutdownStatus.ReconciliationPending : ControllerShutdownStatus.Released, status);
        Assert.Equal(ambiguous ? ControllerLeaseMode.Active : ControllerLeaseMode.Released,
            (await store.ReadLeaseAsync(lease.Deployment, cancellation))?.Mode);
        Assert.True(ownership.OwnershipLost.IsCancellationRequested);
        Assert.Null(ownership.ReadAuthority());
    }

    [Theory]
    [InlineData("admissions", ControllerShutdownStatus.QuiescenceUnconfirmed)]
    [InlineData("polls", ControllerShutdownStatus.QuiescenceUnconfirmed)]
    [InlineData("join", ControllerShutdownStatus.QuiescenceUnconfirmed)]
    [InlineData("close", ControllerShutdownStatus.ReconciliationPending)]
    public async Task RootShutdownAttemptsEverySafeStageAndRetainsAnUnconfirmedLease(string failingStage, ControllerShutdownStatus expected)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(draining: false, cancellation);
        var lease = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        using var ownership = new ControllerLeaseOwnership(store, lease, ClockProof(clock), clock, LeaseDuration, Reserve);
        var dispatcher = new DynamoDbEffectDispatcher(store, clock,
            new EffectDispatcherTiming(TimeSpan.FromSeconds(25), Reserve, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)));
        var shutdown = new ControllerShutdownCoordinator(store, dispatcher);
        var stages = new List<string>();
        var work = new ShutdownWork(stages, (stage, _) => stage == failingStage
            ? Task.FromException(new TimeoutException("Synthetic shutdown stage fault.")) : Task.CompletedTask);

        var status = await shutdown.StopAsync(ownership, [], work, cancellation);

        Assert.Equal(expected, status);
        Assert.Equal(failingStage == "close" ? ["admissions", "polls", "join", "close"] : ["admissions", "polls", "join"], stages);
        Assert.Equal(lease, await store.ReadLeaseAsync(lease.Deployment, cancellation));
        Assert.True(ownership.OwnershipLost.IsCancellationRequested);
    }

    private static async Task ApplyCatalogAsync(DynamoDbLifecycleStore store, ControllerLease lease,
        RuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        for (int pass = 0; pass < 10; pass++)
        {
            var applied = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, 1, cancellationToken);
            if (applied.Status == CatalogRolloutStatus.Applied)
            {
                Assert.Equal(lease.Epoch, applied.Head?.AppliedEpoch);
                return;
            }
            Assert.Equal(CatalogRolloutStatus.Staging, applied.Status);
        }
        Assert.Fail("The small profile catalog did not publish within the fixture pass budget.");
    }

    private static async Task<SessionRecord> OpenAsync(DynamoDbLifecycleStore store, ControllerLease lease, SessionBinding binding,
        FixtureClock clock, CancellationToken cancellationToken)
    {
        var opened = await store.BeginSessionOpenAsync(binding, lease.Fence, null, "fixture-initial", clock.Now.AddSeconds(1),
            clock.Now.AddSeconds(30), cancellationToken);
        var permission = Assert.IsType<SessionSendAuthorization>(opened.Permission);
        Assert.True(permission.TryConsume(out _));
        var confirmed = await store.ConfirmSessionOpenedAsync(permission, Guid.NewGuid(), cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, confirmed.Outcome);
        return Assert.IsType<SessionRecord>(confirmed.Session);
    }

    private static async Task<ControllerLease> AcquireAsync(DynamoDbLifecycleStore store, RuntimeConfiguration configuration,
        FixtureClock clock, string task, CancellationToken cancellationToken, string stoppedTask = MiniStackTable.FirstTask)
    {
        var acquisition = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), task, task,
            ControllerLease.CurrentWriterProtocol, clock.Now.AddSeconds(-1), clock.Now + LeaseDuration, cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquisition.Outcome);
        var recovery = new ControllerLeaseRecoveryCoordinator(store, new StoppedObserver(stoppedTask), clock, Reserve);
        var activated = await recovery.TryActivateAsync(Assert.IsType<ControllerLease>(acquisition.Lease), configuration, ClockProof(clock),
            [], cancellationToken);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        return Assert.IsType<ControllerLease>(activated.Lease);
    }

    internal static async Task<RuntimeConfiguration> ConfigurationAsync(bool draining, CancellationToken cancellationToken) =>
        RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(
            await ConfigurationDocumentAsync(draining, cancellationToken)));

    private static async Task<JsonNode> ConfigurationDocumentAsync(bool draining, CancellationToken cancellationToken)
    {
        var image = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken);
        var document = JsonNode.Parse(image)!;
        var connection = document["spec"]!["github"]!["engineering"]!;
        connection["scopeURL"] = Scope;
        connection["auth"]!["type"] = "enterprise-pat";
        if (draining)
        {
            document["spec"]!["profiles"]![0]!["state"] = "draining";
        }
        return document;
    }

    internal static async Task<GitHubScaleSetConnection> ConnectAsync(RuntimeConfiguration configuration, TimeProvider clock, HttpClient http,
        CancellationToken cancellationToken)
    {
        string value = JsonSerializer.Serialize(new
        {
            ARN = configuration.GitHub["engineering"].SecretArn,
            VersionId = "0123456789abcdef0123456789abcdef",
            VersionStages = CurrentStage,
            SecretString = """{"token":"ghp_FixtureOnlyNeverValid"}""",
        });
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(value));
        using var sdk = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, wire.Configure(new AmazonSecretsManagerConfig()));
        var version = Assert.IsType<GitHubSecretVersion>(await new GitHubSecretLoader(sdk).LoadAsync(configuration, "engineering", cancellationToken));
        return new GitHubScaleSetClientFactory(httpClient: http, timeProvider: clock).Create(version);
    }

    private static ActiveControllerAuthority Authority(ControllerLease lease, FixtureClock clock) => new(lease, ClockProof(clock));

    private static ControllerClockEvidence ClockProof(FixtureClock clock) => new(clock.Now, clock.Now.AddMinutes(3), TimeSpan.FromSeconds(1));

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = MiniStackTable.Now;
        internal bool HoldTimers { get; init; }
        internal Action? FireTimer { get; private set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!HoldTimers)
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }
            FireTimer = () => callback(state);
            return System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class StoppedObserver(string stoppedTask) : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(stoppedTask, taskArn);
            return Task.FromResult(ControllerTaskFenceStatus.Stopped);
        }
    }

    private sealed class ShutdownWork(List<string> stages, Func<string, CancellationToken, Task> check) : IControllerShutdownWork
    {
        public Task StopAdmissionsAsync(CancellationToken cancellationToken) => RecordAsync("admissions", cancellationToken);

        public Task CancelPollsAsync(CancellationToken cancellationToken) => RecordAsync("polls", cancellationToken);

        public Task JoinMutatorsAsync(CancellationToken cancellationToken) => RecordAsync("join", cancellationToken);

        public Task CloseOwnedSessionsAsync(CancellationToken cancellationToken) => RecordAsync("close", cancellationToken);

        private Task RecordAsync(string stage, CancellationToken cancellationToken)
        {
            stages.Add(stage);
            return check(stage, cancellationToken);
        }
    }
}
