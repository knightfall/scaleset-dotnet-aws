using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.GitHub;

internal sealed class ListenerDeliveryLedger(DynamoDbLifecycleStore store, OwnedSessionFence fence, TimeProvider clock)
{
    /// <summary>Bounds each transaction even when jobs touch two executors and their mappings.</summary>
    internal const int MaximumJobsPerTransaction = 11;
    private const int MaximumTransactionActions = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static async Task<bool> HasCoherentAdmissionAsync(
        DynamoDbLifecycleStore store, LifecycleProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            await ReadBalanceAsync(store, profile, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal async Task<Entry<SessionDemandObservation>?> ReadDemandAsync(CancellationToken cancellationToken)
    {
        var entry = await ReadAsync<SessionDemandObservation>(LifecycleKey.Demand(fence.Identity.Profile), cancellationToken).ConfigureAwait(false);
        if (entry?.Value is { } demand && (demand.SessionId == Guid.Empty || demand.ObservedAt == default ||
            demand.MessageId < 0 || demand.TotalAssignedJobs < 0))
        {
            throw InvalidEvidence();
        }
        return entry;
    }

    internal async Task<Entry<MessageReceipt>?> ReadReceiptAsync(
        long messageId, string digest, CancellationToken cancellationToken, string? priorDigest = null)
    {
        var entry = await ReadAsync<MessageReceipt>(MessageKey(messageId), cancellationToken).ConfigureAwait(false);
        if (entry is not null && ((entry.Value.Digest != digest && (priorDigest is null || entry.Value.Digest != priorDigest)) ||
            entry.Value.ObservedAt == default || entry.Value.JobOffset < 0 ||
            (entry.Value.Admission is { } attempt && !MatchesEnvelope(attempt, messageId))))
        {
            throw InvalidEvidence();
        }
        return entry;
    }

    internal async Task<LifecycleWriteOutcome> PersistReceiptAsync(
        long messageId, string digest, DateTimeOffset observedAt, int? assigned, CancellationToken cancellationToken)
    {
        List<LifecycleRecordWrite> writes =
            [NewWrite(MessageKey(messageId), new MessageReceipt(digest, observedAt, 0, null))];
        if (assigned is int totalAssigned &&
            await DemandWriteAsync(new SessionDemandObservation(fence.Identity.SessionId, observedAt, messageId, totalAssigned),
                cancellationToken).ConfigureAwait(false) is { } demand)
        {
            writes.Add(demand);
        }
        return await WriteAsync(writes, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> PersistInitialStatisticsAsync(
        DateTimeOffset observedAt, int assigned, CancellationToken cancellationToken)
    {
        var write = await DemandWriteAsync(new SessionDemandObservation(fence.Identity.SessionId, observedAt, null, assigned), cancellationToken)
            .ConfigureAwait(false);
        return write is null ||
            await WriteAsync([write], cancellationToken).ConfigureAwait(false) == LifecycleWriteOutcome.Committed;
    }

    internal Task<LifecycleWriteOutcome> RefreshDemandAsync(Entry<SessionDemandObservation> expected,
        DateTimeOffset observedAt, int assigned, CancellationToken cancellationToken)
    {
        if (expected.Value.SessionId != fence.Identity.SessionId || observedAt <= expected.Value.ObservedAt ||
            observedAt > clock.GetUtcNow() || assigned < 0)
        {
            return Task.FromResult(LifecycleWriteOutcome.Conflict);
        }

        var observation = expected.Value with { ObservedAt = observedAt, TotalAssignedJobs = assigned };
        return WriteAsync([Replace(expected, observation)], cancellationToken);
    }

    internal async Task<LifecycleWriteOutcome> PersistJobObservationsAsync(
        Entry<MessageReceipt> receipt, long messageId, IReadOnlyList<JobObservation> jobs,
        bool releaseOnAssignment, CancellationToken cancellationToken)
    {
        if (jobs.Count is < 1 or > MaximumJobsPerTransaction || receipt.Value.Admission is not null)
        {
            throw InvalidEvidence();
        }

        List<LifecycleRecordWrite> writes = [];
        List<LifecycleRecordCondition> guards = [];
        List<ExecutorJobObservation> executorJobs = [];
        int released = 0;
        foreach (var job in jobs)
        {
            var key = JobKey(job.RequestId);
            var previous = await ReadAsync<StoredJob>(key, cancellationToken).ConfigureAwait(false);
            if (previous is not null && (previous.Value.Observation.RequestId != job.RequestId ||
                !Enum.IsDefined(previous.Value.Observation.Phase)))
            {
                throw InvalidEvidence();
            }
            var current = previous?.Value;
            long? knownRunner = current?.Observation.RunnerId;
            long? observedRunner = job.RunnerId ?? job.RelatedRunnerId ?? knownRunner;
            long? otherRunner = job.RunnerId is not null ? job.RelatedRunnerId : null;
            otherRunner ??= knownRunner != observedRunner ? knownRunner : null;
            if (current?.Observation.RunnerId is long prior && prior != observedRunner && prior != otherRunner)
            {
                throw InvalidEvidence();
            }
            bool conflict = current?.HasConflict == true || otherRunner is not null;
            var candidate = new StoredJob(job with { RunnerId = observedRunner, RelatedRunnerId = otherRunner }, fence.Identity.SessionId,
                messageId, receipt.Value.ObservedAt, conflict);
            var next = current is null || job.Phase > current.Observation.Phase ||
                (job.Phase == current.Observation.Phase && receipt.Value.ObservedAt >= current.ObservedAt)
                ? candidate : current with { HasConflict = conflict };
            if (next != current)
            {
                writes.Add(Write(key, previous, next));
            }
            if (job.Phase is ExecutorJobPhase.Started or ExecutorJobPhase.Completed && observedRunner is long runnerId)
            {
                executorJobs.Add(new ExecutorJobObservation(job.RequestId, runnerId, job.Phase, fence.Identity.SessionId,
                    messageId, receipt.Value.ObservedAt, conflict));
                if (otherRunner is long rival)
                {
                    executorJobs.Add(new ExecutorJobObservation(job.RequestId, rival, job.Phase, fence.Identity.SessionId,
                        messageId, receipt.Value.ObservedAt, HasConflict: true));
                }
            }

            if (!conflict && (job.Phase == ExecutorJobPhase.Completed || releaseOnAssignment))
            {
                var acquisition = await ReadAcquisitionAsync(job.RequestId, cancellationToken).ConfigureAwait(false);
                if (acquisition is null)
                {
                    guards.Add(new LifecycleRecordCondition(LifecycleKey.Acquisition(fence.Identity.Profile, job.RequestId), null));
                }
                else if (acquisition.Value.IsHeld)
                {
                    var reason = job.Phase == ExecutorJobPhase.Completed
                        ? AdmissionEvidenceKind.Completed : AdmissionEvidenceKind.Assignment;
                    writes.Add(Release(acquisition, new AdmissionEvidence(reason, receipt.Record.Key.SortKey)));
                    released++;
                }
            }
        }
        if (released != 0)
        {
            var balance = await ReadBalanceAsync(cancellationToken).ConfigureAwait(false);
            writes.Add(DecrementBalance(balance, released));
        }
        writes.Add(Replace(receipt, receipt.Value with { JobOffset = checked(receipt.Value.JobOffset + jobs.Count) }));
        return await store.WriteSessionDeliveryRecordsAsync(fence, Guid.NewGuid(), writes, executorJobs, cancellationToken, guards)
            .ConfigureAwait(false);
    }

    internal async Task<CommittedAdmission?> ReserveAsync(
        Entry<MessageReceipt> receipt, long messageId, ImmutableArray<long> offers, int allowance, WarmAdmissionCredit? warm,
        IReadOnlyDictionary<LifecycleKey, long?> readGuards, CancellationToken cancellationToken)
    {
        if (receipt.Value.Admission is not null)
        {
            throw InvalidEvidence();
        }

        var guards = new Dictionary<LifecycleKey, long?>(readGuards);
        if (guards.Remove(receipt.Record.Key, out long? receiptRevision) && receiptRevision != receipt.Record.Revision)
        {
            return null;
        }
        var profile = fence.Identity.Profile;
        // The effect revision can refine the mandatory effect-index condition without adding an action.
        int actions = 4 + guards.Count - (guards.ContainsKey(LifecycleKey.Effects(profile)) ? 1 : 0);
        if (actions > MaximumTransactionActions)
        {
            return null;
        }

        var balanceKey = LifecycleKey.Admission(profile);
        bool hasBalanceGuard = guards.TryGetValue(balanceKey, out long? balanceRevision);
        int totalAllowance = checked(allowance + (warm?.Count ?? 0));
        var balance = totalAllowance > 0 && !offers.IsEmpty ? await ReadBalanceAsync(cancellationToken).ConfigureAwait(false) : null;
        int held = balance?.Value.HeldCount ?? 0;
        int normalHeadroom = Math.Max(0, allowance - held);
        int headroom = Math.Max(0, totalAllowance - held);
        List<LifecycleRecordWrite> writes = [];
        var chosen = ImmutableArray.CreateBuilder<long>();
        Guid attemptId = Guid.NewGuid();
        foreach (long requestId in offers)
        {
            if (chosen.Count == headroom)
            {
                break;
            }

            var jobKey = JobKey(requestId);
            var acquisitionKey = LifecycleKey.Acquisition(profile, requestId);
            bool hasJobGuard = guards.TryGetValue(jobKey, out long? jobRevision);
            bool hasAcquisitionGuard = guards.TryGetValue(acquisitionKey, out long? acquisitionRevision);
            // A nonempty reservation also fences the applied profile catalog.
            int additionalActions = (chosen.Count == 0 ? 1 + (hasBalanceGuard ? 0 : 1) : 0) +
                (hasJobGuard ? 0 : 1) + (hasAcquisitionGuard ? 0 : 1);
            bool firstWarm = chosen.Count == normalHeadroom;
            if (firstWarm)
            {
                additionalActions += warm!.Guards.Count(guard => !guards.ContainsKey(guard.Key));
            }
            if (actions + additionalActions > MaximumTransactionActions)
            {
                continue;
            }

            var acquisition = await ReadAcquisitionAsync(requestId, cancellationToken).ConfigureAwait(false);
            if ((acquisition is not null && !CanReplaceUnsent(acquisition.Value, messageId)) ||
                await store.ReadRecordAsync(jobKey, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }
            if ((hasBalanceGuard && balanceRevision != balance?.Record.Revision) ||
                jobRevision is not null || (hasAcquisitionGuard && acquisitionRevision != acquisition?.Record.Revision))
            {
                return null;
            }

            actions += additionalActions;
            if (firstWarm)
            {
                foreach (var guard in warm!.Guards)
                {
                    guards[guard.Key] = guard.ExpectedRevision;
                }
            }
            chosen.Add(requestId);
            guards[jobKey] = null;
            guards.Remove(acquisitionKey);
            writes.Add(Write(acquisitionKey, acquisition, new Acquisition(
                requestId, fence.Identity.ScopeIdentity, fence.Identity.ScaleSetId, fence.Identity.SessionId,
                messageId, attemptId, AcquisitionState.Reserved, null)));
        }

        var request = new AcquisitionRequest(fence.Identity.ScopeIdentity, fence.Identity.ScaleSetId,
            fence.Identity.SessionId, messageId, chosen.ToImmutable());
        var attempt = new AdmissionAttempt(attemptId, request, RequestDigest(request), chosen.Count == 0);
        if (chosen.Count != 0)
        {
            var nextBalance = new AdmissionBalance(checked((balance?.Value.HeldCount ?? 0) + chosen.Count));
            guards.Remove(balanceKey);
            writes.Add(Write(balanceKey, balance, nextBalance));
        }
        writes.Add(Replace(receipt, receipt.Value with { Admission = attempt }));
        var conditions = guards.Select(guard => new LifecycleRecordCondition(guard.Key, guard.Value)).ToArray();
        var warmValidThrough = chosen.Count > normalHeadroom ? warm!.ValidThrough : (DateTimeOffset?)null;
        if (warmValidThrough <= clock.GetUtcNow())
        {
            return null;
        }
        var result = chosen.Count == 0
            ? await store.WriteSessionRecordsAsync(fence, attemptId, writes, cancellationToken, conditions).ConfigureAwait(false)
            : await store.WriteSessionReservationAsync(fence, attemptId, writes, conditions, cancellationToken).ConfigureAwait(false);
        return result == LifecycleWriteOutcome.Committed ? new CommittedAdmission(attempt, warmValidThrough) : null;
    }

    internal async Task<LifecycleWriteOutcome> SettleAttemptAsync(
        Entry<MessageReceipt> receipt, long messageId, IReadOnlySet<long> accepted, CancellationToken cancellationToken,
        bool abandonedBeforeSend = false)
    {
        if (receipt.Value.Admission is not { OutcomeRecorded: false } attempt)
        {
            return LifecycleWriteOutcome.Committed;
        }

        var writes = new List<LifecycleRecordWrite>(attempt.Request.RequestIds.Length + 1);
        int released = 0;
        foreach (long requestId in attempt.Request.RequestIds)
        {
            var acquisition = await ReadAcquisitionAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (acquisition is null || acquisition.Value.AttemptId != attempt.Id ||
                acquisition.Value.SessionId != fence.Identity.SessionId || acquisition.Value.MessageId != messageId)
            {
                throw InvalidEvidence();
            }
            if (acquisition.Value.State == AcquisitionState.Reserved)
            {
                if (abandonedBeforeSend)
                {
                    writes.Add(Release(acquisition, new AdmissionEvidence(AdmissionEvidenceKind.AbandonedBeforeSend, attempt.Id.ToString("D"))));
                    released++;
                }
                else
                {
                    var state = accepted.Contains(requestId) ? AcquisitionState.Accepted : AcquisitionState.Uncertain;
                    writes.Add(Replace(acquisition, acquisition.Value with { State = state }));
                }
            }
        }
        if (released != 0)
        {
            var balance = await ReadBalanceAsync(cancellationToken).ConfigureAwait(false);
            writes.Add(DecrementBalance(balance, released));
        }
        writes.Add(Replace(receipt, receipt.Value with { Admission = attempt with { OutcomeRecorded = true } }));
        return await WriteAsync(writes, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> ConfirmDefinitiveRejectionAsync(
        long requestId, Guid attemptId, Guid evidenceId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        if (attemptId == Guid.Empty || evidenceId == Guid.Empty)
        {
            throw new ArgumentException("Exact acquisition attempt and rejection evidence identifiers are required.");
        }

        var acquisition = await ReadAcquisitionAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (acquisition is null || acquisition.Value.AttemptId != attemptId)
        {
            return false;
        }
        var evidence = new AdmissionEvidence(AdmissionEvidenceKind.Rejected, evidenceId.ToString("D"));
        if (acquisition.Value.Evidence is not null)
        {
            return acquisition.Value.Evidence == evidence;
        }
        if (acquisition.Value.State == AcquisitionState.Accepted)
        {
            return false;
        }

        var balance = await ReadBalanceAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync([Release(acquisition, evidence), DecrementBalance(balance, 1)], cancellationToken)
            .ConfigureAwait(false) == LifecycleWriteOutcome.Committed;
    }

    private async Task<LifecycleRecordWrite?> DemandWriteAsync(SessionDemandObservation observation, CancellationToken cancellationToken)
    {
        if (observation.ObservedAt == default || observation.TotalAssignedJobs < 0)
        {
            throw InvalidEvidence();
        }
        var previous = await ReadDemandAsync(cancellationToken).ConfigureAwait(false);
        if (previous?.Value is { } current && current.SessionId == observation.SessionId &&
            (observation.ObservedAt < current.ObservedAt || observation.MessageId is null ||
             (current.MessageId is long cursor && observation.MessageId <= cursor)))
        {
            return null;
        }
        return Write(LifecycleKey.Demand(fence.Identity.Profile), previous, observation);
    }

    private Task<Entry<AdmissionBalance>?> ReadBalanceAsync(CancellationToken cancellationToken) =>
        ReadBalanceAsync(store, fence.Identity.Profile, cancellationToken);

    private static async Task<Entry<AdmissionBalance>?> ReadBalanceAsync(
        DynamoDbLifecycleStore store, LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var key = LifecycleKey.Admission(profile);
        var entry = await ReadAsync<AdmissionBalance>(store, key, cancellationToken).ConfigureAwait(false);
        if (entry is null && await store.HasAcquisitionHistoryAsync(profile, cancellationToken).ConfigureAwait(false))
        {
            entry = await ReadAsync<AdmissionBalance>(store, key, cancellationToken).ConfigureAwait(false) ?? throw InvalidEvidence();
        }
        if (entry?.Value.HeldCount < 0)
        {
            throw InvalidEvidence();
        }
        return entry;
    }

    private async Task<Entry<Acquisition>?> ReadAcquisitionAsync(long requestId, CancellationToken cancellationToken)
    {
        var key = LifecycleKey.Acquisition(fence.Identity.Profile, requestId);
        var entry = await ReadAsync<Acquisition>(key, cancellationToken).ConfigureAwait(false);
        if (entry?.Value is { } acquisition && (acquisition.RequestId != requestId ||
            acquisition.ScopeIdentity != fence.Identity.ScopeIdentity || acquisition.ScaleSetId != fence.Identity.ScaleSetId ||
            acquisition.SessionId == Guid.Empty || acquisition.MessageId < 0 || acquisition.AttemptId == Guid.Empty ||
            !Enum.IsDefined(acquisition.State) || (acquisition.IsHeld == (acquisition.Evidence is not null))))
        {
            throw InvalidEvidence();
        }
        return entry;
    }

    private bool CanReplaceUnsent(Acquisition acquisition, long messageId) =>
        acquisition is { State: AcquisitionState.Released, Evidence: { Kind: AdmissionEvidenceKind.AbandonedBeforeSend } evidence } &&
        evidence.Reference == acquisition.AttemptId.ToString("D") &&
        (acquisition.SessionId != fence.Identity.SessionId || acquisition.MessageId < messageId);

    private bool MatchesEnvelope(AdmissionAttempt attempt, long messageId)
    {
        var request = attempt.Request;
        return attempt.Id != Guid.Empty && request is not null && request.ScopeIdentity == fence.Identity.ScopeIdentity &&
            request.ScaleSetId == fence.Identity.ScaleSetId && request.SessionId == fence.Identity.SessionId &&
            request.MessageId == messageId && !request.RequestIds.IsDefault && (attempt.OutcomeRecorded || !request.RequestIds.IsEmpty) &&
            request.RequestIds.All(id => id > 0) && request.RequestIds.Distinct().Count() == request.RequestIds.Length &&
            attempt.Sha256 == RequestDigest(request);
    }

    private static string RequestDigest(AcquisitionRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json)));

    private Task<Entry<T>?> ReadAsync<T>(LifecycleKey key, CancellationToken cancellationToken) where T : class =>
        ReadAsync<T>(store, key, cancellationToken);

    private static async Task<Entry<T>?> ReadAsync<T>(
        DynamoDbLifecycleStore store, LifecycleKey key, CancellationToken cancellationToken)
        where T : class
    {
        var record = await store.ReadRecordAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }
        try
        {
            return record.Payload is { } payload && JsonSerializer.Deserialize<T>(payload, Json) is { } value
                ? new Entry<T>(record, value) : throw InvalidEvidence();
        }
        catch (JsonException)
        {
            throw InvalidEvidence();
        }
    }

    private static LifecycleRecordWrite DecrementBalance(Entry<AdmissionBalance>? balance, int released) =>
        balance is not null && balance.Value.HeldCount >= released
            ? Replace(balance, new AdmissionBalance(balance.Value.HeldCount - released)) : throw InvalidEvidence();

    private static LifecycleRecordWrite Release(Entry<Acquisition> entry, AdmissionEvidence evidence) =>
        Replace(entry, entry.Value with { State = AcquisitionState.Released, Evidence = evidence });

    private Task<LifecycleWriteOutcome> WriteAsync(IReadOnlyList<LifecycleRecordWrite> writes, CancellationToken cancellationToken) =>
        store.WriteSessionRecordsAsync(fence, Guid.NewGuid(), writes, cancellationToken);

    private static LifecycleRecordWrite NewWrite<T>(LifecycleKey key, T value) => Write<T>(key, null, value);

    private static LifecycleRecordWrite Replace<T>(Entry<T> entry, T value) => Write(entry.Record.Key, entry, value);

    private static LifecycleRecordWrite Write<T>(LifecycleKey key, Entry<T>? entry, T value) =>
        new(key, entry?.Record.Revision, JsonSerializer.Serialize(value, Json));

    private LifecycleKey MessageKey(long messageId) => LifecycleKey.Message(fence.Identity.Profile, fence.Identity.SessionId, messageId);

    private LifecycleKey JobKey(long requestId) => LifecycleKey.Job(fence.Identity.Profile, requestId.ToString(CultureInfo.InvariantCulture));

    private static InvalidDataException InvalidEvidence() => new("Listener delivery state does not match its recorded identity or evidence.");

    internal sealed record Entry<T>(LifecycleRecord Record, T Value);
    internal sealed record MessageReceipt(string Digest, DateTimeOffset ObservedAt, int JobOffset, AdmissionAttempt? Admission);
    internal sealed record WarmAdmissionCredit(int Count, DateTimeOffset ValidThrough, IReadOnlyList<LifecycleRecordCondition> Guards);
    internal sealed record CommittedAdmission(AdmissionAttempt Attempt, DateTimeOffset? WarmValidThrough);
    internal sealed record AdmissionAttempt(Guid Id, AcquisitionRequest Request, string Sha256, bool OutcomeRecorded);
    internal sealed record AcquisitionRequest(string ScopeIdentity, long ScaleSetId, Guid SessionId, long MessageId,
        ImmutableArray<long> RequestIds);
    internal sealed record JobObservation(long RequestId, ExecutorJobPhase Phase, long? RunnerId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RelatedRunnerId = null);
    private sealed record StoredJob(JobObservation Observation, Guid SessionId, long MessageId, DateTimeOffset ObservedAt,
        bool HasConflict = false);

    private sealed record AdmissionBalance(int HeldCount);

    private sealed record Acquisition(long RequestId, string ScopeIdentity, long ScaleSetId, Guid SessionId,
        long MessageId, Guid AttemptId, AcquisitionState State, AdmissionEvidence? Evidence)
    {
        [JsonIgnore]
        public bool IsHeld => State != AcquisitionState.Released;
    }

    private sealed record AdmissionEvidence(AdmissionEvidenceKind Kind, string Reference);

    private enum AdmissionEvidenceKind
    {
        Completed = 0,
        Assignment = 1,
        Rejected = 2,
        AbandonedBeforeSend = 3,
    }

    private enum AcquisitionState
    {
        Reserved = 0,
        Accepted = 1,
        Uncertain = 2,
        Released = 3,
    }
}
