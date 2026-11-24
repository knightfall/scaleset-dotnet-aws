using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubJitHandoff
{
    public Task<LifecycleWriteOutcome> RecoverFencedAsync(
        ControllerLease lease, ControllerClockEvidence clock, EffectJournalSnapshot effect, EffectRecoveryAuthority recovery,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken) =>
        recovery.Coordinator.ResolveFencedEffectAsync(lease, effect, Guid.NewGuid(), clock, recovery.Isolation,
            (attempt, token) => ObserveCreatedAsync(lease, clock, attempt, connection, token), cancellationToken);

    private async Task<GitHubJitResult> RecoverActiveAsync(
        ControllerClockEvidence clock, ControllerLease lease, EffectJournalSnapshot attempt,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken)
    {
        if (attempt.Entry.Attempt?.LeaseEpoch != lease.Epoch)
        {
            return new GitHubJitResult(GitHubJitStatus.RecoveryRequired);
        }
        try
        {
            var evidence = await ObserveCreatedAsync(lease, clock, attempt, connection, cancellationToken).ConfigureAwait(false);
            var profile = attempt.Entry.Envelope.Profile;
            if (evidence is null || !attempt.Entry.CanResolve(evidence) ||
                await ReadCurrentAsync(lease, cancellationToken).ConfigureAwait(false) is not { } live ||
                Window(live, clock) is null ||
                await _journal.ResolveAsync(live, attempt, Guid.NewGuid(), evidence, cancellationToken).ConfigureAwait(false) !=
                    LifecycleWriteOutcome.Committed)
            {
                return new GitHubJitResult(GitHubJitStatus.RecoveryRequired);
            }
            var request = (GitHubJitGenerateEffectRequest)attempt.Entry.Envelope.Request;
            return Describe(await _store.ReadJitHandoffAsync(profile, request.AllocationId, cancellationToken).ConfigureAwait(false));
        }
        catch (GitHubOperationException failure)
        {
            return new GitHubJitResult(GitHubJitStatus.RecoveryRequired, Failure: failure);
        }
    }

    private async Task<GitHubJitResult> SettlePublishedAsync(
        ControllerClockEvidence clock, ControllerLease lease, EffectJournalSnapshot attempt, JitHandoffSnapshot handoff,
        CancellationToken cancellationToken)
    {
        var evidence = Applied(attempt.Entry.Envelope);
        if (Window(lease, clock) is not null && ConfirmedCreate(attempt, handoff) && attempt.Entry.CanResolve(evidence))
        {
            _ = await _journal.ResolveAsync(lease, attempt, Guid.NewGuid(), evidence, cancellationToken)
                .ConfigureAwait(false);
        }
        return Describe(handoff);
    }

    private async Task<EffectEvidence?> ObserveCreatedAsync(
        ControllerLease lease, ControllerClockEvidence clock, EffectJournalSnapshot attempt,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken)
    {
        var envelope = attempt.Entry.Envelope;
        if (envelope.Request is not GitHubJitGenerateEffectRequest request ||
            attempt.Entry.State is not (EffectState.Executing or EffectState.Uncertain) ||
            Window(lease, clock) is not { } window ||
            await _store.ReadJitHandoffAsync(envelope.Profile, request.AllocationId, cancellationToken).ConfigureAwait(false) is not
                { } handoff || handoff.IntentId != envelope.IntentId || handoff.Identity.ScopeIdentity != connection.ScopeIdentity)
        {
            return null;
        }
        if (ConfirmedCreate(attempt, handoff))
        {
            return Applied(envelope);
        }
        if (handoff.State != JitHandoffState.Staged ||
            (lease.Mode == ControllerLeaseMode.Active && attempt.Entry.State != EffectState.Uncertain))
        {
            return null;
        }

        using var deadline = new CancellationTokenSource(window.Deadline - window.UpperNow, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var runner = await connection.FindExactRunnerAsync(request, bounded.Token).ConfigureAwait(false);
            if (runner is null || Window(lease, clock) is null)
            {
                return null;
            }
            // Exact lookup can prove the runner exists. The omitted JIT bytes cannot be reconstructed.
            var recorded = await _store.RecordUnissuedJitAsync(lease, handoff, attempt, runner.Id, bounded.Token).ConfigureAwait(false);
            return recorded is { Outcome: LifecycleWriteOutcome.Committed, Handoff: { } found } && ConfirmedCreate(attempt, found)
                ? Applied(envelope) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (GitHubOperationException) when (lease.Mode == ControllerLeaseMode.RecoveryOnly)
        {
            return null;
        }
    }

    private EffectEvidence Applied(EffectEnvelope envelope) =>
        new(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, _clock.GetUtcNow());

    private static bool ConfirmedCreate(EffectJournalSnapshot attempt, JitHandoffSnapshot handoff) =>
        handoff.RunnerId > 0 && handoff.JitAttemptId is Guid id && attempt.Entry.Attempt?.Id == id &&
        attempt.Entry.Envelope.IntentId == handoff.IntentId;
}
