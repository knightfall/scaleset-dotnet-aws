using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

/// <summary>Submits immutable HELLO bytes to the role's authenticated regional ingress.</summary>
internal sealed class HelloEndpointRegistration
{
    private const int MaximumAttempts = 7;
    private static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(25);
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _region;
    private readonly AWSCredentials _credentials;
    private readonly TimeProvider _clock;

    internal HelloEndpointRegistration(HttpClient http, string? endpoint, string region, AWSCredentials credentials, TimeProvider clock)
    {
        _http = http;
        _endpoint = ValidateEndpoint(endpoint, region);
        _region = region;
        _credentials = credentials;
        _clock = clock;
    }

    internal static HttpClient CreateTransport() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxResponseHeadersLength = 16,
    }) { Timeout = TimeSpan.FromSeconds(8) };

    internal async Task<LifecycleWriteOutcome> RegisterAsync(JitHello hello, CancellationToken cancellationToken)
    {
        JitOfferCryptography.ValidateHello(hello);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(hello, BootstrapJson.Options);
        using var deadline = new CancellationTokenSource(RetryBudget, _clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = lifetime.Token;
        try
        {
            for (int attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var credentials = await _credentials.GetCredentialsAsync().WaitAsync(token).ConfigureAwait(false);
                using var request = Sign(payload, credentials);
                try
                {
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (response.StatusCode == HttpStatusCode.NoContent)
                    {
                        return LifecycleWriteOutcome.Committed;
                    }
                    if (!IsTransient(response.StatusCode))
                    {
                        return LifecycleWriteOutcome.Conflict;
                    }
                }
                catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.Unknown or
                    HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.HttpProtocolError or
                    HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded)
                {
                    // A lost response can follow a committed write. Only resend the original bytes.
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // The HTTP transport's per-request timeout can expire before the registration budget.
                }
                if (attempt + 1 < MaximumAttempts)
                {
                    int ceiling = Math.Min(250 << attempt, 4_000);
                    var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(ceiling / 2, ceiling + 1));
                    await Task.Delay(delay, _clock, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Exhausted elapsed-time budget. The server may have committed a request whose reply was lost.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return LifecycleWriteOutcome.Indeterminate;
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private HttpRequestMessage Sign(byte[] payload, ImmutableCredentials credentials)
    {
        if (!credentials.UseToken)
        {
            throw new InvalidOperationException("HELLO registration requires temporary role credentials.");
        }
        var awsRequest = new DefaultRequest(new HelloRequest(), "lambda")
        {
            Endpoint = _endpoint,
            ResourcePath = "/",
            HttpMethod = "POST",
            Content = payload,
            AuthenticationRegion = _region,
            OverrideSigningServiceName = "lambda",
        };
        awsRequest.Headers.Add("Content-Type", "application/json");
        awsRequest.Headers.Add("X-Amz-Security-Token", credentials.Token);
        awsRequest.Headers.Add("X-Amz-Content-SHA256", Convert.ToHexStringLower(SHA256.HashData(payload)));
        var signer = new AWS4Signer();
        var signature = signer.SignRequest(awsRequest, new AmazonDynamoDBConfig
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_region),
        }, new RequestMetrics(), credentials.AccessKey, credentials.SecretKey, _clock.GetUtcNow().UtcDateTime);

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(payload) };
        foreach (var (name, value) in awsRequest.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        request.Headers.TryAddWithoutValidation("Authorization", signature.ForAuthorizationHeader);
        return request;
    }

    private static Uri ValidateEndpoint(string? endpoint, string region)
    {
        string suffix = ".lambda-url." + region + ".on.aws";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.HostNameType != UriHostNameType.Dns || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.IdnHost.EndsWith(suffix, StringComparison.Ordinal) || uri.IdnHost.Length == suffix.Length)
        {
            throw new ArgumentException("A regional AWS IAM HELLO endpoint URL is required.", nameof(endpoint));
        }
        foreach (char character in uri.IdnHost.AsSpan(0, uri.IdnHost.Length - suffix.Length))
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))
            {
                throw new ArgumentException("A regional AWS IAM HELLO endpoint URL is required.", nameof(endpoint));
            }
        }
        return uri;
    }

    private sealed class HelloRequest : AmazonWebServiceRequest;
}
