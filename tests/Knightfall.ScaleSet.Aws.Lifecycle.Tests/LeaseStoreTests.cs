using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class LeaseStoreTests
{
    private const string TaskTwo = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const string TaskThree = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000003";

    [Fact]
    public async Task ConcurrentInitialAcquisitionsCreateOneOwnerAndGateOrdinaryWritesDuringRecovery()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        const string deployment = "initial";
        var now = MiniStackTable.Now;
        var validUntil = now.AddMinutes(10);
        Assert.Null(await store.ReadLeaseAsync(deployment, cancellation));

        table.Wire.Clear();
        table.Wire.HoldNextTwoTransactions();
        var attempts = await Task.WhenAll(
            store.TryAcquireLeaseAsync(deployment, "first", MiniStackTable.FirstTask, 1, now, validUntil, cancellation),
            store.TryAcquireLeaseAsync(deployment, "second", TaskTwo, 1, now, validUntil, cancellation));
        var winner = Assert.Single(attempts, attempt => attempt.Outcome == LifecycleWriteOutcome.Committed);
        var loser = Assert.Single(attempts, attempt => attempt.Outcome == LifecycleWriteOutcome.Conflict);
        Assert.Null(loser.Lease);
        var lease = Assert.IsType<ControllerLease>(winner.Lease);
        Assert.Equal(1, lease.Epoch);
        Assert.Equal(1, lease.Revision);
        Assert.Equal(validUntil, lease.ValidUntil);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, lease.Mode);
        Assert.Null(lease.PreviousOwner);
        Assert.Null(lease.RequiredFencingTaskArn);
        Assert.Equal(lease.TransitionId, winner.AttemptedTransitionId);
        Assert.Equal(lease, await store.ReadLeaseAsync(deployment, cancellation));

        var transactions = table.Wire.Requests.Where(call => call.Operation == "TransactWriteItems").ToArray();
        Assert.Equal(2, transactions.Length);
        Assert.All(transactions, transaction =>
        {
            var action = Assert.Single(transaction.Request.GetProperty("TransactItems").EnumerateArray()).GetProperty("Put");
            Assert.Equal("attribute_not_exists(PK)", action.GetProperty("ConditionExpression").GetString());
            Assert.Equal("LEASE", action.GetProperty("Item").GetProperty("SK").GetProperty("S").GetString());
        });
        Assert.Contains(transactions, transaction =>
            transaction.Request.GetProperty("ClientRequestToken").GetString() == lease.TransitionId.ToString("D"));

        var profile = new LifecycleProfile(deployment, "recovery");
        var rebuild = await store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, rebuild.Outcome);
        Assert.Equal(0, rebuild.Head?.UnfinishedCount);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteEffectAsync(profile, lease.Fence, Guid.NewGuid(),
            new EffectWrite("forbidden", null, EffectState.Planned, "{}"), cancellation));
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteRecordsAsync(
            lease.Fence, Guid.NewGuid(), [new(LifecycleKey.Session(profile), null, "{}")], cancellation));
        Assert.Null(await store.ReadEffectAsync(profile, "forbidden", cancellation));
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Session(profile), cancellation));
    }

    [Fact]
    public async Task HeartbeatAndCleanReleasePreserveMonotonicOwnerEpochsAndExactTaskFencing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var now = MiniStackTable.Now;
        var active = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(table.Lease.Deployment, cancellation));
        Assert.Equal(ControllerLeaseMode.Active, active.Mode);
        Assert.Equal(table.Lease, active.Fence);
        var session = LifecycleKey.Session(table.Profile("active"));

        var incorrectTask = active.Fence with { OwnerTaskArn = TaskTwo };
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteRecordsAsync(
            incorrectTask, Guid.NewGuid(), [new(session, null, "wrong-task")], cancellation));

        var renewal = await store.RenewLeaseAsync(active, now, active.ValidUntil.AddMinutes(3), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, renewal.Outcome);
        var extended = Assert.IsType<ControllerLease>(renewal.Lease);
        Assert.Equal(active.Epoch, extended.Epoch);
        Assert.Equal(active.Revision + 1, extended.Revision);
        Assert.Equal(active.ValidUntil.AddMinutes(3), extended.ValidUntil);
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await store.ReleaseLeaseAsync(active, now, cancellation)).Outcome);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, (await store.TryAcquireLeaseAsync(active.Deployment, "next", TaskTwo,
            active.WriterProtocol, now, extended.ValidUntil.AddMinutes(3), cancellation)).Outcome);

        var release = await store.ReleaseLeaseAsync(extended, now, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, release.Outcome);
        var released = Assert.IsType<ControllerLease>(release.Lease);
        Assert.Equal(ControllerLeaseMode.Released, released.Mode);
        Assert.Equal(extended.Epoch, released.Epoch);
        Assert.Equal(extended.ValidUntil, released.ValidUntil);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteRecordsAsync(
            extended.Fence, Guid.NewGuid(), [new(session, null, "after-release")], cancellation));

        var handoff = await store.TryAcquireLeaseAsync(released.Deployment, "next", TaskTwo, released.WriterProtocol,
            now, now.AddMinutes(20), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, handoff.Outcome);
        var next = Assert.IsType<ControllerLease>(handoff.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, next.Mode);
        Assert.Equal(released.Epoch + 1, next.Epoch);
        Assert.Equal(released.Revision + 1, next.Revision);
        Assert.Equal(new ControllerLeasePredecessor(released.OwnerId, released.OwnerTaskArn, released.Epoch,
            released.WriterProtocol, true), next.PreviousOwner);
        Assert.Null(next.RequiredFencingTaskArn);
        Assert.Equal(next, await store.ReadLeaseAsync(next.Deployment, cancellation));
        Assert.Null(await store.ReadRecordAsync(session, cancellation));
    }

    [Fact]
    public async Task ReplacingAnExpiredRecoveryOwnerRetainsLastActiveWriterAndRejectsStaleHeartbeat()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var originalStore = table.CreateStore();
        var active = Assert.IsType<ControllerLease>(await originalStore.ReadLeaseAsync(table.Lease.Deployment, cancellation));
        var firstTakeoverStore = table.CreateStore(active.ValidUntil);
        var nextDeadline = active.ValidUntil.AddMinutes(5);

        var early = await firstTakeoverStore.TryAcquireLeaseAsync(active.Deployment, "recovery-1", TaskTwo,
            active.WriterProtocol, active.ValidUntil.AddTicks(-1), nextDeadline, cancellation);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, early.Outcome);
        Assert.Equal(active, early.Lease);

        var acquisition = await firstTakeoverStore.TryAcquireLeaseAsync(active.Deployment, "recovery-1", TaskTwo,
            active.WriterProtocol, active.ValidUntil, nextDeadline, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquisition.Outcome);
        var recovery = Assert.IsType<ControllerLease>(acquisition.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, recovery.Mode);
        Assert.Equal(active.OwnerTaskArn, recovery.RequiredFencingTaskArn);
        Assert.Equal(active.OwnerTaskArn, recovery.PreviousOwner?.OwnerTaskArn);
        Assert.False(recovery.PreviousOwner?.CleanRelease);

        var later = table.CreateStore(nextDeadline);
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await later.RenewLeaseAsync(recovery, nextDeadline,
            nextDeadline.AddMinutes(10), cancellation)).Outcome);
        var replacement = await later.TryAcquireLeaseAsync(active.Deployment, "recovery-2", TaskThree, active.WriterProtocol,
            nextDeadline, nextDeadline.AddMinutes(10), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, replacement.Outcome);
        var current = Assert.IsType<ControllerLease>(replacement.Lease);
        Assert.Equal(recovery.Epoch + 1, current.Epoch);
        Assert.Equal(recovery.OwnerTaskArn, current.PreviousOwner?.OwnerTaskArn);
        Assert.Equal(active.OwnerTaskArn, current.RequiredFencingTaskArn);
        Assert.Equal(current, await later.ReadLeaseAsync(active.Deployment, cancellation));

        var stale = await originalStore.WriteRecordsAsync(active.Fence, Guid.NewGuid(),
            [new(LifecycleKey.Session(table.Profile("stale-owner")), null, "{}")], cancellation);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, stale);
    }

    [Fact]
    public async Task HeartbeatAndTakeoverCannotBothCommitAgainstTheSameLeaseRevision()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var lease = Assert.IsType<ControllerLease>(
            await table.CreateStore().ReadLeaseAsync(table.Lease.Deployment, cancellation));
        var justBeforeExpiry = lease.ValidUntil.AddMilliseconds(-1);
        var oldStore = table.CreateStore(justBeforeExpiry);
        var successorStore = table.CreateStore(lease.ValidUntil);
        var nextDeadline = lease.ValidUntil.AddMinutes(10);

        table.Wire.Clear();
        table.Wire.HoldNextTwoTransactions();
        var attempts = await Task.WhenAll(
            oldStore.RenewLeaseAsync(lease, justBeforeExpiry, nextDeadline, cancellation),
            successorStore.TryAcquireLeaseAsync(lease.Deployment, "takeover", TaskTwo, lease.WriterProtocol,
                lease.ValidUntil, nextDeadline, cancellation));
        var winner = Assert.Single(attempts, attempt => attempt.Outcome == LifecycleWriteOutcome.Committed);
        Assert.Single(attempts, attempt => attempt.Outcome == LifecycleWriteOutcome.Conflict);
        var current = Assert.IsType<ControllerLease>(winner.Lease);
        Assert.Equal(lease.Revision + 1, current.Revision);
        Assert.Equal(current, await successorStore.ReadLeaseAsync(lease.Deployment, cancellation));
        Assert.Equal(nextDeadline, current.ValidUntil);

        var actions = table.Wire.Requests.Where(call => call.Operation == "TransactWriteItems")
            .Select(call => Assert.Single(call.Request.GetProperty("TransactItems").EnumerateArray()).GetProperty("Put"))
            .ToArray();
        Assert.Equal(2, actions.Length);
        Assert.All(actions, put =>
            Assert.Equal("1", put.GetProperty("ExpressionAttributeValues").GetProperty(":revision").GetProperty("N").GetString()));
    }

    [Fact]
    public async Task LostLeaseAcquisitionResponseCanBeReconciledThroughExactDurableTransition()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var now = MiniStackTable.Now;

        table.Wire.DropNextSuccessfulTransactionResponse();
        var uncertain = await store.TryAcquireLeaseAsync("ambiguous", "only-owner", TaskTwo, 1, now, now.AddMinutes(5), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Indeterminate, uncertain.Outcome);
        Assert.Null(uncertain.Lease);
        Assert.NotNull(uncertain.AttemptedTransitionId);
        Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems");

        var observed = Assert.IsType<ControllerLease>(await table.CreateStore().ReadLeaseAsync("ambiguous", cancellation));
        Assert.Equal(uncertain.AttemptedTransitionId, observed.TransitionId);
        Assert.Equal("only-owner", observed.OwnerId);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, observed.Mode);
        Assert.Equal(1, observed.Epoch);
        Assert.All(table.Wire.Requests.Where(call => call.Operation == "GetItem"),
            call => Assert.True(call.Request.GetProperty("ConsistentRead").GetBoolean()));
    }

    [Fact]
    public async Task EffectRosterUsesStrongCatalogPagesAndSurvivesOrdinaryProfileEnrichment()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var active = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(table.Lease.Deployment, cancellation));
        var retained = table.Profile("removed-from-configuration");
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.EnsureEffectProfileAsync(active, retained, cancellation));
        var key = LifecycleKey.Profile(retained);
        var enrolled = await table.ReadAsync(key, cancellation);
        Assert.Equal(retained.ProfileId, enrolled["effectProfileId"].S);
        Assert.DoesNotContain("expiresAt", enrolled);

        const string metadata = """{"catalogState":"draining"}""";
        var current = Enumerable.Range(0, 34).Select(index => table.Profile(FormattableString.Invariant($"current-{index:D2}"))).ToArray();
        var writes = current.Select(profile => new LifecycleRecordWrite(LifecycleKey.Profile(profile), null, "{}"))
            .Append(new LifecycleRecordWrite(key, 1, metadata)).ToArray();
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.WriteRecordsAsync(active.Fence, Guid.NewGuid(), writes, cancellation));

        var existing = await table.ReadAsync(key, cancellation);
        Assert.Equal(enrolled["effectProfileId"].S, existing["effectProfileId"].S);
        Assert.Equal(metadata, existing["payload"].S);

        // A predecessor from before explicit registration still owns the exact catalog key.
        var legacy = await table.ReadAsync(LifecycleKey.Profile(current[0]), cancellation);
        Assert.True(legacy.Remove("effectProfileId"));
        await table.Client.PutItemAsync(table.Name, legacy, cancellation);

        table.Wire.Clear();
        var roster = await store.ReadEffectProfilesAsync(active.Deployment, cancellation);
        Assert.Equal(current.Append(retained), roster);
        Assert.Equal(2, table.Wire.Requests.Count);
        Assert.All(table.Wire.Requests, query =>
        {
            Assert.Equal("PROFILE#", query.QueryPrefix);
            Assert.True(query.Request.GetProperty("ConsistentRead").GetBoolean());
            Assert.Equal(32, query.Request.GetProperty("Limit").GetInt32());
        });
    }

    [Fact]
    public async Task CatalogRegistrationReconcilesContentionAndLostCommitWithoutReplacingIdentity()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var active = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(table.Lease.Deployment, cancellation));
        var contested = table.Profile("contested");
        table.Wire.Clear();
        table.Wire.HoldNextTwoTransactions();

        var attempts = await Task.WhenAll(
            store.EnsureEffectProfileAsync(active, contested, cancellation),
            store.EnsureEffectProfileAsync(active, contested, cancellation));
        Assert.All(attempts, attempt => Assert.Equal(LifecycleWriteOutcome.Committed, attempt));
        Assert.Equal(2, table.Wire.Requests.Count(call => call.Operation == "TransactWriteItems"));
        Assert.Equal(3, table.Wire.Requests.Count(call => call.Operation == "GetItem"));
        Assert.Equal("1", (await table.ReadAsync(LifecycleKey.Profile(contested), cancellation))["revision"].N);

        var lost = table.Profile("lost");
        table.Wire.Clear();
        table.Wire.DropNextSuccessfulTransactionResponse();
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.EnsureEffectProfileAsync(active, lost, cancellation));
        Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems");
        Assert.Equal(2, table.Wire.Requests.Count(call => call.Operation == "GetItem"));
        Assert.Equal([contested, lost], await store.ReadEffectProfilesAsync(active.Deployment, cancellation));
    }
}
