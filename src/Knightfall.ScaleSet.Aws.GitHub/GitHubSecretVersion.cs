using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed class GitHubSecretVersion
{
    private readonly ScaleSetCredentials _credentials;
    private readonly GitHubAppAuthorization? _app;
    private readonly string? _enterprisePat;

    internal GitHubSecretVersion(string secretArn, string versionId, GitHubTarget target, GitHubAppAuthorization app)
        : this(secretArn, versionId, GitHubAuthType.GitHubApp, target,
            ScaleSetCredentials.FromGitHubApp(app.ClientId, app.InstallationId, app.JwtProvider))
    {
        _app = app;
    }

    internal GitHubSecretVersion(string secretArn, string versionId, GitHubTarget target, string enterprisePat)
        : this(secretArn, versionId, GitHubAuthType.EnterprisePat, target, ScaleSetCredentials.FromPersonalAccessToken(enterprisePat))
    {
        _enterprisePat = enterprisePat;
    }

    private GitHubSecretVersion(
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

    internal GitHubRunnerAvailability CreateRunnerAvailability(HttpClient? http, TimeProvider clock, string userAgent) =>
        new(Target, _app, _enterprisePat, http, clock, userAgent);
}

internal sealed class GitHubAppAuthorization(string clientId, long installationId, IGitHubAppJwtProvider jwtProvider)
{
    internal string ClientId { get; } = clientId;
    internal long InstallationId { get; } = installationId;
    internal IGitHubAppJwtProvider JwtProvider { get; } = jwtProvider;
}

internal sealed class PemGitHubAppJwtProvider : IGitHubAppJwtProvider
{
    private static readonly string Header = Base64Url.EncodeToString("""{"alg":"RS256","typ":"JWT"}"""u8);
    private readonly string _privateKeyPem;

    internal PemGitHubAppJwtProvider(string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        _ = rsa.SignData([], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _privateKeyPem = privateKeyPem;
    }

    public ValueTask<string> CreateJwtAsync(string clientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issuedAt = now.AddMinutes(-1);
        byte[] claims = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = clientId,
            iat = issuedAt.ToUnixTimeSeconds(),
            exp = issuedAt.AddMinutes(9).ToUnixTimeSeconds(),
        });
        string unsigned = Header + "." + Base64Url.EncodeToString(claims);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);
        byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(unsigned + "." + Base64Url.EncodeToString(signature));
    }
}

public sealed class GitHubSecretLoadException(string code, string message, int? statusCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? StatusCode { get; } = statusCode;
}
