using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubJitHandoff
{
    public async Task<LifecycleWriteOutcome> RemoveTerminatedRunnerAsync(
        ActiveControllerAuthority authority, LifecycleProfile profile, Guid allocationId, GitHubScaleSetConnection connection,
        CancellationToken ownershipLost, CancellationToken cancellationToken)
    {
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(ownershipLost, cancellationToken);
        var token = owner.Token;
        var lease = await ReadCurrentAsync(authority.Lease, token).ConfigureAwait(false);
        if (lease is not { Mode: ControllerLeaseMode.Active } || lease.Deployment != profile.Deployment || Window(lease, authority.Clock) is null)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        var handoff = await _store.ReadJitHandoffAsync(profile, allocationId, token).ConfigureAwait(false);
        var executor = await _store.ReadExecutorAsync(profile, allocationId.ToString("D"), token).ConfigureAwait(false);
        if (handoff is null || handoff.Identity.ScopeIdentity != connection.ScopeIdentity ||
            executor is not { State: { Resource.State: ExecutorResourceState.IrreversiblyTerminated, HasConflict: false } } ||
            executor.Binding.ResourceArn != handoff.Identity.ResourceArn || executor.Binding.Incarnation != handoff.Identity.Incarnation)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var generation = await _journal.ReadAsync(profile, handoff.IntentId, token).ConfigureAwait(false);
        if (handoff.RunnerId is not long runnerId)
        {
            return (await _store.CompleteTerminatedJitHandoffAsync(lease, handoff, generation, null, token).ConfigureAwait(false)).Outcome;
        }
        if (!DynamoDbEffectJournal.AllowsRemoval(executor, runnerId) || generation is not { Entry.State: EffectState.Settled } ||
            generation.Entry.Resolution?.Kind != EffectResolutionKind.Applied || !ConfirmedCreate(generation, handoff))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        if (handoff.State == JitHandoffState.Offered)
        {
            var revoked = await _store.RevokeJitHandoffAsync(lease, handoff, token).ConfigureAwait(false);
            if (revoked.Outcome == LifecycleWriteOutcome.Indeterminate)
            {
                return revoked.Outcome;
            }
            handoff = revoked.Handoff ?? handoff;
        }
        string intentId = "remove-" + handoff.OfferId;
        var removal = await _journal.ReadAsync(profile, intentId, token).ConfigureAwait(false);
        if (removal is not { Entry.State: EffectState.Settled })
        {
            var request = new GitHubRunnerRemoveEffectRequest(handoff.Identity.ScopeIdentity, handoff.Identity.ScaleSetId,
                allocationId, handoff.Identity.ResourceArn, handoff.Identity.Incarnation, handoff.RunnerName, runnerId);
            removal = await RemoveAsync(lease, authority.Clock, profile, intentId, removal, executor, handoff, request, connection, token)
                .ConfigureAwait(false);
        }
        return removal is null ? LifecycleWriteOutcome.Indeterminate :
            (await _store.CompleteTerminatedJitHandoffAsync(lease, handoff, generation, removal, token).ConfigureAwait(false)).Outcome;
    }

    private async Task<EffectJournalSnapshot?> RemoveAsync(
        ControllerLease lease, ControllerClockEvidence clock, LifecycleProfile profile, string intentId,
        EffectJournalSnapshot? intent, ExecutorSnapshot executor, JitHandoffSnapshot handoff, GitHubRunnerRemoveEffectRequest request,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken)
    {
        if (intent is { Entry.State: EffectState.Executing or EffectState.Uncertain })
        {
            if (intent.Entry.Attempt?.LeaseEpoch != lease.Epoch)
            {
                return null;
            }
            var evidence = await ObserveRemovalAsync(lease, clock, intent, connection, cancellationToken).ConfigureAwait(false);
            return evidence is null ? null : await ResolveRemovalAsync(lease, intent, evidence, cancellationToken).ConfigureAwait(false);
        }

        var catalog = await _store.ReadCatalogProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        if (catalog is not { State: CatalogProfileState.Active or CatalogProfileState.Draining } ||
            catalog.Binding.ScopeIdentity != request.ScopeIdentity || catalog.Binding.ScaleSetId != request.ScaleSetId ||
            Window(lease, clock) is null)
        {
            return null;
        }
        if (intent is null)
        {
            var envelope = EffectEnvelope.Create(profile, intentId, catalog.AppliedGeneration, request, _clock.GetUtcNow(), null);
            if (await _journal.PlanAsync(lease, envelope, Guid.NewGuid(), cancellationToken).ConfigureAwait(false) != LifecycleWriteOutcome.Committed)
            {
                return null;
            }
            intent = await _journal.ReadAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
        }
        if (intent is not { Entry.State: EffectState.Planned } || intent.Entry.Envelope.Request != request || Window(lease, clock) is not { } window)
        {
            return null;
        }
        var begin = await _journal.BeginRunnerRemovalAsync(lease, catalog, executor, handoff, intent, window.UpperNow, window.Deadline,
            _timing.WriteReserve, cancellationToken).ConfigureAwait(false);
        var executing = await _journal.ReadAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
        if (begin.Permission is not { } authorized || !MatchesAttempt(executing, authorized))
        {
            return null;
        }
        return await SendRemovalAsync(lease, clock, catalog, executor, executing!, authorized, request, connection, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<EffectJournalSnapshot?> SendRemovalAsync(
        ControllerLease lease, ControllerClockEvidence clock, CatalogProfile catalog, ExecutorSnapshot executor,
        EffectJournalSnapshot executing, EffectSendAuthorization authorized, GitHubRunnerRemoveEffectRequest request,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var found = await connection.ReadExactRunnerAsync(request.ScopeIdentity, request.ScaleSetId, request.RunnerName, request.RunnerId,
                cancellationToken).ConfigureAwait(false);
            if (found is null || await ReadCurrentAsync(lease, cancellationToken).ConfigureAwait(false) is not { } live ||
                Window(live, clock) is null ||
                await _store.ReadExecutorAsync(catalog.Profile, executor.Binding.AllocationId, cancellationToken).ConfigureAwait(false) != executor ||
                await _store.ReadCatalogProfileAsync(catalog.Profile, cancellationToken).ConfigureAwait(false) is not { } applied ||
                applied.TransitionId != catalog.TransitionId || applied.AppliedGeneration != catalog.AppliedGeneration)
            {
                return null;
            }
            if (await connection.RemoveExactRunnerAsync(authorized, cancellationToken).ConfigureAwait(false))
            {
                return await ResolveRemovalAsync(live, executing, Applied(executing.Entry.Envelope), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (GitHubOperationException failure) when (failure.DomainError == ScaleSetDomainError.JobStillRunning)
        {
            // A scheduling conflict leaves a registration to reconcile. Retry only the exact readback.
            _ = await _journal.MarkUncertainAsync(lease, executing, Guid.NewGuid(), authorized.Attempt.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubOperationException)
        {
            // Preserve the one-shot attempt. Later reconciliation reads exact absence.
        }
        return null;
    }

    public Task<LifecycleWriteOutcome> RecoverFencedRemovalAsync(
        ControllerLease lease, ControllerClockEvidence clock, EffectJournalSnapshot effect, EffectRecoveryAuthority recovery,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken) =>
        recovery.Coordinator.ResolveFencedEffectAsync(lease, effect, Guid.NewGuid(), clock, recovery.Isolation,
            (attempt, token) => ObserveRemovalAsync(lease, clock, attempt, connection, token), cancellationToken);

    private async Task<EffectEvidence?> ObserveRemovalAsync(
        ControllerLease lease, ControllerClockEvidence clock, EffectJournalSnapshot intent, GitHubScaleSetConnection connection,
        CancellationToken cancellationToken)
    {
        var envelope = intent.Entry.Envelope;
        var lowerBound = _clock.GetUtcNow() - clock.MaximumAbsoluteSkew;
        if (envelope.Request is not GitHubRunnerRemoveEffectRequest request || Window(lease, clock) is not { } window ||
            intent.Entry.LatestAuthorizedDeadline is not { } maySendUntil || lowerBound < maySendUntil)
        {
            return null;
        }
        using var deadline = new CancellationTokenSource(window.Deadline - window.UpperNow, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var found = await connection.ReadExactRunnerAsync(request.ScopeIdentity, request.ScaleSetId, request.RunnerName, request.RunnerId,
                bounded.Token).ConfigureAwait(false);
            if (found is not null)
            {
                return null;
            }
        }
        catch (GitHubOperationException failure) when (failure.StatusCode == 404 && failure.DomainError == ScaleSetDomainError.AgentNotFound)
        {
        }
        catch (GitHubOperationException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        var observed = _clock.GetUtcNow();
        var evidence = new EffectEvidence(EffectResolutionKind.Absent, envelope.Sha256, envelope.Target, observed,
            EarliestObservedAt: lowerBound);
        return Window(lease, clock) is not null && intent.Entry.CanResolve(evidence) ? evidence : null;
    }

    private async Task<EffectJournalSnapshot?> ResolveRemovalAsync(
        ControllerLease lease, EffectJournalSnapshot attempt, EffectEvidence evidence, CancellationToken cancellationToken)
    {
        if (!attempt.Entry.CanResolve(evidence) ||
            await _journal.ResolveAsync(lease, attempt, Guid.NewGuid(), evidence, cancellationToken).ConfigureAwait(false) !=
                LifecycleWriteOutcome.Committed)
        {
            return null;
        }
        var envelope = attempt.Entry.Envelope;
        return await _journal.ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
    }
}
