using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record EffectJournalSnapshot(long Revision, Guid TransitionId, EffectJournalEntry Entry);

public sealed record EffectBeginOutcome(LifecycleWriteOutcome Outcome, EffectSendAuthorization? Permission);

/// <summary>Evidence that an attempt was durably begun under its recorded lease epoch.</summary>
/// <remarks>A caller must retain current ownership and respect the attempt deadline when sending.</remarks>
public sealed class EffectSendAuthorization
{
    private readonly TimeProvider _clock;
    private readonly long _beganAt;
    private readonly DateTimeOffset _upperUtcNow;
    private int _consumed;

    internal EffectSendAuthorization(
        EffectEnvelope envelope,
        EffectAttempt attempt,
        long revision,
        TimeProvider clock,
        long beganAt,
        DateTimeOffset upperUtcNow)
    {
        Envelope = envelope;
        Attempt = attempt;
        Revision = revision;
        _clock = clock;
        _beganAt = beganAt;
        _upperUtcNow = upperUtcNow;
    }

    public EffectEnvelope Envelope { get; }
    public EffectAttempt Attempt { get; }
    public long Revision { get; }

    public bool TryConsume(out TimeSpan remaining)
    {
        var estimate = _upperUtcNow + _clock.GetElapsedTime(_beganAt);
        var local = _clock.GetUtcNow();
        remaining = Attempt.Deadline - (estimate > local ? estimate : local);
        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0 && remaining > TimeSpan.Zero;
    }
}

