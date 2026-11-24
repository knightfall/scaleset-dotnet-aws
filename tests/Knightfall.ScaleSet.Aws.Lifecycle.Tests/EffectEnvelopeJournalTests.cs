using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class EffectEnvelopeJournalTests
{
    private static readonly DateTimeOffset Now = MiniStackTable.Now;

    [Fact]
    public async Task SendPermissionRequiresCommittedExactEnvelopeAndAttempt()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("typed-journal");
        var lease = await ReadyLeaseAsync(store, profile, cancellation);
        var journal = new DynamoDbEffectJournal(store, new FixedClock());
        var envelope = Envelope(profile, 17, generation: 1);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await journal.PlanAsync(lease, envelope, Guid.NewGuid(), cancellation));
        var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(EffectState.Planned, planned.Entry.State);
        Assert.Null(planned.Entry.Attempt);

        var changed = planned with { Entry = EffectJournalEntry.Plan(Envelope(profile, 19, generation: 2)) };
        table.Wire.Clear();
        var rejected = await journal.BeginAsync(lease, changed, Guid.NewGuid(), Guid.NewGuid(),
            Now, Now.AddMinutes(5), TimeSpan.FromSeconds(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, rejected.Outcome);
        Assert.Null(rejected.Permission);
        Assert.DoesNotContain(table.Wire.Requests, call => call.Operation == "TransactWriteItems");

        var transition = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var begun = await journal.BeginAsync(lease, planned, transition, attempt, Now, Now.AddMinutes(5),
            TimeSpan.FromSeconds(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, begun.Outcome);
        var permission = Assert.IsType<EffectSendAuthorization>(begun.Permission);

        var durable = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(transition, durable.TransitionId);
        Assert.Equal(EffectState.Executing, durable.Entry.State);
        Assert.Equal(durable.Revision, permission.Revision);
        Assert.Equal(durable.Entry.Attempt, permission.Attempt);
        Assert.Equal(attempt, permission.Attempt.Id);
        Assert.Equal(Now.AddMinutes(5), permission.Attempt.Deadline);
        Assert.True(durable.Entry.Envelope.Matches(permission.Envelope));
        Assert.True(envelope.Matches(permission.Envelope));
        Assert.Equal(1, durable.Entry.Envelope.Generation);

        var application = new EffectEvidence(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, Now);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await journal.ResolveAsync(lease, durable, Guid.NewGuid(), application, cancellation));
        var settled = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(EffectState.Settled, settled.Entry.State);
        Assert.Equal(EffectResolutionKind.Applied, settled.Entry.Resolution?.Kind);
        Assert.Equal(0, (await store.ReadEffectIndexAsync(profile, cancellation))?.UnfinishedCount);
    }

    [Fact]
    public async Task LostBeginResponseKeepsDurableAttemptWithoutPermissionOrRetryOfTheOldTransition()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("lost-typed-begin");
        var lease = await ReadyLeaseAsync(store, profile, cancellation);
        var journal = new DynamoDbEffectJournal(store, new FixedClock());
        var envelope = Envelope(profile, 17, generation: 1);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await journal.PlanAsync(lease, envelope, Guid.NewGuid(), cancellation));
        var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));

        table.Wire.Clear();
        table.Wire.DropNextSuccessfulTransactionResponse();
        var transition = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var lost = await journal.BeginAsync(lease, planned, transition, attempt, Now, Now.AddMinutes(5),
            TimeSpan.FromSeconds(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Indeterminate, lost.Outcome);
        Assert.Null(lost.Permission);
        Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems");

        var restarted = new DynamoDbEffectJournal(store, new FixedClock());
        var uncertain = Assert.IsType<EffectJournalSnapshot>(await restarted.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(transition, uncertain.TransitionId);
        Assert.Equal(attempt, uncertain.Entry.Attempt?.Id);
        Assert.Equal(EffectState.Executing, uncertain.Entry.State);
        var second = await restarted.BeginAsync(lease, planned, Guid.NewGuid(), Guid.NewGuid(), Now,
            Now.AddMinutes(5), TimeSpan.FromSeconds(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, second.Outcome);
        Assert.Null(second.Permission);
        Assert.Single(table.Wire.Requests, call => call.Operation == "TransactWriteItems");

        var cancellationEvidence = new EffectEvidence(EffectResolutionKind.CanceledBeforeSend, envelope.Sha256, envelope.Target, Now);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => restarted.ResolveAsync(lease, uncertain, Guid.NewGuid(), cancellationEvidence, cancellation));
        var retained = Assert.IsType<EffectJournalSnapshot>(await restarted.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(transition, retained.TransitionId);
        Assert.Equal(EffectState.Executing, retained.Entry.State);
        Assert.Equal(1, (await store.ReadEffectIndexAsync(profile, cancellation))?.UnfinishedCount);
    }

    [Fact]
    public async Task PublicResolutionRefusesRecoveryLeaseBeforeReadingOrWritingTheOperation()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var profile = table.Profile("internal-recovery-only");
        var lease = await ReadyLeaseAsync(store, profile, cancellation);
        var journal = new DynamoDbEffectJournal(store, new FixedClock());
        var envelope = Envelope(profile, 17, generation: 1);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await journal.PlanAsync(lease, envelope, Guid.NewGuid(), cancellation));
        var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        var begun = await journal.BeginAsync(lease, planned, Guid.NewGuid(), Guid.NewGuid(), Now,
            Now.AddMinutes(5), TimeSpan.FromSeconds(1), cancellation);
        Assert.NotNull(begun.Permission);
        var current = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        var evidence = new EffectEvidence(EffectResolutionKind.Absent, envelope.Sha256, envelope.Target,
            Now.AddMinutes(6), EarliestObservedAt: Now.AddMinutes(5));

        table.Wire.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ResolveAsync(
            lease with { Mode = ControllerLeaseMode.RecoveryOnly }, current, Guid.NewGuid(), evidence, cancellation));
        Assert.Empty(table.Wire.Requests);
    }

    private static async Task<ControllerLease> ReadyLeaseAsync(
        DynamoDbLifecycleStore store, LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var lease = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(profile.Deployment, cancellationToken));
        var rebuild = await store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, rebuild.Outcome);
        Assert.Equal(EffectIndexState.Ready, rebuild.Head?.State);
        return lease;
    }

    private static EffectEnvelope Envelope(LifecycleProfile profile, int taskRevision, long generation) =>
        EffectEnvelope.Create(profile, "launch-1", generation,
            new FargateLaunchEffectRequest(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                "arn:aws:ecs:us-east-1:123456789012:cluster/runners",
                FormattableString.Invariant($"arn:aws:ecs:us-east-1:123456789012:task-definition/runner:{taskRevision}"),
                ["subnet-0123456789abcdef0"], ["sg-0123456789abcdef0"],
                "arn:aws:iam::123456789012:role/bootstrap",
                "arn:aws:iam::123456789012:role/execution",
                "runner", Guid.Parse("10000000-0000-0000-0000-000000000002")),
            Now, Now.AddMinutes(50));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => 0;
    }
}
