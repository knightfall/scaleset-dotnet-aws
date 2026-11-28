using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbEffectDispatcher
{
    /// <summary>Observes or replays one immutable RunTask request within the same active lease epoch.</summary>
    public async Task<EffectDispatchStatus> ReplayFargateLaunchAsync(ControllerLease expected, ControllerClockEvidence clockEvidence,
        LifecycleProfile profile, string intentId, IFargateReplayEffectProvider provider, CancellationToken ownershipLost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(clockEvidence);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(provider);
        using var telemetry = DynamoDbDiagnostics.Start("fargate-replay", cancellationToken);
        if (expected.Mode != ControllerLeaseMode.Active || expected.Deployment != profile.Deployment || ownershipLost.IsCancellationRequested)
        {
            return telemetry.Complete(EffectDispatchStatus.OwnershipUnavailable, "ownership");
        }

        using var owner = CancellationTokenSource.CreateLinkedTokenSource(ownershipLost, cancellationToken);
        var token = owner.Token;
        var lease = await ReadCurrentLeaseAsync(expected, token).ConfigureAwait(false);
        if (lease is null || Window(lease, clockEvidence) is not { } readWindow)
        {
            return telemetry.Complete(EffectDispatchStatus.OwnershipUnavailable, "ownership");
        }
        var previous = await _journal.ReadAsync(profile, intentId, token).ConfigureAwait(false);
        if (previous is not { Entry.State: EffectState.Executing or EffectState.Uncertain, Entry.Attempt: { } attempt,
                Entry.LatestAuthorizedDeadline: { } previousDeadline,
                Entry.Envelope: { Request: FargateLaunchEffectRequest { OwnershipTags: not null } request, ProviderReplayDeadline: { } cutoff } envelope } ||
            envelope.IntentId != EffectIntentIdentity.ForAllocation(request))
        {
            return telemetry.Complete(EffectDispatchStatus.NotPlanned, "journal_state");
        }
        if (attempt.LeaseEpoch != lease.Epoch)
        {
            return telemetry.Complete(EffectDispatchStatus.OwnershipUnavailable, "ownership");
        }

        var observedAfter = _clock.GetUtcNow();
        var probe = await CallBoundedAsync(ct => provider.ObserveForReplayAsync(envelope, attempt, ct), readWindow.Duration, token)
            .ConfigureAwait(false);
        if (probe is not { IsComplete: true } || probe.EnvelopeSha256 != envelope.Sha256 || probe.Target != envelope.Target ||
            !IsCurrentObservation(probe.ObservedAt, observedAfter, _clock.GetUtcNow()))
        {
            return telemetry.Complete(EffectDispatchStatus.AwaitingEvidence, "incomplete_readback");
        }
        if (probe.Evidence is { } evidence)
        {
            var settled = await ResolveObservedAsync(lease, clockEvidence, previous, evidence, observedAfter, token).ConfigureAwait(false);
            return telemetry.Complete(settled == LifecycleWriteOutcome.Committed ? EffectDispatchStatus.Settled :
                EffectDispatchStatus.AwaitingEvidence, "readback");
        }

        var index = await _store.ReadEffectIndexAsync(profile, token).ConfigureAwait(false);
        var catalog = await _store.ReadCatalogProfileAsync(profile, token).ConfigureAwait(false);
        string allocation = request.AllocationId.ToString("D");
        var executor = await _store.ReadExecutorAsync(profile, allocation, token).ConfigureAwait(false);
        lease = await ReadCurrentLeaseAsync(lease, token).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (lease is null || Window(lease, clockEvidence) is not { } sendWindow)
        {
            return telemetry.Complete(EffectDispatchStatus.OwnershipUnavailable, "ownership");
        }
        if (!ReadyIndex(index, lease))
        {
            return telemetry.Complete(EffectDispatchStatus.RebuildRequired, "index_rebuild");
        }
        if (catalog is null || !DynamoDbLifecycleStore.MatchesFargateLaunchCatalog(catalog, envelope, request) || executor is not null)
        {
            return telemetry.Complete(EffectDispatchStatus.Ineligible, "allocation_guard");
        }

        var lower = now - clockEvidence.MaximumAbsoluteSkew;
        var deadline = sendWindow.UpperUtcNow + Min(sendWindow.Duration, _timing.ObservationFreshness - (now - probe.ObservedAt));
        var replayDeadline = cutoff - _timing.WriteReserve;
        if (replayDeadline < deadline)
        {
            deadline = replayDeadline;
        }
        if (probe.ObservedAt - clockEvidence.MaximumAbsoluteSkew < previousDeadline || lower < previousDeadline ||
            lower > sendWindow.UpperUtcNow || deadline <= sendWindow.UpperUtcNow)
        {
            return telemetry.Complete(EffectDispatchStatus.AwaitingEvidence, "replay_window");
        }

        // An ambiguous first attempt may never have reached ECS. Revalidate the immutable launch before granting a resend.
        var checkedAfter = now;
        var precondition = await CallBoundedAsync(ct => provider.CheckReplayAsync(envelope, catalog.Binding, ct),
            deadline - sendWindow.UpperUtcNow, token).ConfigureAwait(false);
        lease = await ReadCurrentLeaseAsync(lease, token).ConfigureAwait(false);
        now = _clock.GetUtcNow();
        if (lease is null || Window(lease, clockEvidence) is not { } currentWindow)
        {
            return telemetry.Complete(EffectDispatchStatus.OwnershipUnavailable, "ownership");
        }
        if (precondition is not { Eligible: true } || precondition.EnvelopeSha256 != envelope.Sha256 ||
            precondition.Target != envelope.Target || !IsCurrentObservation(precondition.ObservedAt, checkedAfter, now))
        {
            return telemetry.Complete(EffectDispatchStatus.AwaitingEvidence, "launch_precondition");
        }
        lower = now - clockEvidence.MaximumAbsoluteSkew;
        deadline = deadline < currentWindow.Deadline ? deadline : currentWindow.Deadline;
        if (lower < previousDeadline || lower > currentWindow.UpperUtcNow || deadline <= currentWindow.UpperUtcNow)
        {
            return telemetry.Complete(EffectDispatchStatus.AwaitingEvidence, "replay_window");
        }

        var guard = new EffectDispatchGuard(catalog, index!, allocation, null);
        var begun = await _journal.BeginFargateReplayAsync(lease, previous, lower, currentWindow.UpperUtcNow, deadline,
            _timing.WriteReserve, guard, token).ConfigureAwait(false);
        if (begun.Permission is { } permission)
        {
            var result = await SendAsync(lease, clockEvidence, guard, permission, provider, token).ConfigureAwait(false);
            return telemetry.Complete(result, permission.HasStartedSending ? "resend_authorized" : "resend_suppressed");
        }
        return telemetry.Complete(begun.Outcome switch
        {
            LifecycleWriteOutcome.Conflict => EffectDispatchStatus.Conflict,
            LifecycleWriteOutcome.LeaseRejected => EffectDispatchStatus.OwnershipUnavailable,
            LifecycleWriteOutcome.RebuildRequired => EffectDispatchStatus.RebuildRequired,
            _ => EffectDispatchStatus.AwaitingEvidence,
        }, "guarded_begin");
    }
}
