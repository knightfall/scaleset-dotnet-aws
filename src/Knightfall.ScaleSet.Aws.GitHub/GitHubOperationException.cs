using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public enum GitHubOperationError
{
    RequestFailed = 0,
    BindingMismatch = 1,
    CredentialUnavailable = 2,
    DeadlineElapsed = 3,
    AuthorizationConsumed = 4,
}

public sealed record GitHubRateLimit(ScaleSetRateLimitKind Kind, TimeSpan RetryAfter, DateTimeOffset? Reset, int Retries);

public sealed class GitHubOperationException(
    GitHubOperationError error,
    int? statusCode = null,
    bool isTransientTransportFailure = false,
    GitHubRateLimit? rateLimit = null,
    ScaleSetDomainError? domainError = null) : Exception(error switch
{
    GitHubOperationError.BindingMismatch => "GitHub returned a different runner scale-set binding.",
    GitHubOperationError.CredentialUnavailable => "The GitHub credential is unavailable.",
    GitHubOperationError.DeadlineElapsed => "The GitHub operation's deadline elapsed.",
    GitHubOperationError.AuthorizationConsumed => "The GitHub effect send authorization was already consumed.",
    _ when rateLimit is not null => "GitHub rate limited the operation.",
    _ => "The GitHub operation failed.",
})
{
    public GitHubOperationError Error { get; } = error;
    public int? StatusCode { get; } = statusCode;
    public bool IsTransientTransportFailure { get; } = isTransientTransportFailure;
    public GitHubRateLimit? RateLimit { get; } = rateLimit;
    public ScaleSetDomainError? DomainError { get; } = domainError;

    internal static GitHubOperationException From(Exception error) =>
        error switch
        {
            GitHubOperationException safe => safe,
            ScaleSetRateLimitException rate => new GitHubOperationException(
                GitHubOperationError.RequestFailed, rate.StatusCode, rate.IsTransientTransportFailure,
                new GitHubRateLimit(rate.Kind, rate.RetryAfter, rate.Reset, rate.Retries), rate.DomainError),
            ScaleSetMaxElapsedExceededException elapsed => new GitHubOperationException(
                GitHubOperationError.DeadlineElapsed, elapsed.StatusCode, elapsed.IsTransientTransportFailure),
            ScaleSetApiException api => new GitHubOperationException(
                GitHubOperationError.RequestFailed, api.StatusCode, api.IsTransientTransportFailure, domainError: api.DomainError),
            HttpRequestException request => new GitHubOperationException(
                GitHubOperationError.RequestFailed, (int?)request.StatusCode, request.StatusCode is null),
            OperationCanceledException or TimeoutException => new GitHubOperationException(GitHubOperationError.DeadlineElapsed),
            _ => new GitHubOperationException(GitHubOperationError.RequestFailed),
        };
}
