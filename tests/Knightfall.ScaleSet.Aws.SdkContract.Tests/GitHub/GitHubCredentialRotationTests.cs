using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SecretsManager;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Microsoft.Extensions.Logging;
using ScaleSet.Client;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

public sealed class GitHubCredentialRotationTests
{
    private const string FirstPat = "ghp_111111111111111111111111111111111111";
    private const string SecondPat = "ghp_222222222222222222222222222222222222";
    private const string KeyMarker = "synthetic-private-key-material-never-log";
    private const string Groups = GitHubWire.ServicePath + "/_apis/runtime/runnergroups/";
    private const string Sets = GitHubWire.ServicePath + "/_apis/runtime/runnerscalesets";
    private static string Set => FormattableString.Invariant($"{Sets}/{GitHubWire.SetId}");

    [Fact]
    public async Task CandidateMustValidateExactPreviouslyBoundSetAndDoesNotChangeCurrentConnection()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var fixture = new RotationFixture();
        using var current = await fixture.ConnectAsync(cancellation);
        var binding = Assert.IsType<GitHubScaleSetBinding>(
            (await current.LookupAsync(fixture.Routing, null, cancellation)).Binding);
        Assert.Equal("https://github.com/enterprises/testing-team", binding.ScopeUrl);
        Assert.Equal("github.com/enterprises/testing-team", current.ScopeIdentity);
        Assert.Equal(GitHubWire.GroupId, binding.RunnerGroupId);
        Assert.Equal(GitHubWire.SetId, binding.ScaleSetId);

        var before = fixture.Wire.Requests.Length;
        var unchanged = await fixture.Rotator.CheckAsync(
            fixture.Configuration, fixture.Routing, binding, current, cancellation);
        Assert.Equal(GitHubCredentialRotationStatus.Unchanged, unchanged.Status);
        Assert.Null(unchanged.Candidate);
        Assert.Equal(before, fixture.Wire.Requests.Length);

        fixture.Rotate();
        var rotation = await fixture.Rotator.CheckAsync(
            fixture.Configuration, fixture.Routing, binding, current, cancellation);
        Assert.Equal(GitHubCredentialRotationStatus.CandidateValidated, rotation.Status);
        using var candidate = Assert.IsType<GitHubScaleSetConnection>(rotation.Candidate);
        Assert.Equal("second-version", candidate.VersionId);
        Assert.Equal("first-version", current.VersionId);
        Assert.Null(rotation.Failure);

