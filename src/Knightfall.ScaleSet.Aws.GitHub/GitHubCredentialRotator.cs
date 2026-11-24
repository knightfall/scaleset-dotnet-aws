using Knightfall.ScaleSet.Aws.Core.Configuration;
using Microsoft.Extensions.Logging;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public enum GitHubCredentialRotationStatus
{
    Disabled = 0,
    Unchanged = 1,
    CandidateValidated = 2,
    CandidateRejected = 3,
}

public sealed record GitHubCredentialRotationResult(
    GitHubCredentialRotationStatus Status,
    GitHubScaleSetConnection? Candidate = null,
    GitHubOperationException? Failure = null);

public sealed partial class GitHubCredentialRotator
{
    private readonly GitHubSecretLoader _secrets;
    private readonly GitHubScaleSetClientFactory _clients;
    private readonly ILogger<GitHubCredentialRotator>? _logger;

    public GitHubCredentialRotator(
        GitHubSecretLoader secrets, GitHubScaleSetClientFactory clients, ILogger<GitHubCredentialRotator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clients);
        _secrets = secrets;
        _clients = clients;
        _logger = logger;
    }

    /// <summary>Validates a new version without changing the current connection.</summary>
    /// <remarks>The serialized listener lane owns a validated candidate and decides when to adopt it.</remarks>
    public async Task<GitHubCredentialRotationResult> CheckAsync(
        RuntimeConfiguration configuration,
        RunnerGitHubConfiguration routing,
        GitHubScaleSetBinding currentBinding,
        GitHubScaleSetConnection current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Mode != ControllerMode.Active)
        {
            return new GitHubCredentialRotationResult(GitHubCredentialRotationStatus.Disabled);
        }

        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(currentBinding);
        ArgumentNullException.ThrowIfNull(current);
        GitHubSecretVersion? version;
        try
        {
            version = await _secrets.LoadAsync(configuration, routing.Connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (GitHubSecretLoadException failure)
        {
            return Rejected(new GitHubOperationException(GitHubOperationError.CredentialUnavailable, failure.StatusCode));
        }
        catch (Exception)
        {
            return Rejected(new GitHubOperationException(GitHubOperationError.CredentialUnavailable));
        }
        if (version is null)
        {
            return Rejected(new GitHubOperationException(GitHubOperationError.CredentialUnavailable));
        }

        GitHubScaleSetConnection? candidate = null;
        try
        {
            string scope = GitHubRoutingIdentity.Scope(version.Target.SourceUri.AbsoluteUri);
            if (scope != current.ScopeIdentity || scope != GitHubRoutingIdentity.Scope(currentBinding.ScopeUrl))
            {
                return Rejected(new GitHubOperationException(GitHubOperationError.BindingMismatch));
            }
            if (version.VersionId == current.VersionId && version.SecretArn == current.SecretArn && version.AuthType == current.AuthType)
            {
                return new GitHubCredentialRotationResult(GitHubCredentialRotationStatus.Unchanged);
            }

            candidate = _clients.Create(version);
            _ = await candidate.LookupAsync(routing, currentBinding, cancellationToken).ConfigureAwait(false);
            var validated = new GitHubCredentialRotationResult(GitHubCredentialRotationStatus.CandidateValidated, candidate);
            candidate = null;
            return validated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception error)
        {
            return Rejected(GitHubOperationException.From(error));
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private GitHubCredentialRotationResult Rejected(GitHubOperationException failure)
    {
        if (_logger is not null)
        {
            LogCandidateRejected(_logger, failure.Error, failure.StatusCode, failure.RateLimit?.Kind,
                failure.RateLimit?.RetryAfter, failure.RateLimit?.Reset, failure.RateLimit?.Retries);
        }
        return new GitHubCredentialRotationResult(GitHubCredentialRotationStatus.CandidateRejected, Failure: failure);
    }

    [LoggerMessage(EventId = 801, Level = LogLevel.Warning,
        Message = "GitHub credential candidate rejected ({Error}, HTTP {StatusCode}, rate limit {RateLimitKind}, retry after {RetryAfter}, reset {Reset}, retries {Retries})")]
    private static partial void LogCandidateRejected(
        ILogger logger, GitHubOperationError error, int? statusCode, ScaleSetRateLimitKind? rateLimitKind,
        TimeSpan? retryAfter, DateTimeOffset? reset, int? retries);
}
