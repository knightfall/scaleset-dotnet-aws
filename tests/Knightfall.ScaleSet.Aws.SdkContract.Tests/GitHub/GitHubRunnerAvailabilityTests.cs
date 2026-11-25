using System.Globalization;
using System.Net;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.GitHub;
using ScaleSet.Client;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

public sealed class GitHubRunnerAvailabilityTests
{
    private const string RunnerName = "runner-availability-fixture";
    private const string EnterprisePat = "ghp_FixtureOnlyNeverValid";
    private const string Installation = "/app/installations/42/access_tokens";
    private const string Membership = GitHubWire.ServicePath + "/_apis/distributedtask/pools/0/agents/9000000003";
    private static readonly string RunnerId = GitHubWire.RunnerId.ToString(CultureInfo.InvariantCulture);
    private static readonly string MembershipBody =
        $$"""{"id":{{GitHubWire.RunnerId}},"runnerScaleSetId":{{GitHubWire.SetId}},"name":"{{RunnerName}}"}""";
    private static readonly string Available = $$"""{"id":{{GitHubWire.RunnerId}},"name":"{{RunnerName}}","status":"online","busy":false}""";

    [Theory]
    [InlineData("https://github.com/owner", "api.github.com", "/orgs/owner", "organization_self_hosted_runners")]
    [InlineData("https://tenant.ghe.com/owner/repo", "api.tenant.ghe.com", "/repos/owner/repo", "administration")]
    public async Task AppReadinessUsesScopedReadOnlyTokenAndRechecksRunnerOnEveryCall(
        string targetUrl, string expectedHost, string scopePath, string permission)
    {
        string readiness = scopePath + "/actions/runners/" + RunnerId;
        bool busy = false;
        using var wire = new GitHubWire((request, _) => request.Uri.AbsolutePath == Membership
            ? GitHubWire.Reply(HttpStatusCode.OK, MembershipBody)
            : GitHubWire.Reply(HttpStatusCode.OK, busy ? Available.Replace("false", "true", StringComparison.Ordinal) : Available));
        using var http = new HttpClient(wire);
        var clock = new ContractClock();
        var signer = new TestJwtProvider();
        var version = new GitHubSecretVersion("fixture", "version", GitHubTarget.Parse(targetUrl),
            new GitHubAppAuthorization("fixture-app", 42, signer));
        using var connection = new GitHubScaleSetClientFactory(http, timeProvider: clock).Create(version);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.True(await connection.IsExactRunnerAvailableAsync(connection.ScopeIdentity, GitHubWire.SetId, RunnerName, GitHubWire.RunnerId, cancellation));
        Assert.Equal(2, wire.Requests.Count(request => request.Uri.AbsolutePath == Installation));
        busy = true;
        Assert.False(await connection.IsExactRunnerAvailableAsync(connection.ScopeIdentity, GitHubWire.SetId, RunnerName, GitHubWire.RunnerId, cancellation));
        Assert.Equal(2, wire.Requests.Count(request => request.Uri.AbsolutePath == Installation));

        clock.Now = clock.Now.AddMinutes(10);
        busy = false;
        Assert.True(await connection.IsExactRunnerAvailableAsync(connection.ScopeIdentity, GitHubWire.SetId, RunnerName, GitHubWire.RunnerId, cancellation));

        var requests = wire.Requests;
        Assert.Equal(new[] { Membership, readiness, Membership, readiness, Membership, readiness },
            requests.Where(request => request.Uri.AbsolutePath == Membership || request.Uri.AbsolutePath == readiness)
                .Select(request => request.Uri.AbsolutePath));
        Assert.All(requests.Where(request => request.Uri.AbsolutePath == readiness), request =>
        {
            Assert.Equal(expectedHost, request.Uri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.True(request.Authorization == "Bearer " + GitHubWire.InstallationToken);
            Assert.Empty(request.Uri.Query);
        });
        var installations = requests.Where(request => request.Uri.AbsolutePath == Installation).ToArray();
        Assert.Equal(3, signer.Calls);
        Assert.Equal(3, installations.Length);
        Assert.All(installations, request => Assert.Equal(expectedHost, request.Uri.Host));
        Assert.All(installations.Where(request => !string.IsNullOrWhiteSpace(request.Body) && request.Body != "{}"), request =>
        {
            using var payload = JsonDocument.Parse(request.Body!);
            Assert.Equal("read", Assert.Single(payload.RootElement.GetProperty("permissions").EnumerateObject()).Value.GetString());
            Assert.True(payload.RootElement.GetProperty("permissions").TryGetProperty(permission, out _));
            if (permission == "administration")
            {
                Assert.Equal("repo", Assert.Single(payload.RootElement.GetProperty("repositories").EnumerateArray()).GetString());
            }
            else
            {
                Assert.False(payload.RootElement.TryGetProperty("repositories", out _));
            }
        });
        Assert.Equal(2, installations.Count(request => request.Body?.Contains(permission, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task EnterpriseReadinessChecksMembershipBeforeSendingTheExistingPat()
    {
        const string expectedPath = "/enterprises/enterprise/actions/runners/9000000003";
        using var wire = new GitHubWire((request, _) => request.Uri.AbsolutePath == Membership
            ? GitHubWire.Reply(HttpStatusCode.OK, MembershipBody)
            : GitHubWire.Reply(HttpStatusCode.OK, Available));
        using var http = new HttpClient(wire);
        using var connection = CreateEnterprise(http);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.False(await connection.IsExactRunnerAvailableAsync("github.com/another-scope", GitHubWire.SetId,
            RunnerName, GitHubWire.RunnerId, cancellation));
        Assert.Empty(wire.Requests);
        Assert.False(await connection.IsExactRunnerAvailableAsync(connection.ScopeIdentity, GitHubWire.SetId + 1,
            RunnerName, GitHubWire.RunnerId, cancellation));
        Assert.DoesNotContain(wire.Requests, request => request.Uri.AbsolutePath == expectedPath);
        Assert.True(await connection.IsExactRunnerAvailableAsync(connection.ScopeIdentity, GitHubWire.SetId,
            RunnerName, GitHubWire.RunnerId, cancellation));

        var readiness = Assert.Single(wire.Requests, request => request.Uri.AbsolutePath == expectedPath);
        Assert.Equal("api.github.com", readiness.Uri.Host);
        Assert.True(readiness.Authorization == "Bearer " + EnterprisePat);
        Assert.DoesNotContain(wire.Requests, request => request.Uri.AbsolutePath == Installation);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("forbidden")]
    [InlineData("wrong id")]
    [InlineData("wrong name")]
    [InlineData("offline")]
    [InlineData("duplicate busy")]
    [InlineData("missing busy")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task ReadinessFailsClosedWhenExactCurrentStatusIsUnavailable(string fault)
    {
        using var wire = new GitHubWire((request, _) => request.Uri.AbsolutePath == Membership
            ? GitHubWire.Reply(HttpStatusCode.OK, MembershipBody) : fault switch
            {
                "redirect" => GitHubWire.Reply(HttpStatusCode.Redirect, Available),
                "forbidden" => GitHubWire.Reply(HttpStatusCode.Forbidden, Available),
                "wrong id" => GitHubWire.Reply(HttpStatusCode.OK, Available.Replace(RunnerId, "42", StringComparison.Ordinal)),
                "wrong name" => GitHubWire.Reply(HttpStatusCode.OK, Available.Replace(RunnerName, "different", StringComparison.Ordinal)),
                "offline" => GitHubWire.Reply(HttpStatusCode.OK, Available.Replace("online", "offline", StringComparison.Ordinal)),
                "duplicate busy" => GitHubWire.Reply(HttpStatusCode.OK, Available[..^1] + ""","busy":false}"""),
                "missing busy" => GitHubWire.Reply(HttpStatusCode.OK, Available.Replace(""","busy":false""", "", StringComparison.Ordinal)),
                "oversized" => GitHubWire.Reply(HttpStatusCode.OK, Available + new string(' ', 65536)),
                _ => GitHubWire.Reply(HttpStatusCode.OK, "{"),
            });
        using var http = new HttpClient(wire);
        using var connection = CreateEnterprise(http);

        Assert.False(await connection.IsExactRunnerAvailableAsync(connection.ScopeIdentity, GitHubWire.SetId,
            RunnerName, GitHubWire.RunnerId, TestContext.Current.CancellationToken));
        Assert.Single(wire.Requests, request => request.Uri.AbsolutePath.EndsWith("/actions/runners/" + RunnerId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    public async Task ReadinessReturnsRedactedRateLimitWithoutRetrying(HttpStatusCode status, bool primary)
    {
        var clock = new ContractClock();
        var resetAt = clock.Now.AddMinutes(2);
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Uri.AbsolutePath == Membership)
            {
                return GitHubWire.Reply(HttpStatusCode.OK, MembershipBody);
            }
            var response = GitHubWire.Json(status, "synthetic response payload");
            if (primary)
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", resetAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            }
            return Task.FromResult(response);
        });
        using var http = new HttpClient(wire);
        using var connection = CreateEnterprise(http);
        var failure = await Assert.ThrowsAsync<GitHubOperationException>(() => connection.IsExactRunnerAvailableAsync(
            connection.ScopeIdentity, GitHubWire.SetId, RunnerName, GitHubWire.RunnerId, TestContext.Current.CancellationToken));

        Assert.Equal((int)status, failure.StatusCode);
        Assert.NotNull(failure.RateLimit);
        Assert.Equal(primary ? ScaleSetRateLimitKind.Primary : ScaleSetRateLimitKind.Secondary, failure.RateLimit.Kind);
        Assert.Equal(primary ? resetAt : (DateTimeOffset?)null, failure.RateLimit.Reset);
        Assert.Equal(primary ? TimeSpan.Zero : TimeSpan.FromSeconds(10), failure.RateLimit.RetryAfter);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("synthetic response payload", failure.ToString(), StringComparison.Ordinal);
        Assert.Single(wire.Requests, request => request.Uri.AbsolutePath.EndsWith("/actions/runners/" + RunnerId, StringComparison.Ordinal));
    }

    private static GitHubScaleSetConnection CreateEnterprise(HttpClient http)
    {
        var target = GitHubTarget.Parse("https://github.com/enterprises/enterprise");
        var version = new GitHubSecretVersion("fixture", "version", target, EnterprisePat);
        return new GitHubScaleSetClientFactory(http, timeProvider: new ContractClock()).Create(version);
    }

    private sealed class TestJwtProvider : IGitHubAppJwtProvider
    {
        internal int Calls { get; private set; }

        public ValueTask<string> CreateJwtAsync(string clientId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("fixture-app", clientId);
            Calls++;
            return ValueTask.FromResult(GitHubWire.AdminToken);
        }
    }
}
