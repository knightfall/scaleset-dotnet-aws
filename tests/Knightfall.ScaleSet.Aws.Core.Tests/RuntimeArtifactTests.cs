using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Web;
using Amazon.Runtime;
using Amazon.S3;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class RuntimeArtifactTests
{
    internal const string S3Uri = "s3://runner-config/profiles/blue%2Bgreen/runtime%20config.json";
    internal const string Version = "specific+version/=";

    [Theory]
    [InlineData("controller-common-v1alpha1.json")]
    [InlineData("controller-config-v1alpha1.json")]
    [InlineData("controller-runtime-v1alpha1.json")]
    public async Task ControllerEmbedsTheExactCanonicalSchema(string fileName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string resource = $"Knightfall.ScaleSet.Aws.Controller.Schemas.{fileName}";
        var assembly = typeof(ConfigVerifyCommand).Assembly;
        Assert.Contains(resource, assembly.GetManifestResourceNames());
        await using var embedded = assembly.GetManifestResourceStream(resource)!;
        await using var bytes = new MemoryStream();
        await embedded.CopyToAsync(bytes, cancellationToken);
        byte[] source = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "ConfigFixtures", "schema", fileName), cancellationToken);

        Assert.Equal(source, bytes.ToArray());
    }

    [Fact]
    public async Task VerifyPrintsTheExactDigestAndRejectsDifferentBytesBeforeParsing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("scaleset-artifact-");
        string path = Path.Combine(directory.FullName, "runtime.json");
        try
        {
            await using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            await using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            byte[] expected = ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed);
            string sha256 = ConfigurationFixtures.Sha256(expected);
            await File.WriteAllBytesAsync(path, expected, cancellationToken);

            int verified = await ConfigVerifyCommand.RunAsync(
                ["config", "verify", "--sha256", sha256.ToUpperInvariant(), "--file", path], stdout, stderr, cancellationToken);

            Assert.Equal(0, verified);
            Assert.Equal(sha256, stdout.ToString().TrimEnd());
            Assert.Empty(stderr.ToString());

            _ = stdout.GetStringBuilder().Clear();
            await File.WriteAllTextAsync(path, "{", cancellationToken);

            int mismatch = await ConfigVerifyCommand.RunAsync(
                ["config", "verify", "--file", path, "--sha256", sha256], stdout, stderr, cancellationToken);

            Assert.Equal(1, mismatch);
            Assert.Empty(stdout.ToString());
            Assert.StartsWith("digest / ", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InvalidTelemetryPreventsEvenConstructionOfTheS3Client()
    {
        byte[] bytes = ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed);
        string digest = ConfigurationFixtures.Sha256(bytes);
        var created = 0;
        var error = await Assert.ThrowsAsync<TelemetrySettingsException>(() => RuntimeInput.LoadAsync(
            RuntimeArtifact.FromS3(S3Uri, Version, digest), null, _ =>
            {
                created++;
                throw new InvalidOperationException();
            }, key => key == "OTEL_EXPORTER_OTLP_PROTOCOL" ? "unsupported-protocol" : null,
            TestContext.Current.CancellationToken));

        Assert.Equal("OTEL_EXPORTER_OTLP_PROTOCOL", error.Setting);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task FetchesTheExactVersionAndBindsValidatedConfigWithoutRereadingEnvironment()
    {
        byte[] bytes = ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed);
        string sha256 = ConfigurationFixtures.Sha256(bytes);
        using var wire = new VersionedS3Wire(bytes, Version);
        bool clientCreated = false;
        var inputs = await RuntimeInput.LoadAsync(RuntimeArtifact.FromS3(S3Uri, Version, sha256), null, region =>
        {
            Assert.Null(region);
            clientCreated = true;
            return wire.CreateClient();
        }, key =>
        {
            Assert.False(clientCreated);
            return key == "OTEL_SERVICE_NAME" ? "fixture-controller" : null;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerMode.Active, inputs.Configuration.Mode);
        Assert.Equal(sha256, inputs.Configuration.Sha256);
        Assert.Equal("fixture-controller", inputs.Telemetry.ResourceAttributes["service.name"]);
        Assert.Equal("engineering-dev", inputs.Telemetry.ResourceAttributes["scaleset.deployment.name"]);
        Assert.Equal("ap-southeast-2", inputs.Telemetry.ResourceAttributes["cloud.region"]);
        Assert.Equal("111122223333", inputs.Telemetry.ResourceAttributes["cloud.account.id"]);
        Assert.Equal(TelemetrySettings.Load(readEnvironment: _ => null).ResourceAttributes["service.instance.id"],
            inputs.Telemetry.ResourceAttributes["service.instance.id"]);
        var request = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/runner-config/profiles/blue%2Bgreen/runtime%20config.json", request.Uri.AbsolutePath);
        Assert.Equal(Version, HttpUtility.ParseQueryString(request.Uri.Query)["versionId"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different-version")]
    public async Task WrongOrMissingS3VersionPreventsRuntimeValidation(string? returnedVersion)
    {
        using var wire = new VersionedS3Wire("{"u8.ToArray(), returnedVersion);
        var artifact = RuntimeArtifact.FromS3(S3Uri, Version, new string('a', 64));

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            RuntimeArtifactLoader.LoadAsync(artifact, _ => wire.CreateClient(), TestContext.Current.CancellationToken));

        Assert.Contains("different object version", failure.Message, StringComparison.Ordinal);
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task RightVersionWithWrongBytesFailsTheDigestCheck()
    {
        var artifact = RuntimeArtifact.FromS3(S3Uri, Version, new string('a', 64));
        using var wire = new VersionedS3Wire("{"u8.ToArray(), Version);

        var failure = await Assert.ThrowsAsync<RuntimeConfigurationException>(() =>
            RuntimeArtifactLoader.LoadAsync(artifact, _ => wire.CreateClient(), TestContext.Current.CancellationToken));

        Assert.Equal("digest", failure.Code);
        Assert.Single(wire.Requests);
    }

    [Theory]
    [InlineData("s3://runner-config/key?versionId=x", "version")]
    [InlineData("s3://runner-config/key", "null")]
    [InlineData("s3://runner-config/", "version")]
    [InlineData("s3://runner-config/key%gx", "version")]
    [InlineData("https://runner-config/key", "version")]
    [InlineData("s3://private@runner-config/key", "version")]
    public void RejectsUnpinnedOrAmbiguousS3Addresses(string uri, string version) =>
        Assert.Throws<ArgumentException>(() => RuntimeArtifact.FromS3(uri, version, new string('a', 64)));
}

internal sealed class VersionedS3Wire(byte[] body, string? version) : HttpMessageHandler
{
    private readonly ConcurrentQueue<(HttpMethod Method, Uri Uri)> _requests = new();

    internal IReadOnlyCollection<(HttpMethod Method, Uri Uri)> Requests => _requests;

    internal IAmazonS3 CreateClient() => new AmazonS3Client(
        new BasicAWSCredentials("fixture-key", "fixture-secret"), new AmazonS3Config
        {
            ServiceURL = "https://aws.fixture.invalid",
            AuthenticationRegion = "ap-southeast-2",
            ForcePathStyle = true,
            AllowAutoRedirect = false,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
            DisableClockSkewCorrection = true,
            HttpClientFactory = new WireFactory(this),
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue((request.Method, request.RequestUri!));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        if (version is not null)
        {
            response.Headers.Add("x-amz-version-id", version);
        }
        return Task.FromResult(response);
    }

    private sealed class WireFactory(VersionedS3Wire wire) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(wire, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
