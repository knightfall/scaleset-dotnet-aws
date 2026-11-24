using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Client;
using ScaleSet.Listener;
using static Knightfall.ScaleSet.Aws.GitHub.ListenerDeliveryLedger;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed class GitHubListenerDeliveryHandler : IScaleSetListenerDeliveryHandler
{
    private static readonly TimeSpan DemandFreshness = TimeSpan.FromMinutes(10);
    private static readonly ImmutableHashSet<long> NoAcceptedRequests = ImmutableHashSet<long>.Empty;

    private readonly DynamoDbLifecycleStore _store;
    private readonly OwnedSessionFence _owned;
    private readonly IMessageSessionClient _session;
    private readonly Func<CancellationToken, Task<ListenerCapacity>> _readCapacity;
    private readonly TimeProvider _timeProvider;
    private readonly bool _releaseOnAssignment;
    private readonly ListenerDeliveryLedger _ledger;

    public GitHubListenerDeliveryHandler(
        DynamoDbLifecycleStore store, OwnedSessionFence owned, IMessageSessionClient session,
        Func<CancellationToken, Task<ListenerCapacity>> readCapacity, TimeProvider? timeProvider = null,
        bool releaseOnAssignment = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(readCapacity);
        _store = store;
        _owned = owned;
        _session = session;
        _readCapacity = readCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _releaseOnAssignment = releaseOnAssignment;
        _ledger = new ListenerDeliveryLedger(store, owned);
    }

    public async Task<int> ReadPollCapacityAsync(CancellationToken cancellationToken)
    {
        var basis = await ReadBasisAsync(cancellationToken).ConfigureAwait(false);
        if (basis is null || !IsCurrent(basis.Demand?.Value))
        {
            return 0;
        }
        return basis.Capacity.Maximum;
    }

    public async Task<ScaleSetListenerDeliveryDisposition> HandleAsync(
        ScaleSetListenerDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        RequireOwnedSession(delivery.SessionKey);
        if (delivery.Kind == ScaleSetListenerDeliveryKind.EmptyPoll)
        {
            return ScaleSetListenerDeliveryDisposition.Completed;
        }

        if (delivery.Kind == ScaleSetListenerDeliveryKind.InitialStatistics)
        {
            bool recorded = await _ledger.PersistInitialStatisticsAsync(
                delivery.ObservedAt, delivery.InitialStatistics!.TotalAssignedJobs, cancellationToken).ConfigureAwait(false);
            return Disposition(recorded);
        }

        var message = delivery.Message!;
        var facts = GetFacts(message);
        string digest = DeliveryDigest(facts);
        string? priorDigest = facts.Jobs.Any(job => job.RelatedRunnerId is not null)
            ? DeliveryDigest(facts with { Jobs = facts.Jobs.Select(job => job with { RelatedRunnerId = null }).ToArray() })
            : null;
        long messageId = message.MessageId;
        var receipt = await _ledger.ReadReceiptAsync(messageId, digest, cancellationToken, priorDigest).ConfigureAwait(false);
        if (receipt is null)
        {
            var result = await _ledger.PersistReceiptAsync(messageId, digest, delivery.ObservedAt, facts.Assigned, cancellationToken)
                .ConfigureAwait(false);
            if (result != LifecycleWriteOutcome.Committed)
            {
                return ScaleSetListenerDeliveryDisposition.Deferred;
            }
            receipt = await RequireReceiptAsync(messageId, digest, cancellationToken).ConfigureAwait(false);
        }

        if (receipt.Value.JobOffset > facts.Jobs.Length)
        {
            throw new InvalidDataException("Listener job progress exceeds its recorded delivery.");
        }
        for (int offset = receipt.Value.JobOffset; offset < facts.Jobs.Length; offset = receipt.Value.JobOffset)
        {
            int count = Math.Min(MaximumJobsPerTransaction, facts.Jobs.Length - offset);
            var result = await _ledger.PersistJobObservationsAsync(receipt, messageId,
                new ArraySegment<JobObservation>(facts.Jobs, offset, count), _releaseOnAssignment, cancellationToken).ConfigureAwait(false);
            if (result != LifecycleWriteOutcome.Committed)
            {
                return ScaleSetListenerDeliveryDisposition.Deferred;
            }
            receipt = await RequireReceiptAsync(messageId, receipt.Value.Digest, cancellationToken).ConfigureAwait(false);
        }

        return await HandleOffersAsync(receipt, messageId, facts.OfferedIds, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ConfirmDefinitiveRejectionAsync(
        long requestId, Guid attemptId, Guid evidenceId, CancellationToken cancellationToken) =>
        _ledger.ConfirmDefinitiveRejectionAsync(requestId, attemptId, evidenceId, cancellationToken);

    private async Task<ScaleSetListenerDeliveryDisposition> HandleOffersAsync(
        Entry<MessageReceipt> receipt, long messageId, ImmutableArray<long> offeredIds, CancellationToken cancellationToken)
    {
        if (receipt.Value.Admission is { } recorded)
        {
            if (recorded.OutcomeRecorded)
            {
                return ScaleSetListenerDeliveryDisposition.Completed;
            }
            var carried = await _ledger.SettleAttemptAsync(receipt, messageId, NoAcceptedRequests, cancellationToken).ConfigureAwait(false);
            return Disposition(carried == LifecycleWriteOutcome.Committed);
        }

        var basis = await ReadBasisAsync(cancellationToken).ConfigureAwait(false);
        if (basis is null)
        {
            return ScaleSetListenerDeliveryDisposition.Deferred;
        }
        int allowance = IsCurrent(basis.Demand?.Value)
            ? (int)Math.Max(0L, (long)basis.Capacity.Maximum - basis.Capacity.OutsideAssigned - basis.Demand!.Value.TotalAssignedJobs) : 0;
        var attempt = await _ledger.ReserveAsync(receipt, messageId, offeredIds, allowance, basis.Guards, cancellationToken)
            .ConfigureAwait(false);
        if (attempt?.Request.RequestIds.IsEmpty != false)
        {
            return Disposition(attempt is not null);
        }

        // Only the caller that received the successful reservation commit can reach the wire.
        RequireOwnedSession();
        if (!await _store.HasSessionOwnershipAsync(_owned, cancellationToken).ConfigureAwait(false))
        {
            return ScaleSetListenerDeliveryDisposition.Deferred;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = NoAcceptedRequests;
        GitHubOperationException? cooldown = null;
        try
        {
            using var retry = ScaleSetRequestRetry.Configure(static options => options with { MaxRetries = 0 });
            var response = await _session.AcquireJobsAsync(attempt.Request.RequestIds, cancellationToken).ConfigureAwait(false);
            accepted = AcceptedSubset(response, attempt.Request.RequestIds);
        }
        catch (Exception issue) when (issue is ScaleSetApiException or HttpRequestException or IOException or JsonException or
                                      TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (issue is ScaleSetRateLimitException or ScaleSetApiException { StatusCode: 401 })
            {
                cooldown = GitHubOperationException.From(issue);
            }
        }

        receipt = await RequireReceiptAsync(messageId, receipt.Value.Digest, cancellationToken).ConfigureAwait(false);
        var settled = await _ledger.SettleAttemptAsync(receipt, messageId, accepted, cancellationToken).ConfigureAwait(false);
        if (cooldown is not null)
        {
            throw cooldown;
        }
        return Disposition(settled == LifecycleWriteOutcome.Committed);
    }

    private async Task<AdmissionBasis?> ReadBasisAsync(CancellationToken cancellationToken)
    {
        var profile = _owned.Identity.Profile;
        var effects = await _store.ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        if (effects?.State != EffectIndexState.Ready || effects.OwnerEpoch != _owned.Lease.Epoch ||
            effects.WriterProtocol != _owned.Lease.WriterProtocol)
        {
            return null;
        }

        var demand = await _ledger.ReadDemandAsync(cancellationToken).ConfigureAwait(false);
        var capacity = await _readCapacity(cancellationToken).ConfigureAwait(false);
        if (capacity.Maximum < 0 || capacity.OutsideAssigned < 0)
        {
            throw new InvalidDataException("Listener capacity values must be nonnegative.");
        }
        if (!capacity.IsReady)
        {
            capacity = capacity with { Maximum = 0 };
        }

        var demandKey = LifecycleKey.Demand(profile);
        var effectsKey = LifecycleKey.Effects(profile);
        var guards = new Dictionary<LifecycleKey, long?>
        {
            [demandKey] = demand?.Record.Revision,
            [effectsKey] = effects.Revision,
        };
        foreach (var guard in capacity.ReadGuards ?? [])
        {
            if (!guards.TryAdd(guard.Key, guard.ExpectedRevision) && guards[guard.Key] != guard.ExpectedRevision)
            {
                return null;
            }
        }
        return new AdmissionBasis(demand, capacity, guards);
    }

    private bool IsCurrent(SessionDemandObservation? demand)
    {
        var now = _timeProvider.GetUtcNow();
        return demand is not null && demand.SessionId == _owned.Identity.SessionId &&
            demand.ObservedAt <= now && now - demand.ObservedAt <= DemandFreshness;
    }

    private async Task<Entry<MessageReceipt>> RequireReceiptAsync(long messageId, string digest, CancellationToken cancellationToken) =>
        await _ledger.ReadReceiptAsync(messageId, digest, cancellationToken).ConfigureAwait(false) ??
        throw new InvalidDataException("The committed listener receipt is missing.");

    private void RequireOwnedSession(ScaleSetListenerSessionKey? key = null)
    {
        var bound = _owned.Identity;
        var snapshot = _session.Session;
        if (snapshot.SessionId != bound.SessionId ||
            (snapshot.RunnerScaleSet is { Id: > 0 } scaleSet && scaleSet.Id != bound.ScaleSetId) ||
            (key is not null && (key.Scope != bound.ScopeIdentity || key.ScaleSetId != bound.ScaleSetId || key.SessionId != bound.SessionId)))
        {
            throw new InvalidDataException("Listener delivery does not belong to the owned queue session.");
        }
    }

    private static DeliveryFacts GetFacts(RunnerScaleSetMessage message)
    {
        var offeredIds = message.JobAvailableMessages.Select(job => job.RunnerRequestId)
            .Where(requestId => requestId > 0).Distinct().ToImmutableArray();
        var jobs = message.JobCompletedMessages.Select(job => new JobObservation(job.RunnerRequestId, ExecutorJobPhase.Completed, PositiveId(job.RunnerId)))
            .Concat(message.JobStartedMessages.Select(job => new JobObservation(job.RunnerRequestId, ExecutorJobPhase.Started, PositiveId(job.RunnerId))))
            .Concat(message.JobAssignedMessages.Select(job => new JobObservation(job.RunnerRequestId, ExecutorJobPhase.Assigned, null)))
            .Where(job => job.RequestId > 0)
            .GroupBy(job => job.RequestId)
            .Select(MergeJobFacts)
            .OrderBy(job => job.RequestId)
            .ToArray();
        return new DeliveryFacts(message.Statistics?.TotalAssignedJobs, offeredIds, jobs);
    }

    private static ImmutableHashSet<long> AcceptedSubset(IReadOnlyList<long> response, ImmutableArray<long> sent)
    {
        var accepted = ImmutableHashSet.CreateBuilder<long>();
        foreach (long request in response)
        {
            if (!sent.Contains(request) || !accepted.Add(request))
            {
                return NoAcceptedRequests;
            }
        }
        return accepted.ToImmutable();
    }

    private static long? PositiveId(long value) => value > 0 ? value : null;

    private static string DeliveryDigest(DeliveryFacts facts) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(facts)));

    private static JobObservation MergeJobFacts(IGrouping<long, JobObservation> observations)
    {
        var selected = observations.First();
        var others = observations.Where(job => job.RunnerId is not null && job.RunnerId != selected.RunnerId)
            .Select(job => job.RunnerId).Distinct().Take(2).ToArray();
        if (others.Length == 2)
        {
            throw new InvalidDataException("GitHub delivery contains incompatible runner identities for a job.");
        }
        return selected with { RelatedRunnerId = others.SingleOrDefault() };
    }

    private static ScaleSetListenerDeliveryDisposition Disposition(bool committed) => committed
        ? ScaleSetListenerDeliveryDisposition.Completed : ScaleSetListenerDeliveryDisposition.Deferred;

    private sealed record DeliveryFacts(int? Assigned, ImmutableArray<long> OfferedIds, JobObservation[] Jobs);

    private sealed record AdmissionBasis(
        Entry<SessionDemandObservation>? Demand, ListenerCapacity Capacity, IReadOnlyDictionary<LifecycleKey, long?> Guards);
}
