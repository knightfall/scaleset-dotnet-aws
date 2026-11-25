using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

internal sealed class GitHubDemandReadTransport : HttpMessageHandler
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient? _injected;
    private readonly HttpMessageInvoker? _owned;
    private readonly AsyncLocal<DemandRead?> _read = new();

    internal GitHubDemandReadTransport(HttpClient? injected)
    {
        _injected = injected;
        if (injected is null)
        {
            _owned = new HttpMessageInvoker(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            });
        }

        Client = new HttpClient(this, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = injected?.DefaultRequestVersion ?? HttpVersion.Version20,
            DefaultVersionPolicy = injected?.DefaultVersionPolicy ?? HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    internal HttpClient Client { get; }

    internal async Task<(RunnerScaleSet? Set, DemandStatistics? Statistics)> ReadAsync(
        ScaleSetClient client, long setId, CancellationToken cancellationToken)
    {
        // Scope raw validation to this SDK lookup. Other calls can share the connection.
        var prior = _read.Value;
        var read = new DemandRead(FormattableString.Invariant($"/_apis/runtime/runnerscalesets/{setId}"));
        _read.Value = read;
        try
        {
            var set = await client.GetScaleSetAsync(setId, cancellationToken).ConfigureAwait(false);
            return (set, read.Statistics);
        }
        finally
        {
            _read.Value = prior;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var read = _read.Value;
        bool statistics = read is not null && request.Method == HttpMethod.Get &&
            request.RequestUri?.AbsolutePath.EndsWith(read.Path, StringComparison.Ordinal) == true;
        if (statistics)
        {
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        }

        var response = _injected is null
            ? await _owned!.SendAsync(request, cancellationToken).ConfigureAwait(false)
            : await SendInjectedAsync(_injected, request, cancellationToken).ConfigureAwait(false);
        if (!statistics || response.StatusCode != HttpStatusCode.OK)
        {
            return response;
        }

        try
        {
            if ((response.RequestMessage?.RequestUri is { } responseUri && responseUri != request.RequestUri) ||
                response.Headers.Age > TimeSpan.Zero)
            {
                throw new InvalidDataException("The scale-set statistics response is not a current direct response.");
            }

            // The released SDK model uses an int and cannot distinguish a missing count from a reported zero.
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false),
                new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
            if (response.Headers.Date is { } date && document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("statistics", out var shape) && shape.ValueKind == JsonValueKind.Object &&
                shape.TryGetProperty("totalAssignedJobs", out var count) && count.ValueKind == JsonValueKind.Number &&
                count.TryGetInt32(out int assigned) && assigned >= 0 &&
                (!shape.TryGetProperty("totalRunningJobs", out var running) ||
                    (running.ValueKind == JsonValueKind.Number && running.TryGetInt32(out int jobCount) &&
                        jobCount >= 0 && jobCount <= assigned)))
            {
                read!.Statistics = new DemandStatistics(assigned, date);
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendInjectedAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Each HttpClient marks its request as sent. Borrow the body while forwarding through the caller's transport.
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = request.Content,
        };
        foreach (var header in request.Headers)
        {
            _ = forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in request.Options)
        {
            forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }
        try
        {
            return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            forwarded.Content = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Client.Dispose();
            _owned?.Dispose();
        }
        base.Dispose(disposing);
    }

    internal sealed record DemandStatistics(int TotalAssignedJobs, DateTimeOffset ResponseDate);

    private sealed class DemandRead(string path)
    {
        internal string Path { get; } = path;
        internal DemandStatistics? Statistics { get; set; }
    }
}
