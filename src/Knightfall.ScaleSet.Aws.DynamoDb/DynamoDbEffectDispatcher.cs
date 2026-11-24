using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record EffectDispatcherTiming(
    TimeSpan OperationBudget, TimeSpan WriteReserve, TimeSpan ObservationFreshness, TimeSpan LogFlushGrace);

public enum EffectDispatchStatus
{
    NotPlanned = 0,
    Ineligible = 1,
    OwnershipUnavailable = 2,
    Conflict = 3,
    RebuildRequired = 4,
    AwaitingEvidence = 5,
    Settled = 6,
    StalePlan = 7,
}

public sealed partial class DynamoDbEffectDispatcher
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly DynamoDbEffectJournal _journal;
    private readonly TimeProvider _clock;
    private readonly EffectDispatcherTiming _timing;

    public DynamoDbEffectDispatcher(DynamoDbLifecycleStore store, TimeProvider clock, EffectDispatcherTiming timing)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timing);
        if (timing.OperationBudget <= TimeSpan.Zero || timing.OperationBudget > TimeSpan.FromMilliseconds(uint.MaxValue - 1) ||
            timing.WriteReserve <= TimeSpan.Zero || timing.ObservationFreshness <= TimeSpan.Zero || timing.LogFlushGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timing), "Effect timing bounds must be positive and the operation budget must fit a timer.");
        }
        _store = store;
        _clock = clock;
        _timing = timing;
        _journal = new DynamoDbEffectJournal(store, clock);
    }

    public async Task<EffectDispatchStatus> DispatchPlannedAsync(
        ControllerLease expected,
        ControllerClockEvidence clockEvidence,
        LifecycleProfile profile,
        string intentId,
        IReconciliationEffectProvider provider,
        CancellationToken ownershipLost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(clockEvidence);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(provider);
        if (expected.Mode != ControllerLeaseMode.Active || expected.Deployment != profile.Deployment ||
            ownershipLost.IsCancellationRequested)
        {
            return EffectDispatchStatus.OwnershipUnavailable;
        }

        using var owner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownershipLost);
        var token = owner.Token;
        var lease = await ReadCurrentLeaseAsync(expected, token).ConfigureAwait(false);
        if (lease is null || Window(lease, clockEvidence) is null)
        {
            return EffectDispatchStatus.OwnershipUnavailable;
        }
        var effect = await _journal.ReadAsync(profile, intentId, token).ConfigureAwait(false);
        if (effect is null || effect.Entry.State == EffectState.Settled)
        {
            return EffectDispatchStatus.NotPlanned;
        }
        if (effect.Entry.State != EffectState.Planned)
        {
            return EffectDispatchStatus.AwaitingEvidence;
        }

        var envelope = effect.Entry.Envelope;
        var index = await _store.ReadEffectIndexAsync(profile, token).ConfigureAwait(false);
        if (!ReadyIndex(index, lease))
        {
            return EffectDispatchStatus.RebuildRequired;
        }
        var catalog = await _store.ReadCatalogProfileAsync(profile, token).ConfigureAwait(false);
        bool trackedEc2Launch = envelope.Request is Ec2LaunchEffectRequest { OwnershipTags: not null };
        if (trackedEc2Launch && catalog is not null && catalog.AppliedGeneration > envelope.Generation &&
            await _store.ReadCatalogRolloutAsync(profile.Deployment, token).ConfigureAwait(false) is
                { AppliedEpoch: var appliedEpoch, AppliedGeneration: var appliedGeneration, Pending: null } &&
            appliedEpoch == lease.Epoch && appliedGeneration == catalog.AppliedGeneration)
        {
            return EffectDispatchStatus.StalePlan;
        }
        if (envelope.IntentId != EffectIntentIdentity.ForAllocation(envelope.Request) ||
            catalog is null || catalog.AppliedGeneration != envelope.Generation ||
            catalog.Binding.ScaleSetId is not > 0 ||
            catalog.State is not (CatalogProfileState.Active or CatalogProfileState.Draining) ||
            RequestAllocation(envelope.Request, catalog.Binding) is not { } allocationId)
        {
            return EffectDispatchStatus.Ineligible;
        }

        Ec2LaunchLineageSnapshot? lineage = null;
        if (envelope.Request is Ec2LaunchEffectRequest { OwnershipTags: not null } launch)
        {
            lineage = await _store.ReadEc2LaunchLineageAsync(profile, launch.AllocationId, token).ConfigureAwait(false);
            if (!DynamoDbLifecycleStore.MatchesEc2Lineage(lineage, envelope))
            {
                return EffectDispatchStatus.Ineligible;
            }
        }
        var allocation = await _store.ReadExecutorAsync(profile, allocationId, token).ConfigureAwait(false);
        var observedAfter = _clock.GetUtcNow();
        if (!AllocationAllows(envelope.Request, allocation, observedAfter) || Window(lease, clockEvidence) is not { } checkWindow)
        {
            return EffectDispatchStatus.Ineligible;
        }
        var observation = await CallBoundedAsync(
            operation => provider.CheckAsync(envelope, catalog.Binding, operation), checkWindow.Duration, token).ConfigureAwait(false);
        lease = await ReadCurrentLeaseAsync(lease, token).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (lease is null || Window(lease, clockEvidence) is not { } sendWindow)
        {
            return EffectDispatchStatus.OwnershipUnavailable;
        }
        if (observation is null || observation.EnvelopeSha256 != envelope.Sha256 || observation.Target != envelope.Target ||
            !IsCurrentObservation(observation.ObservedAt, observedAfter, now) || !AllocationAllows(envelope.Request, allocation, now) ||
            sendWindow.UpperUtcNow < envelope.EarliestPossibleFirstSend)
        {
            return EffectDispatchStatus.Ineligible;
        }
        if (observation.DefinitivelyObsolete)
        {
            return trackedEc2Launch && !observation.Eligible ? EffectDispatchStatus.StalePlan : EffectDispatchStatus.Ineligible;
        }
        if (!observation.Eligible)
        {
            return EffectDispatchStatus.Ineligible;
        }

        var oldestEvidence = allocation?.State.Resource is { } resource && resource.ObservedAt < observation.ObservedAt
            ? resource.ObservedAt : observation.ObservedAt;
        var usable = Min(sendWindow.Duration, _timing.ObservationFreshness - (now - oldestEvidence));
        if (usable <= TimeSpan.Zero)
        {
            return EffectDispatchStatus.Ineligible;
        }
        var guard = new EffectDispatchGuard(catalog, index!, allocationId, allocation?.Revision, Ec2Lineage: lineage);
        var begun = await _journal.BeginGuardedAsync(lease, effect, sendWindow.UpperUtcNow, sendWindow.UpperUtcNow + usable,
            _timing.WriteReserve, guard, token).ConfigureAwait(false);
        if (begun.AbandonedBeforeSend)
        {
            return EffectDispatchStatus.Settled;
        }
        if (begun.Permission is not { } permission)
        {
            return begun.Outcome switch
            {
                LifecycleWriteOutcome.Conflict => EffectDispatchStatus.Conflict,
                LifecycleWriteOutcome.LeaseRejected => EffectDispatchStatus.OwnershipUnavailable,
                LifecycleWriteOutcome.RebuildRequired => EffectDispatchStatus.RebuildRequired,
                _ => EffectDispatchStatus.AwaitingEvidence,
            };
        }

        return await SendAsync(lease, clockEvidence, guard, permission, provider, token).ConfigureAwait(false);
    }

    private async Task<EffectDispatchStatus> SendAsync(
        ControllerLease lease,
        ControllerClockEvidence clockEvidence,
        EffectDispatchGuard guard,
        EffectSendAuthorization permission,
        IReconciliationEffectProvider provider,
        CancellationToken cancellationToken)
    {
        var envelope = permission.Envelope;
        var effect = await _journal.ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
        if (effect is null || effect.Revision != permission.Revision || effect.Entry.State != EffectState.Executing ||
            effect.TransitionId != permission.BeginTransitionId || effect.Entry.Attempt != permission.Attempt || !effect.Entry.Envelope.Matches(envelope))
        {
            return await AbandonNoSendAsync(lease, permission, cancellationToken).ConfigureAwait(false);
        }

        var allocation = await _store.ReadExecutorAsync(envelope.Profile, guard.AllocationId, cancellationToken).ConfigureAwait(false);
        var catalog = await _store.ReadCatalogProfileAsync(envelope.Profile, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
        if (allocation?.Revision != guard.AllocationRevision || !AllocationAllows(envelope.Request, allocation, _clock.GetUtcNow()) ||
            catalog is null || catalog.Revision != guard.Catalog.Revision || catalog.TransitionId != guard.Catalog.TransitionId ||
            catalog.AppliedGeneration != guard.Catalog.AppliedGeneration || current is null ||
            Window(current, clockEvidence) is not { } window || !permission.TryConsume(out var remaining))
        {
            return await AbandonNoSendAsync(lease, permission, cancellationToken).ConfigureAwait(false);
        }

        var started = _clock.GetUtcNow();
        var evidence = await CallBoundedAsync(
            token => provider.SendAsync(envelope, permission.Attempt, token), Min(remaining, window.Duration), cancellationToken).ConfigureAwait(false);
        if (evidence is null)
        {
            return EffectDispatchStatus.AwaitingEvidence;
        }

        var outcome = await ResolveObservedAsync(current, clockEvidence, effect, evidence, started, cancellationToken,
            sent: permission).ConfigureAwait(false);
        return outcome == LifecycleWriteOutcome.Committed ? EffectDispatchStatus.Settled : EffectDispatchStatus.AwaitingEvidence;
    }

    private async Task<EffectDispatchStatus> AbandonNoSendAsync(
        ControllerLease lease, EffectSendAuthorization permission, CancellationToken cancellationToken)
    {
        if (permission.Envelope.Request is Ec2LaunchEffectRequest { OwnershipTags: not null } &&
            await _journal.AbandonUnsentEffectAsync(lease, permission, cancellationToken).ConfigureAwait(false) == LifecycleWriteOutcome.Committed)
        {
            return EffectDispatchStatus.Settled;
        }
        return EffectDispatchStatus.AwaitingEvidence;
    }

    private async Task<ControllerLease?> ReadCurrentLeaseAsync(ControllerLease expected, CancellationToken cancellationToken)
    {
        var current = await _store.ReadLeaseAsync(expected.Deployment, cancellationToken).ConfigureAwait(false);
        return current is not null && current.Fence == expected.Fence && current.Mode == expected.Mode && current.ValidUntil > _clock.GetUtcNow()
            ? current : null;
    }

    private EffectWindow? Window(ControllerLease lease, ControllerClockEvidence proof)
    {
        if (!proof.TryGetUpperBound(_clock.GetUtcNow(), lease.ValidUntil, _timing.WriteReserve, out var upper))
        {
            return null;
        }
        var deadline = lease.ValidUntil - _timing.WriteReserve;
        if (proof.ValidUntil < deadline)
        {
            deadline = proof.ValidUntil;
        }
        var duration = Min(deadline - upper, _timing.OperationBudget);
        return duration > TimeSpan.Zero ? new EffectWindow(upper, upper + duration) : null;
    }

    private async Task<T?> CallBoundedAsync<T>(Func<CancellationToken, Task<T?>> operation, TimeSpan budget, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = new CancellationTokenSource(budget, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            bounded.Token.ThrowIfCancellationRequested();
            var result = await operation(bounded.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return timeout.IsCancellationRequested ? null : result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException)
        {
            return null;
        }
    }

    private static bool ReadyIndex(EffectIndexHead? index, ControllerLease lease) =>
        index is { State: EffectIndexState.Ready } && index.OwnerEpoch == lease.Epoch && index.WriterProtocol == lease.WriterProtocol;

    private static string? RequestAllocation(EffectEnvelopeRequest request, CatalogBinding binding) => request switch
    {
        Ec2LaunchEffectRequest value when binding.ProvisionerType == RunnerProvisionerType.Ec2 &&
            value.LaunchTemplateId == binding.ProviderDefinition => value.AllocationId.ToString("D"),
        Ec2TerminateEffectRequest value when binding.ProvisionerType == RunnerProvisionerType.Ec2 &&
            value.LaunchTemplateId == binding.ProviderDefinition => value.AllocationId.ToString("D"),
        FargateLaunchEffectRequest value when binding.ProvisionerType == RunnerProvisionerType.Fargate &&
            value.TaskDefinitionArn == binding.ProviderDefinition && value.TaskRoleArn == binding.BootstrapRoleArn => value.AllocationId.ToString("D"),
        FargateStopEffectRequest value when binding.ProvisionerType == RunnerProvisionerType.Fargate &&
            value.TaskDefinitionArn == binding.ProviderDefinition => value.AllocationId.ToString("D"),
        _ => null,
    };

    private bool AllocationAllows(EffectEnvelopeRequest request, ExecutorSnapshot? allocation, DateTimeOffset now)
    {
        if (request is Ec2LaunchEffectRequest or FargateLaunchEffectRequest)
        {
            return allocation is null;
        }
        if (allocation is null || !ExecutorClassifier.Classify(allocation.State, now, _timing.ObservationFreshness, _timing.LogFlushGrace)
            .CompletedCleanupCandidate)
        {
            return false;
        }
        return request switch
        {
            Ec2TerminateEffectRequest value => allocation.Binding.ResourceArn == value.InstanceArn &&
                allocation.Binding.Provider is Ec2ExecutorIdentity identity && identity.InstanceLaunchTime == value.InstanceLaunchTime &&
                identity.LaunchTemplateId == value.LaunchTemplateId && identity.LaunchTemplateVersion == value.LaunchTemplateVersion,
            FargateStopEffectRequest value => allocation.Binding.ResourceArn == value.TaskArn &&
                allocation.Binding.Provider is FargateExecutorIdentity identity && identity.ClusterArn == value.ClusterArn &&
                identity.TaskDefinitionArn == value.TaskDefinitionArn,
            _ => false,
        };
    }

    private bool IsCurrentObservation(DateTimeOffset observedAt, DateTimeOffset started, DateTimeOffset now) =>
        observedAt >= started && observedAt <= now && now - observedAt <= _timing.ObservationFreshness;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private readonly record struct EffectWindow(DateTimeOffset UpperUtcNow, DateTimeOffset Deadline)
    {
        public TimeSpan Duration => Deadline - UpperUtcNow;
    }
}
