using System.Collections.Concurrent;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed class GitHubCredentialCooldown
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _unauthorizedDelay;

    public GitHubCredentialCooldown(TimeProvider clock, TimeSpan unauthorizedDelay)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(unauthorizedDelay, TimeSpan.Zero);
        _clock = clock;
        _unauthorizedDelay = unauthorizedDelay;
    }

    public DateTimeOffset? Read(string secretArn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretArn);
        return _until.TryGetValue(secretArn, out var until) && until > _clock.GetUtcNow() ? until : null;
    }

    public void Record(string secretArn, GitHubOperationException failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretArn);
        ArgumentNullException.ThrowIfNull(failure);
        var now = _clock.GetUtcNow();
        DateTimeOffset? until = null;
        if (failure.RateLimit is { } rate)
        {
            var delay = rate.RetryAfter > TimeSpan.Zero ? rate.RetryAfter : _unauthorizedDelay;
            until = AddBounded(now, delay);
            if (rate.Reset is { } reset && reset > until)
            {
                until = reset;
            }
        }
        else if (failure.StatusCode == 401)
        {
            until = AddBounded(now, _unauthorizedDelay);
        }

        if (until is { } retryAt)
        {
            _until.AddOrUpdate(secretArn, retryAt, (_, current) => current > retryAt ? current : retryAt);
        }
    }

    private static DateTimeOffset AddBounded(DateTimeOffset now, TimeSpan delay) =>
        DateTimeOffset.MaxValue - now < delay ? DateTimeOffset.MaxValue : now + delay;
}

internal sealed class GitHubListenerExecutionPolicy : IScaleSetListenerExecutionPolicy
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly OwnedSessionFence _fence;
    private readonly ScaleSetListenerSessionKey _key;
    private readonly GitHubCredentialCooldown _cooldown;
    private readonly string _secretArn;
    private readonly Func<CancellationToken, Task<GitHubRegistrationLease?>> _readOwnership;
    private readonly Func<CancellationToken, Task<int>> _readPollCapacity;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _reserve;
    private readonly CancellationToken _ownershipLost;

    internal GitHubListenerExecutionPolicy(
        DynamoDbLifecycleStore store,
        OwnedSessionFence fence,
        ScaleSetListenerSessionKey key,
        GitHubCredentialCooldown cooldown,
        string secretArn,
        Func<CancellationToken, Task<GitHubRegistrationLease?>> readOwnership,
        Func<CancellationToken, Task<int>> readPollCapacity,
        TimeProvider clock,
        TimeSpan reserve,
        CancellationToken ownershipLost)
    {
        _store = store;
        _fence = fence;
        _key = key;
        _cooldown = cooldown;
        _secretArn = secretArn;
        _readOwnership = readOwnership;
        _readPollCapacity = readPollCapacity;
        _clock = clock;
        _reserve = reserve;
        _ownershipLost = ownershipLost;
    }

    public async Task<ScaleSetListenerAuthorization> AuthorizeAsync(
        ScaleSetListenerOperationContext context, CancellationToken cancellationToken)
    {
        _ownershipLost.ThrowIfCancellationRequested();
        if (context.SessionKey != _key)
        {
            return ScaleSetListenerAuthorization.Defer();
        }
        if (IsRemote(context.Operation) && _cooldown.Read(_secretArn) is { } retryAt)
        {
            GitHubListenerDiagnostics.Count("authorize", "deferred", "cooldown");
            return ScaleSetListenerAuthorization.Defer(retryAt);
        }

        var ownership = await _readOwnership(cancellationToken).ConfigureAwait(false);
        if (ownership is not { Lease.Mode: ControllerLeaseMode.Active } || ownership.Lease.Fence != _fence.Lease ||
            ListenerTime.Remaining(ownership, _clock.GetUtcNow(), _reserve) <= TimeSpan.Zero ||
            !await _store.HasSessionOwnershipAsync(_fence, cancellationToken).ConfigureAwait(false))
        {
            return ScaleSetListenerAuthorization.Defer();
        }

        int? capacity = context.Operation == ScaleSetListenerOperation.Poll
            ? await _readPollCapacity(cancellationToken).ConfigureAwait(false) : null;
        var now = _clock.GetUtcNow();
        var remaining = ListenerTime.Remaining(ownership, now, _reserve);
        _ownershipLost.ThrowIfCancellationRequested();
        if (IsRemote(context.Operation) && _cooldown.Read(_secretArn) is { } latestRetryAt)
        {
            GitHubListenerDiagnostics.Count("authorize", "deferred", "cooldown");
            return ScaleSetListenerAuthorization.Defer(latestRetryAt);
        }
        return remaining > TimeSpan.Zero
            ? ScaleSetListenerAuthorization.Allow(now + remaining, capacity, _ownershipLost)
            : ScaleSetListenerAuthorization.Defer();
    }

    private static bool IsRemote(ScaleSetListenerOperation operation) => operation is
        ScaleSetListenerOperation.Poll or ScaleSetListenerOperation.HandleDelivery or ScaleSetListenerOperation.Acknowledge;
}

internal static class ListenerTime
{
    internal static TimeSpan Remaining(GitHubRegistrationLease ownership, DateTimeOffset now, TimeSpan reserve)
    {
        if (!ownership.Clock.TryGetUpperBound(now, ownership.Lease.ValidUntil, reserve, out var upper))
        {
            return TimeSpan.Zero;
        }
        var deadline = ownership.Lease.ValidUntil - reserve;
        if (ownership.Clock.ValidUntil < deadline)
        {
            deadline = ownership.Clock.ValidUntil;
        }
        return deadline - upper;
    }
}
