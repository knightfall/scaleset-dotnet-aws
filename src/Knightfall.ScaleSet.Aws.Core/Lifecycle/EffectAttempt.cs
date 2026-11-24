using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record EffectAttempt(Guid Id, long LeaseEpoch, DateTimeOffset BeganAt, DateTimeOffset Deadline);

public enum EffectResolutionKind
{
    CanceledBeforeSend = 0,
    Applied = 1,
    Rejected = 2,
    Absent = 3,
    AbandonedBeforeSend = 4,
}

public sealed record EffectEvidence(
    EffectResolutionKind Kind,
    string EnvelopeSha256,
    EffectTargetBinding Target,
    DateTimeOffset ObservedAt,
    Guid? AttemptId = null,
    DateTimeOffset? EarliestObservedAt = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutorBinding? ExecutorBinding { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutorResourceObservation? ResourceObservation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Ec2LaunchRejection? Ec2LaunchRejection { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LatestObservedAt { get; init; }
}

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

    public EffectJournalEntry BeginFargateReplay(Guid attemptId, long leaseEpoch, DateTimeOffset lowerUtcNow,
        DateTimeOffset upperUtcNow, DateTimeOffset requestedDeadline, DateTimeOffset leaseDeadline)
    {
        if (Envelope.Request is not FargateLaunchEffectRequest { OwnershipTags: not null } ||
            State is not (EffectState.Executing or EffectState.Uncertain) || Attempt is not { } previousAttempt || previousAttempt.LeaseEpoch != leaseEpoch ||
            LatestAuthorizedDeadline is not { } priorDeadline || lowerUtcNow < priorDeadline || lowerUtcNow > upperUtcNow)
        {
            throw new InvalidOperationException("Only an exact expired Fargate send attempt in the same lease epoch may be replayed.");
        }

        var previous = State == EffectState.Executing ? MarkUncertain(previousAttempt.Id) : this;
        return previous.Begin(attemptId, leaseEpoch, upperUtcNow, requestedDeadline, leaseDeadline);
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

        return Settle(evidence);
    }

    internal EffectJournalEntry AbandonBeforeSend(EffectAttempt attempt, DateTimeOffset observedAt)
    {
        var evidence = new EffectEvidence(EffectResolutionKind.AbandonedBeforeSend, Envelope.Sha256, Envelope.Target,
            observedAt, attempt.Id);
        if (State != EffectState.Executing || Attempt != attempt || !ValidAbandonment(evidence))
        {
            throw new InvalidOperationException("Only the exact initial effect attempt may be abandoned before send.");
        }

        return Settle(evidence);
    }

    internal EffectJournalEntry? RearmAbandonedBeforeSend() =>
        State == EffectState.Settled && Envelope.Request is GitHubRunnerRemoveEffectRequest or FargateStopEffectRequest { Incarnation: not null } &&
        Resolution is { Kind: EffectResolutionKind.AbandonedBeforeSend } evidence && ValidAbandonment(evidence)
            ? Plan(Envelope) : null;

    private EffectJournalEntry Settle(EffectEvidence evidence) =>
        new(Envelope, EffectState.Settled,
            _body with
            {
                Resolution = evidence with
                {
                    ObservedAt = evidence.ObservedAt.ToUniversalTime(),
                    EarliestObservedAt = evidence.EarliestObservedAt?.ToUniversalTime(),
                    LatestObservedAt = evidence.LatestObservedAt?.ToUniversalTime(),
                },
            });

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
        if (State == EffectState.Settled || !MatchesEvidence(evidence))
        {
            return false;
        }

        if (evidence.Kind == EffectResolutionKind.CanceledBeforeSend)
        {
            return State == EffectState.Planned && evidence.AttemptId is null;
        }

        if (State == EffectState.Planned)
        {
            return Envelope.Request is GitHubRunnerRemoveEffectRequest && Attempt is null && LatestAuthorizedDeadline is null &&
                !EarlierSendUncertain && evidence is { Kind: EffectResolutionKind.Absent, AttemptId: null, EarliestObservedAt: { } lowerBound } &&
                lowerBound != default && evidence.ObservedAt >= Envelope.EarliestPossibleFirstSend;
        }

        if (State is not (EffectState.Executing or EffectState.Uncertain) || Attempt is not { } attempt)
        {
            return false;
        }

        return evidence.Kind switch
        {
            EffectResolutionKind.Applied => evidence.AttemptId is null && evidence.ObservedAt >= Envelope.EarliestPossibleFirstSend,
            EffectResolutionKind.Rejected => !EarlierSendUncertain && evidence.AttemptId == attempt.Id &&
                (evidence.Ec2LaunchRejection is null ? evidence.ObservedAt >= attempt.BeganAt :
                    evidence.LatestObservedAt >= attempt.BeganAt && evidence.LatestObservedAt <= attempt.Deadline),
            EffectResolutionKind.Absent => evidence.AttemptId is null &&
                evidence.EarliestObservedAt is { } lowerBound && lowerBound >= LatestAuthorizedDeadline,
            _ => false,
        };
    }

    private bool MatchesEvidence(EffectEvidence evidence) =>
        evidence.EnvelopeSha256 == Envelope.Sha256 && evidence.Target == Envelope.Target &&
        evidence.ObservedAt != default && !(evidence.EarliestObservedAt > evidence.ObservedAt) &&
        !(evidence.LatestObservedAt < evidence.ObservedAt) && MatchesProviderEvidence(evidence);

    internal static bool AllowsAbandonmentBeforeSend(EffectEnvelope envelope) =>
        envelope.ProviderReplayDeadline is null && envelope.Request is (GitHubJitGenerateEffectRequest or
            GitHubRunnerRemoveEffectRequest or Ec2LaunchEffectRequest { OwnershipTags: not null } or FargateStopEffectRequest { Incarnation: not null });

    private bool ValidAbandonment(EffectEvidence evidence) =>
        MatchesEvidence(evidence) && evidence.EarliestObservedAt is null && !EarlierSendUncertain &&
        AllowsAbandonmentBeforeSend(Envelope) &&
        Attempt is { } attempt && evidence.AttemptId == attempt.Id && evidence.ObservedAt >= attempt.BeganAt &&
        LatestAuthorizedDeadline == attempt.Deadline;

    private bool MatchesProviderEvidence(EffectEvidence evidence)
    {
        if (evidence.Ec2LaunchRejection is { } rejected)
        {
            return Envelope.Request is Ec2LaunchEffectRequest { OwnershipTags: not null } && Enum.IsDefined(rejected) &&
                evidence is { Kind: EffectResolutionKind.Rejected, LatestObservedAt: not null, ExecutorBinding: null, ResourceObservation: null };
        }
        if (evidence.LatestObservedAt is not null)
        {
            return false;
        }
        if (evidence.ExecutorBinding is { } binding && evidence.ResourceObservation is { } resource)
        {
            return evidence.Kind == EffectResolutionKind.Applied && resource.ObservedAt <= evidence.ObservedAt && Envelope.Request switch
            {
                Ec2LaunchEffectRequest { OwnershipTags: not null } ec2 => Ec2LaunchProof.Matches(Envelope.Profile, ec2, binding, resource),
                Ec2TerminateEffectRequest terminate => Ec2LaunchProof.Matches(Envelope.Profile, terminate, binding, resource),
                FargateLaunchEffectRequest { OwnershipTags: not null } fargate => FargateEffectProof.Matches(Envelope.Profile, fargate, binding, resource),
                FargateStopEffectRequest stop => FargateEffectProof.Matches(Envelope.Profile, stop, binding, resource),
                _ => false,
            };
        }
        return evidence is { ExecutorBinding: null, ResourceObservation: null } &&
            (Envelope.Request is not Ec2LaunchEffectRequest { OwnershipTags: not null } ||
             evidence.Kind is not (EffectResolutionKind.Applied or EffectResolutionKind.Rejected)) &&
            (Envelope.Request is not (FargateLaunchEffectRequest { OwnershipTags: not null } or FargateStopEffectRequest { Incarnation: not null }) ||
                evidence.Kind == EffectResolutionKind.CanceledBeforeSend);
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
            EffectState.Settled when Resolution is { Kind: EffectResolutionKind.AbandonedBeforeSend } abandonment =>
                ValidAbandonment(abandonment),
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
