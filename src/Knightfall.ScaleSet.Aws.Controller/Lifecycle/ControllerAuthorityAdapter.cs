using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

public sealed class ControllerAuthorityAdapter
{
    private readonly RuntimeConfiguration _configuration;
    private readonly DynamoDbLifecycleStore _store;
    private readonly IControllerTaskFenceObserver _tasks;
    private readonly ControllerLeaseRecoveryCoordinator _recovery;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _writeReserve;
    private readonly string _deployment;
    private readonly string _clusterArn;
    private string _ownerId = Guid.NewGuid().ToString("N");
    private string? _taskArn;
    private int _running;

    public ControllerAuthorityAdapter(RuntimeConfiguration configuration, DynamoDbLifecycleStore store,
        IControllerTaskFenceObserver tasks, TimeProvider clock, TimeSpan leaseDuration, TimeSpan writeReserve)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(writeReserve, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, writeReserve);
        if (configuration.Mode != ControllerMode.Active)
        {
            throw new ArgumentException("Active configuration is required for controller lease authority.", nameof(configuration));
        }

        _configuration = configuration;
        _store = store;
        _tasks = tasks;
        _recovery = new ControllerLeaseRecoveryCoordinator(store, tasks, clock, writeReserve);
        _clock = clock;
        _leaseDuration = leaseDuration;
        _writeReserve = writeReserve;
        _deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        _clusterArn = EcsControllerMetadata.ResolveClusterArn(configuration);
    }

    /// <summary>Attempts one bounded ownership and recovery pass using a fresh observation of this task.</summary>
    public async Task<ControllerLeaseOwnership?> TryActivateAsync(ControllerTaskAuthority observation,
        Func<ControllerLease, ControllerClockEvidence, CancellationToken, Task<bool>> recover, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(recover);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            var now = _clock.GetUtcNow();
            var proof = observation.Clock;
            if (observation.ClusterArn != _clusterArn || !EcsControllerMetadata.MatchesTask(_clusterArn, observation.TaskArn) ||
                (_taskArn is not null && _taskArn != observation.TaskArn) ||
                !proof.TryGetUpperBound(now, now + _leaseDuration, _writeReserve, out var latest))
            {
                return null;
            }
            _taskArn = observation.TaskArn;

            using var deadline = new CancellationTokenSource(proof.ValidUntil - latest, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var token = linked.Token;
            var lease = await ReadOrAcquireAsync(observation, token).ConfigureAwait(false);
            if (lease is null || !proof.TryGetUpperBound(_clock.GetUtcNow(), lease.ValidUntil, _writeReserve, out _))
            {
                return null;
            }

            if (lease.Mode == ControllerLeaseMode.RecoveryOnly)
            {
                lease = await RecoverAsync(lease, proof, recover, token).ConfigureAwait(false);
            }

            return lease is { Mode: ControllerLeaseMode.Active } &&
                proof.TryGetUpperBound(_clock.GetUtcNow(), lease.ValidUntil, _writeReserve, out _)
                ? new ControllerLeaseOwnership(_store, lease, proof, _clock, _leaseDuration, _writeReserve)
                : null;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or AmazonClientException or
                                      AmazonServiceException or TimeoutException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ControllerLease?> ReadOrAcquireAsync(ControllerTaskAuthority observation, CancellationToken cancellationToken)
    {
        var current = await _store.ReadLeaseAsync(_deployment, cancellationToken).ConfigureAwait(false);
        var proof = observation.Clock;
        var now = _clock.GetUtcNow();
        if (!proof.TryGetUpperBound(now, now + _leaseDuration, _writeReserve, out _))
        {
            return null;
        }
        var earliest = now - proof.MaximumAbsoluteSkew;
        if (IsOwned(current, observation.TaskArn) && current is not null)
        {
            if (current.Mode != ControllerLeaseMode.Released && current.ValidUntil > earliest)
            {
                return await ExtendRecoveryIfNeededAsync(current, proof, now, cancellationToken).ConfigureAwait(false);
            }
            _ownerId = Guid.NewGuid().ToString("N");
        }
        else if (current is { Mode: not ControllerLeaseMode.Released } && current.ValidUntil > earliest)
        {
            return null;
        }

        var acquisition = await _store.TryAcquireLeaseAsync(_deployment, _ownerId, observation.TaskArn,
            ControllerLease.CurrentWriterProtocol, earliest, now + _leaseDuration, cancellationToken).ConfigureAwait(false);
        var claimed = await ReadExactWriteAsync(acquisition, cancellationToken).ConfigureAwait(false);
        return IsOwned(claimed, observation.TaskArn) ? claimed : null;
    }

    private async Task<ControllerLease?> ExtendRecoveryIfNeededAsync(ControllerLease lease, ControllerClockEvidence clock,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (lease.Mode == ControllerLeaseMode.Active || lease.ValidUntil - now > _leaseDuration / 2)
        {
            return lease;
        }

        if (!clock.TryGetUpperBound(now, lease.ValidUntil, _writeReserve, out var latest))
        {
            return null;
        }
        var renewal = await _store.RenewLeaseAsync(lease, latest, now + _leaseDuration, cancellationToken).ConfigureAwait(false);
        var renewed = await ReadExactWriteAsync(renewal, cancellationToken).ConfigureAwait(false);
        return renewed is { Mode: ControllerLeaseMode.RecoveryOnly } && renewed.Fence == lease.Fence ? renewed : null;
    }

    private async Task<ControllerLease?> RecoverAsync(ControllerLease lease, ControllerClockEvidence proof,
        Func<ControllerLease, ControllerClockEvidence, CancellationToken, Task<bool>> recover, CancellationToken cancellationToken)
    {
        if (!proof.TryGetUpperBound(_clock.GetUtcNow(), lease.ValidUntil, _writeReserve, out var latest))
        {
            return null;
        }
        using var deadline = new CancellationTokenSource(lease.ValidUntil - latest - _writeReserve, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;

        string? predecessor = lease.PreviousOwner is { CleanRelease: false } previous ? previous.OwnerTaskArn : null;
        if ((predecessor is not null && !await IsStoppedAsync(predecessor, token).ConfigureAwait(false)) ||
            (lease.RequiredFencingTaskArn is { } required && required != predecessor &&
                !await IsStoppedAsync(required, token).ConfigureAwait(false)) ||
            !proof.TryGetUpperBound(_clock.GetUtcNow(), lease.ValidUntil, _writeReserve, out _) ||
            !await recover(lease, proof, token).ConfigureAwait(false))
        {
            return null;
        }

        var activated = await _recovery.TryActivateAsync(lease, _configuration, proof, [], token).ConfigureAwait(false);
        return activated.Status is LeaseActivationStatus.Activated or LeaseActivationStatus.AlreadyActive &&
            activated.Lease?.Fence == lease.Fence ? activated.Lease : null;
    }

    private async Task<bool> IsStoppedAsync(string taskArn, CancellationToken cancellationToken) =>
        EcsControllerMetadata.MatchesTask(_clusterArn, taskArn) &&
        await _tasks.ReadAsync(taskArn, cancellationToken).ConfigureAwait(false) == ControllerTaskFenceStatus.Stopped;

    private bool IsOwned(ControllerLease? lease, string taskArn) =>
        lease is not null && lease.OwnerId == _ownerId && lease.OwnerTaskArn == taskArn &&
        lease.WriterProtocol == ControllerLease.CurrentWriterProtocol;

    private async Task<ControllerLease?> ReadExactWriteAsync(ControllerLeaseChange result, CancellationToken cancellationToken)
    {
        if (result.Outcome == LifecycleWriteOutcome.Committed)
        {
            return result.Lease;
        }
        if (result is not { Outcome: LifecycleWriteOutcome.Indeterminate, AttemptedTransitionId: Guid operation })
        {
            return null;
        }
        var read = await _store.ReadLeaseAsync(_deployment, cancellationToken).ConfigureAwait(false);
        return read?.TransitionId == operation ? read : null;
    }
}
