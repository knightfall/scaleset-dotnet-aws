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
        bool rearm = effect is { Entry.State: EffectState.Settled, Entry.Resolution.Kind: EffectResolutionKind.AbandonedBeforeSend,
            Entry.Envelope.Request: FargateStopEffectRequest { Incarnation: not null } };
        if (effect is null || (effect.Entry.State == EffectState.Settled && !rearm))
        {
            return EffectDispatchStatus.NotPlanned;
        }
        if (effect.Entry.State != EffectState.Planned && !rearm)
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
        if (envelope.Request is FargateLaunchEffectRequest { OwnershipTags: not null } fargateLaunch &&
            !DynamoDbLifecycleStore.MatchesFargateLaunchCatalog(catalog, envelope, fargateLaunch))
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
        bool knownTerminalStop = IsKnownTerminalFargateStop(envelope.Request, allocation);
        if ((!AllocationAllows(envelope.Request, allocation, observedAfter) && !knownTerminalStop) ||
            Window(lease, clockEvidence) is not { } checkWindow)
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
            !IsCurrentObservation(observation.ObservedAt, observedAfter, now) ||
            (!AllocationAllows(envelope.Request, allocation, now) && !knownTerminalStop) ||
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

        var oldestEvidence = observation.ObservedAt;
        if (knownTerminalStop)
        {
            // The durable resource retains its earliest death observation. Bound this no-send settlement by fresh readback.
            if (envelope.Request is not FargateStopEffectRequest stop || observation.AlreadyApplied is not
                { Kind: EffectResolutionKind.Applied, AttemptId: null, ExecutorBinding: { } binding, ResourceObservation: { } terminal } applied ||
                applied.EnvelopeSha256 != envelope.Sha256 || applied.Target != envelope.Target || binding != allocation?.Binding ||
                !IsCurrentObservation(applied.ObservedAt, observedAfter, now) ||
                !IsCurrentObservation(terminal.ObservedAt, observedAfter, applied.ObservedAt) ||
                !FargateEffectProof.Matches(profile, stop, binding, terminal))
            {
                return EffectDispatchStatus.Ineligible;
            }
            if (terminal.ObservedAt < oldestEvidence)
            {
                oldestEvidence = terminal.ObservedAt;
            }
        }
        else if (allocation?.State.Resource is { } resource && resource.ObservedAt < oldestEvidence)
        {
            oldestEvidence = resource.ObservedAt;
        }
        var usable = Min(sendWindow.Duration, _timing.ObservationFreshness - (now - oldestEvidence));
        if (envelope.Request is FargateLaunchEffectRequest { OwnershipTags: not null } && envelope.ProviderReplayDeadline is { } cutoff)
        {
            usable = Min(usable, cutoff - sendWindow.UpperUtcNow - _timing.WriteReserve);
        }
        if (usable <= TimeSpan.Zero)
        {
            return EffectDispatchStatus.Ineligible;
        }
        LifecycleRecordCondition? stopAdmission = null;
        if (envelope.Request is FargateStopEffectRequest && observation.AlreadyApplied is null)
        {
            stopAdmission = await _store.ReadZeroFargateAcquisitionsAsync(profile, token).ConfigureAwait(false);
            if (stopAdmission is null)
            {
                return EffectDispatchStatus.Ineligible;
            }
        }
        var guard = new EffectDispatchGuard(catalog, index!, allocationId, allocation?.Revision, Ec2Lineage: lineage,
            FargateStopAdmission: stopAdmission);
        if (rearm)
        {
            var restored = await _journal.RearmAbandonedFargateStopAsync(lease, effect, guard, token).ConfigureAwait(false);
            if (restored != LifecycleWriteOutcome.Committed)
            {
                return DispatchOutcome(restored);
            }
            var planned = await _journal.ReadAsync(profile, intentId, token).ConfigureAwait(false);
            if (planned is not { Entry.State: EffectState.Planned } || !planned.Entry.Envelope.Matches(envelope))
            {
                return EffectDispatchStatus.Conflict;
            }
            effect = planned;
            index = await _store.ReadEffectIndexAsync(profile, token).ConfigureAwait(false);
            if (!ReadyIndex(index, lease))
            {
                return EffectDispatchStatus.RebuildRequired;
            }
            guard = guard with { Index = index! };
        }
        var deadline = sendWindow.UpperUtcNow + usable;
        if (Window(lease, clockEvidence) is not { } latestWindow || latestWindow.UpperUtcNow >= deadline)
        {
            return EffectDispatchStatus.Ineligible;
        }
        var begun = await _journal.BeginGuardedAsync(lease, effect, latestWindow.UpperUtcNow, deadline,
            _timing.WriteReserve, guard, token).ConfigureAwait(false);
        if (begun.AbandonedBeforeSend)
        {
            return trackedEc2Launch ? EffectDispatchStatus.Settled : EffectDispatchStatus.AwaitingEvidence;
        }
        if (begun.Permission is not { } permission)
        {
            return DispatchOutcome(begun.Outcome);
        }

        try
        {
            return await SendAsync(lease, clockEvidence, guard, permission, provider, token, observation.AlreadyApplied, observedAfter)
                .ConfigureAwait(false);
        }
        catch when (!permission.HasStartedSending && (trackedEc2Launch || envelope.Request is FargateStopEffectRequest { Incarnation: not null }))
        {
            _ = await AbandonNoSendAsync(lease, permission).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<EffectDispatchStatus> SendAsync(
        ControllerLease lease,
        ControllerClockEvidence clockEvidence,
        EffectDispatchGuard guard,
        EffectSendAuthorization permission,
        IReconciliationEffectProvider provider,
        CancellationToken cancellationToken,
        EffectEvidence? alreadyApplied = null,
        DateTimeOffset observationStarted = default)
    {
        var envelope = permission.Envelope;
        var effect = await _journal.ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
        if (effect is null || effect.Revision != permission.Revision || effect.Entry.State != EffectState.Executing ||
            effect.TransitionId != permission.BeginTransitionId || effect.Entry.Attempt != permission.Attempt || !effect.Entry.Envelope.Matches(envelope))
        {
            return await AbandonNoSendAsync(lease, permission).ConfigureAwait(false);
        }

        var allocation = await _store.ReadExecutorAsync(envelope.Profile, guard.AllocationId, cancellationToken).ConfigureAwait(false);
        var catalog = await _store.ReadCatalogProfileAsync(envelope.Profile, cancellationToken).ConfigureAwait(false);
        if (envelope.Request is FargateStopEffectRequest && alreadyApplied is null &&
            (guard.FargateStopAdmission is null || await _store.ReadZeroFargateAcquisitionsAsync(envelope.Profile, cancellationToken).ConfigureAwait(false) is null))
        {
            return await AbandonNoSendAsync(lease, permission).ConfigureAwait(false);
        }
        var current = await ReadCurrentLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
        if (allocation?.Revision != guard.AllocationRevision ||
            (!AllocationAllows(envelope.Request, allocation, _clock.GetUtcNow()) &&
                (alreadyApplied is null || !IsKnownTerminalFargateStop(envelope.Request, allocation))) ||
            catalog is null || catalog.Revision != guard.Catalog.Revision || catalog.TransitionId != guard.Catalog.TransitionId ||
            catalog.AppliedGeneration != guard.Catalog.AppliedGeneration || current is null ||
            Window(current, clockEvidence) is not { } window)
        {
            return await AbandonNoSendAsync(lease, permission).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (alreadyApplied is not null)
        {
            if (envelope.Request is not FargateStopEffectRequest || alreadyApplied is not
                { Kind: EffectResolutionKind.Applied, ResourceObservation.State: ExecutorResourceState.IrreversiblyTerminated })
            {
                return EffectDispatchStatus.AwaitingEvidence;
            }
            var settled = await ResolveObservedAsync(current, clockEvidence, effect, alreadyApplied, observationStarted, cancellationToken)
                .ConfigureAwait(false);
            return settled == LifecycleWriteOutcome.Committed ? EffectDispatchStatus.Settled : EffectDispatchStatus.AwaitingEvidence;
        }
        if (!permission.TryConsume(out var remaining))
        {
            return await AbandonNoSendAsync(lease, permission).ConfigureAwait(false);
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

    private async Task<EffectDispatchStatus> AbandonNoSendAsync(ControllerLease lease, EffectSendAuthorization permission)
    {
        var request = permission.Envelope.Request;
        if (request is Ec2LaunchEffectRequest { OwnershipTags: not null } or FargateStopEffectRequest { Incarnation: not null } &&
            await _journal.FinalizeInterruptedEffectAsync(lease, permission, _timing.WriteReserve).ConfigureAwait(false) ==
                LifecycleWriteOutcome.Committed)
        {
            return request is Ec2LaunchEffectRequest ? EffectDispatchStatus.Settled : EffectDispatchStatus.AwaitingEvidence;
        }
        return EffectDispatchStatus.AwaitingEvidence;
    }

    private static EffectDispatchStatus DispatchOutcome(LifecycleWriteOutcome outcome) => outcome switch
    {
        LifecycleWriteOutcome.Conflict => EffectDispatchStatus.Conflict,
        LifecycleWriteOutcome.LeaseRejected => EffectDispatchStatus.OwnershipUnavailable,
        LifecycleWriteOutcome.RebuildRequired => EffectDispatchStatus.RebuildRequired,
        _ => EffectDispatchStatus.AwaitingEvidence,
    };

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
        if (allocation is null)
        {
            return false;
        }
        if (request is FargateStopEffectRequest stop && MatchesFargateStop(stop, allocation.Binding) && ClosedForFargateStop(allocation.State, now))
        {
            return true;
        }
        if (!ExecutorClassifier.Classify(allocation.State, now, _timing.ObservationFreshness, _timing.LogFlushGrace).CompletedCleanupCandidate)
        {
            return false;
        }
        return request switch
        {
            Ec2TerminateEffectRequest value => allocation.Binding.ResourceArn == value.InstanceArn &&
                allocation.Binding.Provider is Ec2ExecutorIdentity identity && identity.InstanceLaunchTime == value.InstanceLaunchTime &&
                identity.LaunchTemplateId == value.LaunchTemplateId && identity.LaunchTemplateVersion == value.LaunchTemplateVersion,
            FargateStopEffectRequest value => MatchesFargateStop(value, allocation.Binding),
            _ => false,
        };
    }

    private bool ClosedForFargateStop(ExecutorState state, DateTimeOffset now) =>
        state is { HasConflict: false, Job: null, Process: null, Resource: { } resource } &&
        resource.State is ExecutorResourceState.Starting or ExecutorResourceState.Running &&
        IsCurrentObservation(resource.ObservedAt, DateTimeOffset.MinValue, now) &&
        state.AdmissionClosure switch
        {
            FargateAdmissionClosure.Unused => state.RunnerId is null,
            FargateAdmissionClosure.OfferRevoked => state.RunnerId is > 0,
            _ => false,
        };

    private static bool IsKnownTerminalFargateStop(EffectEnvelopeRequest request, ExecutorSnapshot? allocation) =>
        request is FargateStopEffectRequest stop &&
        allocation is { State.Resource.State: ExecutorResourceState.IrreversiblyTerminated, State.HasConflict: false } &&
        MatchesFargateStop(stop, allocation.Binding);

    private static bool MatchesFargateStop(FargateStopEffectRequest request, ExecutorBinding binding) =>
        binding.ResourceArn == request.TaskArn && (request.Incarnation is null || binding.Incarnation == request.Incarnation) &&
        binding.Provider is FargateExecutorIdentity identity && identity.ClusterArn == request.ClusterArn &&
        identity.TaskDefinitionArn == request.TaskDefinitionArn;

    private bool IsCurrentObservation(DateTimeOffset observedAt, DateTimeOffset started, DateTimeOffset now) =>
        observedAt >= started && observedAt <= now && now - observedAt <= _timing.ObservationFreshness;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private readonly record struct EffectWindow(DateTimeOffset UpperUtcNow, DateTimeOffset Deadline)
    {
        public TimeSpan Duration => Deadline - UpperUtcNow;
    }
}
