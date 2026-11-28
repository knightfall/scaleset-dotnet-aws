using System.Net;
using System.Text.Json;
using ScaleSet.Client;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

public sealed class GitHubClientContracts
{
    private const string SetsPath = GitHubWire.ServicePath + "/_apis/runtime/runnerscalesets";
    private const string AppJwt = "synthetic-app-jwt";
    private static string SetPath => $"{SetsPath}/{GitHubWire.SetId}";
    private static string SessionPath => $"{SetPath}/sessions";
    private static string RetirementPath => $"{SessionPath}/{GitHubWire.SessionId:D}";

    [Theory]
    [InlineData("https://github.com/owner", "api.github.com", "/orgs/owner/actions/runners/registration-token", false)]
    [InlineData("https://github.com/owner", "api.github.com", "/orgs/owner/actions/runners/registration-token", true)]
    [InlineData("https://github.com/owner/repo", "api.github.com", "/repos/owner/repo/actions/runners/registration-token", false)]
    [InlineData("https://github.com/owner/repo", "api.github.com", "/repos/owner/repo/actions/runners/registration-token", true)]
    [InlineData("https://tenant.ghe.com/owner", "api.tenant.ghe.com", "/orgs/owner/actions/runners/registration-token", false)]
    [InlineData("https://tenant.ghe.com/owner", "api.tenant.ghe.com", "/orgs/owner/actions/runners/registration-token", true)]
    [InlineData("https://tenant.ghe.com/owner/repo", "api.tenant.ghe.com", "/repos/owner/repo/actions/runners/registration-token", false)]
    [InlineData("https://tenant.ghe.com/owner/repo", "api.tenant.ghe.com", "/repos/owner/repo/actions/runners/registration-token", true)]
    [InlineData("https://github.com/enterprises/owner", "api.github.com", "/enterprises/owner/actions/runners/registration-token", false)]
    public async Task AuthenticationExchangesCredentialsAtTheExactScope(
        string targetUrl, string apiHost, string registrationPath, bool app)
    {
        using var wire = new GitHubWire(static (_, _) => GitHubWire.Reply(HttpStatusCode.OK, "null"));
        using var http = new HttpClient(wire);
        var clock = new ContractClock();
        var jwt = new FixedAppJwt();
        var credentials = app
            ? ScaleSetCredentials.FromGitHubApp("app-id", GitHubWire.RunnerId, jwt)
            : ScaleSetCredentials.FromPersonalAccessToken(GitHubWire.Pat);
        using var client = new ScaleSetClient(GitHubTarget.Parse(targetUrl), credentials, httpClient: http, timeProvider: clock);

        Assert.Null(await client.GetScaleSetAsync(GitHubWire.SetId, TestContext.Current.CancellationToken));

        var requests = wire.Requests;
        Assert.Equal(app ? 4 : 3, requests.Length);
        Assert.All(requests.SkipLast(1), request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(apiHost, request.Uri.Host);
        });
        if (app)
        {
            Assert.Equal($"/app/installations/{GitHubWire.RunnerId}/access_tokens", requests[0].Uri.AbsolutePath);
            Assert.Equal("Bearer " + AppJwt, requests[0].Authorization);
            Assert.Equal(("app-id", clock.Now), jwt.Invocation);
        }
        Assert.Equal(registrationPath, requests[^3].Uri.AbsolutePath);
        Assert.Equal("Bearer " + (app ? GitHubWire.InstallationToken : GitHubWire.Pat), requests[^3].Authorization);
        Assert.Equal("/actions/runner-registration", requests[^2].Uri.AbsolutePath);
        Assert.Equal("RemoteAuth " + GitHubWire.RegistrationToken, requests[^2].Authorization);
        using var discovery = JsonDocument.Parse(requests[^2].Body!);
        Assert.Equal(targetUrl, discovery.RootElement.GetProperty("url").GetString());
        Assert.Equal("register", discovery.RootElement.GetProperty("runner_event").GetString());
        string serviceHost = apiHost == "api.tenant.ghe.com"
            ? "service-fixture.actions.tenant.ghe.com"
            : GitHubWire.ServiceHost;
        AssertAdmin(requests[^1], HttpMethod.Get, SetPath, serviceHost);
    }

    [Fact]
    public void EnterpriseAppsAndCustomGhesAreRejectedBeforeAnyExchange()
    {
        using var wire = new GitHubWire(static (_, _) => throw new InvalidOperationException("Unexpected transport call."));
        using var http = new HttpClient(wire);
        Assert.Throws<NotSupportedException>(() => GitHubTarget.Parse("https://github.example.test/owner/repo"));
        var enterprise = GitHubTarget.Parse("https://github.com/enterprises/owner");
        var credentials = ScaleSetCredentials.FromGitHubApp("app-id", 1, new FixedAppJwt());

        var error = Assert.Throws<ArgumentException>(() => new ScaleSetClient(enterprise, credentials, httpClient: http));

        Assert.Equal("credentials", error.ParamName);
        Assert.Contains("classic PAT", error.Message, StringComparison.Ordinal);
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public void ReleasedOptionsHaveNoCustomCaOrMutualTlsSettings()
    {
        Assert.DoesNotContain(typeof(ScaleSetClientOptions).GetProperties(),
            property => property.Name is "CaBundlePath" or "ClientCertificate" or "ClientCertificatePath");
        Assert.DoesNotContain(typeof(ScaleSetClient).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.Name is "caBundlePath" or "clientCertificate" or "clientCertificatePath");
    }

    [Theory]
    [InlineData("set")]
    [InlineData("jit")]
    [InlineData("session")]
    public async Task JournalOwnedMutationMakesOneWireAttemptAfterCommittedResponseLoss(string operation)
    {
        var commits = new List<GitHubRequest>();
        using var wire = new GitHubWire((request, _) =>
        {
            commits.Add(request);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Response lost after commit."));
        });
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock(), new ScaleSetClientOptions
        {
            Retry = new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                ShouldRetry = static (_, _) => ValueTask.FromResult(true),
                WaitMin = TimeSpan.Zero,
            },
        });
        using var singleAttempt = ScaleSetRequestRetry.Configure(static options => options with { MaxRetries = 0 });
        var cancellationToken = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<ScaleSetApiException>(() => operation switch
        {
            "set" => client.CreateScaleSetAsync(new RunnerScaleSet { Name = "runner-profile" }, cancellationToken),
            "jit" => client.GenerateJitConfigAsync(GitHubWire.SetId,
                new RunnerScaleSetJitRunnerSetting { Name = "runner", WorkFolder = "_work" }, cancellationToken),
            "session" => client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", cancellationToken),
            _ => throw new InvalidOperationException(operation),
        });

        Assert.True(error.IsTransientTransportFailure);
        Assert.Null(error.StatusCode);
        var committed = Assert.Single(commits);
        var expectedPath = operation switch { "set" => SetsPath, "jit" => SetPath + "/generatejitconfig", _ => SessionPath };
        AssertAdmin(committed, HttpMethod.Post, expectedPath);
        using var body = JsonDocument.Parse(committed.Body!);
        switch (operation)
        {
            case "set":
                Assert.Equal("runner-profile", body.RootElement.GetProperty("name").GetString());
                var label = Assert.Single(body.RootElement.GetProperty("labels").EnumerateArray());
                Assert.Equal("runner-profile", label.GetProperty("name").GetString());
                Assert.Equal("System", label.GetProperty("type").GetString());
                break;
            case "jit":
                Assert.Equal("runner", body.RootElement.GetProperty("name").GetString());
                Assert.Equal("_work", body.RootElement.GetProperty("workFolder").GetString());
                break;
            case "session":
                Assert.Equal("owner", body.RootElement.GetProperty("ownerName").GetString());
                break;
        }
    }

    [Theory]
    [InlineData(204)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(0)]
    public async Task RetirementTargetsTheJournaledSessionAndPreservesUncertainty(int status)
    {
        using var wire = new GitHubWire((_, _) => status switch
        {
            0 => Task.FromException<HttpResponseMessage>(new HttpRequestException("Retirement response lost.")),
            204 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
            _ => GitHubWire.Reply((HttpStatusCode)status, """{"typeKey":"RunnerScaleSetSessionExpiredException"}"""),
        });
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock(), new ScaleSetClientOptions
        {
            Retry = new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                ShouldRetry = static (_, _) => ValueTask.FromResult(true),
                WaitMin = TimeSpan.Zero,
            },
        });
        var cancellationToken = TestContext.Current.CancellationToken;

        if (status == 204)
        {
            await client.DeleteMessageSessionAsync(GitHubWire.SetId, GitHubWire.SessionId, cancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ScaleSetApiException>(() =>
                client.DeleteMessageSessionAsync(GitHubWire.SetId, GitHubWire.SessionId, cancellationToken));
            Assert.Equal(status == 0 ? null : status, error.StatusCode);
            Assert.Equal(status == 0, error.IsTransientTransportFailure);
        }

        var call = Assert.Single(wire.Requests, request => request.Uri.Host == GitHubWire.ServiceHost);
        AssertAdmin(call, HttpMethod.Delete, RetirementPath);
        Assert.Equal(string.Empty, call.Body);
        Assert.DoesNotContain(wire.Requests, request => request.Uri.Host == GitHubWire.QueueHost);
    }

    [Theory]
    [InlineData("set", "null")]
    [InlineData("set", """{"id":7}{"id":8}""")]
    [InlineData("jit", "null")]
    [InlineData("jit", """{"encodedJITConfig":"synthetic-jit"}{"runner":{"id":1}}""")]
    [InlineData("session", "null")]
    [InlineData("session", """{"ownerName":"owner"}{"ownerName":"other"}""")]
    public async Task RequiredMutationResponsesRejectNullAndTrailingJson(string operation, string response)
    {
        using var wire = new GitHubWire((_, _) => GitHubWire.Reply(HttpStatusCode.OK, response));
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock());

        var token = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAsync<ScaleSetApiException>(() => operation switch
        {
            "set" => client.CreateScaleSetAsync(new RunnerScaleSet { Name = "runner-profile" }, token),
            "jit" => client.GenerateJitConfigAsync(GitHubWire.SetId,
                new RunnerScaleSetJitRunnerSetting { Name = "runner" }, token),
            "session" => client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", token),
            _ => throw new InvalidOperationException(operation),
        });

        Assert.Equal(200, error.StatusCode);
        var expectedPath = operation switch { "set" => SetsPath, "jit" => SetPath + "/generatejitconfig", _ => SessionPath };
        AssertAdmin(Assert.Single(wire.Requests, request => request.Uri.Host == GitHubWire.ServiceHost),
            HttpMethod.Post, expectedPath);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "22", 42, 22, ScaleSetRateLimitKind.Secondary)]
    [InlineData(HttpStatusCode.Forbidden, null, 125, 125, ScaleSetRateLimitKind.Primary)]
    public async Task RateLimitKeepsRetryAfterResetAndClassification(
        HttpStatusCode status, string? retryAfter, int resetOffset, int seconds, ScaleSetRateLimitKind kind)
    {
        var clock = new ContractClock();
        var reset = clock.Now.AddSeconds(resetOffset);
        using var wire = new GitHubWire((_, _) =>
        {
            var response = GitHubWire.Json(status, """{"message":"rate limited"}""");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset",
                reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }
            if (kind == ScaleSetRateLimitKind.Primary)
            {
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            }
            return Task.FromResult(response);
        });
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, clock, new ScaleSetClientOptions { Retry = ScaleSetRetryOptions.Disabled });

        var error = await Assert.ThrowsAsync<ScaleSetRateLimitException>(() =>
            client.GetScaleSetAsync(GitHubWire.SetId, TestContext.Current.CancellationToken));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(TimeSpan.FromSeconds(seconds), error.RetryAfter);
        Assert.Equal(reset, error.Reset);
        Assert.Equal(0, error.Retries);
        AssertAdmin(Assert.Single(wire.Requests, request => request.Uri.Host == GitHubWire.ServiceHost), HttpMethod.Get, SetPath);
    }

    [Theory]
    [InlineData("https://github.com/owner/repository", GitHubWire.ServiceHost, GitHubWire.QueueHost)]
    [InlineData("https://tenant.ghe.com/owner/repository", "service-fixture.actions.tenant.ghe.com", "queue-fixture.actions.tenant.ghe.com")]
    public async Task SessionRoutesAdministrationAndQueueTrafficToTheSameTarget(
        string url, string serviceHost, string queueHost)
    {
        GitHubTarget target = GitHubTarget.Parse(url);
        using var wire = new GitHubWire((call, _) => call.Method == HttpMethod.Get
            ? GitHubWire.Reply(HttpStatusCode.Accepted, "")
            : GitHubWire.Reply(HttpStatusCode.OK, GitHubWire.SessionJson(target: target)));
        using var http = new HttpClient(wire);
        using var client = new ScaleSetClient(target, ScaleSetCredentials.FromPersonalAccessToken(GitHubWire.Pat),
            httpClient: http, timeProvider: new ContractClock());

        var token = TestContext.Current.CancellationToken;
        var session = await client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", token);
        Assert.Null(await session.GetMessageAsync(0, 1, token));

        AssertAdmin(Assert.Single(wire.Requests, call => call.Uri.AbsolutePath == SessionPath), HttpMethod.Post,
            SessionPath, serviceHost);
        var poll = Assert.Single(wire.Requests, call => call.Method == HttpMethod.Get);
        Assert.Equal(queueHost, poll.Uri.Host);
        Assert.Equal(GitHubWire.QueuePath, poll.Uri.AbsolutePath);
        Assert.Equal("Bearer " + GitHubWire.QueueToken, poll.Authorization);
    }

    [Fact]
    public async Task SessionRejectsQueueForAnotherTenantBeforeSendingItsToken()
    {
        GitHubTarget target = GitHubTarget.Parse("https://tenant.ghe.com/owner/repository");
        GitHubTarget other = GitHubTarget.Parse("https://other.ghe.com/owner/repository");
        using var wire = new GitHubWire((_, _) => GitHubWire.Reply(HttpStatusCode.OK, GitHubWire.SessionJson(target: other)));
        using var http = new HttpClient(wire);
        using var client = new ScaleSetClient(target, ScaleSetCredentials.FromPersonalAccessToken(GitHubWire.Pat),
            httpClient: http, timeProvider: new ContractClock());

        await Assert.ThrowsAsync<ScaleSetApiException>(() =>
            client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", TestContext.Current.CancellationToken));

        AssertAdmin(Assert.Single(wire.Requests, call => call.Uri.AbsolutePath == SessionPath), HttpMethod.Post,
            SessionPath, "service-fixture.actions.tenant.ghe.com");
        Assert.DoesNotContain(wire.Requests, call => call.Uri.Host == "queue-fixture.actions.other.ghe.com"
            || call.Authorization == "Bearer " + GitHubWire.QueueToken);
    }

    [Fact]
    public async Task QueueFailureKeepsRawResponseButRedactsDiagnosticSurfaces()
    {
        const string keyPayload = "synthetic-private-key-material";
        string pem = $"-----BEGIN PRIVATE KEY-----\n{keyPayload}\n-----END PRIVATE KEY-----";
        string failure = JsonSerializer.Serialize(new
        {
            message = $"Failed for {GitHubWire.Pat}.",
            token = GitHubWire.QueueToken,
            privateKey = pem,
            sessionUrl = GitHubWire.QueueUrl,
        });
        using var wire = new GitHubWire((request, _) => request.Uri.Host == GitHubWire.QueueHost
            ? GitHubWire.Reply(HttpStatusCode.BadRequest, failure)
            : GitHubWire.Reply(HttpStatusCode.OK, GitHubWire.SessionJson()));
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock());
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = await client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", cancellationToken);

        var error = await Assert.ThrowsAsync<ScaleSetApiException>(() => session.GetMessageAsync(0, 0, cancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.NotNull(error.BodyPreview);
        Assert.NotNull(error.Response);
        Assert.Equal(failure, System.Text.Encoding.UTF8.GetString(error.Response.Body.Span));
        foreach (string diagnostic in new[] { error.ToString(), error.BodyPreview, error.SafeUri!, error.Response.ToString(), session.Session.ToString() })
        {
            Assert.DoesNotContain(GitHubWire.Pat, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(GitHubWire.QueueToken, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(keyPayload, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(GitHubWire.QueuePath, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-queue-signature", diagnostic, StringComparison.Ordinal);
        }
    }

    private static void AssertAdmin(
        GitHubRequest request, HttpMethod method, string path, string serviceHost = GitHubWire.ServiceHost)
    {
        Assert.Equal(method, request.Method);
        Assert.Equal(serviceHost, request.Uri.Host);
        Assert.Equal(path, request.Uri.AbsolutePath);
        Assert.Equal("Bearer " + GitHubWire.AdminToken, request.Authorization);
    }

    private sealed class FixedAppJwt : IGitHubAppJwtProvider
    {
        internal (string ClientId, DateTimeOffset Now)? Invocation { get; private set; }

        public ValueTask<string> CreateJwtAsync(string clientId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocation = (clientId, now);
            return ValueTask.FromResult(AppJwt);
        }
    }
}
