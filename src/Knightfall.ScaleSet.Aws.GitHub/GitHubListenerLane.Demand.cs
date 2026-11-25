using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubListenerLane
{
    private static readonly TimeSpan ActiveDemandRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuietDemandRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan QuietDemandReadBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResponseDateTolerance = TimeSpan.FromSeconds(5);
    private (Guid SessionId, DateTimeOffset AttemptedAt)? _lastQuietDemandRead;

    private async Task<int> ReadPollCapacityAsync(GitHubListenerDeliveryHandler handler, CancellationToken cancellationToken)
    {
        // The pump reaches this callback only when it can poll again. Do not
        // depend on an empty response arriving within the current lease window.
        await RefreshQuietDemandAsync(cancellationToken).ConfigureAwait(false);
        return await handler.ReadPollCapacityAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshQuietDemandAsync(CancellationToken cancellationToken)
    {
        if (_ownedFence is not { } fence || !MatchesCurrentSession(fence))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        if (_lastQuietDemandRead is { } last && last.SessionId == fence.Identity.SessionId &&
            now - last.AttemptedAt < QuietDemandRetryInterval)
        {
            return;
        }

        var ledger = new ListenerDeliveryLedger(_store, fence, _clock);
        var prior = await ledger.ReadDemandAsync(cancellationToken).ConfigureAwait(false);
        var interval = _profile.State == RunnerProfileState.Draining ? QuietDemandRetryInterval : ActiveDemandRefreshInterval;
        if (prior?.Value.SessionId != fence.Identity.SessionId || now - prior.Value.ObservedAt < interval ||
            await ReadOwnershipAsync(cancellationToken).ConfigureAwait(false) is not { } ownership ||
            !await _store.HasSessionOwnershipAsync(fence, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var startedAt = _clock.GetUtcNow();
        var remaining = ListenerTime.Remaining(ownership, startedAt, _timing.WriteReserve);
        if (startedAt < now || remaining <= TimeSpan.Zero)
        {
            return;
        }

        _lastQuietDemandRead = (fence.Identity.SessionId, startedAt);
        using var timeout = new CancellationTokenSource(
            remaining < QuietDemandReadBudget ? remaining : QuietDemandReadBudget, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var telemetry = GitHubListenerDiagnostics.Start("demand-refresh", _clock, cancellationToken);
        GitHubDemandReadTransport.DemandStatistics? statistics;
        try
        {
            statistics = await _connection.ReadDemandAsync(_definition.Binding, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            telemetry.Complete("unknown", "timeout");
            return;
        }
        catch (GitHubOperationException error) when (error.RateLimit is null && error.StatusCode != 401)
        {
            telemetry.Complete("unknown", "demand_unavailable");
            return;
        }

        var observedAt = _clock.GetUtcNow();
        if (statistics is null || timeout.IsCancellationRequested || observedAt < startedAt ||
            observedAt - startedAt >= QuietDemandReadBudget ||
            startedAt - statistics.ResponseDate > ResponseDateTolerance ||
            statistics.ResponseDate - observedAt > ResponseDateTolerance ||
            ListenerTime.Remaining(ownership, observedAt, _timing.WriteReserve) <= TimeSpan.Zero || !MatchesCurrentSession(fence))
        {
            telemetry.Complete("unknown", "demand_unavailable");
            return;
        }

        // Use the read's lower time bound and retain the cursor. The ledger compares the
        // revision seen before the request so this read cannot replace a newer delivery.
        var outcome = await ledger.RefreshDemandAsync(prior, startedAt, statistics.TotalAssignedJobs, cancellationToken).ConfigureAwait(false);
        telemetry.Complete(GitHubListenerDiagnostics.WriteOutcome(outcome));
    }

    private bool MatchesCurrentSession(OwnedSessionFence fence) =>
        _ownedFence == fence && _ownedSession?.Session is { } session && session.SessionId == fence.Identity.SessionId &&
        (session.RunnerScaleSet is not { Id: > 0 } set || set.Id == fence.Identity.ScaleSetId);
}
