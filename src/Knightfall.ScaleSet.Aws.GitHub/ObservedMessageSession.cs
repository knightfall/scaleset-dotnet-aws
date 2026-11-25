using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

/// <summary>Measures the exact released queue calls while the Listener owns their authorization and retry semantics.</summary>
internal sealed class ObservedMessageSession(IMessageSessionClient inner, TimeProvider clock) : IMessageSessionClient
{
    public RunnerScaleSetSession Session => inner.Session;

    internal bool PollCredentialRejected { get; set; }

    public async Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
    {
        using var telemetry = GitHubListenerDiagnostics.Start("poll", clock, cancellationToken);
        try
        {
            var message = await inner.GetMessageAsync(lastMessageId, maxCapacity, cancellationToken).ConfigureAwait(false);
            telemetry.Complete(message is null ? "empty" : "received");
            return message;
        }
        catch (ScaleSetApiException error) when (error is { StatusCode: 401, Method: "GET" })
        {
            PollCredentialRejected = true;
            telemetry.Complete("failed", "unauthorized");
            throw;
        }
        catch (ScaleSetRateLimitException)
        {
            telemetry.Complete("deferred", "rate_limit");
            throw;
        }
        catch (Exception error) when (GitHubListenerDiagnostics.IsDeadline(error, cancellationToken))
        {
            telemetry.Complete("unknown", "timeout");
            throw;
        }
    }

    public async Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
    {
        using var telemetry = GitHubListenerDiagnostics.Start("acknowledge", clock, CancellationToken.None);
        try
        {
            await inner.DeleteMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
            telemetry.Complete("returned");
        }
        catch (ScaleSetRateLimitException)
        {
            telemetry.Complete("unknown", "rate_limit");
            throw;
        }
        catch (Exception error) when (GitHubListenerDiagnostics.IsDeadline(error, cancellationToken))
        {
            telemetry.Complete("unknown", "timeout");
            throw;
        }
    }

    public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) =>
        inner.AcquireJobsAsync(runnerRequestIds, cancellationToken);
}
