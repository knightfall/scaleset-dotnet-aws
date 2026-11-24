using System.Text.Json;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record EffectAttempt(Guid Id, long LeaseEpoch, DateTimeOffset BeganAt, DateTimeOffset Deadline);

public enum EffectResolutionKind
{
    CanceledBeforeSend = 0,
    Applied = 1,
    Rejected = 2,
    Absent = 3,
}

public sealed record EffectEvidence(
    EffectResolutionKind Kind,
    string EnvelopeSha256,
    EffectTargetBinding Target,
    DateTimeOffset ObservedAt,
    Guid? AttemptId = null,
    DateTimeOffset? EarliestObservedAt = null);

public sealed class EffectJournalEntry
{
    private readonly JournalBody _body;

    private EffectJournalEntry(EffectEnvelope envelope, EffectState state, JournalBody body)
    {
        Envelope = envelope;
        State = state;
        _body = body;
    }

    public EffectEnvelope Envelope { get; }
    public EffectState State { get; }
    public EffectAttempt? Attempt => _body.Attempt;
    public DateTimeOffset? LatestAuthorizedDeadline => _body.LatestAuthorizedDeadline;
    public bool EarlierSendUncertain => _body.EarlierSendUncertain;
    public EffectEvidence? Resolution => _body.Resolution;

    public static EffectJournalEntry Plan(EffectEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new EffectJournalEntry(envelope, EffectState.Planned,
            new JournalBody(envelope.CanonicalJson, envelope.Sha256, null, null, false, null));
    }

    public EffectJournalEntry Begin(
        Guid attemptId,
        long leaseEpoch,
        DateTimeOffset upperUtcNow,
        DateTimeOffset requestedDeadline,
        DateTimeOffset leaseDeadline)
    {
        if (State is not (EffectState.Planned or EffectState.Uncertain) ||
            (State == EffectState.Uncertain && Envelope.ProviderReplayDeadline is null) ||
            upperUtcNow < Envelope.EarliestPossibleFirstSend || Attempt?.Id == attemptId)
        {
            throw new InvalidOperationException("The effect cannot authorize a new send attempt.");
        }

        EffectRequestIdentifiers.Guid(attemptId, nameof(attemptId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseEpoch);
        var deadline = BoundDeadline(upperUtcNow, requestedDeadline, leaseDeadline);
        var attempt = new EffectAttempt(attemptId, leaseEpoch, upperUtcNow.ToUniversalTime(), deadline);
        return NewAttempt(EffectState.Executing, attempt, EarlierSendUncertain || State == EffectState.Uncertain);
    }

    public EffectJournalEntry RenewAttemptDeadline(
        Guid attemptId,
        long leaseEpoch,
        DateTimeOffset upperUtcNow,
        DateTimeOffset requestedDeadline,
        DateTimeOffset leaseDeadline)
    {
        var attempt = RequireAttempt(attemptId);
        if (State != EffectState.Executing || attempt.LeaseEpoch != leaseEpoch ||
            upperUtcNow < attempt.BeganAt || upperUtcNow >= attempt.Deadline)
        {
            throw new InvalidOperationException("Only the matching live attempt can renew its deadline.");
        }

        var deadline = BoundDeadline(upperUtcNow, requestedDeadline, leaseDeadline);
        if (deadline <= attempt.Deadline)
        {
            throw new InvalidOperationException("No extension remains in the lease and provider replay bounds.");
        }

        return NewAttempt(EffectState.Executing, attempt with { Deadline = deadline }, EarlierSendUncertain);
    }

    public EffectJournalEntry MarkUncertain(Guid attemptId)
    {
        _ = RequireAttempt(attemptId);
        if (State != EffectState.Executing)
        {
            throw new InvalidOperationException("Only an executing attempt can be marked uncertain.");
        }

        return new EffectJournalEntry(Envelope, EffectState.Uncertain, _body);
    }

    public EffectJournalEntry Resolve(EffectEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!CanResolve(evidence))
        {
            throw new InvalidOperationException("The evidence cannot settle this exact effect.");
        }

        return new EffectJournalEntry(Envelope, EffectState.Settled,
            _body with
            {
                Resolution = evidence with
                {
                    ObservedAt = evidence.ObservedAt.ToUniversalTime(),
                    EarliestObservedAt = evidence.EarliestObservedAt?.ToUniversalTime(),
                },
            });
    }

    public string WritePayload() => JsonSerializer.Serialize(_body, EffectEnvelope.Serializer);

