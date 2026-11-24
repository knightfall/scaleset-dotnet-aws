using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubScaleSetConnection
{
    internal async Task<IMessageSession> OpenSessionAsync(SessionSendAuthorization authorization, CancellationToken cancellationToken)
    {
        var session = authorization.Session;
        if (session.Phase != SessionPhase.OpenIntent || session.SessionId.HasValue)
        {
            throw BindingMismatch();
        }
        var opened = await RunSessionMutationAsync(authorization,
            token => _client.CreateMessageSessionAsync(session.Binding.ScaleSetId, session.OwnerName, token), cancellationToken)
            .ConfigureAwait(false);
        if (opened.Session.RunnerScaleSet is { Id: not 0 } set && set.Id != session.Binding.ScaleSetId)
        {
            throw BindingMismatch();
        }
        return opened;
    }

    internal async Task RetireSessionAsync(
        SessionSendAuthorization authorization, IMessageSession? owned, CancellationToken cancellationToken)
    {
        var session = authorization.Session;
        if (session.Phase != SessionPhase.Closing || session.SessionId is not { } id ||
            (owned is null ? session.CloseKind != SessionCloseKind.FencedRetirement :
                session.CloseKind != SessionCloseKind.OwnedClient || owned.Session.SessionId != id))
        {
            throw BindingMismatch();
        }

        _ = await RunSessionMutationAsync(authorization, async token =>
        {
            if (owned is null)
            {
                await _client.DeleteMessageSessionAsync(session.Binding.ScaleSetId, id, token).ConfigureAwait(false);
            }
            else
            {
                await owned.CloseAsync(token).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<T> RunSessionMutationAsync<T>(
        SessionSendAuthorization authorization, Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        if (authorization.Session.Binding.ScopeIdentity != ScopeIdentity)
        {
            throw BindingMismatch();
        }
        return RunBoundMutationAsync(authorization.TryConsume, send, cancellationToken);
    }

    private delegate bool ConsumeAuthorization(out TimeSpan remaining);

    private async Task<T> RunBoundMutationAsync<T>(
        ConsumeAuthorization consume, Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long starting = _clock.GetTimestamp();
        if (!consume(out var remaining))
        {
            throw new GitHubOperationException(remaining > TimeSpan.Zero
                ? GitHubOperationError.AuthorizationConsumed : GitHubOperationError.DeadlineElapsed);
        }

        var now = _clock.GetUtcNow();
        remaining -= _clock.GetElapsedTime(starting);
        if (remaining <= TimeSpan.Zero)
        {
            throw new GitHubOperationException(GitHubOperationError.DeadlineElapsed);
        }
        var deadline = now + remaining;
        using var timeout = new CancellationTokenSource(remaining, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var retry = ScaleSetRequestRetry.Configure(options => options with
        {
            MaxRetries = 0,
            Deadline = options.Deadline is { } existing && existing < deadline ? existing : deadline,
        });
        return await InvokeAsync(() => send(bounded.Token), cancellationToken).ConfigureAwait(false);
    }
}
