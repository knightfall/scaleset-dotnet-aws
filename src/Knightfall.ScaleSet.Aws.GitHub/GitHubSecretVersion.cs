using Knightfall.ScaleSet.Aws.Core.Configuration;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed class GitHubSecretVersion
{
    private readonly ScaleSetCredentials _credentials;

    internal GitHubSecretVersion(
        string secretArn,
        string versionId,
        GitHubAuthType authType,
        GitHubTarget target,
        ScaleSetCredentials credentials)
    {
        SecretArn = secretArn;
        VersionId = versionId;
        AuthType = authType;
        Target = target;
        _credentials = credentials;
    }

    public string SecretArn { get; }
    public string VersionId { get; }
    public GitHubAuthType AuthType { get; }
    public GitHubTarget Target { get; }

    public ScaleSetClient CreateClient(
        ScaleSetClientOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null) =>
        new(Target, _credentials, options, httpClient, timeProvider: timeProvider);
}

public sealed class GitHubSecretLoadException(string code, string message, int? statusCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? StatusCode { get; } = statusCode;
}