public sealed class DynamoDbEffectJournal
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly TimeProvider _timeProvider;

    public DynamoDbEffectJournal(DynamoDbLifecycleStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<EffectJournalSnapshot?> ReadAsync(
        LifecycleProfile profile, string intentId, CancellationToken cancellationToken)
    {
        var stored = await _store.ReadEffectAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : new EffectJournalSnapshot(
            stored.Revision, stored.TransitionId, EffectJournalEntry.Read(profile, stored));
    }

    public async Task<LifecycleWriteOutcome> PlanAsync(
        ControllerLease lease, EffectEnvelope envelope, Guid transitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(envelope);
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            throw new InvalidOperationException("An active lease is required to plan an effect.");
        }
        ArgumentOutOfRangeException.ThrowIfEqual(transitionId, Guid.Empty);

        var registration = await _store.EnsureEffectProfileAsync(lease, envelope.Profile, cancellationToken)
            .ConfigureAwait(false);
        if (registration != LifecycleWriteOutcome.Committed)
        {
            return registration;
        }

        return await WriteAsync(lease, EffectJournalEntry.Plan(envelope), null, transitionId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EffectBeginOutcome> BeginAsync(
        ControllerLease lease,
        EffectJournalSnapshot expected,
        Guid transitionId,
        Guid attemptId,
        DateTimeOffset upperUtcNow,
        DateTimeOffset requestedDeadline,
        TimeSpan leaseReserve,
        CancellationToken cancellationToken)
    {
        long beganAt = _timeProvider.GetTimestamp();
        var current = await ReadCurrentAsync(lease, expected, transitionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new EffectBeginOutcome(LifecycleWriteOutcome.Conflict, null);
        }

        var now = UpperNow(upperUtcNow, beganAt);
        var entry = current.Entry.Begin(attemptId, lease.Fence.Epoch, now, requestedDeadline, LeaseDeadline(lease, now, leaseReserve));
        var outcome = await WriteAsync(lease, entry, current.Revision, transitionId, cancellationToken).ConfigureAwait(false);
        var attempt = entry.Attempt!;
        // A write can commit at the end of the window. The durable attempt remains unresolved.
        var permission = outcome == LifecycleWriteOutcome.Committed && !cancellationToken.IsCancellationRequested &&
            UpperNow(upperUtcNow, beganAt) < attempt.Deadline
            ? new EffectSendAuthorization(entry.Envelope, attempt, checked(current.Revision + 1),
                _timeProvider, beganAt, upperUtcNow)
            : null;
        return new EffectBeginOutcome(outcome, permission);
    }

    public async Task<LifecycleWriteOutcome> RenewAttemptDeadlineAsync(
        ControllerLease lease,
        EffectJournalSnapshot expected,
        Guid transitionId,
        Guid attemptId,
        DateTimeOffset upperUtcNow,
        DateTimeOffset requestedDeadline,
        TimeSpan leaseReserve,
        CancellationToken cancellationToken)
    {
        long beganAt = _timeProvider.GetTimestamp();
        var current = await ReadCurrentAsync(lease, expected, transitionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var now = UpperNow(upperUtcNow, beganAt);
        var entry = current.Entry.RenewAttemptDeadline(
            attemptId, lease.Fence.Epoch, now, requestedDeadline, LeaseDeadline(lease, now, leaseReserve));
        return await WriteAsync(lease, entry, current.Revision, transitionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LifecycleWriteOutcome> MarkUncertainAsync(
        ControllerLease lease,
        EffectJournalSnapshot expected,
        Guid transitionId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(lease, expected, transitionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var entry = current.Entry.MarkUncertain(attemptId);
        return await WriteAsync(lease, entry, current.Revision, transitionId, cancellationToken).ConfigureAwait(false);
    }

    public Task<LifecycleWriteOutcome> ResolveAsync(
        ControllerLease lease,
        EffectJournalSnapshot expected,
        Guid transitionId,
        EffectEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            throw new InvalidOperationException("An active lease is required for ordinary effect resolution.");
        }

        return ResolveCoreAsync(lease, expected, transitionId, evidence, cancellationToken);
    }

    internal Task<LifecycleWriteOutcome> ResolveRecoveryAsync(
        ControllerLease lease,
        EffectJournalSnapshot expected,
        Guid transitionId,
        EffectEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Mode != ControllerLeaseMode.RecoveryOnly)
        {
            throw new InvalidOperationException("A recovery-only lease is required for fenced effect resolution.");
        }

        return ResolveCoreAsync(lease, expected, transitionId, evidence, cancellationToken);
    }

    private async Task<LifecycleWriteOutcome> ResolveCoreAsync(
        ControllerLease lease,
        EffectJournalSnapshot expected,
        Guid transitionId,
        EffectEvidence evidence,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(lease, expected, transitionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var entry = current.Entry.Resolve(evidence);
        if (lease.Mode == ControllerLeaseMode.RecoveryOnly)
        {
            return await _store.WriteRecoveredEffectAsync(entry.Envelope.Profile, lease.Fence, transitionId,
                new EffectWrite(entry.Envelope.IntentId, current.Revision, EffectState.Settled, entry.WritePayload()),
                cancellationToken).ConfigureAwait(false);
        }

        return await WriteAsync(lease, entry, current.Revision, transitionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EffectJournalSnapshot?> ReadCurrentAsync(
        ControllerLease lease,
        EffectJournalSnapshot expected,
        Guid transitionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(expected);
        var envelope = expected.Entry.Envelope;
        if (lease.Fence.Deployment != envelope.Profile.Deployment)
        {
            throw new ArgumentException("An effect and its lease must use the same deployment.", nameof(lease));
        }
        if (transitionId == Guid.Empty || transitionId == expected.TransitionId)
        {
            throw new ArgumentException("Each journal update needs a fresh transition identity.", nameof(transitionId));
        }

        var current = await ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
        return current is not null && current.Revision == expected.Revision && current.TransitionId == expected.TransitionId &&
            current.Entry.Envelope.Matches(envelope)
            ? current
            : null;
    }

    private Task<LifecycleWriteOutcome> WriteAsync(
        ControllerLease lease,
        EffectJournalEntry entry,
        long? revision,
        Guid transitionId,
        CancellationToken cancellationToken)
    {
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            throw new InvalidOperationException("An active lease is required for the effect update.");
        }

        return _store.WriteEffectAsync(entry.Envelope.Profile, lease.Fence, transitionId,
            new EffectWrite(entry.Envelope.IntentId, revision, entry.State, entry.WritePayload()), cancellationToken);
    }

    private DateTimeOffset UpperNow(DateTimeOffset initialUpperBound, long beganAt)
    {
        var bound = initialUpperBound + _timeProvider.GetElapsedTime(beganAt);
        var local = _timeProvider.GetUtcNow();
        return bound > local ? bound : local;
    }

    private static DateTimeOffset LeaseDeadline(ControllerLease lease, DateTimeOffset now, TimeSpan reserve)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reserve, TimeSpan.Zero);
        if (lease.Mode != ControllerLeaseMode.Active || lease.ValidUntil - now <= reserve)
        {
            throw new InvalidOperationException("The lease has no safe time remaining for an effect attempt.");
        }

        return lease.ValidUntil - reserve;
    }
}