        var candidateRequests = fixture.Wire.Requests.Skip(before).ToArray();
        Assert.Equal(new[] { Groups, Set }, candidateRequests.Where(IsActions).Select(request => request.Uri.AbsolutePath));
        var auth = Assert.Single(candidateRequests, request =>
            request.Uri.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal));
        Assert.Equal("Bearer " + SecondPat, auth.Authorization);

        Assert.Equal(binding, (await current.LookupAsync(fixture.Routing, binding, cancellation)).Binding);
        Assert.DoesNotContain(fixture.Wire.Requests, request => request.Uri.AbsolutePath.Contains("/sessions", StringComparison.Ordinal));
        Assert.Empty(fixture.Logger.Entries);
    }

    [Fact]
    public async Task RejectedCandidateLeavesCurrentUsableAndRedactsExceptionAndStructuredLog()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var fixture = new RotationFixture();
        using var current = await fixture.ConnectAsync(cancellation);
        var binding = Assert.IsType<GitHubScaleSetBinding>(
            (await current.LookupAsync(fixture.Routing, null, cancellation)).Binding);

        fixture.Rotate();
        fixture.FailSetWithRateLimit = true;
        var rejected = await fixture.Rotator.CheckAsync(
            fixture.Configuration, fixture.Routing, binding, current, cancellation);
        Assert.Equal(GitHubCredentialRotationStatus.CandidateRejected, rejected.Status);
        Assert.Null(rejected.Candidate);
        var error = Assert.IsType<GitHubOperationException>(rejected.Failure);
        Assert.Equal(429, error.StatusCode);
        Assert.Equal(new GitHubRateLimit(ScaleSetRateLimitKind.Secondary, TimeSpan.FromSeconds(24),
            fixture.Clock.Now.AddMinutes(2), 0), error.RateLimit);
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);

        var logged = Assert.Single(fixture.Logger.Entries);
        Assert.Null(logged.Exception);
        Assert.Contains(logged.Fields, field => field.Key == "StatusCode" && Equals(field.Value, 429));
        Assert.Contains(logged.Fields, field => field.Key == "RateLimitKind" && Equals(field.Value, ScaleSetRateLimitKind.Secondary));
        string diagnostics = error + logged.Message + string.Join(' ', logged.Fields.Select(field => $"{field.Key}={field.Value}"));
        foreach (string marker in new[] { FirstPat, SecondPat, KeyMarker, GitHubWire.QueueUrl, GitHubWire.QueueToken })
        {
            Assert.DoesNotContain(marker, diagnostics, StringComparison.Ordinal);
        }

        fixture.FailSetWithRateLimit = false;
        Assert.Equal(binding, (await current.LookupAsync(fixture.Routing, binding, cancellation)).Binding);
        Assert.DoesNotContain(fixture.Wire.Requests, request => request.Uri.AbsolutePath.Contains("/sessions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("group-name")]
    [InlineData("group-id")]
    [InlineData("set-name")]
    [InlineData("set-id")]
    [InlineData("extra-label")]
    [InlineData("duplicate-label")]
    [InlineData("full-fold-only-label")]
    [InlineData("control-label")]
    public async Task PinnedLookupDoesNotAcceptMismatchedRemoteIdentity(string mismatch)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var fixture = new RotationFixture { Mismatch = mismatch };
        using var current = await fixture.ConnectAsync(cancellation);
        var pinned = fixture.Routing with { ScaleSetId = GitHubWire.SetId };
        var error = await Assert.ThrowsAsync<GitHubOperationException>(() => current.LookupAsync(pinned, null, cancellation));
        Assert.Equal(GitHubOperationError.BindingMismatch, error.Error);
        Assert.DoesNotContain(fixture.Wire.Requests, request => request.Uri.AbsolutePath == Sets);
        Assert.DoesNotContain(fixture.Wire.Requests, request => request.Uri.AbsolutePath.Contains("/sessions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DryRunRotationCallsNeitherSecretsManagerNorGitHub()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var fixture = new RotationFixture();
        using var current = await fixture.ConnectAsync(cancellation);
        var binding = Assert.IsType<GitHubScaleSetBinding>(
            (await current.LookupAsync(fixture.Routing, null, cancellation)).Binding);
        int github = fixture.Wire.Requests.Length;
        int secrets = fixture.SecretRequestCount;

        var disabled = await fixture.Rotator.CheckAsync(
            RotationFixture.LoadConfiguration(dryRun: true), fixture.Routing, binding, current, cancellation);

        Assert.Equal(GitHubCredentialRotationStatus.Disabled, disabled.Status);
        Assert.Null(disabled.Candidate);
        Assert.Equal(secrets, fixture.SecretRequestCount);
        Assert.Equal(github, fixture.Wire.Requests.Length);
    }

    private static bool IsActions(GitHubRequest request) => request.Uri.Host == GitHubWire.ServiceHost;

    private sealed class RotationFixture : IDisposable
    {
        private static readonly string[] CurrentVersionStage = ["AWSCURRENT"];
        private readonly AwsWireTransport _secretsWire;
        private readonly AmazonSecretsManagerClient _secretsClient;
        private readonly GitHubSecretLoader _loader;
        private readonly HttpClient _http;
        private readonly GitHubScaleSetClientFactory _factory;
        private string _pat = FirstPat;
        private string _version = "first-version";

        internal RotationFixture()
        {
            Configuration = LoadConfiguration(dryRun: false);
            Routing = Configuration.Profiles[0].GitHub with { Labels = ["Straße", "linux"] };
            _secretsWire = new AwsWireTransport(_ =>
            {
                var settings = Configuration.GitHub[Routing.Connection];
                return AwsWireTransport.Json(JsonSerializer.Serialize(new
                {
                    ARN = settings.SecretArn,
                    VersionId = _version,
                    VersionStages = CurrentVersionStage,
                    SecretString = JsonSerializer.Serialize(new { token = _pat }),
                }), mediaType: "application/x-amz-json-1.1");
            });
            _secretsClient = new AmazonSecretsManagerClient(AwsWireTransport.Credentials,
                _secretsWire.Configure(new AmazonSecretsManagerConfig()));
            _loader = new GitHubSecretLoader(_secretsClient);
            Wire = new GitHubWire(RespondAsync);
            _http = new HttpClient(Wire, disposeHandler: false);
            _factory = new GitHubScaleSetClientFactory(_http,
                new ScaleSetClientOptions { Retry = ScaleSetRetryOptions.Disabled }, Clock);
            Rotator = new GitHubCredentialRotator(_loader, _factory, Logger);
        }

        internal RuntimeConfiguration Configuration { get; }
        internal RunnerGitHubConfiguration Routing { get; }
        internal ContractClock Clock { get; } = new();
        internal GitHubWire Wire { get; }
        internal RecordingLogger Logger { get; } = new();
        internal GitHubCredentialRotator Rotator { get; }
        internal int SecretRequestCount => _secretsWire.Requests.Count;
        internal bool FailSetWithRateLimit { get; set; }
        internal string? Mismatch { get; init; }

        internal void Rotate()
        {
            _pat = SecondPat;
            _version = "second-version";
        }

        internal async Task<GitHubScaleSetConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            var version = await _loader.LoadAsync(Configuration, Routing.Connection, cancellationToken);
            return _factory.Create(Assert.IsType<GitHubSecretVersion>(version));
        }

        internal static RuntimeConfiguration LoadConfiguration(bool dryRun)
        {
            var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fargate-only.json")))!;
            var spec = root["spec"]!;
            var connectionName = spec["profiles"]![0]!["github"]!["connection"]!.GetValue<string>();
            var connection = spec["github"]![connectionName]!;
            connection["scopeURL"] = "https://www.github.com/enterprises/Testing-Team";
            connection["auth"]!["type"] = "enterprise-pat";
            spec["mode"] = dryRun ? "dry-run" : "active";
            return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(root));
        }

        private Task<HttpResponseMessage> RespondAsync(GitHubRequest request, CancellationToken _)
        {
            if (request.Uri.AbsolutePath == Groups)
            {
                return GitHubWire.Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    count = 1,
                    value = new[]
                    {
                        new { id = GitHubWire.GroupId, name = Mismatch == "group-name" ? "different-group" : Routing.RunnerGroup.ToLowerInvariant() },
                    },
                }));
            }
            if (request.Uri.AbsolutePath == Set && FailSetWithRateLimit)
            {
                var failure = GitHubWire.Json(HttpStatusCode.TooManyRequests, JsonSerializer.Serialize(new
                {
                    message = $"-----BEGIN PRIVATE KEY-----\n{KeyMarker}\n-----END PRIVATE KEY----- {SecondPat} {GitHubWire.QueueUrl}",
                    messageQueueAccessToken = GitHubWire.QueueToken,
                }));
                failure.Headers.TryAddWithoutValidation("Retry-After", "24");
                failure.Headers.TryAddWithoutValidation("X-RateLimit-Reset", Clock.Now.AddMinutes(2).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                failure.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", SecondPat);
                return Task.FromResult(failure);
            }
            if (request.Uri.AbsolutePath == Sets || request.Uri.AbsolutePath == Set)
            {
                string[] labels = Mismatch switch
                {
                    "extra-label" => ["LINUX", "STRAẞE", "surprise"],
                    "duplicate-label" => ["LINUX", "STRAẞE", "straße"],
                    "full-fold-only-label" => ["LINUX", "STRASSE"],
                    "control-label" => ["LINUX", "STRAẞE\u0001"],
                    _ => ["LINUX", "STRAẞE"],
                };
                var set = new
                {
                    id = Mismatch == "set-id" ? GitHubWire.SetId + 1 : GitHubWire.SetId,
                    name = Mismatch == "set-name" ? "other-set" : Routing.ScaleSet.ToUpperInvariant(),
                    runnerGroupId = Mismatch == "group-id" ? GitHubWire.GroupId + 1 : GitHubWire.GroupId,
                    runnerGroupName = Routing.RunnerGroup,
                    labels = labels.Select(name => new { name, type = "System" }).ToArray(),
                };
                string body = request.Uri.AbsolutePath == Sets
                    ? JsonSerializer.Serialize(new { count = 1, value = new[] { set } })
                    : JsonSerializer.Serialize(set);
                return GitHubWire.Reply(HttpStatusCode.OK, body);
            }

            return GitHubWire.Reply(HttpStatusCode.NotFound, """{"message":"unexpected fixture route"}""");
        }

        public void Dispose()
        {
            _http.Dispose();
            Wire.Dispose();
            _secretsClient.Dispose();
            _secretsWire.Dispose();
        }
    }

    private sealed record LogEntry(string Message, Exception? Exception, KeyValuePair<string, object?>[] Fields);

    private sealed class RecordingLogger : ILogger<GitHubCredentialRotator>
    {
        internal List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> structured ? structured.ToArray() : [];
            Entries.Add(new LogEntry(formatter(state, exception), exception, fields));
        }
    }
}
