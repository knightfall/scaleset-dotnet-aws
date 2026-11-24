using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class HelloEndpointRegistrationTests
{
    private const string Region = "ap-southeast-2";
    private const string Endpoint = "https://exampleid.lambda-url." + Region + ".on.aws/";

    [Theory]
    [InlineData("http://exampleid.lambda-url.ap-southeast-2.on.aws/")]
    [InlineData("https://exampleid.lambda-url.us-east-1.on.aws/")]
    [InlineData("https://untrusted.exampleid.lambda-url.ap-southeast-2.on.aws/")]
    [InlineData("https://exampleid.lambda-url.ap-southeast-2.on.aws:434/")]
    [InlineData("https://exampleid.lambda-url.ap-southeast-2.on.aws/?ignored=value")]
    public void OnlyAcceptsAnExactRegionalFunctionUrl(string endpoint)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() =>
            new HelloEndpointRegistration(http, endpoint, Region, new SessionAWSCredentials("fixture", "fixture", "fixture"), TimeProvider.System));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, 2, LifecycleWriteOutcome.Committed)]
    [InlineData(HttpStatusCode.Conflict, 2, LifecycleWriteOutcome.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3, LifecycleWriteOutcome.Indeterminate)]
    public async Task SignsTheSameCanonicalHelloForBoundedRetries(HttpStatusCode response, int attempts, LifecycleWriteOutcome expected)
    {
        var token = TestContext.Current.CancellationToken;
        using var recipient = JitRecipientKey.Create();
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(recipient.Hello, BootstrapJson.Options);
        using var wire = new SigningWire(canonical, response);
        using var http = new HttpClient(wire);
        var clock = new ContractClock { Now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };
        var endpoint = new HelloEndpointRegistration(http, Endpoint, Region,
            new SessionAWSCredentials("fixture-key", "fixture-secret", "fixture-session"), clock);

        Assert.Equal(expected, await endpoint.RegisterAsync(recipient.Hello, token));
        Assert.Equal(attempts, wire.Attempts);
    }

    private sealed class SigningWire(byte[] canonical, HttpStatusCode response) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Endpoint, request.RequestUri?.AbsoluteUri);
            var content = Assert.IsType<ByteArrayContent>(request.Content);
            Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
            Assert.Equal(canonical, await content.ReadAsByteArrayAsync(cancellationToken));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(canonical)),
                Assert.Single(request.Headers.GetValues("X-Amz-Content-SHA256")));
            Assert.Equal("fixture-session", Assert.Single(request.Headers.GetValues("X-Amz-Security-Token")));
            Assert.Equal("20260102T030405Z", Assert.Single(request.Headers.GetValues("X-Amz-Date")));
            string authorization = Assert.Single(request.Headers.GetValues("Authorization"));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=fixture-key/20260102/ap-southeast-2/lambda/aws4_request, ", authorization);
            Assert.Contains("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token", authorization, StringComparison.Ordinal);
            return new HttpResponseMessage(Attempts == 1 ? HttpStatusCode.TooManyRequests : response);
        }
    }
}
