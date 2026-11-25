using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubScaleSetConnection
{
    private static readonly TimeSpan RunnerAvailabilityBudget = TimeSpan.FromSeconds(5);

    internal async Task<bool> IsExactRunnerAvailableAsync(
        string scopeIdentity, long scaleSetId, string runnerName, long runnerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scopeIdentity != ScopeIdentity || scaleSetId <= 0 || runnerId <= 0 || string.IsNullOrWhiteSpace(runnerName))
        {
            return false;
        }

        using var deadline = new CancellationTokenSource(RunnerAvailabilityBudget, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // The scale set service attests membership. The REST API attests current runner availability.
            bool available = await ReadExactRunnerAsync(scopeIdentity, scaleSetId, runnerName, runnerId, bounded.Token).ConfigureAwait(false) is not null &&
                await _availability.ReadAsync(runnerId, runnerName, bounded.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return available && !deadline.IsCancellationRequested;
        }
        catch (GitHubOperationException error) when (error.StatusCode == 401 || error.RateLimit is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception error) when (error is GitHubOperationException or HttpRequestException or IOException or
            JsonException or CryptographicException or InvalidOperationException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}

internal sealed class GitHubRunnerAvailability(
    GitHubTarget target, GitHubAppAuthorization? app, string? enterprisePat, HttpClient? injectedHttp, TimeProvider clock, string userAgent)
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan MaximumTokenReuse = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TokenExpiryReserve = TimeSpan.FromMinutes(1);
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false, MaxDepth = 16 };
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private InstallationToken? _cachedToken;

    internal async Task<bool> ReadAsync(long runnerId, string runnerName, CancellationToken cancellationToken)
    {
        string? token = enterprisePat ?? await ReadInstallationTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return false;
        }

        string prefix = target.Scope switch
        {
            GitHubTargetScope.Repository => $"/repos/{Uri.EscapeDataString(target.Owner)}/{Uri.EscapeDataString(target.Repository!)}",
            GitHubTargetScope.Organization => $"/orgs/{Uri.EscapeDataString(target.Owner)}",
            GitHubTargetScope.Enterprise => $"/enterprises/{Uri.EscapeDataString(target.Enterprise!)}",
            _ => throw new InvalidOperationException("The GitHub runner scope is unsupported."),
        };
        using var request = CreateRequest(HttpMethod.Get, prefix + "/actions/runners/" + runnerId.ToString(CultureInfo.InvariantCulture), token);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && Volatile.Read(ref _cachedToken) is { } cached && cached.Value == token)
        {
            _ = Interlocked.CompareExchange(ref _cachedToken, null, cached);
        }
        using var document = await ReadDocumentAsync(request, response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        return document?.RootElement is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long reportedId) &&
            reportedId == runnerId && ReadString(root, "name") == runnerName && ReadString(root, "status") == "online" &&
            root.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.False;
    }

    private async Task<string?> ReadInstallationTokenAsync(CancellationToken cancellationToken)
    {
        if (app is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (Volatile.Read(ref _cachedToken) is { } cached && cached.IssuedAt <= now && now < cached.ValidUntil)
        {
            return cached.Value;
        }

        string jwt = await app.JwtProvider.CreateJwtAsync(app.ClientId, now, cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Post,
            "/app/installations/" + app.InstallationId.ToString(CultureInfo.InvariantCulture) + "/access_tokens", jwt);
        string payload = target.Scope == GitHubTargetScope.Repository
            ? JsonSerializer.Serialize(new { repositories = new[] { target.Repository }, permissions = new { administration = "read" } })
            : """{"permissions":{"organization_self_hosted_runners":"read"}}""";
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(request, response, HttpStatusCode.Created, cancellationToken).ConfigureAwait(false);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            ReadString(root, "token") is not { Length: > 0 and <= 4096 } token ||
            !token.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~' or '+' or '/' or '=') ||
            !root.TryGetProperty("expires_at", out var expiry) || expiry.ValueKind != JsonValueKind.String ||
            !expiry.TryGetDateTimeOffset(out var expiresAt))
        {
            return null;
        }

        var tokenReadAt = clock.GetUtcNow();
        if (tokenReadAt < now || expiresAt - tokenReadAt <= TokenExpiryReserve)
        {
            return null;
        }
        var reuse = expiresAt - tokenReadAt - TokenExpiryReserve;
        var validUntil = tokenReadAt + (reuse < MaximumTokenReuse ? reuse : MaximumTokenReuse);
        Volatile.Write(ref _cachedToken, new InstallationToken(token, tokenReadAt, validUntil));
        return token;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(target.ApiBaseUri, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        _ = request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return request;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        (injectedHttp ?? DefaultHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private async Task<JsonDocument?> ReadDocumentAsync(HttpRequestMessage request, HttpResponseMessage response,
        HttpStatusCode expected, CancellationToken cancellationToken)
    {
        if (response.RequestMessage?.RequestUri is { } receivedUri && receivedUri != request.RequestUri)
        {
            return null;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubOperationException(GitHubOperationError.RequestFailed, 401);
        }
        if (ReadRateLimit(response) is { } rateLimit)
        {
            throw new GitHubOperationException(GitHubOperationError.RequestFailed, (int)response.StatusCode, rateLimit: rateLimit);
        }
        if (response.StatusCode != expected || response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[MaximumResponseBytes + 1];
        int count = await body.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return count is > 0 and <= MaximumResponseBytes ? JsonDocument.Parse(bytes.AsMemory(0, count), StrictJson) : null;
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private GitHubRateLimit? ReadRateLimit(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden))
        {
            return null;
        }
        bool primary = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0");
        if (!primary && response.StatusCode == HttpStatusCode.Forbidden && !response.Headers.Contains("Retry-After"))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } after && after > now ? after - now : TimeSpan.Zero);
        DateTimeOffset? reset = null;
        if (primary && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            long.TryParse(resetValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) &&
            seconds >= 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return new GitHubRateLimit(primary ? ScaleSetRateLimitKind.Primary : ScaleSetRateLimitKind.Secondary, delay, reset, 0);
    }

    private sealed class InstallationToken(string value, DateTimeOffset issuedAt, DateTimeOffset validUntil)
    {
        internal string Value { get; } = value;
        internal DateTimeOffset IssuedAt { get; } = issuedAt;
        internal DateTimeOffset ValidUntil { get; } = validUntil;
    }
}
