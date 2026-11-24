using System.Collections.Immutable;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record DeploymentLeaseFence(string Deployment, string OwnerId, long Epoch, int WriterProtocol);

public sealed record SettledHistoryRetention(DateTimeOffset SettledAt, TimeSpan Retention);

public sealed record LifecycleRecordWrite(
    LifecycleKey Key,
    long? ExpectedRevision,
    string Payload,
    SettledHistoryRetention? HistoryRetention = null);

public sealed record LifecycleRecord(
    LifecycleKey Key,
    long Revision,
    Guid TransitionId,
    string? Payload,
    DateTimeOffset? ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && expiresAt <= now;
}

public enum LifecycleWriteOutcome
{
    Committed = 0,
    Conflict = 1,
    LeaseRejected = 2,
    Indeterminate = 3,
    RebuildRequired = 4,
}

public enum EffectState
{
    Planned = 0,
    Executing = 1,
    Uncertain = 2,
    Settled = 3,
}

public sealed record EffectWrite(
    string IntentId,
    long? ExpectedRevision,
    EffectState State,
    string Payload,
    DateTimeOffset? DueAt = null);

public sealed record StoredEffect(
    string IntentId,
    long Revision,
    Guid TransitionId,
    EffectState State,
    string Payload,
    DateTimeOffset? DueAt)
{
    public bool IsUnfinished => State != EffectState.Settled;
}

public enum EffectIndexState
{
    Rebuilding = 0,
    Ready = 1,
}

public sealed record EffectIndexHead(
    long Revision,
    Guid Generation,
    long OwnerEpoch,
    int WriterProtocol,
    EffectIndexState State,
    long? UnfinishedCount);

public sealed record EffectRebuildPass(EffectIndexHead? Head, LifecycleWriteOutcome Outcome, int PagesRead);

public sealed record ActiveEffectCursor(Guid Generation, long IndexRevision, string AfterSortKey, long SeenCount);

public sealed record ActiveEffectPage(ImmutableArray<string> IntentIds, ActiveEffectCursor? Continuation);

public sealed record DueEffectHint(string PartitionKey, string SortKey, string Shard, DateTimeOffset DueAt);

public sealed record DueEffectHintPage(ImmutableArray<DueEffectHint> Hints, DueEffectHint? Continuation);
