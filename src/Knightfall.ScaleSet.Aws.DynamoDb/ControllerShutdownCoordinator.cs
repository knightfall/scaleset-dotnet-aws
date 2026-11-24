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
        await work.StopAdmissionsAsync(cleanupToken).ConfigureAwait(false);
        await work.CancelPollsAsync(cleanupToken).ConfigureAwait(false);
        await work.JoinMutatorsAsync(cleanupToken).ConfigureAwait(false);

        if (ownership.ReadAuthority() is not { } authority)
        {
            return ControllerShutdownStatus.OwnershipUnavailable;
        }
        if (configuredProfiles.Any(profile => profile.Deployment != authority.Lease.Deployment))
        {
            throw new ArgumentException("Shutdown profiles must belong to the controller deployment.", nameof(configuredProfiles));
        }

        var durableProfiles = await _store.ReadEffectProfilesAsync(authority.Lease.Deployment, cleanupToken).ConfigureAwait(false);
        var profiles = configuredProfiles.Concat(durableProfiles).Distinct().ToArray();
        foreach (var profile in profiles)
        {
            if (!await CancelPlannedAsync(ownership, profile, cleanupToken).ConfigureAwait(false))
            {
                break;
            }
        }

        await work.CloseOwnedSessionsAsync(cleanupToken).ConfigureAwait(false);
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

        return await ownership.ReleaseAsync(cleanupToken).ConfigureAwait(false)
            ? ControllerShutdownStatus.Released : ControllerShutdownStatus.ReleaseUnconfirmed;
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
