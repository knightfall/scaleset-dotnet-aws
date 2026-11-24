using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class GitHubListenerCheckpointTests
{
    private const long ScaleSetId = 9_007_199_254_740_993;
    private static readonly Guid SessionId = Guid.Parse("51688c9f-f017-4a9a-b59f-77ed34074c1b");
    private static readonly DateTimeOffset ObservedAt = MiniStackTable.Now.ToOffset(TimeSpan.FromHours(-4)).AddTicks(12345);

    [Fact]
    public async Task PumpPersistsFourFencedTransactionsForMessageZeroWithExactDelivery()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var clock = new CheckpointClock { Now = ObservedAt };
        var message = Message(0);
        var queue = new QueueSession { Message = message };
        var pump = CreatePump(store, store.SessionKey, queue, clock, async (delivery, token) =>
        {
            var saved = Assert.IsType<ScaleSetListenerCheckpoint>(await store.ReadAsync(store.SessionKey, token));
            Assert.Equal(ScaleSetListenerPendingPhase.Received, saved.Pending?.Phase);
            Assert.Equal(JsonSerializer.Serialize(delivery), JsonSerializer.Serialize(saved.Pending?.Delivery));
            Assert.Equal(ObservedAt.Offset, delivery.ObservedAt.Offset);
            if (delivery.Kind == ScaleSetListenerDeliveryKind.InitialStatistics)
            {
                Assert.Null(saved.LastAcknowledgedMessageId);
                Assert.Equal(3, saved.Pending?.Delivery.InitialStatistics?.TotalAssignedJobs);
            }
            return ScaleSetListenerDeliveryDisposition.Completed;
        });

        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(cancellation)).Status);
        Assert.Null((await store.ReadAsync(store.SessionKey, cancellation))?.LastAcknowledgedMessageId);
        table.Wire.Clear();
        clock.Now = ObservedAt.AddMinutes(1);
        queue.OnDelete = async (_, token) =>
        {
            var saved = Assert.IsType<ScaleSetListenerCheckpoint>(await store.ReadAsync(store.SessionKey, token));
            Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, saved.Pending?.Phase);
            Assert.Null(saved.LastAcknowledgedMessageId);
        };

        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, 0), await pump.StepAsync(cancellation));
        Assert.Equal((0L, 7), Assert.Single(queue.Polls));
        Assert.Equal(0, Assert.Single(queue.Deletes));

        var writes = CheckpointWrites(table, fence);
        Assert.Equal(new long[] { 3, 4, 5, 6 }, writes.Select(checkpoint => checkpoint.Revision));
        Assert.Equal(4, writes.Select(checkpoint => checkpoint.TransitionId).Distinct().Count());
        Assert.Equal(new ScaleSetListenerPendingPhase?[]
        {
            ScaleSetListenerPendingPhase.Received, ScaleSetListenerPendingPhase.Processed,
            ScaleSetListenerPendingPhase.Acknowledging, null,
        }, writes.Select(checkpoint => checkpoint.Pending?.Phase));
        var expected = new ScaleSetListenerDelivery(store.SessionKey, ScaleSetListenerDeliveryKind.Message, clock.Now, message);
        Assert.All(writes.Take(3), checkpoint =>
        {
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(checkpoint.Pending?.Delivery));
            Assert.Null(checkpoint.LastAcknowledgedMessageId);
            Assert.Equal(string.Empty, Assert.Single(checkpoint.Pending!.Delivery.Message!.JobAvailableMessages).AcquireJobUrl);
        });
        var settled = Assert.IsType<ScaleSetListenerCheckpoint>(await store.ReadAsync(store.SessionKey, cancellation));
        Assert.Equal(writes[3], settled);
        Assert.Equal(0, settled.LastAcknowledgedMessageId);
        Assert.DoesNotContain(table.Wire.Requests, call => call.Request.GetRawText().Contains("synthetic-acquire-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostReceivedWriteResumesExactTransitionAndOriginalObservationWithoutSecondPoll()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var clock = new CheckpointClock { Now = ObservedAt };
        var queue = new QueueSession { Message = Message(17) };
        var handled = new List<ScaleSetListenerDelivery>();
        var pump = CreatePump(store, store.SessionKey, queue, clock, (delivery, _) =>
        {
            handled.Add(delivery);
            return Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed);
        });
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(cancellation)).Status);
        handled.Clear();
        table.Wire.Clear();
        table.Wire.DropNextSuccessfulTransactionResponse();

        await Assert.ThrowsAsync<IOException>(() => pump.StepAsync(cancellation));
        Assert.Empty(handled);
        Assert.Empty(queue.Deletes);
        var attempted = Assert.Single(CheckpointWrites(table, fence));
        Assert.Equal(3, attempted.Revision);
        Assert.Equal(ScaleSetListenerPendingPhase.Received, attempted.Pending?.Phase);
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, 17), await pump.StepAsync(cancellation));

        var replay = Assert.Single(handled);
        Assert.True(replay.IsReplay);
        Assert.Equal(ObservedAt, replay.ObservedAt);
        Assert.Equal(ObservedAt.Offset, replay.ObservedAt.Offset);
        Assert.Equal(JsonSerializer.Serialize(attempted.Pending?.Delivery), JsonSerializer.Serialize(replay));
        Assert.Single(queue.Polls);
        Assert.Equal(17, Assert.Single(queue.Deletes));
        var writes = CheckpointWrites(table, fence);
        Assert.Equal(new long[] { 3, 4, 5, 6 }, writes.Select(checkpoint => checkpoint.Revision));
        Assert.Equal(attempted.TransitionId, writes[0].TransitionId);
        var afterWrite = table.Wire.Requests.SkipWhile(call => call.Operation != "TransactWriteItems").Skip(1).First();
        Assert.Equal("GetItem", afterWrite.Operation);
        Assert.True(afterWrite.Request.GetProperty("ConsistentRead").GetBoolean());
        Assert.Equal(LifecycleKey.Checkpoint(fence.Identity.Profile, SessionId).SortKey,
            afterWrite.Request.GetProperty("Key").GetProperty("SK").GetProperty("S").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedDeleteRetriesOnlySettlementAcrossBothLostWriteOutcomes(bool committed)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var queue = new QueueSession { Message = Message(17) };
        int handled = 0;
        var pump = CreatePump(store, store.SessionKey, queue, new CheckpointClock(), (delivery, _) =>
        {
            if (delivery.Kind == ScaleSetListenerDeliveryKind.Message)
            {
                handled++;
            }
            return Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed);
        });
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(cancellation)).Status);
        table.Wire.Clear();
        queue.OnDelete = (_, _) =>
        {
            if (committed)
            {
                table.Wire.DropNextSuccessfulTransactionResponse();
            }
            else
            {
                table.Wire.DropNextTransactionBeforeSend();
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<IOException>(() => pump.StepAsync(cancellation));
        var ambiguous = Assert.IsType<ScaleSetListenerCheckpoint>(await store.ReadAsync(store.SessionKey, cancellation));
        Assert.Equal(committed ? 6 : 5, ambiguous.Revision);
        Assert.Equal(committed ? null : ScaleSetListenerPendingPhase.Acknowledging, ambiguous.Pending?.Phase);
        Assert.Equal(committed ? 17L : null, ambiguous.LastAcknowledgedMessageId);
        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, 17), await pump.StepAsync(cancellation));

        Assert.Equal(1, handled);
        Assert.Single(queue.Polls);
        Assert.Equal(17, Assert.Single(queue.Deletes));
        var writes = CheckpointWrites(table, fence);
        long[] expectedRevisions = committed ? [3, 4, 5, 6] : [3, 4, 5, 6, 6];
        Assert.Equal(expectedRevisions, writes.Select(checkpoint => checkpoint.Revision));
        Assert.Equal(writes[3], writes[^1]);
        Assert.Equal(17, writes[^1].LastAcknowledgedMessageId);
        Assert.Null(writes[^1].Pending);
        Assert.Equal(writes[^1], await store.ReadAsync(store.SessionKey, cancellation));
    }

    [Fact]
    public async Task EffectIndexRebuildKeepsKnownDeleteAvailableForSettlementOnlyRetry()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var queue = new QueueSession { Message = Message(17) };
        var pump = CreatePump(store, store.SessionKey, queue, new CheckpointClock());
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(cancellation)).Status);
        var profile = fence.Identity.Profile;
        var effects = LifecycleKey.Effects(profile);
        var ready = await table.ReadAsync(effects, cancellation);
        var rebuilding = new Dictionary<string, AttributeValue>(ready, StringComparer.Ordinal)
        {
            ["indexState"] = new("rebuilding"),
        };
        rebuilding.Remove("unfinishedCount");
        queue.OnDelete = (_, token) => table.Client.PutItemAsync(table.Name, rebuilding, token);
        table.Wire.Clear();

        await Assert.ThrowsAsync<IOException>(() => pump.StepAsync(cancellation));
        Assert.Equal(EffectIndexState.Rebuilding, (await table.CreateStore().ReadEffectIndexAsync(profile, cancellation))?.State);
        var waiting = Assert.IsType<ScaleSetListenerCheckpoint>(await store.ReadAsync(store.SessionKey, cancellation));
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, waiting.Pending?.Phase);
        Assert.Null(waiting.LastAcknowledgedMessageId);
        await table.Client.PutItemAsync(table.Name, ready, cancellation);

        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, 17), await pump.StepAsync(cancellation));
        Assert.Single(queue.Polls);
        Assert.Equal(17, Assert.Single(queue.Deletes));
        var writes = CheckpointWrites(table, fence);
        Assert.Equal(new long[] { 3, 4, 5, 6, 6 }, writes.Select(checkpoint => checkpoint.Revision));
        Assert.Equal(writes[3], writes[4]);
        Assert.Equal(writes[4], await store.ReadAsync(store.SessionKey, cancellation));
    }

    [Fact]
    public async Task ReconstructedPumpKeepsLostDeleteAcknowledgingAndDoesNotReprocess()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var queue = new QueueSession { Message = Message(9) };
        var clock = new CheckpointClock();
        var pump = CreatePump(store, store.SessionKey, queue, clock);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(cancellation)).Status);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await pump.StepAsync(cancellation)).Status);
        queue.Message = Message(17);
        queue.OnDelete = (_, _) => Task.FromException(new HttpRequestException("The queue response was lost."));
        table.Wire.Clear();

        await Assert.ThrowsAsync<HttpRequestException>(() => pump.StepAsync(cancellation));
        var restarted = new GitHubListenerCheckpointStore(table.CreateStore(), fence);
        var reconstructed = CreatePump(restarted, restarted.SessionKey, queue, clock,
            (_, _) => throw new InvalidOperationException("Recovery must not invoke the handler."));
        var result = await reconstructed.StepAsync(cancellation);

        Assert.Equal(new(ScaleSetListenerStepStatus.RecoveryRequired, 17, ScaleSetListenerPendingPhase.Acknowledging), result);
        Assert.Equal(new[] { (0L, 7), (9L, 7) }, queue.Polls);
        Assert.Equal(new long[] { 9, 17 }, queue.Deletes);
        var writes = CheckpointWrites(table, fence);
        Assert.Equal(new long[] { 7, 8, 9 }, writes.Select(checkpoint => checkpoint.Revision));
        var saved = Assert.IsType<ScaleSetListenerCheckpoint>(await restarted.ReadAsync(restarted.SessionKey, cancellation));
        Assert.Equal(9, saved.LastAcknowledgedMessageId);
        Assert.Equal(writes[2].TransitionId, saved.TransitionId);
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, saved.Pending?.Phase);
        Assert.Equal(17, saved.Pending?.Delivery.Message?.MessageId);
    }

    [Fact]
    public async Task ReconstructedPumpAcknowledgesProcessedCheckpointWithoutPollingOrHandling()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var queue = new QueueSession { Message = Message(17) };
        var clock = new CheckpointClock();
        var pump = CreatePump(store, store.SessionKey, queue, clock, (delivery, _) =>
        {
            if (delivery.Kind == ScaleSetListenerDeliveryKind.Message)
            {
                table.Wire.DropNextSuccessfulTransactionResponse();
            }
            return Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed);
        });
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(cancellation)).Status);

        await Assert.ThrowsAsync<IOException>(() => pump.StepAsync(cancellation));
        var saved = Assert.IsType<ScaleSetListenerCheckpoint>(await store.ReadAsync(store.SessionKey, cancellation));
        Assert.Equal(ScaleSetListenerPendingPhase.Processed, saved.Pending?.Phase);
        Assert.Empty(queue.Deletes);
        table.Wire.Clear();
        var restarted = new GitHubListenerCheckpointStore(table.CreateStore(), fence);
        var resumed = CreatePump(restarted, restarted.SessionKey, queue, clock,
            (_, _) => throw new InvalidOperationException("A processed checkpoint does not need handling again."));

        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, 17), await resumed.StepAsync(cancellation));
        Assert.Single(queue.Polls);
        Assert.Equal(17, Assert.Single(queue.Deletes));
        var writes = CheckpointWrites(table, fence);
        Assert.Equal(new long[] { 5, 6 }, writes.Select(checkpoint => checkpoint.Revision));
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, writes[0].Pending?.Phase);
        Assert.Equal(JsonSerializer.Serialize(saved.Pending?.Delivery), JsonSerializer.Serialize(writes[0].Pending?.Delivery));
        Assert.Equal(17, writes[1].LastAcknowledgedMessageId);
        Assert.Null(writes[1].Pending);
    }

    [Fact]
    public async Task RevisionAndStaleOwnershipConflictsKeepTheOriginalCheckpoint()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, _) = await CreateCheckpointStoreAsync(table, cancellation);
        var first = InitialCheckpoint(store.SessionKey);
        Assert.Equal(ScaleSetListenerCheckpointWriteResult.Committed, await store.WriteAsync(store.SessionKey, null, first, cancellation));
        Assert.Equal(ScaleSetListenerCheckpointWriteResult.Conflict, await store.WriteAsync(
            store.SessionKey, null, first with { TransitionId = Guid.NewGuid() }, cancellation));
        var next = first with { Revision = 2, TransitionId = Guid.NewGuid(), InitialStatisticsCompleted = true, Pending = null };

        await table.SetLeaseAsync(table.Lease with { Epoch = table.Lease.Epoch + 1 }, MiniStackTable.Now.AddHours(1), cancellation);
        Assert.Equal(ScaleSetListenerCheckpointWriteResult.Conflict, await store.WriteAsync(store.SessionKey, 1, next, cancellation));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await store.ReadAsync(store.SessionKey, cancellation)));
    }

    [Fact]
    public async Task ClosingSessionRejectsItsCheckpointWriteUnderTheSameLease()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var first = InitialCheckpoint(store.SessionKey);
        Assert.Equal(ScaleSetListenerCheckpointWriteResult.Committed, await store.WriteAsync(store.SessionKey, null, first, cancellation));
        var lifecycle = table.CreateStore();
        var session = Assert.IsType<SessionRecord>(await lifecycle.ReadSessionAsync(fence.Identity.Profile, cancellation));
        var closed = await lifecycle.BeginSessionCloseAsync(session, table.Lease, SessionCloseKind.OwnedClient,
            MiniStackTable.Now, MiniStackTable.Now.AddMinutes(1), cancellation);
        Assert.Equal(SessionPhase.Closing, closed.Session?.Phase);
        var next = first with { Revision = 2, TransitionId = Guid.NewGuid(), InitialStatisticsCompleted = true, Pending = null };

        Assert.Equal(ScaleSetListenerCheckpointWriteResult.Conflict, await store.WriteAsync(store.SessionKey, 1, next, cancellation));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await store.ReadAsync(store.SessionKey, cancellation)));
    }

    [Fact]
    public async Task RejectsForeignKeysAndOversizedSnapshotsBeforeAnyCheckpointWrite()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var first = InitialCheckpoint(store.SessionKey);
        table.Wire.Clear();
        foreach (var wrongKey in new[] { store.SessionKey with { Scope = "github.com/a/different-repo" },
            store.SessionKey with { ScaleSetId = ScaleSetId + 1 }, store.SessionKey with { SessionId = Guid.NewGuid() } })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(wrongKey, cancellation));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(wrongKey, null, first, cancellation));
        }

        var largeMessage = new RunnerScaleSetMessage
        {
            MessageId = 17,
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 18, JobDisplayName = new string('x', 384 * 1024) }],
        };
        var large = new ScaleSetListenerCheckpoint(store.SessionKey, 1, 3, Guid.NewGuid(), true, null,
            new(new(store.SessionKey, ScaleSetListenerDeliveryKind.Message, ObservedAt, largeMessage), ScaleSetListenerPendingPhase.Received));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(store.SessionKey, 2, large, cancellation));
        Assert.Empty(table.Wire.Requests);
        Assert.Null(await table.CreateStore().ReadRecordAsync(LifecycleKey.Checkpoint(fence.Identity.Profile, SessionId), cancellation));
    }

    [Fact]
    public async Task RejectsPersistedAcknowledgmentForADifferentDeleteRequest()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var (store, fence) = await CreateCheckpointStoreAsync(table, cancellation);
        var queue = new QueueSession { Message = Message(17), OnDelete = (_, _) => Task.FromException(new HttpRequestException()) };
        var pump = CreatePump(store, store.SessionKey, queue, new CheckpointClock());
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(cancellation)).Status);
        await Assert.ThrowsAsync<HttpRequestException>(() => pump.StepAsync(cancellation));
        var key = LifecycleKey.Checkpoint(fence.Identity.Profile, SessionId);
        var row = await table.ReadAsync(key, cancellation);
        var payload = JsonNode.Parse(row["payload"].S)!.AsObject();
        payload["AcknowledgmentSha256"] = AcknowledgmentDigest(store.SessionKey, 18);
        row["payload"].S = payload.ToJsonString();
        await table.Client.PutItemAsync(table.Name, row, cancellation);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(store.SessionKey, cancellation));
    }

    private static async Task<(GitHubListenerCheckpointStore Store, OwnedSessionFence Fence)> CreateCheckpointStoreAsync(
        MiniStackTable table, CancellationToken cancellation)
    {
        var store = table.CreateStore();
        var profile = table.Profile("checkpoint");
        var ready = await store.AdvanceEffectRebuildAsync(profile, table.Lease, cancellation);
        Assert.Equal(EffectIndexState.Ready, ready.Head?.State);
        var opened = await store.BeginSessionOpenAsync(new SessionBinding(profile, "github.com/owner/repository", ScaleSetId),
            table.Lease, null, "fixture-owner", MiniStackTable.Now, MiniStackTable.Now.AddMinutes(1), cancellation);
        var permission = Assert.IsType<SessionSendAuthorization>(opened.Permission);
        Assert.True(permission.TryConsume(out _));
        var confirmed = await store.ConfirmSessionOpenedAsync(permission, SessionId, cancellation);
        var fence = Assert.IsType<OwnedSessionFence>(confirmed.Session?.Fence);
        return (new GitHubListenerCheckpointStore(store, fence), fence);
    }

    private static ScaleSetListenerCheckpoint InitialCheckpoint(ScaleSetListenerSessionKey key) =>
        new(key, ScaleSetListenerCheckpoint.CurrentSchemaVersion, 1, Guid.NewGuid(), false, null,
            new(new(key, ScaleSetListenerDeliveryKind.InitialStatistics, ObservedAt,
                initialStatistics: new RunnerScaleSetStatistic()), ScaleSetListenerPendingPhase.Received));

    private static ScaleSetListenerCheckpoint[] CheckpointWrites(MiniStackTable table, OwnedSessionFence fence)
    {
        var writes = new List<ScaleSetListenerCheckpoint>();
        var key = LifecycleKey.Checkpoint(fence.Identity.Profile, fence.Identity.SessionId);
        var tokens = new HashSet<Guid>();
        foreach (var call in table.Wire.Requests.Where(call => call.Operation == "TransactWriteItems"))
        {
            var actions = call.Request.GetProperty("TransactItems").EnumerateArray().ToArray();
            var put = Assert.Single(actions, action => action.TryGetProperty("Put", out _)).GetProperty("Put");
            var item = put.GetProperty("Item");
            Assert.Equal(key.PartitionKey, item.GetProperty("PK").GetProperty("S").GetString());
            Assert.Equal(key.SortKey, item.GetProperty("SK").GetProperty("S").GetString());
            string?[] guards = actions.Where(action => action.TryGetProperty("ConditionCheck", out _))
                .Select(action => action.GetProperty("ConditionCheck").GetProperty("Key").GetProperty("SK").GetProperty("S").GetString())
                .ToArray();
            Assert.Contains("LEASE", guards);
            Assert.Contains("SESSION", guards);
            Assert.Contains("EFFECTS", guards);
            Assert.False(item.TryGetProperty("expiresAt", out _));
            using var payload = JsonDocument.Parse(item.GetProperty("payload").GetProperty("S").GetString()!);
            var checkpoint = Assert.IsType<ScaleSetListenerCheckpoint>(
                payload.RootElement.GetProperty("Checkpoint").Deserialize<ScaleSetListenerCheckpoint>());
            var digest = payload.RootElement.GetProperty("AcknowledgmentSha256");
            if (checkpoint.Pending is { Phase: ScaleSetListenerPendingPhase.Acknowledging, Delivery.Message: { } message })
            {
                Assert.Equal(AcknowledgmentDigest(checkpoint.SessionKey, message.MessageId), digest.GetString());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, digest.ValueKind);
            }
            Assert.Equal(checkpoint.TransitionId.ToString("D"), item.GetProperty("transitionId").GetProperty("S").GetString());
            Assert.True(Guid.TryParseExact(call.Request.GetProperty("ClientRequestToken").GetString(), "D", out var token));
            Assert.True(tokens.Add(token));
            writes.Add(checkpoint);
        }
        return writes.ToArray();
    }

    private static string AcknowledgmentDigest(ScaleSetListenerSessionKey key, long messageId) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            Operation = "delete-message",
            key.Scope,
            key.ScaleSetId,
            key.SessionId,
            MessageId = messageId,
        })));

    private static RunnerScaleSetMessage Message(long id) => new()
    {
        MessageId = id,
        Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 0, TotalIdleRunners = 5 },
        JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = ScaleSetId, RequestLabels = ["α", "linux"],
            AcquireJobUrl = "https://example.test/acquire?synthetic-acquire-key", QueueTime = ObservedAt }],
        JobAssignedMessages = [new JobAssignedMessage { RunnerRequestId = ScaleSetId + 1, JobDisplayName = "Compile", ScaleSetAssignTime = ObservedAt }],
        JobStartedMessages = [new JobStartedMessage { RunnerRequestId = ScaleSetId + 2, RunnerId = 15, RunnerName = "runner", RunnerAssignTime = ObservedAt }],
        JobCompletedMessages = [new JobCompletedMessage { RunnerRequestId = ScaleSetId + 3, RunnerId = 16, Result = "success", FinishTime = ObservedAt }],
    };

    private static ScaleSetListenerPump CreatePump(IScaleSetListenerCheckpointStore checkpoints,
        ScaleSetListenerSessionKey key, QueueSession queue, CheckpointClock clock,
        Func<ScaleSetListenerDelivery, CancellationToken, Task<ScaleSetListenerDeliveryDisposition>>? handle = null) =>
        new(queue, new ScaleSetListenerPumpOptions(key, CancellationToken.None), checkpoints,
            new DeliveryHandler(handle ?? ((_, _) => Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed))), new Policy(clock), clock);

    private sealed class CheckpointClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = ObservedAt;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class QueueSession : IMessageSessionClient
    {
        public RunnerScaleSetSession Session { get; } = new()
        {
            SessionId = GitHubListenerCheckpointTests.SessionId,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 3 },
        };

        internal RunnerScaleSetMessage? Message { get; set; }
        internal List<(long Cursor, int Capacity)> Polls { get; } = [];
        internal List<long> Deletes { get; } = [];
        internal Func<long, CancellationToken, Task>? OnDelete { get; set; }

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
        {
            Polls.Add((lastMessageId, maxCapacity));
            return Task.FromResult(Message);
        }

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
        {
            Deletes.Add(messageId);
            return OnDelete?.Invoke(messageId, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This checkpoint test has no acquisitions.");
    }

    private sealed class Policy(CheckpointClock clock) : IScaleSetListenerExecutionPolicy
    {
        public Task<ScaleSetListenerAuthorization> AuthorizeAsync(ScaleSetListenerOperationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ScaleSetListenerAuthorization.Allow(clock.Now.AddMinutes(2),
                context.Operation == ScaleSetListenerOperation.Poll ? 7 : null, CancellationToken.None));
    }

    private sealed class DeliveryHandler(Func<ScaleSetListenerDelivery, CancellationToken, Task<ScaleSetListenerDeliveryDisposition>> handle)
        : IScaleSetListenerDeliveryHandler
    {
        public Task<ScaleSetListenerDeliveryDisposition> HandleAsync(ScaleSetListenerDelivery delivery, CancellationToken cancellationToken) =>
            handle(delivery, cancellationToken);
    }
}
