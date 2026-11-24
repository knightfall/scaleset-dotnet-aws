using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubListenerLane
{
    public async Task<GitHubListenerLaneResult> RetireDrainingSessionAsync(
        ProfileRetirementCoordinator retirement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var profileTelemetry = TelemetryProfileScope.Enter(_telemetryProfile);
        try
        {
            using var administration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _definition.OwnershipLost);
            var token = administration.Token;
            if (_known is not null && !await ConfirmKnownAsync(token).ConfigureAwait(false))
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired));
            }
            if (await ReadOwnershipAsync(token).ConfigureAwait(false) is not { } ownership)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable));
            }
            var session = await _store.ReadSessionAsync(_binding.Profile, token).ConfigureAwait(false);
            if (session is not { Phase: SessionPhase.Active } || session.Binding != _binding)
            {
                return Report(new GitHubListenerLaneResult(session is { Phase: SessionPhase.Retired }
                    ? GitHubListenerLaneStatus.Stopped : GitHubListenerLaneStatus.RecoveryRequired));
            }

            bool ownedHere = session.Owner == ownership.Lease.Fence;
            if (ownedHere && (_ownedSession is null || _ownedFence != session.Fence))
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable));
            }
            var ownedClient = ownedHere ? _ownedSession : null;
            var change = await retirement.BeginSessionRetirementAsync(_binding.Profile,
                new ActiveControllerAuthority(ownership.Lease, ownership.Clock), session, _timing.SessionSendWindow, token).ConfigureAwait(false);
            if (change.Permission is not { } permission)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired));
            }
            Interlocked.Exchange(ref _stopping, 1);
            ClearPump();
            await _connection.RetireSessionAsync(permission, ownedClient, token).ConfigureAwait(false);
            _known = new KnownSessionResponse(permission, null);
            return Report(new GitHubListenerLaneResult(await ConfirmKnownAsync(token).ConfigureAwait(false)
                ? GitHubListenerLaneStatus.Stopped : GitHubListenerLaneStatus.RecoveryRequired));
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && !_definition.OwnershipLost.IsCancellationRequested)
        {
            return Report(Failed(error));
        }
        catch (Exception error) when (error is GitHubOperationException or ScaleSetApiException or IOException or InvalidDataException or TimeoutException)
        {
            return Report(Failed(error));
        }
        finally
        {
            _serial.Release();
        }
    }
}
