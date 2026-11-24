using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class SessionStoreTests
{
    private const string Scope = "github.com/owner/repository";
    private const long SetId = 9_000_000_004;
    private const string SecondTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";

    [Fact]
    public async Task OpenAndCloseIntentsIssueOnePermitAndReplacementKeepsExactOldCheckpoint()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var binding = new SessionBinding(table.Profile("clean-replacement"), Scope, SetId);
        var now = MiniStackTable.Now;
        await ReadyAsync(store, binding.Profile, table.Lease, cancellation);

        var begun = await store.BeginSessionOpenAsync(binding, table.Lease, null, "fixture-old", now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, begun.Outcome);
        var preparing = Assert.IsType<SessionRecord>(begun.Session);
        Assert.Equal(SessionPhase.OpenIntent, preparing.Phase);
        Assert.Null(preparing.SessionId);
        Assert.Matches("^[0-9a-f]{64}$", preparing.RequestSha256);
        Assert.Equal(preparing.RequestSha256, (await table.ReadAsync(LifecycleKey.Session(binding.Profile), cancellation))["requestSha256"].S);
        Assert.Equal(preparing, await store.ReadSessionAsync(binding.Profile, cancellation));
        Assert.Equal([binding.Profile], await store.ReadEffectProfilesAsync(binding.Profile.Deployment, cancellation));

        var permission = Assert.IsType<SessionSendAuthorization>(begun.Permission);
        var actual = Guid.NewGuid();
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await store.ConfirmSessionOpenedAsync(permission, actual, cancellation)).Outcome);
        Assert.True(permission.TryConsume(out var remaining));
        Assert.InRange(remaining, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.False(permission.TryConsume(out _));

        var first = await store.ConfirmSessionOpenedAsync(permission, actual, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, first.Outcome);
        var active = Assert.IsType<SessionRecord>(first.Session);
        var fence = Assert.IsType<OwnedSessionFence>(active.Fence);
        Assert.Equal(actual, active.SessionId);
        Assert.Equal(new SessionIdentity(binding.Profile, Scope, SetId, actual), fence.Identity);
        Assert.Equal(preparing.Incarnation, fence.Incarnation);
        Assert.Equal(preparing.RequestSha256, active.RequestSha256);
        Assert.True(await store.HasSessionOwnershipAsync(fence, cancellation));
        var checkpoint = LifecycleKey.Checkpoint(binding.Profile, actual);
        var checkpointWrite = new LifecycleRecordWrite(checkpoint, null, "old-cursor");
        var transition = Guid.NewGuid();
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.WriteSessionRecordsAsync(fence, transition, [checkpointWrite], cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(binding.Profile, table.Lease, Guid.NewGuid(),
            new EffectWrite("another-effect", null, EffectState.Planned, "{}"), cancellation));

        var closing = await store.BeginSessionCloseAsync(active, table.Lease, SessionCloseKind.OwnedClient, now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, closing.Outcome);
        Assert.Equal(SessionPhase.Closing, closing.Session?.Phase);
        Assert.Equal(actual, closing.Session?.SessionId);
        Assert.NotEqual(active.RequestSha256, closing.Session?.RequestSha256);
        var closePermission = Assert.IsType<SessionSendAuthorization>(closing.Permission);
        Assert.True(closePermission.TryConsume(out _));
        Assert.False(await store.HasSessionOwnershipAsync(fence, cancellation));
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteSessionRecordsAsync(fence, Guid.NewGuid(),
            [checkpointWrite with { ExpectedRevision = 1, Payload = "late-ack" }], cancellation));

        table.Wire.DropNextSuccessfulTransactionResponse();
        var retired = await store.ConfirmSessionRetiredAsync(closePermission, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, retired.Outcome);
        var retiredRecord = Assert.IsType<SessionRecord>(retired.Session);
        Assert.Equal(SessionPhase.Retired, retiredRecord.Phase);
        Assert.Equal(closing.Session?.RequestSha256, retiredRecord.RequestSha256);
        Assert.Equal(retiredRecord, (await store.ConfirmSessionRetiredAsync(closePermission, cancellation)).Session);

        Assert.Equal(LifecycleWriteOutcome.RebuildRequired, (await store.BeginSessionOpenAsync(
            binding, table.Lease, retiredRecord, "fixture-new", now, now.AddMinutes(1), cancellation)).Outcome);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(binding.Profile, table.Lease, Guid.NewGuid(),
            new EffectWrite("another-effect", 1, EffectState.Settled, "{}"), cancellation));
        var next = await store.BeginSessionOpenAsync(binding, table.Lease, retiredRecord, "fixture-new", now, now.AddMinutes(1), cancellation);
        var newPermit = Assert.IsType<SessionSendAuthorization>(next.Permission);
        Assert.True(newPermit.TryConsume(out _));
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await store.ConfirmSessionOpenedAsync(newPermit, actual, cancellation)).Outcome);
        var replacementId = Guid.NewGuid();
        var replacement = await store.ConfirmSessionOpenedAsync(newPermit, replacementId, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, replacement.Outcome);
        Assert.NotEqual(preparing.Incarnation, replacement.Session?.Incarnation);
        Assert.Equal(actual, replacement.Session?.PriorSessionId);
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Checkpoint(binding.Profile, replacementId), cancellation));
        Assert.Equal(new LifecycleRecord(checkpoint, 1, transition, "old-cursor", null),
            await store.ReadRecordAsync(checkpoint, cancellation));
    }

    [Fact]
    public async Task ChangingAnIntentRequestFieldInvalidatesItsDurableDigest()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var binding = new SessionBinding(table.Profile("digest"), Scope, SetId);
        var now = MiniStackTable.Now;
        await ReadyAsync(store, binding.Profile, table.Lease, cancellation);
        var begun = await store.BeginSessionOpenAsync(binding, table.Lease, null, "original-name", now, now.AddMinutes(1), cancellation);
        var intent = Assert.IsType<SessionRecord>(begun.Session);
        Assert.Equal(intent, await store.ReadSessionAsync(binding.Profile, cancellation));
        var persisted = await table.ReadAsync(LifecycleKey.Session(binding.Profile), cancellation);
        persisted["ownerName"] = new AttributeValue("different-name");
        await table.Client.PutItemAsync(table.Name, persisted, cancellation);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadSessionAsync(binding.Profile, cancellation));
    }

    [Fact]
    public async Task AmbiguousOpenQuarantinesUnknownRemoteSessionAndPreventsAutomaticReplacement()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var binding = new SessionBinding(table.Profile("ambiguous-open"), Scope, SetId);
        var now = MiniStackTable.Now;
        Assert.Equal(LifecycleWriteOutcome.RebuildRequired, (await store.BeginSessionOpenAsync(
            binding, table.Lease, null, "fixture", now, now.AddMinutes(1), cancellation)).Outcome);
        Assert.Null(await store.ReadSessionAsync(binding.Profile, cancellation));

        await ReadyAsync(store, binding.Profile, table.Lease, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(binding.Profile, table.Lease, Guid.NewGuid(),
            new EffectWrite("unfinished", null, EffectState.Planned, "{}"), cancellation));
        var unresolved = await store.BeginSessionOpenAsync(binding, table.Lease, null, "fixture", now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.RebuildRequired, unresolved.Outcome);
        Assert.Null(unresolved.Permission);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteEffectAsync(binding.Profile, table.Lease, Guid.NewGuid(),
            new EffectWrite("unfinished", 1, EffectState.Settled, "{}"), cancellation));
        var tooLong = await store.BeginSessionOpenAsync(binding, table.Lease, null, "fixture", now, now.AddHours(2), cancellation);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, tooLong.Outcome);
        Assert.Null(tooLong.Permission);

        var opened = await store.BeginSessionOpenAsync(binding, table.Lease, null, "fixture", now, now.AddMinutes(1), cancellation);
        var permission = Assert.IsType<SessionSendAuthorization>(opened.Permission);
        Assert.True(permission.TryConsume(out _));
        var intent = Assert.IsType<SessionRecord>(await table.CreateStore().ReadSessionAsync(binding.Profile, cancellation));
        var failure = await store.QuarantineSessionAsync(intent, table.Lease, cancellation);
        var quarantined = Assert.IsType<SessionRecord>(failure.Session);
        Assert.Equal(SessionPhase.Quarantined, quarantined.Phase);
        Assert.Equal(intent.AttemptId, quarantined.AttemptId);
        Assert.Null(quarantined.SessionId);
        Assert.Null(quarantined.Fence);
        Assert.Equal(quarantined, (await store.QuarantineSessionAsync(intent, table.Lease, cancellation)).Session);

        var blocked = await store.BeginSessionOpenAsync(binding, table.Lease, quarantined, "again", now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, blocked.Outcome);
        Assert.Null(blocked.Permission);
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            (await store.ConfirmSessionOpenedAsync(permission, Guid.NewGuid(), cancellation)).Outcome);
        Assert.Equal(quarantined, await store.ReadSessionAsync(binding.Profile, cancellation));
    }

    [Fact]
    public async Task NewActiveEpochCanRetireTheExactPreviousSessionButCannotReplayAmbiguousClose()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var original = table.CreateStore();
        var binding = new SessionBinding(table.Profile("takeover"), Scope, SetId);
        var active = await OpenAsync(original, binding, table.Lease, cancellation);
        var oldFence = Assert.IsType<OwnedSessionFence>(active.Fence);
        var owner = Assert.IsType<ControllerLease>(await original.ReadLeaseAsync(binding.Profile.Deployment, cancellation));
        var now = owner.ValidUntil;
        var successorStore = table.CreateStore(now);
        var acquired = await successorStore.TryAcquireLeaseAsync(owner.Deployment, "controller-2", SecondTask,
            owner.WriterProtocol, now, now.AddHours(1), cancellation);
        var recovery = Assert.IsType<ControllerLease>(acquired.Lease);

        var recoveryClose = await successorStore.BeginSessionCloseAsync(active, recovery.Fence,
            SessionCloseKind.FencedRetirement, now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, recoveryClose.Outcome);
        Assert.Null(recoveryClose.Permission);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            (await successorStore.TryReuseEffectIndexAsync(binding.Profile, recovery, cancellation)).Outcome);
        var promoted = await successorStore.PromoteLeaseAsync(recovery, now, cancellation);
        var newLease = Assert.IsType<ControllerLease>(promoted.Lease);
        Assert.Equal(ControllerLeaseMode.Active, newLease.Mode);

        var wrongPath = await successorStore.BeginSessionCloseAsync(active, newLease.Fence,
            SessionCloseKind.OwnedClient, now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, wrongPath.Outcome);
        var request = await successorStore.BeginSessionCloseAsync(active, newLease.Fence,
            SessionCloseKind.FencedRetirement, now, now.AddMinutes(1), cancellation);
        var permission = Assert.IsType<SessionSendAuthorization>(request.Permission);
        var closing = Assert.IsType<SessionRecord>(request.Session);
        Assert.Equal(active.Identity, closing.Identity);
        Assert.Equal(newLease.Fence, closing.Owner);
        Assert.Equal(SessionCloseKind.FencedRetirement, closing.CloseKind);
        Assert.True(permission.TryConsume(out _));

        var staleWrite = await original.WriteSessionRecordsAsync(oldFence, Guid.NewGuid(),
            [new(LifecycleKey.Demand(binding.Profile), null, "stale")], cancellation);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, staleWrite);

        var quarantine = await successorStore.QuarantineSessionAsync(closing, newLease.Fence, cancellation);
        var quarantined = Assert.IsType<SessionRecord>(quarantine.Session);
        Assert.Equal(SessionPhase.Quarantined, quarantined.Phase);
        Assert.Equal(active.Identity, quarantined.Identity);
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await successorStore.BeginSessionCloseAsync(
            quarantined, newLease.Fence, SessionCloseKind.FencedRetirement, now, now.AddMinutes(1), cancellation)).Outcome);
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await successorStore.BeginSessionOpenAsync(
            binding, newLease.Fence, quarantined, "replacement", now, now.AddMinutes(1), cancellation)).Outcome);
    }

    [Fact]
    public async Task OrdinarySessionWritesFenceScopeAndAdmissionReadsAndUseDistinctWireRequestTokens()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var binding = new SessionBinding(table.Profile("state-cas"), Scope, SetId);
        var active = await OpenAsync(store, binding, table.Lease, cancellation);
        var fence = Assert.IsType<OwnedSessionFence>(active.Fence);
        var demand = LifecycleKey.Demand(binding.Profile);
        var admission = LifecycleKey.Admission(binding.Profile);
        var effects = LifecycleKey.Effects(binding.Profile);
        var checkpoint = LifecycleKey.Checkpoint(binding.Profile, fence.Identity.SessionId);

        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.WriteSessionRecordsAsync(fence, Guid.NewGuid(), [new(demand, null, "capacity")], cancellation));
        var invalidScope = fence with { Identity = fence.Identity with { ScopeIdentity = "github.com/owner/different" } };
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected,
            await store.WriteSessionRecordsAsync(invalidScope, Guid.NewGuid(), [new(admission, null, "{}")], cancellation));

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteSessionRecordsAsync(fence, Guid.NewGuid(),
            [new(LifecycleKey.Message(binding.Profile, Guid.NewGuid(), 0), null, "{}")], cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteRecordsAsync(table.Lease, Guid.NewGuid(),
            [new(LifecycleKey.Session(binding.Profile), active.Revision, "{}")], cancellation));
        var head = Assert.IsType<EffectIndexHead>(await store.ReadEffectIndexAsync(binding.Profile, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.WriteSessionRecordsAsync(fence, Guid.NewGuid(),
            [new(admission, null, "{}")], cancellation, [new(demand, 2)]));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.WriteSessionRecordsAsync(fence, Guid.NewGuid(),
            [new(admission, null, "{}")], cancellation, [new(effects, head.Revision + 1)]));
        Assert.Null(await store.ReadRecordAsync(admission, cancellation));

        table.Wire.Clear();
        table.Wire.DropNextSuccessfulTransactionResponse();
        var transition = Guid.NewGuid();
        LifecycleRecordWrite[] intended = [new(checkpoint, null, "{}"), new(admission, null, "reserve")];
        LifecycleRecordCondition[] conditions = [new(demand, 1), new(effects, head.Revision)];
        Assert.Equal(LifecycleWriteOutcome.Indeterminate,
            await store.WriteSessionRecordsAsync(fence, transition, intended, cancellation, conditions));
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            await store.WriteSessionRecordsAsync(fence, transition, intended, cancellation, conditions));

        var sends = table.Wire.Requests.Where(call => call.Operation == "TransactWriteItems").ToArray();
        Assert.Equal(2, sends.Length);
        var first = sends[0].Request.GetProperty("ClientRequestToken").GetString();
        var second = sends[1].Request.GetProperty("ClientRequestToken").GetString();
        Assert.NotEqual(first, second);
        Assert.NotEqual(transition.ToString("D"), first);
        Assert.All(sends, call => Assert.Equal(6, call.Request.GetProperty("TransactItems").GetArrayLength()));
        Assert.Equal(transition, (await store.ReadRecordAsync(checkpoint, cancellation))?.TransitionId);
        Assert.Equal(transition, (await store.ReadRecordAsync(admission, cancellation))?.TransitionId);

        var building = await table.ReadAsync(effects, cancellation);
        building["indexState"] = new AttributeValue("rebuilding");
        Assert.True(building.Remove("unfinishedCount"));
        await table.Client.PutItemAsync(table.Name, building, cancellation);
        Assert.Equal(LifecycleWriteOutcome.RebuildRequired, await store.WriteSessionRecordsAsync(fence, Guid.NewGuid(),
            [new(admission, 1, "closed")], cancellation));
        Assert.False(await store.HasSessionOwnershipAsync(fence, cancellation));
    }

    private static async Task<SessionRecord> OpenAsync(
        DynamoDbLifecycleStore store, SessionBinding binding, DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        var now = MiniStackTable.Now;
        await ReadyAsync(store, binding.Profile, lease, cancellationToken);
        var begin = await store.BeginSessionOpenAsync(binding, lease, null, "fixture", now, now.AddMinutes(1), cancellationToken);
        var permission = Assert.IsType<SessionSendAuthorization>(begin.Permission);
        Assert.True(permission.TryConsume(out _));
        var opened = await store.ConfirmSessionOpenedAsync(permission, Guid.NewGuid(), cancellationToken);
        return Assert.IsType<SessionRecord>(opened.Session);
    }

    private static async Task ReadyAsync(
        DynamoDbLifecycleStore store, LifecycleProfile profile, DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        var rebuilt = await store.AdvanceEffectRebuildAsync(profile, lease, cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, rebuilt.Outcome);
        Assert.Equal(EffectIndexState.Ready, rebuilt.Head?.State);
    }
}
