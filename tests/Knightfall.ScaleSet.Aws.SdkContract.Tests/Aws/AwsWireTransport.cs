using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Amazon.Runtime;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

internal sealed record AwsWireRequest(HttpMethod Method, Uri Uri, string Body, string? Target, string? ContentType);

internal sealed class AwsWireTransport(Func<AwsWireRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private const string Endpoint = "https://aws.fixture.invalid";
    private readonly ConcurrentQueue<AwsWireRequest> _requests = new();

    public static BasicAWSCredentials Credentials => new("AKIDEXAMPLE", "offline-fixture-secret");

    public IReadOnlyList<AwsWireRequest> Requests => _requests.ToArray();
    internal Func<AwsWireRequest, CancellationToken, Task>? BeforeRespondAsync { get; set; }

    public T Configure<T>(T config) where T : ClientConfig
    {
        config.ServiceURL = Endpoint;
        config.AuthenticationRegion = "us-east-1";
        config.RetryMode = RequestRetryMode.Standard;
        config.MaxErrorRetry = 0;
        config.MaxStaleConnectionRetries = 0;
        config.DisableClockSkewCorrection = true;
        config.HttpClientFactory = new WireHttpClientFactory(this);
        return config;
    }

    public static HttpResponseMessage Json(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/x-amz-json-1.0") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("The AWS request has no URI.");
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var target = request.Headers.TryGetValues("X-Amz-Target", out var values) ? values.Single() : null;

        var captured = new AwsWireRequest(request.Method, uri, body, target, request.Content?.Headers.ContentType?.MediaType);
        _requests.Enqueue(captured);
        if (BeforeRespondAsync is { } beforeRespond)
        {
            await beforeRespond(captured, cancellationToken);
        }
        return respond(captured);
    }

    private sealed class WireHttpClientFactory(AwsWireTransport transport) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(transport, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
