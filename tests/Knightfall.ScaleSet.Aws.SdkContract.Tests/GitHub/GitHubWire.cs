using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

internal sealed class GitHubWire(
    Func<GitHubRequest, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    internal const string Pat = "synthetic-pat";
    internal const string InstallationToken = "synthetic-installation-token";
    internal const string RegistrationToken = "synthetic-registration-token";
    internal const string AdminToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjQwNzA5MDg4MDB9.test-signature";
    internal const string QueueToken = "synthetic-queue-token";
    internal const string ServiceHost = "service-fixture.actions.githubusercontent.com";
    internal const string ServicePath = "/tenant";
    internal const string QueueHost = "queue-fixture.actions.githubusercontent.com";
    internal const string QueuePath = "/messages/synthetic-queue";
    private const string QueueQuery = "?signature=synthetic-queue-signature";
    internal const string QueueUrl = "https://" + QueueHost + QueuePath + QueueQuery;
    internal const long SetId = 7_000_000_001;
    internal const long GroupId = 8_000_000_002;
    internal const long RunnerId = 9_000_000_003;
    internal static Guid SessionId { get; } = Guid.Parse("51688c9f-f017-4a9a-b59f-77ed34074c1b");

    private readonly ConcurrentQueue<GitHubRequest> _requests = new();

    internal GitHubRequest[] Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var observation = new GitHubRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
            body, request.Headers.TryGetValues("X-ScaleSetMaxCapacity", out var values) ? values.Single() : null);
        _requests.Enqueue(observation);

        if (observation.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Created, $$"""
                {"token":"{{InstallationToken}}","expires_at":"2099-01-01T00:00:00Z"}
                """);
        }
        if (observation.Uri.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Created, $$"""{"token":"{{RegistrationToken}}"}""");
        }
        if (observation.Uri.AbsolutePath == "/actions/runner-registration")
        {
            string host = ActionsHosts(observation.Uri.Host).Service;
            return Json(HttpStatusCode.OK, $$"""
                {"url":"https://{{host}}{{ServicePath}}","token":"{{AdminToken}}"}
                """);
        }

        return await respond(observation, cancellationToken).ConfigureAwait(false);
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
        Task.FromResult(Json(status, body));

    internal static string SessionJson(Guid? sessionId = null, GitHubTarget? target = null)
    {
        string queueUrl = target is null
            ? QueueUrl
            : $"https://{ActionsHosts(target.ApiBaseUri.Host).Queue}{QueuePath}{QueueQuery}";
        return $$"""
            {"sessionId":"{{sessionId ?? SessionId}}","ownerName":"owner",
            "messageQueueUrl":"{{queueUrl}}","messageQueueAccessToken":"{{QueueToken}}",
            "statistics":{"totalAssignedJobs":3} }
            """;
    }

    private static (string Service, string Queue) ActionsHosts(string apiHost)
    {
        if (!apiHost.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase))
        {
            return (ServiceHost, QueueHost);
        }

        string tenant = apiHost["api.".Length..];
        return ($"service-fixture.actions.{tenant}", $"queue-fixture.actions.{tenant}");
    }

    internal static ScaleSetClient Connect(HttpClient http, TimeProvider clock, ScaleSetClientOptions? options = null) =>
        new(GitHubTarget.Parse("https://github.com/owner/repository"), ScaleSetCredentials.FromPersonalAccessToken(Pat),
            options, http, timeProvider: clock);
}

internal sealed record GitHubRequest(
    HttpMethod Method, Uri Uri, string? Authorization, string? Body, string? Capacity);

internal sealed class ContractClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