    public static EffectJournalEntry Read(LifecycleProfile profile, StoredEffect stored)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(stored);
        try
        {
            var body = JsonSerializer.Deserialize<JournalBody>(stored.Payload, EffectEnvelope.Serializer) ?? throw InvalidJournal();
            var envelope = EffectEnvelope.Read(body.Envelope, body.EnvelopeSha256);
            var entry = new EffectJournalEntry(envelope, stored.State, body);
            if (envelope.Profile != profile || envelope.IntentId != stored.IntentId || !entry.ValidState() ||
                !StringComparer.Ordinal.Equals(entry.WritePayload(), stored.Payload))
            {
                throw InvalidJournal();
            }

            return entry;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw InvalidJournal();
        }
    }

    private EffectAttempt RequireAttempt(Guid id) =>
        Attempt is { } attempt && attempt.Id == id
            ? attempt
            : throw new InvalidOperationException("The journal does not contain the matching attempt.");

    private EffectJournalEntry NewAttempt(EffectState state, EffectAttempt attempt, bool earlierSendUncertain)
    {
        var latest = LatestAuthorizedDeadline is { } previous && previous > attempt.Deadline ? previous : attempt.Deadline;
        return new EffectJournalEntry(Envelope, state,
            _body with { Attempt = attempt, LatestAuthorizedDeadline = latest, EarlierSendUncertain = earlierSendUncertain });
    }

    private DateTimeOffset BoundDeadline(DateTimeOffset upperUtcNow, DateTimeOffset requested, DateTimeOffset leaseDeadline)
    {
        var deadline = requested < leaseDeadline ? requested : leaseDeadline;
        if (Envelope.ProviderReplayDeadline is { } cutoff && cutoff < deadline)
        {
            deadline = cutoff;
        }
        if (upperUtcNow >= deadline)
        {
            throw new InvalidOperationException("No time remains for an effect send under its lease and replay cutoff.");
        }

        return deadline.ToUniversalTime();
    }

    public bool CanResolve(EffectEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (State == EffectState.Settled || evidence.EnvelopeSha256 != Envelope.Sha256 || evidence.Target != Envelope.Target ||
            evidence.ObservedAt == default || evidence.EarliestObservedAt > evidence.ObservedAt)
        {
            return false;
        }

        if (evidence.Kind == EffectResolutionKind.CanceledBeforeSend)
        {
            return State == EffectState.Planned && evidence.AttemptId is null;
        }

        if (State is not (EffectState.Executing or EffectState.Uncertain) || Attempt is not { } attempt)
        {
            return false;
        }

        return evidence.Kind switch
        {
            EffectResolutionKind.Applied => evidence.AttemptId is null && evidence.ObservedAt >= Envelope.EarliestPossibleFirstSend,
            EffectResolutionKind.Rejected => !EarlierSendUncertain && evidence.AttemptId == attempt.Id && evidence.ObservedAt >= attempt.BeganAt,
            EffectResolutionKind.Absent => evidence.AttemptId is null &&
                evidence.EarliestObservedAt is { } lowerBound && lowerBound >= LatestAuthorizedDeadline,
            _ => false,
        };
    }

    private bool ValidState()
    {
        if (Attempt is { } attempt)
        {
            if (attempt.Id == Guid.Empty || attempt.LeaseEpoch <= 0 || attempt.BeganAt < Envelope.EarliestPossibleFirstSend ||
                attempt.Deadline <= attempt.BeganAt || LatestAuthorizedDeadline is not { } latest || latest < attempt.Deadline ||
                (EarlierSendUncertain && Envelope.ProviderReplayDeadline is null) ||
                (Envelope.ProviderReplayDeadline is { } cutoff && latest > cutoff))
            {
                return false;
            }
        }
        else if (LatestAuthorizedDeadline is not null || EarlierSendUncertain)
        {
            return false;
        }

        return State switch
        {
            EffectState.Planned => Attempt is null && Resolution is null,
            EffectState.Executing or EffectState.Uncertain => Attempt is not null && Resolution is null,
            EffectState.Settled when Resolution is { } evidence =>
                new EffectJournalEntry(Envelope, Attempt is null ? EffectState.Planned : EffectState.Uncertain, _body).CanResolve(evidence),
            _ => false,
        };
    }

    private static InvalidDataException InvalidJournal() => new("The durable effect attempt or settlement is invalid.");

    private sealed record JournalBody(
        string Envelope,
        string EnvelopeSha256,
        EffectAttempt? Attempt,
        DateTimeOffset? LatestAuthorizedDeadline,
        bool EarlierSendUncertain,
        EffectEvidence? Resolution);
}
