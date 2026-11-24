using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using OneJobBootstrap = Knightfall.ScaleSet.Aws.RunnerBootstrap.RunnerBootstrap;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class Ec2BootstrapTests
{
    private const string Account = "111122223333";
    private const string Region = "ap-southeast-2";
    private const string Instance = "i-0123456789abcdef1";
    private const string Role = "fixture-bootstrap";
    private const string RoleId = "AROAFIXTUREIDENTITY0001";
    private const string Principal = RoleId + ":" + Instance;
    private const string Arn = "arn:aws:ec2:" + Region + ":" + Account + ":instance/" + Instance;
    private const string AccessId = "ASIAFIXTUREBOOTSTRAP1";
    private const string Session = "imds-fixture-session";
    private const string PrivateSentinel = "one-job-fixture-only";
    private static readonly Ec2BootstrapAuthority Authority = new(Account, Region, $"arn:aws:iam::{Account}:role/path/{Role}", RoleId);
    private static readonly RunnerBootstrapTarget Target = new(Account + ":" + Region + ":fixture", "ec2", "github.com/fixture/repository", 99);

    [Theory]
    [InlineData("valid")]
    [InlineData("account")]
    [InlineData("region")]
    [InlineData("instance")]
    [InlineData("assumed-role")]
    [InlineData("role-id")]
    [InlineData("redirect")]
    public async Task ScopedMetadataAndStsMustIdentifyTheSameAttachedRoleBeforeAnyBootstrapRequest(string mismatch)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var imds = new MetadataWire(clock, mismatch);
        using var http = new HttpClient(imds);
        var scope = new Uri("http://127.0.0.1:43123/");
        var stsRequest = new List<string>();
        using var sts = new SignatureWire((_, body) =>
        {
            stsRequest.Add(body);
            string stsArn = $"arn:aws:sts::{Account}:assumed-role/{(mismatch == "assumed-role" ? "another-role" : Role)}/{Instance}";
            string stsId = mismatch == "instance" ? RoleId + ":i-0123456789abcdef2" : Principal;
            if (mismatch == "role-id")
            {
                stsId = "AROAFIXTUREWRONG000003:" + Instance;
            }
            return AwsWireTransport.Xml($"""
                <GetCallerIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                  <GetCallerIdentityResult><Arn>{stsArn}</Arn><UserId>{stsId}</UserId><Account>{Account}</Account></GetCallerIdentityResult>
                  <ResponseMetadata><RequestId>offline-sts</RequestId></ResponseMetadata>
                </GetCallerIdentityResponse>
                """);
        });
        var verifier = new Ec2BootstrapIdentity(new Ec2ImdsV2(http, scope, clock),
            credentials => new AmazonSecurityTokenServiceClient(credentials, Configure(new AmazonSecurityTokenServiceConfig(), sts)));
        if (mismatch == "valid")
        {
            using var ddb = new SignatureWire((request, _) =>
            {
                Assert.Equal("DynamoDB_20120810.PutItem", request.Headers.GetValues("X-Amz-Target").Single());
                return AwsWireTransport.Json("{}");
            });
            var verified = await verifier.VerifyAsync(Authority, cancellation);
            Assert.Equal(Arn, verified.ResourceArn);
            Assert.Equal(Principal, verified.AwsUserId);
            using var client = new AmazonDynamoDBClient(verified.Credentials, Configure(new AmazonDynamoDBConfig(), ddb));
            using var key = JitRecipientKey.Create();
            Assert.Equal(LifecycleWriteOutcome.Committed, await new DynamoDbBootstrapOfferClient(client, "fixture", Principal, Arn, clock)
                .RegisterHelloAsync(key.Hello, cancellation));
            Assert.Equal(1, ddb.Requests);
        }
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => verifier.VerifyAsync(Authority, cancellation));
            Assert.DoesNotContain(PrivateSentinel, error.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(mismatch is "account" or "region" or "redirect" ? 0 : 1, sts.Requests);
        if (sts.Requests != 0)
        {
            Assert.Contains("Action=GetCallerIdentity", Assert.Single(stsRequest), StringComparison.Ordinal);
        }
        int count = mismatch == "redirect" ? 1 : 3;
        Assert.Equal(new[] { "/latest/api/token", "/latest/dynamic/instance-identity/document", "/latest/meta-data/iam/security-credentials/" + Role }
            .Take(count), imds.Paths);
    }

    [Fact]
    public async Task RealLoopbackImdsUsesOneTokenForIdentityAndAttachedRoleCredentials()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var serving = ServeImdsAsync(listener, clock, budget.Token);
        using var http = Ec2ImdsV2.CreateHttpClient();
        var identity = await new Ec2ImdsV2(http, endpoint, clock).ReadAsync(Role, budget.Token);
        await serving;
        Assert.Equal(new Ec2InstanceDocument(Account, Region, Instance), identity.Document);
        Assert.Equal(AccessId, identity.Credentials.GetCredentials().AccessKey);
    }

    private static async Task ServeImdsAsync(TcpListener listener, FixtureClock clock, CancellationToken cancellationToken)
    {
        string[] paths = ["/latest/api/token", "/latest/dynamic/instance-identity/document", "/latest/meta-data/iam/security-credentials/" + Role];
        string[] responses =
        [
            "synthetic-token",
            JsonSerializer.Serialize(new { accountId = Account, region = Region, instanceId = Instance }),
            JsonSerializer.Serialize(new
            {
                Code = "Success", AccessKeyId = AccessId, SecretAccessKey = PrivateSentinel, Token = Session,
                Expiration = clock.GetUtcNow().AddMinutes(10),
            }),
        ];
        for (int index = 0; index < paths.Length; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string verb = index == 0 ? "PUT" : "GET";
            Assert.Equal($"{verb} {paths[index]} HTTP/1.1", await reader.ReadLineAsync(cancellationToken));
            var headers = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
            {
                headers.Add(header);
            }
            string tokenHeader = index == 0
                ? "X-aws-ec2-metadata-token-ttl-seconds: 60" : "X-aws-ec2-metadata-token: synthetic-token";
            Assert.Contains(headers, header => header.Equals(tokenHeader, StringComparison.OrdinalIgnoreCase));
            var body = responses[index];
            string response = $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("bad-target")]
    [InlineData("tampered")]
    [InlineData("expires-in-flight")]
    [InlineData("lost")]
    public async Task OnlyTheAuthenticConsumedOfferLaunchesAndAReconstructedSupervisorCannotRepeatIt(string condition)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var trace = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start();
        var metrics = new List<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Knightfall.ScaleSet.Aws.RunnerBootstrap" &&
                    instrument.Name is "scaleset.aws.handoff.operations" or "scaleset.aws.startup.observations")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (Activity.Current?.TraceId == trace.TraceId)
            {
                metrics.Add((instrument.Name, tags.ToArray()));
            }
        });
        listener.Start();

        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var identity = new JitHandoffIdentity(Target.Deployment, Target.ProfileId, Target.ScopeIdentity, Target.ScaleSetId!.Value,
            Guid.NewGuid(), Arn, Guid.NewGuid(), Principal);
        bool spent = false;
        bool Reserve()
        {
            if (spent)
            {
                return false;
            }
            spent = true;
            return true;
        }
        var runner = new FixtureRunner();
        OneJobBootstrap Bootstrap() => new(new DynamoDbBootstrapOfferClient(table.Client, table.Name, Principal, Arn, clock),
            Target, Arn, Principal, Reserve, runner, clock, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(30), ObserveAsync,
            telemetryProvider: RunnerProvisionerType.Ec2);
        var observed = new List<bool>();
        var status = new DynamoDbEc2BootstrapStatusClient(table.Client, table.Name, Principal, Arn, clock);
        async Task ObserveAsync(BootstrapRunnerObservation observation, CancellationToken token)
        {
            observed.Add(observation.Exited);
            var row = new JitEc2BootstrapStatus(observation.OfferId, observation.RunnerId, observation.Exited ? 2 : 1,
                observation.Exited, clock.GetUtcNow());
            Assert.Equal(LifecycleWriteOutcome.Committed, await status.PublishAsync(observation.Hello, row, token));
        }

        var startup = Bootstrap().RunAsync(cancellation);
        var helloKey = LifecycleKey.Hello(Principal);
        Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue> helloRow;
        do
        {
            helloRow = await table.ReadAsync(helloKey, cancellation);
            if (helloRow.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation);
            }
        }
        while (helloRow.Count == 0 && !startup.IsCompleted);
        var hello = BootstrapItems.DecodeHello(helloKey, helloRow);
        await WaitForFirstConditionalDeleteAsync(table, cancellation);
        var binding = JitOfferBinding.Create(condition == "bad-target" ? identity with { ProfileId = "other" } : identity, hello.BootNonce,
            clock.GetUtcNow().AddSeconds(20));
        var envelope = JitOfferCryptography.Seal(binding, hello, FixtureJit("ss-" + binding.OfferId[..48]), clock);
        if (condition == "tampered")
        {
            envelope = envelope with { Tag = envelope.Tag.SetItem(0, (byte)(envelope.Tag[0] ^ 1)) };
        }
        var offerKey = LifecycleKey.Offer(Principal);
        var successfulDelete = condition == "expires-in-flight" ? table.Wire.PauseAfterNextSuccessfulBootstrapDelete(offerKey) : null;
        if (condition == "lost")
        {
            table.Wire.DropNextSuccessfulBootstrapDeleteResponse();
        }
        await table.Client.PutItemAsync(table.Name, BootstrapItems.EncodeOffer(envelope, BootstrapItems.HelloDigest(hello)), cancellation);
        if (successfulDelete is not null)
        {
            try
            {
                await successfulDelete.Captured.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
                Assert.Empty(await table.ReadAsync(offerKey, cancellation));
                clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(binding.ExpiresAtUnixMilliseconds);
            }
            finally
            {
                successfulDelete.Resume();
            }
        }

        var expected = condition == "valid" ? RunnerBootstrapOutcome.Completed : RunnerBootstrapOutcome.OfferRejected;
        Assert.Equal(expected, await startup);
        Assert.True(spent);
        if (condition == "valid")
        {
            Assert.Equal(1, runner.Starts);
            Assert.Collection(observed, ready => Assert.False(ready), exit => Assert.True(exit));
            var published = await table.ReadAsync(LifecycleKey.Status(Principal), cancellation);
            Assert.Equal("2", published["sequence"].N);
            Assert.Equal("17", published["runnerId"].N);
            Assert.Equal(binding.OfferId, published["offerId"].S);
            Assert.True(published["exited"].BOOL);
        }
        else
        {
            Assert.Equal(0, runner.Starts);
            Assert.Empty(observed);
        }
        var destructive = table.Wire.Requests.Where(call => call.Operation == "DeleteItem").ToArray();
        Assert.All(destructive, call => Assert.Equal("ALL_OLD", call.Request.GetProperty("ReturnValues").GetString()));
        if (condition == "lost")
        {
            Assert.Empty(await table.ReadAsync(LifecycleKey.Offer(Principal), cancellation));
            Assert.Equal(0, runner.Starts);
        }
        int requests = table.Wire.Requests.Count;
        Assert.Equal(RunnerBootstrapOutcome.AlreadySpent, await Bootstrap().RunAsync(cancellation));
        Assert.Equal(requests, table.Wire.Requests.Count);

        var result = condition switch
        {
            "valid" => ("success", "offer_authenticated"),
            "bad-target" => ("rejected", "binding_mismatch"),
            "tampered" => ("rejected", "authentication_failed"),
            "expires-in-flight" => ("expired", "receipt_expired"),
            _ => ("unknown", "receipt_unknown"),
        };
        var handoff = metrics.Where(item => item.Instrument == "scaleset.aws.handoff.operations").ToArray();
        Assert.Collection(handoff,
            first => AssertTags(first.Tags, "bootstrap-consume", result.Item1, result.Item2),
            second => AssertTags(second.Tags, "bootstrap-consume", "deferred", "already_spent"));
        var startupMetrics = metrics.Where(item => item.Instrument == "scaleset.aws.startup.observations").ToArray();
        if (condition == "valid")
        {
            AssertTags(Assert.Single(startupMetrics).Tags, "runner-listen", "success", "listener_connected");
        }
        else
        {
            Assert.Empty(startupMetrics);
        }

        static void AssertTags(KeyValuePair<string, object?>[] tags, string operation, string outcome, string reason) =>
            Assert.Collection(tags,
                item => Assert.Equal(new KeyValuePair<string, object?>("operation", operation), item),
                item => Assert.Equal(new KeyValuePair<string, object?>("outcome", outcome), item),
                item => Assert.Equal(new KeyValuePair<string, object?>("reason", reason), item),
                item => Assert.Equal(new KeyValuePair<string, object?>("provider", "ec2"), item));
    }

    [Fact]
    public async Task OfferAtExactExpiryCannotBeDeleted()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var identity = new JitHandoffIdentity(Target.Deployment, Target.ProfileId, Target.ScopeIdentity, Target.ScaleSetId!.Value,
            Guid.NewGuid(), Arn, Guid.NewGuid(), Principal);
        using var recipient = JitRecipientKey.Create();
        var bootstrap = new DynamoDbBootstrapOfferClient(table.Client, table.Name, Principal, Arn, clock);
        Assert.Equal(LifecycleWriteOutcome.Committed, await bootstrap.RegisterHelloAsync(recipient.Hello, cancellation));
        var expiresAt = clock.Now.AddSeconds(20);
        var binding = JitOfferBinding.Create(identity, recipient.Hello.BootNonce, expiresAt);
        var offer = JitOfferCryptography.Seal(binding, recipient.Hello, FixtureJit("ss-" + binding.OfferId[..48]), clock);
        var key = LifecycleKey.Offer(Principal);
        await table.Client.PutItemAsync(table.Name, BootstrapItems.EncodeOffer(offer, BootstrapItems.HelloDigest(recipient.Hello)), cancellation);
        clock.Now = expiresAt;

        var attempt = await bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, attempt.Status);
        Assert.Null(attempt.Offer);
        var wire = Assert.Single(table.Wire.Requests, call => call.Operation == "DeleteItem");
        Assert.Equal(binding.ExpiresAtUnixMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            wire.Request.GetProperty("ExpressionAttributeValues").GetProperty(":now").GetProperty("N").GetString());
        var retained = await table.ReadAsync(key, cancellation);
        Assert.Equal(binding.OfferId, retained["offerId"].S);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(".runner_migrated")]
    public void RunnerConfigurationRejectsUnexpectedMembers(string name)
    {
        string fixture = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = Convert.ToBase64String("not-written"u8),
            [".runner"] = Convert.ToBase64String("{}"u8),
        }));
        var error = Assert.Throws<InvalidDataException>(() => JitRunnerConfiguration.Decode(Encoding.ASCII.GetBytes(fixture), "fixture"));
        Assert.DoesNotContain("not-written", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerConfigurationRejectsDuplicateMember()
    {
        byte[] payload = Encoding.ASCII.GetBytes(Convert.ToBase64String(
            """{".runner":"e30=",".runner":"e30=",".credentials":"Zml4dHVyZQ==",".credentials_rsaparams":"Zml4dHVyZQ=="}"""u8));
        Assert.Throws<InvalidDataException>(() => JitRunnerConfiguration.Decode(payload, "fixture"));
    }

    [Fact]
    public void RunnerConfigurationRequiresItsExactEphemeralRunnerAndKeepsPlaintextOutOfDiagnostics()
    {
        byte[] payload = FixtureJit("ss-fixture");
        using var settings = JitRunnerConfiguration.Decode(payload, "ss-fixture");
        Assert.Equal(17L, settings.RunnerId);
        Assert.Throws<InvalidDataException>(() => JitRunnerConfiguration.Decode(payload, "wrong-runner"));
        Assert.DoesNotContain(PrivateSentinel, settings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRootRequiresItsEntireExecutableRamMountAndNoGlobalSwap()
    {
        const string runtime = "/run/scaleset-runner";
        const string host = "20 10 0:1 / / rw,relatime - ext4 /dev/root rw";
        const string ram = "30 20 0:2 / /run/scaleset-runner rw,nosuid,nodev - tmpfs tmpfs rw,size=512000k";
        const string nested = "31 30 0:1 / /run/scaleset-runner/bin rw,relatime - ext4 /dev/root rw";
        string[] noSwap = ["Filename Type Size Used Priority"];
        Assert.True(LinuxMounts.IsVolatileRunnerRoot(runtime, [host, ram], noSwap));
        Assert.False(LinuxMounts.IsVolatileRunnerRoot(runtime, [host], noSwap));
        Assert.False(LinuxMounts.IsVolatileRunnerRoot(runtime, [host, ram, nested], noSwap));
        Assert.False(LinuxMounts.IsVolatileRunnerRoot(runtime, [host, ram.Replace("nodev", "nodev,noexec", StringComparison.Ordinal)], noSwap));
        Assert.False(LinuxMounts.IsVolatileRunnerRoot(runtime, [host, ram], [.. noSwap, "/swap file 123 0 -2"]));
        Assert.False(LinuxMounts.IsPersistentFileSystem(runtime, [host, ram]));
        Assert.True(LinuxMounts.IsPersistentFileSystem("/var/lib/scaleset", [host, ram]));
    }

    private static byte[] FixtureJit(string runnerName)
    {
        var settings = JsonSerializer.SerializeToUtf8Bytes(new { AgentId = 17, AgentName = runnerName, Ephemeral = true, WorkFolder = "_work" });
        var members = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".runner"] = Convert.ToBase64String(settings),
            [".credentials"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(PrivateSentinel)),
            [".credentials_rsaparams"] = Convert.ToBase64String("fixture-private-bytes"u8),
        };
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(members)));
    }

    private static async Task WaitForFirstConditionalDeleteAsync(MiniStackTable table, CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        while (!table.Wire.Requests.Any(call => call.Operation == "DeleteItem"))
        {
            await Task.Delay(10, linked.Token);
        }
    }

    private static T Configure<T>(T config, HttpMessageHandler transport) where T : ClientConfig
    {
        config.ServiceURL = "https://bootstrap.fixture.invalid";
        config.AuthenticationRegion = Region;
        config.MaxErrorRetry = 0;
        config.MaxStaleConnectionRetries = 0;
        config.HttpClientFactory = new FixtureFactory(transport);
        return config;
    }

    private sealed class FixtureFactory(HttpMessageHandler transport) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(transport, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }

    private sealed class SignatureWire(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("bootstrap.fixture.invalid", request.RequestUri?.Host);
            Assert.Contains("Credential=" + AccessId + "/", request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
            Assert.Equal(Session, request.Headers.GetValues("X-Amz-Security-Token").Single());
            Requests++;
            return respond(request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        }
    }

    private sealed class MetadataWire(FixtureClock clock, string mismatch) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("127.0.0.1", request.RequestUri?.Host);
            Assert.Equal(43123, request.RequestUri?.Port);
            string path = Assert.IsType<Uri>(request.RequestUri, exactMatch: false).AbsolutePath;
            Paths.Add(path);
            if (path == "/latest/api/token")
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("60", request.Headers.GetValues("X-aws-ec2-metadata-token-ttl-seconds").Single());
                return Task.FromResult(mismatch == "redirect"
                    ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://other.fixture.invalid/") } }
                    : AwsWireTransport.Json("synthetic-token"));
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("synthetic-token", request.Headers.GetValues("X-aws-ec2-metadata-token").Single());
            if (path == "/latest/dynamic/instance-identity/document")
            {
                return Task.FromResult(AwsWireTransport.Json(JsonSerializer.Serialize(new
                {
                    accountId = mismatch == "account" ? "999900001111" : Account,
                    region = mismatch == "region" ? "us-east-1" : Region,
                    instanceId = Instance,
                })));
            }
            Assert.Equal("/latest/meta-data/iam/security-credentials/" + Role, path);
            return Task.FromResult(AwsWireTransport.Json(JsonSerializer.Serialize(new
            {
                Code = "Success", AccessKeyId = AccessId, SecretAccessKey = PrivateSentinel, Token = Session,
                Expiration = clock.GetUtcNow().AddMinutes(10),
            })));
        }
    }

    private sealed class FixtureRunner : IOneJobRunner
    {
        internal int Starts { get; private set; }

        public async Task<OneJobCompletion> RunAsync(JitPlaintext configuration, string expectedName,
            Func<long, CancellationToken, Task> ready, CancellationToken cancellationToken)
        {
            Starts++;
            using var settings = JitRunnerConfiguration.Decode(configuration.Bytes, expectedName);
            Assert.Equal(17L, settings.RunnerId);
            await ready(settings.RunnerId, cancellationToken);
            return new OneJobCompletion(settings.RunnerId, 0);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = MiniStackTable.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
