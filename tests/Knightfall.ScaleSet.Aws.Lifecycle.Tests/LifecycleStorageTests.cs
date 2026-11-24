using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class LifecycleStorageTests
{
    [Fact]
    public void ExactKeyspaceSeparatesEveryLifecycleRecordFamily()
    {
        var profile = new LifecycleProfile("d", "p");
        var session = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var generation = Guid.Parse("20000000-0000-0000-0000-000000000002");
        (LifecycleKey Key, string Partition, string Sort)[] expected =
        [
            (LifecycleKey.Lease("d"), "ENV#d", "LEASE"),
            (LifecycleKey.Profile(profile), "ENV#d", "PROFILE#p"),
            (LifecycleKey.Claim("github.example#installation#7"), "CLAIM#github.example#installation#7", "OWNER"),
            (LifecycleKey.Session(profile), "PROFILE#d#p", "SESSION"),
            (LifecycleKey.Demand(profile), "PROFILE#d#p", "DEMAND"),
            (LifecycleKey.Admission(profile), "PROFILE#d#p", "ADMISSION"),
            (LifecycleKey.Effects(profile), "PROFILE#d#p", "EFFECTS"),
            (LifecycleKey.EffectRebuild(profile), "PROFILE#d#p", "EFFECT-REBUILD"),
            (LifecycleKey.ActiveOperation(profile, generation, "scope#intent"), "PROFILE#d#p", $"ACTIVE-OP#{generation:D}#scope#intent"),
            (LifecycleKey.Checkpoint(profile, session), "PROFILE#d#p", $"CHECKPOINT#{session:D}"),
            (LifecycleKey.Message(profile, session, long.MaxValue), "PROFILE#d#p", $"MSG#{session:D}#9223372036854775807"),
            (LifecycleKey.Acquisition(profile, 17), "PROFILE#d#p", "ACQUIRE#17"),
            (LifecycleKey.Job(profile, "request#attempt-1"), "PROFILE#d#p", "JOB#request#attempt-1"),
            (LifecycleKey.Runner(profile, "allocation-1"), "PROFILE#d#p", "RUNNER#allocation-1"),
            (LifecycleKey.RunnerIdentity(profile, long.MaxValue), "PROFILE#d#p", "RUNNER-ID#9223372036854775807"),
            (LifecycleKey.Operation(profile, "scope#intent"), "PROFILE#d#p", "OP#scope#intent"),
            (LifecycleKey.Hello("AROA:session"), "HELLO#BROKER#AROA:session", "BOOTSTRAP"),
            (LifecycleKey.Offer("AROA:session"), "OFFER#AROA:session", "BOOTSTRAP"),
            (LifecycleKey.Status("AROA:session"), "STATUS#AROA:session", "BOOTSTRAP"),
            (LifecycleKey.RunnerInventory(profile), "PROFILE#d#p", "RUNNER-INVENTORY"),
            (LifecycleKey.ActiveRunner(profile, "allocation-1"), "PROFILE#d#p", "ACTIVE-RUNNER#allocation-1"),
            (LifecycleKey.Drain(profile), "PROFILE#d#p", "DRAIN"),
            (LifecycleKey.Handoff(profile, session), "PROFILE#d#p", $"HANDOFF#{session:D}"),
            (LifecycleKey.HandoffHead(profile), "PROFILE#d#p", "HANDOFFS"),
            (LifecycleKey.Ec2LaunchLineage(profile, session), "PROFILE#d#p", $"EC2-LAUNCH#{session:D}"),
        ];

        Assert.Equal(Enum.GetValues<LifecycleItemKind>(), expected.Select(row => row.Key.Kind));
        Assert.Equal(expected.Select(row => (row.Partition, row.Sort)), expected.Select(row => (row.Key.PartitionKey, row.Key.SortKey)));
        Assert.Equal(expected.Length, expected.Select(row => (row.Key.PartitionKey, row.Key.SortKey)).Distinct().Count());
        Assert.Throws<ArgumentException>(() => new LifecycleProfile("a#b", "c"));
    }

    [Fact]
    public async Task RecordBatchRequiresDistinctKeysExactLeaseAndEachExpectedRevision()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("records");
        var session = LifecycleKey.Admission(profile);
        var demand = LifecycleKey.Demand(profile);
        var token = Guid.NewGuid();
        LifecycleRecordWrite first = new(session, null, """{"session":"one"}""");
        LifecycleRecordWrite second = new(demand, null, """{"count":1}""");

        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.WriteRecordsAsync(table.Lease, token, [first, second], cancellation));
        var write = Assert.Single(table.Wire.Requests);
        Assert.Equal("TransactWriteItems", write.Operation);
        Assert.Equal(token.ToString("D"), write.Request.GetProperty("ClientRequestToken").GetString());
        Assert.Equal(["LEASE", "ADMISSION", "DEMAND"], ActionKeys(write));
        Assert.Equal(new LifecycleRecord(session, 1, token, first.Payload, null), await store.ReadRecordAsync(session, cancellation));
        Assert.Equal(new LifecycleRecord(demand, 1, token, second.Payload, null), await store.ReadRecordAsync(demand, cancellation));

        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.WriteRecordsAsync(
            table.Lease, Guid.NewGuid(), [first with { ExpectedRevision = 1, Payload = "rejected" }, second], cancellation));
        Assert.Equal(first.Payload, (await store.ReadRecordAsync(session, cancellation))?.Payload);

        foreach (var stale in new[]
        {
            table.Lease with { OwnerId = "another-owner" },
            table.Lease with { Epoch = 2 },
            table.Lease with { WriterProtocol = 2 },
        })
        {
            Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteRecordsAsync(
                stale, Guid.NewGuid(), [first with { ExpectedRevision = 1 }], cancellation));
        }
        await table.SetLeaseAsync(table.Lease, MiniStackTable.Now, cancellation);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteRecordsAsync(
            table.Lease, Guid.NewGuid(), [first with { ExpectedRevision = 1 }], cancellation));
        Assert.Equal(1, (await store.ReadRecordAsync(session, cancellation))?.Revision);

        table.Wire.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteRecordsAsync(table.Lease, Guid.NewGuid(), [first, first], cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteRecordsAsync(table.Lease, Guid.NewGuid(),
                [new(LifecycleKey.Demand(new LifecycleProfile("different-deployment", "records")), null, "{}")], cancellation));
        Assert.Empty(table.Wire.Requests);
    }

    [Fact]
    public async Task EffectTransactionMaintainsCountAndMarkersThroughConcurrentWriters()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("conditional");
        var first = new EffectWrite("start", null, EffectState.Planned, "start", MiniStackTable.Now);
        var empty = await ReadyAsync(store, profile, table.Lease, cancellation);
        Assert.Equal(0, empty.UnfinishedCount);

        table.Wire.Clear();
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(profile, table.Lease, Guid.NewGuid(), first, cancellation));
        var add = Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems");
        string marker = LifecycleKey.ActiveOperation(profile, empty.Generation, first.IntentId).SortKey;
        Assert.Equal(["LEASE", "OP#start", "EFFECTS", marker], ActionKeys(add));
        Assert.Equal(["start"], (await store.ReadActiveOperationsAsync(profile, table.Lease, null, cancellation))?.IntentIds);
        Assert.Equal("1", (await table.ReadAsync(LifecycleKey.Effects(profile), cancellation))["unfinishedCount"].N);

        table.Wire.Clear();
        table.Wire.HoldNextTwoTransactions();
        var outcomes = await Task.WhenAll(
            store.WriteEffectAsync(profile, table.Lease, Guid.NewGuid(),
                first with { ExpectedRevision = 1, State = EffectState.Executing, Payload = "left" }, cancellation),
            store.WriteEffectAsync(profile, table.Lease, Guid.NewGuid(),
                first with { ExpectedRevision = 1, State = EffectState.Uncertain, Payload = "right" }, cancellation));
        Assert.Contains(LifecycleWriteOutcome.Committed, outcomes);
        Assert.Contains(LifecycleWriteOutcome.Conflict, outcomes);
        var attempts = table.Wire.Requests.Where(call => call.Operation == "TransactWriteItems").ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.All(attempts, call => Assert.Equal(["LEASE", "OP#start", "EFFECTS"], ActionKeys(call)));
        var current = Assert.IsType<StoredEffect>(await store.ReadEffectAsync(profile, "start", cancellation));
        Assert.Equal(2, current.Revision);
        Assert.Equal(outcomes[0] == LifecycleWriteOutcome.Committed ? "left" : "right", current.Payload);
        Assert.Equal(1, (await store.ReadEffectIndexAsync(profile, cancellation))?.UnfinishedCount);
        Assert.Equal(["start"], (await store.ReadActiveOperationsAsync(profile, table.Lease, null, cancellation))?.IntentIds);

        table.Wire.Clear();
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(profile, table.Lease, Guid.NewGuid(),
            first with { ExpectedRevision = 2, State = EffectState.Settled, DueAt = null }, cancellation));
        var settle = Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems");
        Assert.Equal(marker, ActionKeys(settle)[^1]);
        Assert.True(settle.Request.GetProperty("TransactItems")[3].TryGetProperty("Delete", out _));
        Assert.Empty(await table.ReadAsync(LifecycleKey.ActiveOperation(profile, empty.Generation, "start"), cancellation));
        Assert.Equal(0, (await store.ReadEffectIndexAsync(profile, cancellation))?.UnfinishedCount);
        Assert.Empty(Assert.IsType<ActiveEffectPage>(
            await store.ReadActiveOperationsAsync(profile, table.Lease, null, cancellation)).IntentIds);
        Assert.False((await store.ReadEffectAsync(profile, "start", cancellation))?.IsUnfinished);
        Assert.DoesNotContain("expiresAt", await table.ReadAsync(LifecycleKey.Operation(profile, "start"), cancellation));
    }

    [Fact]
    public async Task LostRecordTransactionResponseLeavesExactDurableTransitionForReconciliation()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var key = LifecycleKey.Demand(table.Profile("uncertain"));
        var token = Guid.NewGuid();
        table.Wire.DropNextSuccessfulTransactionResponse();
        Assert.Equal(LifecycleWriteOutcome.Indeterminate,
            await store.WriteRecordsAsync(table.Lease, token, [new(key, null, "once")], cancellation));
        var request = Assert.Single(table.Wire.Requests);
        Assert.Equal("TransactWriteItems", request.Operation);
        Assert.Equal(token.ToString("D"), request.Request.GetProperty("ClientRequestToken").GetString());

        var observed = await table.CreateStore().ReadRecordAsync(key, cancellation);
        Assert.Equal(new LifecycleRecord(key, 1, token, "once", null), observed);
        var read = Assert.Single(table.Wire.Requests, call => call.Operation == "GetItem");
        Assert.True(read.Request.GetProperty("ConsistentRead").GetBoolean());

        var changedClock = table.CreateStore(MiniStackTable.Now.AddSeconds(1));
        Assert.Equal(LifecycleWriteOutcome.Indeterminate,
            await changedClock.WriteRecordsAsync(table.Lease, token, [new(key, null, "once")], cancellation));
        Assert.Equal(observed, await changedClock.ReadRecordAsync(key, cancellation));
    }

    [Fact]
    public async Task ActiveContinuationPinsGenerationRevisionAndCumulativeCount()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("paged");
        await table.SeedEffectsAsync(profile, 38, Enumerable.Range(0, 38).ToHashSet(), cancellation);
        var original = await ReadyAsync(store, profile, table.Lease, cancellation);

        var page = Assert.IsType<ActiveEffectPage>(await store.ReadActiveOperationsAsync(profile, table.Lease, null, cancellation));
        Assert.Equal(Enumerable.Range(0, 32).Select(MiniStackTable.RetainedIntent), page.IntentIds);
        var old = Assert.IsType<ActiveEffectCursor>(page.Continuation);
        Assert.Equal(original.Generation, old.Generation);
        Assert.Equal(original.Revision, old.IndexRevision);
        Assert.Equal(32, old.SeenCount);

        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(profile, table.Lease, Guid.NewGuid(),
            new EffectWrite("before-the-cursor", null, EffectState.Planned, "{}"), cancellation));
        table.Wire.Clear();
        Assert.Null(await store.ReadActiveOperationsAsync(profile, table.Lease, old, cancellation));
        Assert.DoesNotContain(table.Wire.Requests, call => call.Operation == "Query");

        var first = Assert.IsType<ActiveEffectPage>(await store.ReadActiveOperationsAsync(profile, table.Lease, null, cancellation));
        var next = Assert.IsType<ActiveEffectCursor>(first.Continuation);
        Assert.Null(await store.ReadActiveOperationsAsync(profile, table.Lease, next with { SeenCount = 31 }, cancellation));
        var last = Assert.IsType<ActiveEffectPage>(await store.ReadActiveOperationsAsync(profile, table.Lease, next, cancellation));
        Assert.Null(last.Continuation);
        Assert.Equal(Enumerable.Range(0, 38).Select(MiniStackTable.RetainedIntent).Prepend("before-the-cursor"),
            first.IntentIds.Concat(last.IntentIds));
        Assert.All(table.Wire.Requests.Where(call => call.Operation == "Query"),
            call => Assert.Equal(LifecycleKey.ActiveOperationPrefix(original.Generation), call.QueryPrefix));
    }

    [Fact]
    public async Task RebuildReaderAcceptsPublicationBetweenHeadAndCheckpointReads()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var profile = table.Profile("published-during-read");
        await table.SeedEffectsAsync(profile, 65, new HashSet<int> { 64 }, cancellation);
        var store = table.CreateStore();
        var first = await store.AdvanceEffectRebuildAsync(profile, table.Lease, cancellation);
        Assert.Equal(EffectIndexState.Rebuilding, first.Head?.State);
        var pause = table.Wire.PauseNextRead(LifecycleKey.Effects(profile));
        var overlapping = table.CreateStore().AdvanceEffectRebuildAsync(profile, table.Lease, cancellation);
        EffectIndexHead ready;
        try
        {
            await pause.Captured.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            ready = await ReadyAsync(store, profile, table.Lease, cancellation);
        }
        finally
        {
            pause.Resume();
        }

        var observed = await overlapping;
        Assert.Equal(LifecycleWriteOutcome.Committed, observed.Outcome);
        Assert.Equal(ready, observed.Head);
        Assert.Equal(0, observed.PagesRead);
        Assert.Equal(1, ready.UnfinishedCount);
    }

    [Fact]
    public async Task RebuildResumesCommittedPageAfterResponseLossAndKeepsOtherProfileAvailable()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var large = table.Profile("large");
        var small = table.Profile("small");
        var expectedLarge = new SortedSet<int> { 5, 65, 7777, 9989 };
        await table.SeedEffectsAsync(large, 9990, expectedLarge, cancellation);
        await table.SeedEffectsAsync(small, 37, new HashSet<int> { 36 }, cancellation);
        table.Wire.Clear();

        var first = await store.AdvanceEffectRebuildAsync(large, table.Lease, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, first.Outcome);
        Assert.Equal(2, first.PagesRead);
        Assert.Equal(EffectIndexState.Rebuilding, first.Head?.State);
        Assert.Null(first.Head?.UnfinishedCount);
        AssertHistoryCalls(table, 2);
        Assert.Null(await store.ReadActiveOperationsAsync(large, table.Lease, null, cancellation));
        Assert.Equal(LifecycleWriteOutcome.RebuildRequired, await store.WriteEffectAsync(
            large, table.Lease, Guid.NewGuid(), new EffectWrite("new", null, EffectState.Planned, "{}"), cancellation));

        table.Wire.Clear();
        var smallReady = await ReadyAsync(store, small, table.Lease, cancellation);
        AssertHistoryCalls(table, 2);
        Assert.Equal(1, smallReady.UnfinishedCount);
        Assert.Equal([MiniStackTable.RetainedIntent(36)],
            (await store.ReadActiveOperationsAsync(small, table.Lease, null, cancellation))?.IntentIds);

        table.Wire.Clear();
        table.Wire.DropNextSuccessfulTransactionResponse();
        var lost = await store.AdvanceEffectRebuildAsync(large, table.Lease, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Indeterminate, lost.Outcome);
        Assert.Equal(1, lost.PagesRead);
        AssertHistoryCalls(table, 1);
        var uncertain = Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems");
        Assert.Contains("EFFECT-REBUILD", ActionKeys(uncertain));
        var checkpoint = await table.ReadAsync(LifecycleKey.EffectRebuild(large), cancellation);
        string afterLost = LifecycleKey.Operation(large, MiniStackTable.RetainedIntent(95)).SortKey;
        Assert.Equal(afterLost, checkpoint["after"].S);
        Assert.Equal("2", checkpoint["partialCount"].N);
        Assert.Null((await store.ReadEffectIndexAsync(large, cancellation))?.UnfinishedCount);

        var restarted = table.CreateStore();
        EffectRebuildPass? pass = null;
        for (int index = 0; index < 160; index++)
        {
            table.Wire.Clear();
            pass = await restarted.AdvanceEffectRebuildAsync(large, table.Lease, cancellation);
            Assert.Equal(LifecycleWriteOutcome.Committed, pass.Outcome);
            Assert.InRange(pass.PagesRead, 1, 2);
            var queries = AssertHistoryCalls(table, pass.PagesRead);
            if (index == 0)
            {
                Assert.Equal(afterLost, queries[0].Request.GetProperty("ExclusiveStartKey").GetProperty("SK").GetProperty("S").GetString());
            }
            if (pass.Head?.State == EffectIndexState.Ready)
            {
                break;
            }
            Assert.Null(pass.Head?.UnfinishedCount);
        }

        Assert.Equal(EffectIndexState.Ready, pass?.Head?.State);
        Assert.Equal(expectedLarge.Count, pass?.Head?.UnfinishedCount);
        Assert.Equal(first.Head?.Generation, pass?.Head?.Generation);
        var finished = await table.ReadAsync(LifecycleKey.EffectRebuild(large), cancellation);
        Assert.Equal("complete", finished["rebuildState"].S);
        Assert.DoesNotContain("after", finished);

        table.Wire.Clear();
        var stable = await table.CreateStore().AdvanceEffectRebuildAsync(large, table.Lease, cancellation);
        Assert.Equal(0, stable.PagesRead);
        Assert.Equal(pass?.Head, stable.Head);
        var candidates = Assert.IsType<ActiveEffectPage>(await restarted.ReadActiveOperationsAsync(large, table.Lease, null, cancellation));
        Assert.Equal(expectedLarge.Select(MiniStackTable.RetainedIntent), candidates.IntentIds);
        var steadyQuery = Assert.Single(table.Wire.Requests, call => call.Operation == "Query");
        Assert.Equal(LifecycleKey.ActiveOperationPrefix(pass!.Head!.Generation), steadyQuery.QueryPrefix);
        Assert.DoesNotContain(table.Wire.Requests, call => call.Operation is "Scan" or "TransactWriteItems" || call.QueryPrefix == "OP#");
    }

    [Fact]
    public async Task DueIndexLagAndStaleHintsDoNotDetermineUnfinishedEffects()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("due");
        await ReadyAsync(store, profile, table.Lease, cancellation);
        var write = new EffectWrite("pending", null, EffectState.Uncertain, "durable", MiniStackTable.Now.AddMinutes(-1));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(profile, table.Lease, Guid.NewGuid(), write, cancellation));

        table.Wire.Clear();
        var hints = new List<(int Shard, DueEffectHint Hint)>();
        for (int shard = 0; shard < 8; shard++)
        {
            var page = await store.ReadDueHintsAsync(profile.Deployment, shard, MiniStackTable.Now, null, cancellation);
            hints.AddRange(page.Hints.Select(hint => (shard, hint)));
        }
        var (hintShard, hint) = Assert.Single(hints);
        Assert.Equal("OP#pending", hint.SortKey);
        Assert.Equal(profile.PartitionKey, hint.PartitionKey);
        Assert.Equal(write.DueAt, hint.DueAt);
        Assert.All(table.Wire.Requests, call =>
        {
            Assert.True(call.IsDueQuery);
            Assert.False(call.Request.TryGetProperty("ConsistentRead", out var consistent) && consistent.GetBoolean());
        });

        table.Wire.DueQueryResponse = """{"Items":[],"Count":0,"ScannedCount":0}""";
        Assert.Empty((await store.ReadDueHintsAsync(profile.Deployment, hintShard, MiniStackTable.Now, null, cancellation)).Hints);
        Assert.Equal(["pending"], (await store.ReadActiveOperationsAsync(profile, table.Lease, null, cancellation))?.IntentIds);
        Assert.Equal("durable", (await store.ResolveDueHintAsync(profile, hint, MiniStackTable.Now, cancellation))?.Payload);

        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(profile, table.Lease, Guid.NewGuid(),
            write with { ExpectedRevision = 1, State = EffectState.Settled, DueAt = null }, cancellation));
        table.Wire.DueQueryResponse = JsonSerializer.Serialize(new
        {
            Items = new[]
            {
                new
                {
                    PK = new { S = hint.PartitionKey },
                    SK = new { S = hint.SortKey },
                    dueShard = new { S = hint.Shard },
                    dueAtMillis = new { N = hint.DueAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) },
                },
            },
            Count = 1,
            ScannedCount = 1,
        });

        table.Wire.Clear();
        var stale = Assert.Single((await store.ReadDueHintsAsync(profile.Deployment, hintShard, MiniStackTable.Now, null, cancellation)).Hints);
        Assert.Equal(hint, stale);
        Assert.Null(await store.ResolveDueHintAsync(profile, stale, MiniStackTable.Now, cancellation));
        var baseRead = Assert.Single(table.Wire.Requests, call => call.Operation == "GetItem");
        Assert.True(baseRead.Request.GetProperty("ConsistentRead").GetBoolean());
        Assert.Equal(hint.SortKey, baseRead.Request.GetProperty("Key").GetProperty("SK").GetProperty("S").GetString());
        Assert.Empty(Assert.IsType<ActiveEffectPage>(
            await store.ReadActiveOperationsAsync(profile, table.Lease, null, cancellation)).IntentIds);
    }

    [Fact]
    public async Task TtlIsOnlyForSettledHistoryAndExpiredPresentRowsRemainDistinguishable()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("history");
        var expires = MiniStackTable.Now.AddDays(-1);
        var retention = new SettledHistoryRetention(expires.AddDays(-2).AddMilliseconds(-450), TimeSpan.FromDays(2));
        LifecycleKey[] keys =
        [
            LifecycleKey.Message(profile, Guid.NewGuid(), 5),
            LifecycleKey.Acquisition(profile, 5),
            LifecycleKey.Job(profile, "attempt"),
        ];
        await table.Client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table.Name,
            TimeToLiveSpecification = new TimeToLiveSpecification { AttributeName = "expiresAt", Enabled = true },
        }, cancellation);

        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteRecordsAsync(table.Lease, Guid.NewGuid(),
            keys.Select(key => new LifecycleRecordWrite(key, null, "settled", retention)).ToArray(), cancellation));
        foreach (var key in keys)
        {
            var existing = Assert.IsType<LifecycleRecord>(await store.ReadRecordAsync(key, cancellation));
            Assert.Equal(expires, existing.ExpiresAt);
            Assert.Null(existing.Payload);
            Assert.False(existing.IsExpired(expires.AddMilliseconds(-1)));
            Assert.True(existing.IsExpired(MiniStackTable.Now));
            var physical = await table.ReadAsync(key, cancellation);
            Assert.Equal(expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), physical["expiresAt"].N);
            Assert.Equal("settled", physical["payload"].S);

            Assert.Equal(LifecycleWriteOutcome.Conflict, await store.WriteRecordsAsync(table.Lease, Guid.NewGuid(),
                [new(key, existing.Revision, "revived")], cancellation));
            Assert.Equal(LifecycleWriteOutcome.Conflict, await store.WriteRecordsAsync(table.Lease, Guid.NewGuid(),
                [new(key, existing.Revision, "extended", new SettledHistoryRetention(MiniStackTable.Now, TimeSpan.FromDays(1)))],
                cancellation));
            Assert.Equal(existing, await store.ReadRecordAsync(key, cancellation));
        }
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Job(profile, "missing"), cancellation));

        var liveJob = LifecycleKey.Job(profile, "still-running");
        var demand = LifecycleKey.Demand(profile);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteRecordsAsync(table.Lease, Guid.NewGuid(),
            [new(liveJob, null, "running"), new(demand, null, "current")], cancellation));
        Assert.DoesNotContain("expiresAt", await table.ReadAsync(liveJob, cancellation));
        Assert.DoesNotContain("expiresAt", await table.ReadAsync(demand, cancellation));

        table.Wire.Clear();
        LifecycleKey[] permanent =
        [
            LifecycleKey.Lease(table.Lease.Deployment), LifecycleKey.Profile(profile), LifecycleKey.Claim("scope"),
            LifecycleKey.Session(profile), demand, LifecycleKey.Admission(profile), LifecycleKey.Effects(profile),
            LifecycleKey.EffectRebuild(profile), LifecycleKey.ActiveOperation(profile, Guid.NewGuid(), "intent"),
            LifecycleKey.Checkpoint(profile, Guid.NewGuid()), LifecycleKey.Runner(profile, "allocation"),
            LifecycleKey.RunnerIdentity(profile, 1), LifecycleKey.Operation(profile, "intent"),
            LifecycleKey.Hello("AROA:session"), LifecycleKey.Offer("AROA:session"), LifecycleKey.Status("AROA:session"),
        ];
        foreach (var key in permanent)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.WriteRecordsAsync(table.Lease, Guid.NewGuid(), [new(key, null, "expired", retention)], cancellation));
        }
        Assert.Empty(table.Wire.Requests);
    }

    private static async Task<EffectIndexHead> ReadyAsync(
        DynamoDbLifecycleStore store, LifecycleProfile profile, DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        var result = await store.AdvanceEffectRebuildAsync(profile, lease, cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, result.Outcome);
        var head = Assert.IsType<EffectIndexHead>(result.Head);
        Assert.Equal(EffectIndexState.Ready, head.State);
        return head;
    }

    private static MiniStackCall[] AssertHistoryCalls(MiniStackTable table, int count)
    {
        var queries = table.Wire.Requests.Where(call => call.Operation == "Query").ToArray();
        Assert.Equal(count, queries.Length);
        Assert.All(queries, call =>
        {
            Assert.Equal("OP#", call.QueryPrefix);
            Assert.Equal(32, call.Request.GetProperty("Limit").GetInt32());
            Assert.True(call.Request.GetProperty("ConsistentRead").GetBoolean());
            Assert.False(call.Request.TryGetProperty("IndexName", out _));
        });
        Assert.DoesNotContain(table.Wire.Requests, call => call.Operation == "Scan");
        return queries;
    }

    private static string?[] ActionKeys(MiniStackCall call) => call.Request.GetProperty("TransactItems")
        .EnumerateArray()
        .Select(item => item.EnumerateObject().Single())
        .Select(action => action.Value.GetProperty(action.Name == "Put" ? "Item" : "Key").GetProperty("SK").GetProperty("S").GetString())
        .ToArray();
}
