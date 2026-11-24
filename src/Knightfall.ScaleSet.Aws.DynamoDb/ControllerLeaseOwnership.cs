using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record ActiveControllerAuthority(ControllerLease Lease, ControllerClockEvidence Clock);

public enum ControllerHeartbeatStatus
{
    Renewed = 0,
    Current = 1,
    RetryRequired = 2,
    OwnershipLost = 3,
}

public sealed class ControllerLeaseOwnership : IDisposable
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _reserve;
    private readonly CancellationTokenSource _lost = new();
    private readonly SemaphoreSlim _serial = new(1);
    private ActiveControllerAuthority _authority;
    private Guid? _pendingRenewal;

    public ControllerLeaseOwnership(DynamoDbLifecycleStore store, ControllerLease lease, ControllerClockEvidence clockEvidence,
        TimeProvider clock, TimeSpan duration, TimeSpan reserve)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(clockEvidence);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reserve, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, reserve);
        if (lease.Mode != ControllerLeaseMode.Active || !clockEvidence.TryGetUpperBound(clock.GetUtcNow(), lease.ValidUntil, reserve, out _))
        {
            throw new ArgumentException("A current active lease and bounded clock are required.", nameof(lease));
        }

        _authority = new ActiveControllerAuthority(lease, clockEvidence);
        _store = store;
        _clock = clock;
        _duration = duration;
        _reserve = reserve;
    }

    public CancellationToken OwnershipLost => _lost.Token;

    public ActiveControllerAuthority? ReadAuthority()
    {
        var current = Volatile.Read(ref _authority);
        return !_lost.IsCancellationRequested && current.Clock.TryGetUpperBound(_clock.GetUtcNow(), current.Lease.ValidUntil, _reserve, out _)
            ? current : null;
    }

    public async Task<ControllerHeartbeatStatus> RenewAsync(ControllerClockEvidence? clockEvidence, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _authority.Lease;
            if (_lost.IsCancellationRequested || clockEvidence is not { } proof ||
                !proof.TryGetUpperBound(_clock.GetUtcNow(), current.ValidUntil, _reserve, out _))
            {
                return await LoseAsync().ConfigureAwait(false);
            }

            if (_pendingRenewal is Guid pending)
            {
                var read = await _store.ReadLeaseAsync(current.Deployment, cancellationToken).ConfigureAwait(false);
                if (read is { Mode: ControllerLeaseMode.Active } && read.Fence == current.Fence && read.TransitionId == pending)
                {
                    current = read;
                    Volatile.Write(ref _authority, new ActiveControllerAuthority(current, proof));
                    _pendingRenewal = null;
                }
                else if (read != current)
                {
                    return await LoseAsync().ConfigureAwait(false);
                }
            }

            var now = _clock.GetUtcNow();
            if (!proof.TryGetUpperBound(now, current.ValidUntil, _reserve, out var latest))
            {
                return await LoseAsync().ConfigureAwait(false);
            }
            if (_duration - (current.ValidUntil - now) < TimeSpan.FromMilliseconds(1))
            {
                if (await _store.ReadLeaseAsync(current.Deployment, cancellationToken).ConfigureAwait(false) != current)
                {
                    return await LoseAsync().ConfigureAwait(false);
                }
                Volatile.Write(ref _authority, new ActiveControllerAuthority(current, proof));
                return ControllerHeartbeatStatus.Current;
            }

            var renewed = await _store.RenewLeaseAsync(current, latest, now + _duration, cancellationToken).ConfigureAwait(false);
            if (renewed.Outcome == LifecycleWriteOutcome.Committed && renewed.Lease is { } next)
            {
                Volatile.Write(ref _authority, new ActiveControllerAuthority(next, proof));
                _pendingRenewal = null;
                return ControllerHeartbeatStatus.Renewed;
            }
            if (renewed.Outcome == LifecycleWriteOutcome.Indeterminate)
            {
                _pendingRenewal = renewed.AttemptedTransitionId;
                return ControllerHeartbeatStatus.RetryRequired;
            }
            return await LoseAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ReadAuthority() is not null ? ControllerHeartbeatStatus.RetryRequired : await LoseAsync().ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task RunHeartbeatsAsync(TimeSpan interval,
        Func<CancellationToken, Task<ControllerClockEvidence?>> readClock, CancellationToken stoppingToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(readClock);
        while (!_lost.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            var evidence = await readClock(stoppingToken).ConfigureAwait(false);
            if (await RenewAsync(evidence, stoppingToken).ConfigureAwait(false) == ControllerHeartbeatStatus.OwnershipLost)
            {
                return;
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lost.Token);
            await Task.Delay(interval, _clock, wait.Token).ConfigureAwait(false);
        }
    }

    internal async Task<bool> ReleaseAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool attempted = false;
        try
        {
            var authority = _authority;
            if (_lost.IsCancellationRequested || _pendingRenewal.HasValue ||
                !authority.Clock.TryGetUpperBound(_clock.GetUtcNow(), authority.Lease.ValidUntil, _reserve, out var latest))
            {
                return false;
            }
            attempted = true;
            var release = await _store.ReleaseLeaseAsync(authority.Lease, latest, cancellationToken).ConfigureAwait(false);
            bool committed = release.Outcome == LifecycleWriteOutcome.Committed;
            if (release is { Outcome: LifecycleWriteOutcome.Indeterminate, AttemptedTransitionId: Guid attempt })
            {
                var read = await _store.ReadLeaseAsync(authority.Lease.Deployment, cancellationToken).ConfigureAwait(false);
                committed = read is { Mode: ControllerLeaseMode.Released } && read.Fence == authority.Lease.Fence && read.TransitionId == attempt;
            }
            return committed;
        }
        catch (IOException) when (attempted)
        {
            return false;
        }
        finally
        {
            var ended = attempted ? EndAuthorityAsync() : Task.CompletedTask;
            _serial.Release();
            await ended.ConfigureAwait(false);
        }
    }

    internal Task EndAuthorityAsync() => _lost.CancelAsync();

    private async Task<ControllerHeartbeatStatus> LoseAsync()
    {
        await _lost.CancelAsync().ConfigureAwait(false);
        return ControllerHeartbeatStatus.OwnershipLost;
    }

    public void Dispose()
    {
        _lost.Dispose();
        _serial.Dispose();
    }
}
