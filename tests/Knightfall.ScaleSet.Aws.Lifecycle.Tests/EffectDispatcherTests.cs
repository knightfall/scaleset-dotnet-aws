using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class EffectDispatcherTests
{
    private const string Cluster = "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-shell";
    private const string TaskDefinition = "arn:aws:ecs:ap-southeast-2:111122223333:task-definition/linux-shell-runner:7";
    private const string BootstrapRole = "arn:aws:iam::111122223333:role/fargate-shell-bootstrap";
    private const string ExecutionRole = "arn:aws:iam::111122223333:role/fargate-shell-execution";
    private const string RunnerTask = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-shell/11111111111111111111111111111111";
    private const string NewOwnerTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private static readonly DateTimeOffset Now = MiniStackTable.Now;
    private static readonly TimeSpan Reserve = TimeSpan.FromSeconds(1);
    private static readonly EffectDispatcherTiming Timing = new(TimeSpan.FromSeconds(30), Reserve, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task MissingIntentAndUnstartedShutdownCancelNeverCallProvider()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var envelope = Launch(fixture.Profile);
        var provider = new FakeProvider(fixture.Clock);

        Assert.Equal(EffectDispatchStatus.NotPlanned, await DispatchAsync(fixture, envelope, provider, cancellation));
        await PlanAsync(fixture, envelope, cancellation);
        var stopped = await fixture.Dispatcher.CancelUnstartedPassAsync(fixture.Lease, fixture.Profile, null, cancellation);
        Assert.Equal(new(LifecycleWriteOutcome.Committed, 0, 1, 1, null), stopped);
        Assert.True(await fixture.Dispatcher.HasNoUnfinishedEffectsAsync(fixture.Lease, fixture.Profile, cancellation));
        Assert.Equal(EffectResolutionKind.CanceledBeforeSend, (await ReadAsync(fixture, envelope, cancellation)).Entry.Resolution?.Kind);
        Assert.Equal(EffectDispatchStatus.NotPlanned, await DispatchAsync(fixture, envelope, provider, cancellation));
        Assert.Empty(provider.Checks);
        Assert.Empty(provider.Sends);
        Assert.Empty(provider.Readbacks);
    }

    [Fact]
    public async Task LostCommittedBeginNeverSendsAndKeepsExactAttemptForReadback()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var envelope = Launch(fixture.Profile);
        await PlanAsync(fixture, envelope, cancellation);
        var provider = new FakeProvider(fixture.Clock)
        {
            BeforeCheck = (_, _) =>
            {
                table.Wire.DropNextSuccessfulTransactionResponse();
                return Task.CompletedTask;
            },
        };
        table.Wire.Clear();

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, envelope, provider, cancellation));
        var executing = await ReadAsync(fixture, envelope, cancellation);
        Assert.Equal(2, executing.Revision);
        Assert.Equal(EffectState.Executing, executing.Entry.State);
        var attempt = Assert.IsType<EffectAttempt>(executing.Entry.Attempt);
        Assert.True(envelope.Matches(executing.Entry.Envelope));
        Assert.Empty(provider.Sends);
        CheckGuardedBegin(Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems"), fixture.Profile, envelope);

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, envelope, provider, cancellation));
        Assert.Single(provider.Checks);
        Assert.Equal(0, (await fixture.Dispatcher.CancelUnstartedPassAsync(fixture.Lease, fixture.Profile, null, cancellation)).Settled);
        Assert.False(await fixture.Dispatcher.HasNoUnfinishedEffectsAsync(fixture.Lease, fixture.Profile, cancellation));
        var observed = await ReconcileAsync(fixture, provider, cancellation);
        Assert.Equal(0, observed.Settled);
        Assert.Equal((envelope.Sha256, attempt.Id), Assert.Single(provider.Readbacks));
        Assert.Equal(executing.TransitionId, (await ReadAsync(fixture, envelope, cancellation)).TransitionId);
        Assert.Empty(provider.Sends);
    }

    [Fact]
    public async Task ProviderCommitWithLostResponseAllowsOneExactReadbackAndNoCompetingLaunch()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var envelope = Launch(fixture.Profile);
        await PlanAsync(fixture, envelope, cancellation);
        var competing = EffectEnvelope.Create(fixture.Profile, "second-launch-for-same-allocation", envelope.Generation,
            envelope.Request, envelope.EarliestPossibleFirstSend, envelope.ProviderReplayDeadline);
        await PlanAsync(fixture, competing, cancellation);
        var provider = new FakeProvider(fixture.Clock)
        {
            OnSend = (saved, _, _) =>
            {
                Assert.True(envelope.Matches(saved));
                return Task.FromException<EffectEvidence?>(new HttpRequestException("The provider accepted and discarded its response."));
            },
        };

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, envelope, provider, cancellation));
        var durable = await ReadAsync(fixture, envelope, cancellation);
        Assert.Equal(EffectState.Executing, durable.Entry.State);
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, envelope, provider, cancellation));
        Assert.Equal(EffectDispatchStatus.Ineligible, await DispatchAsync(fixture, competing, provider, cancellation));
        provider.OnObserve = (saved, _, _) => Task.FromResult<EffectEvidence?>(
            new EffectEvidence(EffectResolutionKind.Applied, saved.Sha256, saved.Target, fixture.Clock.Now));

        var recovered = await ReconcileAsync(fixture, provider, cancellation);
        Assert.Equal(1, recovered.Settled);
        var settled = await ReadAsync(fixture, envelope, cancellation);
        Assert.Equal(EffectState.Settled, settled.Entry.State);
        Assert.Equal(EffectResolutionKind.Applied, settled.Entry.Resolution?.Kind);
        Assert.True(envelope.Matches(settled.Entry.Envelope));
        Assert.Equal((envelope.Sha256, durable.Entry.Attempt!.Id), Assert.Single(provider.Sends));
        Assert.Equal(Assert.Single(provider.Sends), Assert.Single(provider.Readbacks));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitiveRejectionMustNameExactCurrentAttempt(bool exact)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var envelope = Launch(fixture.Profile);
        await PlanAsync(fixture, envelope, cancellation);
        var provider = new FakeProvider(fixture.Clock)
        {
            OnSend = (saved, attempt, _) => Task.FromResult<EffectEvidence?>(new EffectEvidence(
                EffectResolutionKind.Rejected, saved.Sha256, saved.Target, fixture.Clock.Now, exact ? attempt.Id : Guid.NewGuid())),
        };

        Assert.Equal(exact ? EffectDispatchStatus.Settled : EffectDispatchStatus.AwaitingEvidence,
            await DispatchAsync(fixture, envelope, provider, cancellation));
        var durable = await ReadAsync(fixture, envelope, cancellation);
        Assert.Equal(exact ? EffectState.Settled : EffectState.Executing, durable.Entry.State);
        Assert.Equal(exact ? durable.Entry.Attempt!.Id : (Guid?)null, durable.Entry.Resolution?.AttemptId);
        Assert.Single(provider.Sends);
    }

    [Fact(Timeout = 10_000)]
    public async Task StaleReplacementAfterLastOwnershipReadFailsGuardedBeginAndPreventsSend()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var envelope = Launch(fixture.Profile);
        await PlanAsync(fixture, envelope, cancellation);
        var pause = new TaskCompletionSource<MiniStackWire.PausedRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(fixture.Clock)
        {
            BeforeCheck = (_, _) =>
            {
                pause.SetResult(table.Wire.PauseNextRead(LifecycleKey.Operation(fixture.Profile, envelope.IntentId)));
                return Task.CompletedTask;
            },
        };
        table.Wire.Clear();
        var dispatch = DispatchAsync(fixture, envelope, provider, cancellation);
        var read = await pause.Task.WaitAsync(cancellation);
        await read.Captured.WaitAsync(cancellation);
        try
        {
            await table.SetLeaseAsync(fixture.Lease.Fence with
            {
                Epoch = fixture.Lease.Epoch + 1,
                OwnerId = "successor",
                OwnerTaskArn = NewOwnerTask,
            }, Now.AddMinutes(5), cancellation);
        }
        finally
        {
            read.Resume();
        }

        Assert.Equal(EffectDispatchStatus.OwnershipUnavailable, await dispatch);
        Assert.Empty(provider.Sends);
        CheckGuardedBegin(Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems"), fixture.Profile, envelope);
        Assert.Equal(EffectState.Planned, (await ReadAsync(fixture, envelope, cancellation)).Entry.State);
    }

    [Fact]
    public async Task ProviderWindowThatExpiresDuringPreflightCannotAuthorizeSend()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var launch = Launch(fixture.Profile);
        var envelope = EffectEnvelope.Create(fixture.Profile, launch.IntentId, launch.Generation, launch.Request,
            launch.EarliestPossibleFirstSend, Now.AddSeconds(2));
        await PlanAsync(fixture, envelope, cancellation);
        var provider = new FakeProvider(fixture.Clock)
        {
            BeforeCheck = (_, _) =>
            {
                fixture.Clock.Now = Now.AddSeconds(3);
                return Task.CompletedTask;
            },
        };
        table.Wire.Clear();

        Assert.Equal(EffectDispatchStatus.Conflict, await DispatchAsync(fixture, envelope, provider, cancellation));
        Assert.Empty(provider.Sends);
        Assert.DoesNotContain(table.Wire.Requests, call => call.Operation == "TransactWriteItems");
        var saved = await ReadAsync(fixture, envelope, cancellation);
        Assert.Equal(EffectState.Planned, saved.Entry.State);
        Assert.Null(saved.Entry.Attempt);
        Assert.Equal(Now.AddSeconds(2), saved.Entry.Envelope.ProviderReplayDeadline);
    }

    [Fact(Timeout = 10_000)]
    public async Task OwnershipLossCancelsInFlightProviderAndRetainsExecutingAttempt()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownershipLost = new CancellationTokenSource();
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var envelope = Launch(fixture.Profile);
        await PlanAsync(fixture, envelope, cancellation);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(fixture.Clock)
        {
            OnSend = async (_, _, token) =>
            {
                started.SetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };
        var dispatch = fixture.Dispatcher.DispatchPlannedAsync(fixture.Lease, ClockProof(fixture.Clock), fixture.Profile,
            envelope.IntentId, provider, ownershipLost.Token, cancellation);
        var operation = await started.Task.WaitAsync(cancellation);
        await ownershipLost.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.True(operation.IsCancellationRequested);
        Assert.Equal(EffectState.Executing, (await ReadAsync(fixture, envelope, cancellation)).Entry.State);
        Assert.Single(provider.Sends);
    }

    [Fact(Timeout = 10_000)]
    public async Task AttemptDeadlineCancelsInFlightProviderAndRetainsExecutingAttempt()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var envelope = Launch(fixture.Profile);
        await PlanAsync(fixture, envelope, cancellation);
        var budget = TimeSpan.FromSeconds(1);
        var dispatcher = new DynamoDbEffectDispatcher(fixture.Store, fixture.Clock, Timing with { OperationBudget = budget });
        CancellationToken dispatched = default;
        var provider = new FakeProvider(fixture.Clock)
        {
            OnSend = async (_, _, token) =>
            {
                dispatched = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await dispatcher.DispatchPlannedAsync(fixture.Lease,
            ClockProof(fixture.Clock), fixture.Profile, envelope.IntentId, provider, CancellationToken.None, cancellation));
        Assert.True(dispatched.IsCancellationRequested);
        Assert.False(cancellation.IsCancellationRequested);
        var state = (await ReadAsync(fixture, envelope, cancellation)).Entry;
        Assert.Equal(EffectState.Executing, state.State);
        Assert.Equal(Now + budget, state.Attempt?.Deadline);
        Assert.Equal(state.Attempt?.Id, Assert.Single(provider.Sends).Attempt);
    }

    [Fact]
    public async Task AdverseProcessObservationBetweenReadAndGuardedBeginRejectsCleanup()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var allocation = Guid.NewGuid();
        var binding = new ExecutorBinding(fixture.Profile, allocation.ToString("D"), RunnerTask, Guid.NewGuid(),
            new FargateExecutorIdentity(Cluster, TaskDefinition));
        await SeedCleanupCandidateAsync(fixture, binding, cancellation);
        var request = new FargateStopEffectRequest(allocation, Cluster, RunnerTask, TaskDefinition);
        var cleanup = EffectEnvelope.Create(fixture.Profile, EffectIntentIdentity.ForAllocation(request)!, 1, request, Now, null);
        await PlanAsync(fixture, cleanup, cancellation);
        var candidate = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, cancellation));
        Assert.True(ExecutorClassifier.Classify(candidate.State, Now, Timing.ObservationFreshness, Timing.LogFlushGrace).CompletedCleanupCandidate);
        var provider = new FakeProvider(fixture.Clock)
        {
            BeforeCheck = async (_, token) => Assert.Equal(LifecycleWriteOutcome.Committed,
                await fixture.Store.ObserveProcessAsync(fixture.Lease.Fence, binding,
                    new ExecutorProcessObservation(17, ExecutorProcessState.Ready, Now, Guid.NewGuid()), token)),
        };

        Assert.Equal(EffectDispatchStatus.Conflict, await DispatchAsync(fixture, cleanup, provider, cancellation));
        var conflicted = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, cancellation));
        Assert.True(conflicted.State.HasConflict);
        Assert.Equal(ExecutorProcessState.Exited, conflicted.State.Process?.State);
        Assert.True(conflicted.Revision > candidate.Revision);
        Assert.Equal(EffectState.Planned, (await ReadAsync(fixture, cleanup, cancellation)).Entry.State);
        Assert.Empty(provider.Sends);
    }

    [Fact]
    public async Task InterruptedTenThousandRowRecoveryIsBoundedAndDoesNotStarveANeighbor()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new DispatchClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var previous = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(table.Lease.Deployment, cancellation));
        var large = table.Profile("large-dispatch-recovery");
        var small = table.Profile("small-dispatch-recovery");
        int[] pendingRows = Enumerable.Range(9965, 35).ToArray();
        await table.SeedEffectsAsync(large, 10_000, pendingRows.ToHashSet(), cancellation);
        await table.SeedEffectsAsync(small, 3, new HashSet<int> { 2 }, cancellation);
        var largeEnvelopes = new List<EffectEnvelope>();
        foreach (int row in pendingRows)
        {
            largeEnvelopes.Add(await ReplaceSeededEffectAsync(table, large, row, previous, cancellation));
        }
        var smallEnvelope = await ReplaceSeededEffectAsync(table, small, 2, previous, cancellation);
        clock.Now = previous.ValidUntil.AddSeconds(2);
        var acquired = await store.TryAcquireLeaseAsync(previous.Deployment, "second", NewOwnerTask, previous.WriterProtocol,
            clock.Now.AddSeconds(-1), clock.Now.AddMinutes(10), cancellation);
        var lease = Assert.IsType<ControllerLease>(acquired.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, lease.Mode);
        var observer = new TaskObserver(previous.OwnerTaskArn);
        var authority = new EffectRecoveryAuthority(new ControllerLeaseRecoveryCoordinator(store, observer, clock, Reserve), []);
        var dispatcher = new DynamoDbEffectDispatcher(store, clock, Timing);
        var provider = new FakeProvider(clock)
        {
            OnObserve = (saved, _, _) => Task.FromResult<EffectEvidence?>(
                new EffectEvidence(EffectResolutionKind.Applied, saved.Sha256, saved.Target, clock.Now)),
        };
        table.Wire.DueQueryResponse = "{\"Items\":[],\"Count\":0}";

        async Task<EffectReconciliationPass> Pass(LifecycleProfile profile, EffectCandidateCursor? cursor = null)
        {
            table.Wire.Clear();
            var pass = await dispatcher.ReconcilePassAsync(lease, ClockProof(clock), profile, cursor, provider, authority, cancellation);
            var queries = table.Wire.Requests.Where(call => call.QueryPrefix == "OP#").ToArray();
            Assert.InRange(queries.Length, 0, 2);
            Assert.All(queries, call =>
            {
                Assert.True(call.Request.GetProperty("ConsistentRead").GetBoolean());
                Assert.Equal(32, call.Request.GetProperty("Limit").GetInt32());
            });
            Assert.DoesNotContain(table.Wire.Requests, call => call.Operation == "Scan" || call.IsDueQuery);
            return pass;
        }

        Assert.Equal(new(LifecycleWriteOutcome.RebuildRequired, 2, 0, 0, null), await Pass(large));
        table.Wire.DropNextSuccessfulTransactionResponse();
        Assert.Equal(new(LifecycleWriteOutcome.Indeterminate, 1, 0, 0, null), await Pass(large));
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, (await Pass(small)).Outcome);
        Assert.Empty(provider.Readbacks);
        observer.Status = ControllerTaskFenceStatus.Stopped;
        Assert.Equal(1, (await Pass(small)).Settled);
        Assert.Equal(smallEnvelope.Sha256, Assert.Single(provider.Readbacks).Sha256);

        var resumed = await Pass(large);
        var after = table.Wire.Requests.First(call => call.QueryPrefix == "OP#");
        Assert.Equal(LifecycleKey.Operation(large, MiniStackTable.RetainedIntent(95)).SortKey,
            after.Request.GetProperty("ExclusiveStartKey").GetProperty("SK").GetProperty("S").GetString());
        for (int iteration = 0; resumed.Outcome == LifecycleWriteOutcome.RebuildRequired && iteration < 160; iteration++)
        {
            resumed = await Pass(large);
        }
        Assert.Equal(LifecycleWriteOutcome.Committed, resumed.Outcome);
        Assert.Equal(32, resumed.Examined);
        Assert.Equal(32, resumed.Settled);
        var continuation = Assert.IsType<EffectCandidateCursor>(resumed.Continuation);
        Assert.Equal(new(LifecycleWriteOutcome.Committed, 0, 3, 3, null), await Pass(large, continuation));
        Assert.Equal(largeEnvelopes.Prepend(smallEnvelope).Select(envelope => envelope.Sha256), provider.Readbacks.Select(read => read.Sha256));
        var journal = new DynamoDbEffectJournal(store, clock);
        Assert.Equal(EffectState.Settled, (await journal.ReadAsync(large, largeEnvelopes[^1].IntentId, cancellation))?.Entry.State);
        Assert.Equal(EffectState.Settled, (await journal.ReadAsync(small, smallEnvelope.IntentId, cancellation))?.Entry.State);
        Assert.Empty(provider.Sends);
        Assert.All(observer.Reads, task => Assert.Equal(previous.OwnerTaskArn, task));
        Assert.Equal(new(LifecycleWriteOutcome.Committed, 0, 0, 0, null), await Pass(large));
        Assert.DoesNotContain(table.Wire.Requests, call => call.QueryPrefix == "OP#");
        Assert.Contains(table.Wire.Requests, call => call.QueryPrefix?.StartsWith("ACTIVE-OP#", StringComparison.Ordinal) == true);
    }

    private static async Task<Fixture> CreateFixtureAsync(MiniStackTable table, CancellationToken cancellationToken)
    {
        var clock = new DispatchClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken));
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var fence = table.Lease with { Deployment = deployment };
        await table.SetLeaseAsync(fence, Now.AddMinutes(5), cancellationToken);
        var lease = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(deployment, cancellationToken));
        CatalogRolloutChange change;
        for (int pass = 0; ; pass++)
        {
            change = await store.AdvanceCatalogRolloutAsync(fence, configuration, 1, cancellationToken);
            if (change.Status != CatalogRolloutStatus.Staging || pass == 3)
            {
                break;
            }
        }
        Assert.Equal(CatalogRolloutStatus.Applied, change.Status);
        var profile = new LifecycleProfile(deployment, Assert.Single(configuration.Profiles).Id);
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(fence, catalog, 7, 37, cancellationToken));
        var ready = await store.AdvanceEffectRebuildAsync(profile, fence, cancellationToken);
        Assert.Equal(EffectIndexState.Ready, ready.Head?.State);
        return new Fixture(store, new DynamoDbEffectJournal(store, clock), new DynamoDbEffectDispatcher(store, clock, Timing),
            lease, profile, clock);
    }

    private static async Task SeedCleanupCandidateAsync(Fixture fixture, ExecutorBinding binding, CancellationToken cancellationToken)
    {
        var lease = fixture.Lease.Fence;
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorAsync(lease, binding, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorRunnerAsync(lease, binding, 17, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(lease, binding,
            new ExecutorResourceObservation(ExecutorResourceState.Running, Now, Now.AddMinutes(-3), Guid.NewGuid()), cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveProcessAsync(lease, binding,
            new ExecutorProcessObservation(17, ExecutorProcessState.Exited, Now.AddSeconds(-20), Guid.NewGuid()), cancellationToken));
        var catalog = Assert.IsType<CatalogProfile>(await fixture.Store.ReadCatalogProfileAsync(fixture.Profile, cancellationToken));
        var opening = await fixture.Store.BeginSessionOpenAsync(
            new SessionBinding(fixture.Profile, catalog.Binding.ScopeIdentity, 37), lease, null,
            "dispatch-fixture", Now, Now.AddMinutes(1), cancellationToken);
        var permission = Assert.IsType<SessionSendAuthorization>(opening.Permission);
        Assert.True(permission.TryConsume(out _));
        var sessionId = Guid.NewGuid();
        var confirmed = await fixture.Store.ConfirmSessionOpenedAsync(permission, sessionId, cancellationToken);
        var owned = Assert.IsType<OwnedSessionFence>(confirmed.Session?.Fence);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.WriteSessionDeliveryRecordsAsync(owned, Guid.NewGuid(),
            [new LifecycleRecordWrite(LifecycleKey.Message(fixture.Profile, sessionId, 0), null, "{}")],
            [new ExecutorJobObservation(9, 17, ExecutorJobPhase.Completed, sessionId, 0, Now.AddSeconds(-21))], cancellationToken));
    }

    private static async Task<EffectEnvelope> ReplaceSeededEffectAsync(
        MiniStackTable table, LifecycleProfile profile, int rowNumber, ControllerLease previous, CancellationToken cancellationToken)
    {
        string intent = MiniStackTable.RetainedIntent(rowNumber);
        var original = Launch(profile);
        var envelope = EffectEnvelope.Create(profile, intent, 1, original.Request, original.EarliestPossibleFirstSend,
            original.ProviderReplayDeadline);
        var typed = EffectJournalEntry.Plan(envelope).Begin(Guid.NewGuid(), previous.Epoch, Now, Now.AddSeconds(30), previous.ValidUntil - Reserve);
        var row = await table.ReadAsync(LifecycleKey.Operation(profile, intent), cancellationToken);
        row["payload"] = new AttributeValue(typed.WritePayload());
        row["effectState"] = new AttributeValue("executing");
        await table.Client.PutItemAsync(table.Name, row, cancellationToken);
        return envelope;
    }

    private static void CheckGuardedBegin(MiniStackCall call, LifecycleProfile profile, EffectEnvelope envelope)
    {
        var actions = call.Request.GetProperty("TransactItems").EnumerateArray().ToArray();
        var checks = actions.Where(action => action.TryGetProperty("ConditionCheck", out _))
            .Select(action => action.GetProperty("ConditionCheck")).ToArray();
        var lease = Assert.Single(checks, action => ReadSortKey(action.GetProperty("Key")) == "LEASE");
        Assert.Contains("#catalogGeneration", lease.GetProperty("ConditionExpression").GetString(), StringComparison.Ordinal);
        Assert.Contains("#until", lease.GetProperty("ConditionExpression").GetString(), StringComparison.Ordinal);
        Assert.Contains(checks, action => ReadSortKey(action.GetProperty("Key")) == LifecycleKey.Profile(profile).SortKey);
        var allocation = Assert.IsType<FargateLaunchEffectRequest>(envelope.Request).AllocationId;
        Assert.Contains(checks, action => ReadSortKey(action.GetProperty("Key")) == LifecycleKey.Runner(profile, allocation.ToString("D")).SortKey);
        var puts = actions.Where(action => action.TryGetProperty("Put", out _)).Select(action => action.GetProperty("Put")).ToArray();
        Assert.Contains(puts, action => ReadSortKey(action.GetProperty("Item")) == LifecycleKey.Operation(profile, envelope.IntentId).SortKey);
        Assert.Contains(puts, action => ReadSortKey(action.GetProperty("Item")) == "EFFECTS");
    }

    private static string? ReadSortKey(JsonElement key) => key.GetProperty("SK").GetProperty("S").GetString();

    private static EffectEnvelope Launch(LifecycleProfile profile)
    {
        var request = new FargateLaunchEffectRequest(Guid.NewGuid(), Cluster, TaskDefinition, ["subnet-0123456789abcdef2"],
            ["sg-0123456789abcdef1"], BootstrapRole, ExecutionRole, "runner", Guid.NewGuid());
        return EffectEnvelope.Create(profile, EffectIntentIdentity.ForAllocation(request)!, 1, request, Now.AddSeconds(-1), Now.AddMinutes(49));
    }

    private static ControllerClockEvidence ClockProof(DispatchClock clock) => new(clock.Now, clock.Now.AddMinutes(2), TimeSpan.Zero);

    private static Task<EffectDispatchStatus> DispatchAsync(Fixture fixture, EffectEnvelope envelope, FakeProvider provider,
        CancellationToken cancellationToken) => fixture.Dispatcher.DispatchPlannedAsync(fixture.Lease, ClockProof(fixture.Clock),
            fixture.Profile, envelope.IntentId, provider, CancellationToken.None, cancellationToken);

    private static Task<EffectReconciliationPass> ReconcileAsync(Fixture fixture, FakeProvider provider, CancellationToken cancellationToken) =>
        fixture.Dispatcher.ReconcilePassAsync(fixture.Lease, ClockProof(fixture.Clock), fixture.Profile, null, provider, null, cancellationToken);

    private static async Task PlanAsync(Fixture fixture, EffectEnvelope envelope, CancellationToken cancellationToken) =>
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Journal.PlanAsync(fixture.Lease, envelope, Guid.NewGuid(), cancellationToken));

    private static async Task<EffectJournalSnapshot> ReadAsync(Fixture fixture, EffectEnvelope envelope, CancellationToken cancellationToken) =>
        Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(fixture.Profile, envelope.IntentId, cancellationToken));

    private sealed record Fixture(DynamoDbLifecycleStore Store, DynamoDbEffectJournal Journal, DynamoDbEffectDispatcher Dispatcher,
        ControllerLease Lease, LifecycleProfile Profile, DispatchClock Clock);

    private sealed class DispatchClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = EffectDispatcherTests.Now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class FakeProvider(DispatchClock clock) : IReconciliationEffectProvider
    {
        internal List<string> Checks { get; } = [];
        internal List<(string Sha256, Guid Attempt)> Sends { get; } = [];
        internal List<(string Sha256, Guid Attempt)> Readbacks { get; } = [];
        internal Func<EffectEnvelope, CancellationToken, Task>? BeforeCheck { get; init; }
        internal Func<EffectEnvelope, EffectAttempt, CancellationToken, Task<EffectEvidence?>>? OnSend { get; init; }
        internal Func<EffectEnvelope, EffectAttempt, CancellationToken, Task<EffectEvidence?>>? OnObserve { get; set; }

        public async Task<EffectDispatchPrecondition?> CheckAsync(EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken)
        {
            Checks.Add(envelope.Sha256);
            if (BeforeCheck is not null)
            {
                await BeforeCheck(envelope, cancellationToken);
            }
            return new EffectDispatchPrecondition(envelope.Sha256, envelope.Target, clock.Now, Eligible: true);
        }

        public Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            Sends.Add((envelope.Sha256, attempt.Id));
            return OnSend?.Invoke(envelope, attempt, cancellationToken) ?? Task.FromResult<EffectEvidence?>(null);
        }

        public Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            Readbacks.Add((envelope.Sha256, attempt.Id));
            return OnObserve?.Invoke(envelope, attempt, cancellationToken) ?? Task.FromResult<EffectEvidence?>(null);
        }
    }

    private sealed class TaskObserver(string expectedTaskArn) : IControllerTaskFenceObserver
    {
        internal List<string> Reads { get; } = [];
        internal ControllerTaskFenceStatus Status { get; set; } = ControllerTaskFenceStatus.Running;

        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            Reads.Add(taskArn);
            return Task.FromResult(taskArn == expectedTaskArn ? Status : ControllerTaskFenceStatus.Unknown);
        }
    }
}
