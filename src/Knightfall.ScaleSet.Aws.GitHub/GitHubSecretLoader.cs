using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed class GitHubSecretLoader
{
    private const string CurrentStage = "AWSCURRENT";
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };
    private readonly IAmazonSecretsManager _secrets;

    public GitHubSecretLoader(IAmazonSecretsManager secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public async Task<GitHubSecretVersion?> LoadAsync(
        RuntimeConfiguration configuration,
        string connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Mode != ControllerMode.Active)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(connection) || !configuration.GitHub.TryGetValue(connection, out var settings))
        {
            throw new GitHubSecretLoadException("unknown-connection", "The requested GitHub connection is not configured.");
        }

        var target = ValidateTarget(settings);
        GetSecretValueResponse response;
        try
        {
            response = await _secrets.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = settings.SecretArn,
                VersionStage = CurrentStage,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is
            AmazonClientException or AmazonServiceException or HttpRequestException or SocketException or
            IOException or JsonException or TimeoutException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? status = error is AmazonServiceException serviceError && (int)serviceError.StatusCode is >= 100 and <= 599
                ? (int)serviceError.StatusCode
                : null;
            throw new GitHubSecretLoadException("secret-read-failed", "Unable to load the current GitHub credential version.", status);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (response is not { HttpStatusCode: HttpStatusCode.OK } ||
            !string.Equals(response.ARN, settings.SecretArn, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(response.VersionId) ||
            response.VersionStages?.Contains(CurrentStage, StringComparer.Ordinal) != true ||
            response.SecretBinary is not null ||
            string.IsNullOrWhiteSpace(response.SecretString))
        {
            throw new GitHubSecretLoadException(
                "invalid-secret-response", "Secrets Manager did not return the exact current text credential version.");
        }

        var credentials = ParseCredentials(response.SecretString, settings.AuthType);
        return new GitHubSecretVersion(settings.SecretArn, response.VersionId, settings.AuthType, target, credentials);
    }

    private static GitHubTarget ValidateTarget(GitHubConnectionConfiguration settings)
    {
        GitHubTarget target;
        try
        {
            target = GitHubTarget.Parse(settings.ScopeUrl);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new GitHubSecretLoadException("invalid-target", "The configured GitHub target is unsupported.");
        }

        bool supported = settings.AuthType switch
        {
            GitHubAuthType.GitHubApp => target.Scope is GitHubTargetScope.Organization or GitHubTargetScope.Repository,
            GitHubAuthType.EnterprisePat => target.Scope == GitHubTargetScope.Enterprise,
            _ => false,
        };
        if (!supported)
        {
            throw new GitHubSecretLoadException("invalid-target", "The authentication type does not match the GitHub target.");
        }

        return target;
    }

    private static ScaleSetCredentials ParseCredentials(string secret, GitHubAuthType authType)
    {
        try
        {
            using var document = JsonDocument.Parse(secret, StrictJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidPayload();
            }

            return authType switch
            {
                GitHubAuthType.GitHubApp when root.GetPropertyCount() == 3 => ParseAppCredentials(root),
                GitHubAuthType.EnterprisePat when root.GetPropertyCount() == 1 => ParseEnterprisePat(root),
                _ => throw InvalidPayload(),
            };
        }
        catch (Exception error) when (error is JsonException or ArgumentException or CryptographicException or InvalidOperationException)
        {
            throw InvalidPayload();
        }
    }

    private static ScaleSetCredentials ParseAppCredentials(JsonElement root)
    {
        if (!root.TryGetProperty("installationId", out var installation) ||
            installation.ValueKind != JsonValueKind.Number ||
            !installation.TryGetInt64(out long installationId) || installationId <= 0)
        {
            throw InvalidPayload();
        }

        return ScaleSetCredentials.FromGitHubApp(
            RequiredText(root, "clientId"), installationId, RequiredText(root, "privateKey"));
    }

    private static ScaleSetCredentials ParseEnterprisePat(JsonElement root)
    {
        const string prefix = "ghp_";
        string token = RequiredText(root, "token");
        if (token.Length <= prefix.Length ||
            !token.StartsWith(prefix, StringComparison.Ordinal) ||
            !token.Skip(prefix.Length).All(char.IsAsciiLetterOrDigit))
        {
            throw InvalidPayload();
        }

        return ScaleSetCredentials.FromPersonalAccessToken(token);
    }

    private static string RequiredText(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } value ||
            string.IsNullOrWhiteSpace(value))
        {
            throw InvalidPayload();
        }

        return value;
    }

    private static GitHubSecretLoadException InvalidPayload() =>
        new("invalid-secret-payload", "The current GitHub credential has an invalid JSON shape or credential value.");
}
