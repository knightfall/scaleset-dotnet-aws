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
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credentials = await _credentials.GetCredentialsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            using var request = Sign(payload, credentials);
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return LifecycleWriteOutcome.Committed;
                }
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    return LifecycleWriteOutcome.Conflict;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or OperationCanceledException)
            {
                // Only the same immutable body can be retried after a lost response.
            }
            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), _clock, cancellationToken).ConfigureAwait(false);
            }
        }
        return LifecycleWriteOutcome.Indeterminate;
    }

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
