using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    private const int SessionRequestVersion = 1;

    public async Task<SessionRecord?> ReadSessionAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var item = await ReadItemAsync(LifecycleKey.Session(profile), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        CheckNoExpiration(item);
        if (Number(item, "requestVersion") != SessionRequestVersion)
        {
            throw InvalidRecord();
        }
        var binding = new SessionBinding(profile, Text(item, "scopeIdentity"), Number(item, "scaleSetId"));
        var owner = new DeploymentLeaseFence(profile.Deployment, Text(item, "ownerId"), Number(item, "ownerEpoch"),
            ReadWriterProtocol(item, "writerProtocol"), Text(item, "ownerTaskArn"));
        var record = new SessionRecord(binding, owner, GuidValue(item, "incarnation"), ReadSessionPhase(item),
            ReadOptionalGuid(item, "sessionId"), Text(item, "ownerName"), GuidValue(item, "attemptId"),
            OptionalTimestamp(item, "attemptStartedAt") ?? throw InvalidRecord(),
            OptionalTimestamp(item, "attemptDeadline") ?? throw InvalidRecord(), Revision(item), GuidValue(item, "transitionId"),
            ReadCloseKind(item), ReadOptionalGuid(item, "priorSessionId"), Text(item, "requestSha256"));
        try
        {
            RequireSessionBinding(record.Binding);
            RequireSessionOwnerName(record.OwnerName);
        }
        catch (ArgumentException)
        {
            throw InvalidRecord();
        }

        if (!IsTaskArn(owner.OwnerTaskArn) || string.IsNullOrWhiteSpace(owner.OwnerId) || owner.Epoch <= 0 ||
            record.AttemptDeadline <= record.AttemptStartedAt ||
            (record.SessionId == record.PriorSessionId && record.SessionId.HasValue) ||
            !ValidSessionPhase(record) || SessionRequestDigest(record) != record.RequestSha256)
        {
            throw InvalidRecord();
        }
        return record;
    }

    public async Task<SessionChange> BeginSessionOpenAsync(
        SessionBinding binding,
        DeploymentLeaseFence lease,
        SessionRecord? expected,
        string ownerName,
        DateTimeOffset upperUtcNow,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        RequireSessionBinding(binding);
        RequireProfileLease(binding.Profile, lease);
        RequireSessionOwnerName(ownerName);
        if (expected is not null && (expected.Binding != binding ||
            expected.Phase != SessionPhase.Retired || expected.Owner.Epoch > lease.Epoch))
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }

        long started = _timeProvider.GetTimestamp();
        var start = ConservativeLatest(upperUtcNow);
        var end = AtMillisecond(deadline);
        if (end <= start)
        {
            return new SessionChange(LifecycleWriteOutcome.LeaseRejected);
        }
        if (await ReadSessionAsync(binding.Profile, cancellationToken).ConfigureAwait(false) != expected)
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }
        var registered = await EnsureEffectProfileAsync(lease, binding.Profile, cancellationToken).ConfigureAwait(false);
        if (registered != LifecycleWriteOutcome.Committed)
        {
            return new SessionChange(registered);
        }

        var next = new SessionRecord(binding, lease, Guid.NewGuid(), SessionPhase.OpenIntent, null, ownerName,
            Guid.NewGuid(), start, end, NextRevision(expected?.Revision), Guid.NewGuid(), null, expected?.SessionId, "");
        return await BeginSessionSendAsync(expected, next, started, upperUtcNow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionChange> BeginSessionCloseAsync(
        SessionRecord expected,
        DeploymentLeaseFence lease,
        SessionCloseKind kind,
        DateTimeOffset upperUtcNow,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        RequireProfileLease(expected.Binding.Profile, lease);
        if (expected.Phase != SessionPhase.Active || expected.Identity is null ||
            (kind == SessionCloseKind.OwnedClient ? expected.Owner != lease :
                kind != SessionCloseKind.FencedRetirement || expected.Owner.Epoch >= lease.Epoch))
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }

        long started = _timeProvider.GetTimestamp();
        var start = ConservativeLatest(upperUtcNow);
        var end = AtMillisecond(deadline);
        if (end <= start)
        {
            return new SessionChange(LifecycleWriteOutcome.LeaseRejected);
        }
        if (await ReadSessionAsync(expected.Binding.Profile, cancellationToken).ConfigureAwait(false) != expected)
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }

        var next = expected with
        {
            Owner = lease,
            Phase = SessionPhase.Closing,
            AttemptId = Guid.NewGuid(),
            AttemptStartedAt = start,
            AttemptDeadline = end,
            Revision = NextRevision(expected.Revision),
            TransitionId = Guid.NewGuid(),
            CloseKind = kind,
        };
        return await BeginSessionSendAsync(expected, next, started, upperUtcNow, cancellationToken).ConfigureAwait(false);
    }

    public Task<SessionChange> ConfirmSessionOpenedAsync(
        SessionSendAuthorization authorization, Guid sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.WasConsumed || authorization.Session.Phase != SessionPhase.OpenIntent ||
            sessionId == Guid.Empty || sessionId == authorization.Session.PriorSessionId)
        {
            return Task.FromResult(new SessionChange(LifecycleWriteOutcome.Conflict));
        }

        return ConfirmSessionSendAsync(authorization.Session, SessionPhase.Active, sessionId, cancellationToken);
    }

    public Task<SessionChange> ConfirmSessionRetiredAsync(
        SessionSendAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.WasConsumed || authorization.Session.Phase != SessionPhase.Closing ||
            authorization.Session.SessionId is not Guid id)
        {
            return Task.FromResult(new SessionChange(LifecycleWriteOutcome.Conflict));
        }

        return ConfirmSessionSendAsync(authorization.Session, SessionPhase.Retired, id, cancellationToken);
    }

    public async Task<SessionChange> QuarantineSessionAsync(
        SessionRecord expected, DeploymentLeaseFence lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        RequireProfileLease(expected.Binding.Profile, lease);
        if (expected.Phase is not (SessionPhase.OpenIntent or SessionPhase.Closing) || expected.Owner.Epoch > lease.Epoch)
        {
            return new SessionChange(LifecycleWriteOutcome.Conflict);
        }

        var current = await ReadSessionAsync(expected.Binding.Profile, cancellationToken).ConfigureAwait(false);
        if (current != expected)
        {
            return MatchesCompletion(current, expected, SessionPhase.Quarantined, expected.SessionId)
                ? new SessionChange(LifecycleWriteOutcome.Committed, current)
                : new SessionChange(LifecycleWriteOutcome.Conflict);
        }

        var quarantined = expected with
        {
            Phase = SessionPhase.Quarantined,
            Revision = NextRevision(expected.Revision),
            TransitionId = Guid.NewGuid(),
        };
        return await WriteSessionTransitionAsync(expected, quarantined, lease, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionChange> BeginSessionSendAsync(
        SessionRecord? expected, SessionRecord next, long started, DateTimeOffset upperUtcNow, CancellationToken cancellationToken)
    {
        next = next with { RequestSha256 = SessionRequestDigest(next) };
        var change = await WriteSessionTransitionAsync(
            expected, next, next.Owner, next.AttemptDeadline, cancellationToken).ConfigureAwait(false);
        if (change.Outcome != LifecycleWriteOutcome.Committed || cancellationToken.IsCancellationRequested)
        {
            return change;
        }

        var estimate = upperUtcNow + _timeProvider.GetElapsedTime(started);
        if (ConservativeLatest(estimate) >= next.AttemptDeadline)
        {
            return change;
        }
        return change with { Permission = new SessionSendAuthorization(next, _timeProvider, started, upperUtcNow) };
    }

    private async Task<SessionChange> ConfirmSessionSendAsync(
        SessionRecord intent, SessionPhase phase, Guid sessionId, CancellationToken cancellationToken)
    {
        var current = await ReadSessionAsync(intent.Binding.Profile, cancellationToken).ConfigureAwait(false);
        if (current != intent)
        {
            return MatchesCompletion(current, intent, phase, sessionId)
                ? new SessionChange(LifecycleWriteOutcome.Committed, current)
                : new SessionChange(LifecycleWriteOutcome.Conflict);
        }

        var completed = intent with
        {
            Phase = phase,
            SessionId = sessionId,
            Revision = NextRevision(intent.Revision),
            TransitionId = Guid.NewGuid(),
        };
        return await WriteSessionTransitionAsync(intent, completed, intent.Owner, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionChange> WriteSessionTransitionAsync(
        SessionRecord? expected,
        SessionRecord next,
        DeploymentLeaseFence lease,
        DateTimeOffset? sendDeadline,
        CancellationToken cancellationToken)
    {
        var profile = next.Binding.Profile;
        var key = LifecycleKey.Session(profile);
        var item = Record(key, profile.Deployment, next.Revision, next.TransitionId);
        item.Add("scopeIdentity", StringValue(next.Binding.ScopeIdentity));
        item.Add("scaleSetId", NumberValue(next.Binding.ScaleSetId));
        item.Add("ownerId", StringValue(next.Owner.OwnerId));
        item.Add("ownerEpoch", NumberValue(next.Owner.Epoch));
        item.Add("ownerTaskArn", StringValue(next.Owner.OwnerTaskArn));
        item.Add("writerProtocol", NumberValue(next.Owner.WriterProtocol));
        item.Add("incarnation", StringValue(next.Incarnation.ToString("D")));
        item.Add("sessionPhase", StringValue(SessionPhaseValue(next.Phase)));
        item.Add("ownerName", StringValue(next.OwnerName));
        item.Add("attemptId", StringValue(next.AttemptId.ToString("D")));
        item.Add("attemptStartedAt", NumberValue(next.AttemptStartedAt.ToUnixTimeMilliseconds()));
        item.Add("attemptDeadline", NumberValue(next.AttemptDeadline.ToUnixTimeMilliseconds()));
        item.Add("requestVersion", NumberValue(SessionRequestVersion));
        item.Add("requestSha256", StringValue(next.RequestSha256));
        if (next.SessionId is Guid active)
        {
            item.Add("sessionId", StringValue(active.ToString("D")));
        }
        if (next.PriorSessionId is Guid previous)
        {
            item.Add("priorSessionId", StringValue(previous.ToString("D")));
        }
        if (next.CloseKind is { } kind)
        {
            item.Add("closeKind", StringValue(kind == SessionCloseKind.OwnedClient ? "owned-client" : "fenced-retirement"));
        }

        var actions = new List<TransactionAction>
        {
            LeaseCondition(lease, validThrough: sendDeadline),
            CompareAndPut(key, item, expected?.Revision, profile.Deployment),
        };
        if (sendDeadline is not null)
        {
            // An open must await prior reconciliation. Closing the exact queue can proceed independently.
            actions.Add(EffectReadyCondition(profile, lease, requireNoUnfinished: next.Phase == SessionPhase.OpenIntent));
        }
        if (next.Phase == SessionPhase.Active && next.SessionId is Guid newSession)
        {
            actions.Add(RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Checkpoint(profile, newSession), null)));
        }

        var outcome = await WriteAsync(actions, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (outcome is LifecycleWriteOutcome.Conflict or LifecycleWriteOutcome.Indeterminate &&
            await ReadSessionAsync(profile, cancellationToken).ConfigureAwait(false) == next)
        {
            outcome = LifecycleWriteOutcome.Committed;
        }
        return new SessionChange(outcome, outcome == LifecycleWriteOutcome.Committed ? next : null);
    }

    private static bool MatchesCompletion(SessionRecord? current, SessionRecord intent, SessionPhase phase, Guid? sessionId) =>
        current is not null && current.Phase == phase && current.SessionId == sessionId &&
        current.Binding == intent.Binding && current.Owner == intent.Owner && current.Incarnation == intent.Incarnation &&
        current.AttemptId == intent.AttemptId && current.RequestSha256 == intent.RequestSha256 &&
        current.Revision == intent.Revision + 1;

    private static string SessionRequestDigest(SessionRecord session)
    {
        var request = new SessionRequest(
            Version: SessionRequestVersion,
            Kind: session.CloseKind switch
            {
                SessionCloseKind.OwnedClient => "close",
                SessionCloseKind.FencedRetirement => "retire",
                _ => "open",
            },
            Scope: session.Binding.ScopeIdentity,
            ProfileId: session.Binding.Profile.ProfileId,
            SetId: session.Binding.ScaleSetId,
            SessionId: session.CloseKind is null ? null : session.SessionId,
            OwnerName: session.OwnerName,
            Incarnation: session.Incarnation,
            Owner: session.Owner,
            AttemptId: session.AttemptId,
            StartedAtMillis: session.AttemptStartedAt.ToUnixTimeMilliseconds(),
            DeadlineMillis: session.AttemptDeadline.ToUnixTimeMilliseconds());
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
    }

    private static bool ValidSessionPhase(SessionRecord record) => record.Phase switch
    {
        SessionPhase.OpenIntent => record.SessionId is null && record.CloseKind is null,
        SessionPhase.Active => record.SessionId is not null && record.CloseKind is null,
        SessionPhase.Closing or SessionPhase.Retired => record.SessionId is not null && record.CloseKind is not null,
        SessionPhase.Quarantined => record.SessionId.HasValue == record.CloseKind.HasValue,
        _ => false,
    };

    private static SessionPhase ReadSessionPhase(Dictionary<string, AttributeValue> item) => Text(item, "sessionPhase") switch
    {
        "open-intent" => SessionPhase.OpenIntent,
        "active" => SessionPhase.Active,
        "closing" => SessionPhase.Closing,
        "retired" => SessionPhase.Retired,
        "quarantined" => SessionPhase.Quarantined,
        _ => throw InvalidRecord(),
    };

    private static string SessionPhaseValue(SessionPhase phase) => phase switch
    {
        SessionPhase.OpenIntent => "open-intent",
        SessionPhase.Active => "active",
        SessionPhase.Closing => "closing",
        SessionPhase.Retired => "retired",
        SessionPhase.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static SessionCloseKind? ReadCloseKind(Dictionary<string, AttributeValue> item)
    {
        if (!item.ContainsKey("closeKind"))
        {
            return null;
        }

        return Text(item, "closeKind") switch
        {
            "owned-client" => SessionCloseKind.OwnedClient,
            "fenced-retirement" => SessionCloseKind.FencedRetirement,
            _ => throw InvalidRecord(),
        };
    }

    private static Guid? ReadOptionalGuid(Dictionary<string, AttributeValue> item, string name) =>
        item.ContainsKey(name) ? GuidValue(item, name) : null;

    private static void RequireSessionBinding(SessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(binding.Profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binding.ScaleSetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.ScopeIdentity);
        int separator = binding.ScopeIdentity.IndexOf('/');
        if (binding.ScopeIdentity.Length > 256 || separator <= 0 || separator == binding.ScopeIdentity.Length - 1)
        {
            throw new ArgumentException("An exact canonical GitHub scope is required.", nameof(binding));
        }

        string scope = "https://" + binding.ScopeIdentity[..separator] + "/" +
            string.Join('/', binding.ScopeIdentity[(separator + 1)..].Split('/').Select(Uri.EscapeDataString));
        if (GitHubRoutingIdentity.Scope(scope) != binding.ScopeIdentity)
        {
            throw new ArgumentException("An exact canonical GitHub scope is required.", nameof(binding));
        }
    }

    private static void RequireSessionOwnerName(string ownerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        if (ownerName.Length > 120 || !ownerName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("A bounded nonsecret session owner name is required.", nameof(ownerName));
        }
    }

    private sealed record SessionRequest(
        int Version,
        string Kind,
        string Scope,
        string ProfileId,
        long SetId,
        Guid? SessionId,
        string OwnerName,
        Guid Incarnation,
        DeploymentLeaseFence Owner,
        Guid AttemptId,
        long StartedAtMillis,
        long DeadlineMillis);
}
