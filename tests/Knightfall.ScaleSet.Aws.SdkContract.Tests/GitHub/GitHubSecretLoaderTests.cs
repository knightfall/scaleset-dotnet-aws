using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SecretsManager;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

public sealed class GitHubSecretLoaderTests
{
    private const string Connection = "engineering";
    private const string SecretArn = "arn:aws:secretsmanager:ap-southeast-2:111122223333:secret:github-app-engineering-a1b2c3";
    private const string Version = "0123456789abcdef0123456789abcdef";
    private const string FixturePat = "ghp_FixtureOnlyNeverValid";
    private const string InvalidKeyMarker = "synthetic-fixture-private-key";
    private static readonly string[] CurrentStages = ["AWSCURRENT"];
    private static readonly Lazy<string> FixturePem = new(CreatePrivateKey);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadsExactCurrentAppVersionAndUsesItsSigningKey(bool pkcs8)
    {
        const string clientId = "fixture-app-client";
        const long installationId = 9_007_199_254_740_993;
        using var rsa = RSA.Create(2048);
        string privateKey = pkcs8 ? rsa.ExportPkcs8PrivateKeyPem() : rsa.ExportRSAPrivateKeyPem();
        string payload = JsonSerializer.Serialize(new { clientId, installationId, privateKey });
        using var secretWire = new AwsWireTransport(_ => AwsWireTransport.Json(SecretResponse(payload)));
        using var sdk = CreateSecretsClient(secretWire);
        var loader = new GitHubSecretLoader(sdk);

        var version = await loader.LoadAsync(ReadConfiguration(), Connection, TestContext.Current.CancellationToken);

        Assert.NotNull(version);
        Assert.Equal(Version, version.VersionId);
        Assert.Equal(SecretArn, version.SecretArn);
        Assert.Equal(GitHubAuthType.GitHubApp, version.AuthType);
        Assert.DoesNotContain(privateKey, version.ToString(), StringComparison.Ordinal);
        AssertExactSecretRead(secretWire);

        using var wire = new GitHubWire(static (_, _) => GitHubWire.Reply(HttpStatusCode.OK, "null"));
        using var http = new HttpClient(wire);
        using var client = version.CreateClient(httpClient: http, timeProvider: new ContractClock());
        Assert.Null(await client.GetScaleSetAsync(GitHubWire.SetId, TestContext.Current.CancellationToken));

        var installation = Assert.Single(wire.Requests, request =>
            request.Uri.AbsolutePath == "/app/installations/9007199254740993/access_tokens");
        Assert.Equal("api.example.ghe.com", installation.Uri.Host);
        Assert.Equal(HttpMethod.Post, installation.Method);
        Assert.StartsWith("Bearer ", installation.Authorization, StringComparison.Ordinal);
        string token = installation.Authorization!["Bearer ".Length..];
        string[] segments = token.Split('.');
        Assert.Equal(3, segments.Length);
        using var claim = JsonDocument.Parse(Base64Url.DecodeFromChars(segments[1]));
        Assert.Equal(clientId, claim.RootElement.GetProperty("iss").GetString());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]),
            Base64Url.DecodeFromChars(segments[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.DoesNotContain(wire.Requests, request => request.Uri.AbsolutePath.Contains("/sessions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadsEnterprisePatAndExchangesItAtTheEnterpriseScope()
    {
        using var secretWire = new AwsWireTransport(_ => AwsWireTransport.Json(
            SecretResponse(JsonSerializer.Serialize(new { token = FixturePat }))));
        using var sdk = CreateSecretsClient(secretWire);
        var loader = new GitHubSecretLoader(sdk);

        var version = await loader.LoadAsync(ReadConfiguration(enterprise: true), Connection, TestContext.Current.CancellationToken);

        Assert.NotNull(version);
        Assert.Equal(Version, version.VersionId);
        Assert.Equal(GitHubAuthType.EnterprisePat, version.AuthType);
        Assert.DoesNotContain(FixturePat, version.ToString(), StringComparison.Ordinal);
        AssertExactSecretRead(secretWire);

        using var wire = new GitHubWire(static (_, _) => GitHubWire.Reply(HttpStatusCode.OK, "null"));
        using var http = new HttpClient(wire);
        using var client = version.CreateClient(httpClient: http, timeProvider: new ContractClock());
        Assert.Null(await client.GetScaleSetAsync(GitHubWire.SetId, TestContext.Current.CancellationToken));

        var registration = Assert.Single(wire.Requests, request =>
            request.Uri.AbsolutePath == "/enterprises/example-enterprise/actions/runners/registration-token");
        Assert.Equal("api.github.com", registration.Uri.Host);
        Assert.Equal("Bearer " + FixturePat, registration.Authorization);
        Assert.DoesNotContain(wire.Requests, request => request.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal));
        Assert.DoesNotContain(wire.Requests, request => request.Uri.AbsolutePath.Contains("/sessions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"token":42}""")]
    [InlineData("""{"token":null}""")]
    [InlineData("""{"token":" "}""")]
    [InlineData("""{"Token":"ghp_FixtureOnlyNeverValid"}""")]
    [InlineData("""{"token":"ghp_FixtureOnlyNeverValid","token":"duplicate"}""")]
    [InlineData("""{"token":"ghp_FixtureOnlyNeverValid","\u0074oken":"duplicate"}""")]
    [InlineData("""{"token":"ghp_FixtureOnlyNeverValid","extra":"unexpected"}""")]
    [InlineData("""{"token":"ghp_FixtureOnlyNeverValid",}""")]
    [InlineData("""{"token":"ghp_FixtureOnlyNeverValid"}{}""")]
    [InlineData("""{"token":"ghp_FixtureOnlyNeverValid\r\n"}""")]
    public async Task RejectsMalformedOrNonClosedPatPayloadWithoutEchoingIt(string payload)
    {
        var error = await LoadInvalidPayload(payload, enterprise: true);

        Assert.Equal("invalid-secret-payload", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(FixturePat, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("github_pat_FixtureOnly")]
    [InlineData("ghs_FixtureOnly")]
    [InlineData("ghu_FixtureOnly")]
    [InlineData("gho_FixtureOnly")]
    [InlineData("ghr_FixtureOnly")]
    [InlineData("ghp_")]
    [InlineData("ghp_Fixture-Only")]
    [InlineData("ghp_FixtureÖnly")]
    public async Task EnterprisePatMustUseClassicTokenSyntax(string token)
    {
        var error = await LoadInvalidPayload(JsonSerializer.Serialize(new { token }), enterprise: true);

        Assert.Equal("invalid-secret-payload", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(token, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("\"42\"")]
    [InlineData("null")]
    public async Task RejectsInstallationIdsOutsidePositiveInt64(string installationId)
    {
        string payload = $$"""
            {"clientId":"fixture-app","installationId":{{installationId}},"privateKey":{{JsonSerializer.Serialize(FixturePem.Value)}}}
            """;

        var error = await LoadInvalidPayload(payload, enterprise: false);

        Assert.Equal("invalid-secret-payload", error.Code);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("extra-member")]
    [InlineData("missing-key")]
    [InlineData("non-string-client")]
    public async Task RejectsAppMemberShapeEvenWithRecognizedValues(string fault)
    {
        var payload = new JsonObject
        {
            ["clientId"] = "fixture-app",
            ["installationId"] = 42,
            ["privateKey"] = FixturePem.Value,
        };
        switch (fault)
        {
            case "extra-member":
                payload["extra"] = "unknown";
                break;
            case "missing-key":
                payload.Remove("privateKey");
                break;
            default:
                payload["clientId"] = 42;
                break;
        }

        var error = await LoadInvalidPayload(payload.ToJsonString(), enterprise: false);

        Assert.Equal("invalid-secret-payload", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsInvalidPemWithoutEchoingIt()
    {
        var payload = JsonSerializer.Serialize(new
        {
            clientId = "fixture-app",
            installationId = 42,
            privateKey = "-----BEGIN PRIVATE KEY-----\n" + InvalidKeyMarker + "\n-----END PRIVATE KEY-----",
        });

        var error = await LoadInvalidPayload(payload, enterprise: false);

        Assert.Equal("invalid-secret-payload", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(InvalidKeyMarker, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("different-arn")]
    [InlineData("missing-version")]
    [InlineData("previous-stage")]
    [InlineData("binary")]
    public async Task OnlyAcceptsExactCurrentTextVersion(string fault)
    {
        string payload = JsonSerializer.Serialize(new { token = FixturePat });
        string body = JsonSerializer.Serialize(new
        {
            ARN = fault == "different-arn" ? SecretArn + "-different" : SecretArn,
            VersionId = fault == "missing-version" ? null : Version,
            VersionStages = new[] { fault == "previous-stage" ? "AWSPREVIOUS" : "AWSCURRENT" },
            SecretString = payload,
            SecretBinary = fault == "binary" ? "c3ludGhldGlj" : null,
        });
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(body));
        using var sdk = CreateSecretsClient(wire);
        var loader = new GitHubSecretLoader(sdk);

        var error = await Assert.ThrowsAsync<GitHubSecretLoadException>(() =>
            loader.LoadAsync(ReadConfiguration(enterprise: true), Connection, TestContext.Current.CancellationToken));

        Assert.Equal("invalid-secret-response", error.Code);
        Assert.DoesNotContain(FixturePat, error.ToString(), StringComparison.Ordinal);
        AssertExactSecretRead(wire);
    }

    [Fact]
    public async Task PreservesHttpStatusAndDropsAwsErrorPayloadAndException()
    {
        string errorBody = JsonSerializer.Serialize(new
        {
            __type = "AccessDeniedException",
            message = string.Join(' ', FixturePat, InvalidKeyMarker, GitHubWire.QueueUrl),
        });
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(errorBody, HttpStatusCode.Forbidden));
        using var sdk = CreateSecretsClient(wire);
        var loader = new GitHubSecretLoader(sdk);

        var error = await Assert.ThrowsAsync<GitHubSecretLoadException>(() =>
            loader.LoadAsync(ReadConfiguration(), Connection, TestContext.Current.CancellationToken));

        Assert.Equal("secret-read-failed", error.Code);
        Assert.Equal((int)HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Null(error.InnerException);
        foreach (string secret in new[] { FixturePat, InvalidKeyMarker, GitHubWire.QueueUrl })
        {
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }
        AssertExactSecretRead(wire);
    }

    [Fact]
    public async Task DryRunDoesNotResolveCredentialsAndUnknownActiveConnectionDoesNotRead()
    {
        using var wire = new AwsWireTransport(_ => throw new InvalidOperationException("Unexpected Secrets Manager call."));
        using var sdk = CreateSecretsClient(wire);
        var loader = new GitHubSecretLoader(sdk);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Null(await loader.LoadAsync(ReadConfiguration(dryRun: true), Connection, cancellation));
        var error = await Assert.ThrowsAsync<GitHubSecretLoadException>(() =>
            loader.LoadAsync(ReadConfiguration(), "unknown-connection", cancellation));

        Assert.Equal("unknown-connection", error.Code);
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task CanceledActiveLoadMakesNoSecretCall()
    {
        using var wire = new AwsWireTransport(_ => throw new InvalidOperationException("Unexpected Secrets Manager call."));
        using var sdk = CreateSecretsClient(wire);
        var loader = new GitHubSecretLoader(sdk);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await source.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loader.LoadAsync(ReadConfiguration(), Connection, source.Token));

        Assert.Equal(source.Token, error.CancellationToken);
        Assert.Empty(wire.Requests);
    }

    [Theory]
    [InlineData("https://github.example.test/engineering/tools", false)]
    [InlineData("https://github.com/enterprises/example-enterprise", false)]
    [InlineData("https://github.com/engineering/tools", true)]
    public async Task InvalidConfigurationScopeIsRejectedBeforeAnySecretRead(string scopeUrl, bool enterprise)
    {
        using var wire = new AwsWireTransport(_ => throw new InvalidOperationException("Unexpected Secrets Manager call."));
        using var sdk = CreateSecretsClient(wire);
        var loader = new GitHubSecretLoader(sdk);

        await Assert.ThrowsAsync<RuntimeConfigurationException>(async () =>
            await loader.LoadAsync(ReadConfiguration(enterprise: enterprise, scopeUrl: scopeUrl), Connection,
                TestContext.Current.CancellationToken));

        Assert.Empty(wire.Requests);
    }

    private static async Task<GitHubSecretLoadException> LoadInvalidPayload(string payload, bool enterprise)
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(SecretResponse(payload)));
        using var sdk = CreateSecretsClient(wire);
        var loader = new GitHubSecretLoader(sdk);
        var error = await Assert.ThrowsAsync<GitHubSecretLoadException>(() =>
            loader.LoadAsync(ReadConfiguration(enterprise), Connection, TestContext.Current.CancellationToken));

        AssertExactSecretRead(wire);
        return error;
    }

    private static string SecretResponse(string payload) => JsonSerializer.Serialize(new
    {
        ARN = SecretArn,
        VersionId = Version,
        VersionStages = CurrentStages,
        SecretString = payload,
    });

    private static AmazonSecretsManagerClient CreateSecretsClient(AwsWireTransport wire) =>
        new(AwsWireTransport.Credentials, wire.Configure(new AmazonSecretsManagerConfig()));

    private static void AssertExactSecretRead(AwsWireTransport wire)
    {
        var request = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("secretsmanager.GetSecretValue", request.Target);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(SecretArn, body.RootElement.GetProperty("SecretId").GetString());
        Assert.Equal("AWSCURRENT", body.RootElement.GetProperty("VersionStage").GetString());
        Assert.False(body.RootElement.TryGetProperty("VersionId", out _));
    }

    private static RuntimeConfiguration ReadConfiguration(bool enterprise = false, bool dryRun = false, string? scopeUrl = null)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fargate-only.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var spec = json["spec"]!;
        var settings = spec["github"]![Connection]!;
        if (dryRun)
        {
            spec["mode"] = "dry-run";
        }
        if (enterprise)
        {
            settings["auth"]!["type"] = "enterprise-pat";
            settings["scopeURL"] = "https://github.com/enterprises/example-enterprise";
        }
        if (scopeUrl is not null)
        {
            settings["scopeURL"] = scopeUrl;
        }
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(json));
    }

    private static string CreatePrivateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
