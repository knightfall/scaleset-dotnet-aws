using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    internal async Task<EffectRebuildPass> TryReuseEffectIndexAsync(
        LifecycleProfile profile, ControllerLease expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        RequireProfileLease(profile, expected.Fence);
        if (expected.Mode != ControllerLeaseMode.RecoveryOnly ||
            await ReadLeaseAsync(expected.Deployment, cancellationToken).ConfigureAwait(false) != expected)
        {
            return new EffectRebuildPass(null, LifecycleWriteOutcome.LeaseRejected, 0);
        }

        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Ready })
        {
            return new EffectRebuildPass(head, LifecycleWriteOutcome.RebuildRequired, 0);
        }
        if (MatchesLease(head, expected.Fence))
        {
            return new EffectRebuildPass(head, LifecycleWriteOutcome.Committed, 0);
        }

        if (expected.WriterProtocol != ControllerLease.CurrentWriterProtocol ||
            expected.PreviousOwner is not { WriterProtocol: ControllerLease.CurrentWriterProtocol } previous ||
            head.WriterProtocol != previous.WriterProtocol || head.OwnerEpoch != previous.Epoch)
        {
            return new EffectRebuildPass(head, LifecycleWriteOutcome.RebuildRequired, 0);
        }

        var checkpoint = await ReadRebuildAsync(profile, cancellationToken).ConfigureAwait(false);
        if (checkpoint is not { Completed: true } || checkpoint.Generation != head.Generation ||
            checkpoint.OwnerEpoch != previous.Epoch)
        {
            return new EffectRebuildPass(head, LifecycleWriteOutcome.RebuildRequired, 0);
        }

        var transition = Guid.NewGuid();
        var nextHead = head with { Revision = NextRevision(head.Revision), OwnerEpoch = expected.Epoch };
        var nextCheckpoint = checkpoint with { Revision = NextRevision(checkpoint.Revision), OwnerEpoch = expected.Epoch };
        var outcome = await WriteAsync(
        [
            LeaseCondition(expected.Fence, LeaseAccess.Recovery),
            WriteIndex(profile, nextHead, head.Revision, transition),
            WriteRebuild(profile, nextCheckpoint, checkpoint.Revision, transition),
        ], transition, cancellationToken).ConfigureAwait(false);
        return new EffectRebuildPass(outcome == LifecycleWriteOutcome.Committed ? nextHead : head, outcome, 0);
    }

    public async Task<EffectRebuildPass> AdvanceEffectRebuildAsync(
        LifecycleProfile profile, DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        RequireProfileLease(profile, lease);
        using var telemetry = DynamoDbDiagnostics.Start("effect-rebuild", cancellationToken);
        var result = await AdvanceEffectRebuildCoreAsync(profile, lease, cancellationToken).ConfigureAwait(false);
        telemetry.Complete(result.Outcome);
        DynamoDbDiagnostics.RecordRebuild(result);
        return result;
    }

    private async Task<EffectRebuildPass> AdvanceEffectRebuildCoreAsync(
        LifecycleProfile profile, DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is { State: EffectIndexState.Ready } && MatchesLease(head, lease))
        {
            return new EffectRebuildPass(head, LifecycleWriteOutcome.Committed, 0);
        }

        var checkpoint = await ReadRebuildAsync(profile, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Rebuilding } || !MatchesLease(head, lease))
        {
            // Without proven previous-writer continuity, only a new, closed generation can publish a count.
            var replacement = new EffectIndexHead(NextRevision(head?.Revision), Guid.NewGuid(), lease.Epoch, lease.WriterProtocol,
                EffectIndexState.Rebuilding, null);
            var start = new RebuildCheckpoint(NextRevision(checkpoint?.Revision), replacement.Generation, lease.Epoch, null, 0, false);
            var transition = Guid.NewGuid();
            var outcome = await WriteAsync(
            [
                LeaseCondition(lease, LeaseAccess.ActiveOrRecovery),
                WriteIndex(profile, replacement, head?.Revision, transition),
                WriteRebuild(profile, start, checkpoint?.Revision, transition),
            ], transition, cancellationToken).ConfigureAwait(false);
            if (outcome != LifecycleWriteOutcome.Committed)
            {
                return new EffectRebuildPass(head, outcome, 0);
            }

            head = replacement;
            checkpoint = start;
        }

        if (checkpoint is null || checkpoint.Generation != head.Generation || checkpoint.OwnerEpoch != head.OwnerEpoch ||
            checkpoint.Completed)
        {
            var current = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
            if (current != head)
            {
                bool ready = current is { State: EffectIndexState.Ready } && MatchesLease(current, lease);
                return new EffectRebuildPass(current, ready ? LifecycleWriteOutcome.Committed : LifecycleWriteOutcome.Conflict, 0);
            }
            throw InvalidRecord();
        }

        for (int pages = 1; pages <= HistoryPagesPerPass; pages++)
        {
            var result = await AdvancePageAsync(profile, lease, head, checkpoint, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != LifecycleWriteOutcome.Committed || result.Checkpoint.Completed)
            {
                return new EffectRebuildPass(result.Head, result.Outcome, pages);
            }

            checkpoint = result.Checkpoint;
        }
        return new EffectRebuildPass(head, LifecycleWriteOutcome.Committed, HistoryPagesPerPass);
    }

    private async Task<RebuildPageResult> AdvancePageAsync(
        LifecycleProfile profile,
        DeploymentLeaseFence lease,
        EffectIndexHead head,
        RebuildCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var page = await ReadBasePageAsync(profile.PartitionKey, OperationPrefix, checkpoint.After, cancellationToken).ConfigureAwait(false);
        var transition = Guid.NewGuid();
        var actions = new List<TransactionAction>(page.Items.Count + 3) { LeaseCondition(lease, LeaseAccess.ActiveOrRecovery) };
        long count = checkpoint.PartialCount;
        foreach (var item in page.Items)
        {
            var effect = DecodeEffect(profile, item);
            if (effect.IsUnfinished)
            {
                actions.Add(CreateMarker(profile, head.Generation, effect.IntentId, transition));
                count = checked(count + 1);
            }
        }

        bool completed = page.Continuation is null;
        var nextCheckpoint = checkpoint with
        {
            Revision = NextRevision(checkpoint.Revision),
            After = page.Continuation,
            PartialCount = count,
            Completed = completed,
        };
        actions.Add(WriteRebuild(profile, nextCheckpoint, checkpoint.Revision, transition));

        var nextHead = head;
        if (completed)
        {
            nextHead = head with { Revision = NextRevision(head.Revision), State = EffectIndexState.Ready, UnfinishedCount = count };
            actions.Add(WriteIndex(profile, nextHead, head.Revision, transition));
        }

        var outcome = await WriteAsync(actions, transition, cancellationToken).ConfigureAwait(false);
        return outcome == LifecycleWriteOutcome.Committed
            ? new RebuildPageResult(nextHead, nextCheckpoint, outcome)
            : new RebuildPageResult(head, checkpoint, outcome);
    }

    private async Task<RebuildCheckpoint?> ReadRebuildAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.EffectRebuild(profile), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        CheckNoExpiration(item);
        string? after = item.ContainsKey("after") ? Text(item, "after") : null;
        long count = Number(item, "partialCount");
        long epoch = Number(item, "ownerEpoch");
        string state = Text(item, "rebuildState");
        if (epoch <= 0 || count < 0 || state is not ("building" or "complete") ||
            after?.StartsWith(OperationPrefix, StringComparison.Ordinal) == false ||
            (state == "complete" && after is not null))
        {
            throw InvalidRecord();
        }

        return new RebuildCheckpoint(Revision(item), GuidValue(item, "indexGeneration"), epoch, after, count, state == "complete");
    }

    private TransactionAction WriteRebuild(
        LifecycleProfile profile, RebuildCheckpoint checkpoint, long? expectedRevision, Guid transitionId)
    {
        var key = LifecycleKey.EffectRebuild(profile);
        var item = Record(key, profile.Deployment, checkpoint.Revision, transitionId);
        item.Add("indexGeneration", StringValue(checkpoint.Generation.ToString("D")));
        item.Add("ownerEpoch", NumberValue(checkpoint.OwnerEpoch));
        item.Add("partialCount", NumberValue(checkpoint.PartialCount));
        item.Add("rebuildState", StringValue(checkpoint.Completed ? "complete" : "building"));
        if (checkpoint.After is { } after)
        {
            item.Add("after", StringValue(after));
        }
        return CompareAndPut(key, item, expectedRevision, profile.Deployment);
    }

    private sealed record RebuildCheckpoint(
        long Revision,
        Guid Generation,
        long OwnerEpoch,
        string? After,
        long PartialCount,
        bool Completed);

    private sealed record RebuildPageResult(
        EffectIndexHead Head,
        RebuildCheckpoint Checkpoint,
        LifecycleWriteOutcome Outcome);
}
