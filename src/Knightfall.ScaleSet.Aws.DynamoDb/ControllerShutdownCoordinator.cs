using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public interface IControllerShutdownWork
{
    Task StopAdmissionsAsync(CancellationToken cancellationToken);

    Task CancelPollsAsync(CancellationToken cancellationToken);

    Task JoinMutatorsAsync(CancellationToken cancellationToken);

    Task CloseOwnedSessionsAsync(CancellationToken cancellationToken);
}

public enum ControllerShutdownStatus
{
    Released = 0,
    OwnershipUnavailable = 1,
    ReconciliationPending = 2,
    ReleaseUnconfirmed = 3,
    QuiescenceUnconfirmed = 4,
}

public sealed class ControllerShutdownCoordinator
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly DynamoDbEffectDispatcher _effects;

    public ControllerShutdownCoordinator(DynamoDbLifecycleStore store, DynamoDbEffectDispatcher effects)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(effects);
        _store = store;
        _effects = effects;
    }

    public async Task<ControllerShutdownStatus> StopAsync(
        ControllerLeaseOwnership ownership,
        IReadOnlyCollection<LifecycleProfile> configuredProfiles,
        IControllerShutdownWork work,
        CancellationToken cleanupToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(configuredProfiles);
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            return await StopAndVerifyAsync(ownership, configuredProfiles, work, cleanupToken).ConfigureAwait(false);
        }
        finally
        {
            await ownership.EndAuthorityAsync().ConfigureAwait(false);
        }
    }

    private async Task<ControllerShutdownStatus> StopAndVerifyAsync(
        ControllerLeaseOwnership ownership,
        IReadOnlyCollection<LifecycleProfile> configuredProfiles,
        IControllerShutdownWork work,
        CancellationToken cleanupToken)
    {
        bool admissionsStopped = await TryStepAsync(work.StopAdmissionsAsync, cleanupToken).ConfigureAwait(false);
        bool pollsStopped = await TryStepAsync(work.CancelPollsAsync, cleanupToken).ConfigureAwait(false);
        bool joined = await TryStepAsync(work.JoinMutatorsAsync, cleanupToken).ConfigureAwait(false);
        if (!admissionsStopped || !pollsStopped || !joined)
        {
            return ControllerShutdownStatus.QuiescenceUnconfirmed;
        }

        if (ownership.ReadAuthority() is not { } authority)
        {
            return ControllerShutdownStatus.OwnershipUnavailable;
        }
        if (configuredProfiles.Any(profile => profile.Deployment != authority.Lease.Deployment))
        {
            throw new ArgumentException("Shutdown profiles must belong to the controller deployment.", nameof(configuredProfiles));
        }

        IReadOnlyCollection<LifecycleProfile> durableProfiles = [];
        bool complete = await TryStepAsync(async token => durableProfiles =
            await _store.ReadEffectProfilesAsync(authority.Lease.Deployment, token).ConfigureAwait(false),
            cleanupToken).ConfigureAwait(false);
        var profiles = configuredProfiles.Concat(durableProfiles).Distinct().ToArray();
        foreach (var profile in profiles)
        {
            if (!await TryStepAsync(async token => complete &=
                await CancelPlannedAsync(ownership, profile, token).ConfigureAwait(false),
                cleanupToken).ConfigureAwait(false))
            {
                complete = false;
            }
        }

        // The exact session fence still protects closure after a failed effect-index read.
        if (ownership.ReadAuthority() is null)
        {
            return ControllerShutdownStatus.OwnershipUnavailable;
        }
        if (!await TryStepAsync(work.CloseOwnedSessionsAsync, cleanupToken).ConfigureAwait(false) || !complete)
        {
            return ControllerShutdownStatus.ReconciliationPending;
        }
        foreach (var profile in profiles)
        {
            if (ownership.ReadAuthority() is not { } current)
            {
                return ControllerShutdownStatus.OwnershipUnavailable;
            }
            var session = await _store.ReadSessionAsync(profile, cleanupToken).ConfigureAwait(false);
            if (session is not (null or { Phase: SessionPhase.Retired }) ||
                !await _effects.HasNoUnfinishedEffectsAsync(current.Lease, profile, cleanupToken).ConfigureAwait(false))
            {
                return ControllerShutdownStatus.ReconciliationPending;
            }
        }

        bool released = false;
        _ = await TryStepAsync(async token => released =
            await ownership.ReleaseAsync(token).ConfigureAwait(false), cleanupToken).ConfigureAwait(false);
        return released
            ? ControllerShutdownStatus.Released : ControllerShutdownStatus.ReleaseUnconfirmed;
    }

    private static async Task<bool> TryStepAsync(Func<CancellationToken, Task> step, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }
        try
        {
            await step(token).ConfigureAwait(false);
            return !token.IsCancellationRequested;
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or
            AmazonClientException or AmazonServiceException or AggregateException)
        {
            return false;
        }
    }

    private async Task<bool> CancelPlannedAsync(ControllerLeaseOwnership ownership, LifecycleProfile profile, CancellationToken cleanupToken)
    {
        EffectCandidateCursor? continuation = null;
        do
        {
            cleanupToken.ThrowIfCancellationRequested();
            if (ownership.ReadAuthority() is not { } current)
            {
                return false;
            }
            var pass = await _effects.CancelUnstartedPassAsync(current.Lease, profile, continuation, cleanupToken).ConfigureAwait(false);
            if (pass.Outcome == LifecycleWriteOutcome.RebuildRequired && pass.RebuildPages > 0)
            {
                continuation = null;
                continue;
            }
            if (pass.Outcome != LifecycleWriteOutcome.Committed)
            {
                return false;
            }
            continuation = pass.Continuation;
            if (continuation is null)
            {
                return true;
            }
        }
        while (true);
    }
}
