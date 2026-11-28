using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class FargateBootstrapTests
{
    private const string Account = "111122223333";
    private const string Region = "ap-southeast-2";
    private const string Deployment = Account + ":" + Region + ":fixture";
    private const string Cluster = "arn:aws:ecs:" + Region + ":" + Account + ":cluster/fixture";
    private const string TaskId = "00112233445566778899aabbccddeeff";
    private const string Role = "fixture-bootstrap";
    private const string RoleId = "AROAFIXTUREIDENTITY0001";
    private const string UserId = RoleId + ":" + TaskId;
    private const string TaskArn = "arn:aws:ecs:" + Region + ":" + Account + ":task/fixture/" + TaskId;
    private const string Scope = "https://github.com/fixture/repository";
    private const string Sensitive = "fixture-jit-sentinel-9ab0d5d6";
    private const string Metadata = "http://169.254.170.2/v4/fixture-context";
    private static readonly FargateBootstrapSettings Settings = new(Account, Region, Deployment, "fargate", Scope, "fixture-table", Cluster,
        "fixture-runner", "runner", $"arn:aws:iam::{Account}:role/path/{Role}", RoleId,
        "/opt/actions-runner", "/opt/runner-dotnet/dotnet", "/opt/runner-child", "/work", 99);

    [Theory]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:44123/v4/fixture-context")]
    [InlineData("http://localhost/v4/fixture")]
    [InlineData("http://127.1/v4/fixture")]
    [InlineData("http://169.254.169.254/v4/fixture")]
    [InlineData("http://169.254.170.2:8080/v4/fixture")]
    [InlineData("https://169.254.170.2/v4/fixture")]
    [InlineData("http://someuser@169.254.170.2/v4/fixture")]
    [InlineData("http://169.254.170.2/v4/fixture?other=yes")]
    [InlineData("http://169.254.170.2/v4/fixture#task")]
    [InlineData("http://169.254.170.2/v4/fixture/task")]
    [InlineData("http://169.254.170.2/v4/../v4/fixture")]
    [InlineData("http://169.254.170.2/v4/%66ixture")]
    public void MetadataUriRejectsAlternateAuthoritiesAndUnapprovedPaths(string? value) =>
        Assert.Throws<InvalidDataException>(() => FargateBootstrapIdentity.ParseMetadataEndpoint(value));

    [Theory]
    [InlineData("http://169.254.170.2/v4/fixture_context-01")]
    [InlineData(Metadata)]
    public void MetadataUriAcceptsTheEcsAuthority(string value) =>
        Assert.Equal(value, FargateBootstrapIdentity.ParseMetadataEndpoint(value).AbsoluteUri);

    [Fact]
    public void NumericLoopbackRequiresExplicitFixtureOptIn()
    {
        const string endpoint = "http://127.0.0.1:44123/v4/fixture-context";
        Assert.Throws<InvalidDataException>(() => FargateBootstrapIdentity.ParseMetadataEndpoint(endpoint));
        Assert.Equal(endpoint, FargateBootstrapIdentity.ParseMetadataEndpoint(endpoint, allowLoopback: true).AbsoluteUri);
    }

    [Theory]
    [InlineData("/v2/credentials/f139cbbd-9696-4c04-89d3-34127db4a957", true)]
    [InlineData("http://169.254.170.2/v2/credentials/f139cbbd-9696-4c04-89d3-34127db4a957", false)]
    [InlineData("/v2/credentials/../identity", false)]
    [InlineData("/v2/credentials/f139cbbd-9696-4c04-89d3-34127db4a957 ", false)]
    [InlineData(null, false)]
    public void OnlyRelativeEcsCredentialIdsAreAccepted(string? path, bool expected) =>
        Assert.Equal(expected, FargateBootstrapIdentity.IsTaskCredentialsUri(path));

    [Theory]
    [InlineData("task")]
    [InlineData("cluster")]
    [InlineData("container")]
    [InlineData("mismatched-runtime-id")]
    [InlineData("loopback-metadata")]
    [InlineData("sidecar")]
    [InlineData("internal-looking-container")]
    [InlineData("family")]
    [InlineData("invalid-revision")]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("duplicate-properties")]
    [InlineData("role")]
    [InlineData("role-id")]
    [InlineData("session")]
    [InlineData("account")]
    public async Task InexactMetadataOrCallerIdentityLeavesTheScopedTableUntouched(string defect)
    {
        var token = TestContext.Current.CancellationToken;
        var metadata = new MetadataWire(defect);
        using var http = new HttpClient(metadata);
        using var stsWire = new AwsWireTransport(_ => StsReply(defect));
        using var ddbWire = new AwsWireTransport(_ => throw new InvalidOperationException("Bootstrap wrote with unverified identity."));
        using var sts = new AmazonSecurityTokenServiceClient(AwsWireTransport.Credentials, stsWire.Configure(new AmazonSecurityTokenServiceConfig()));
        using var ddb = new AmazonDynamoDBClient(AwsWireTransport.Credentials, ddbWire.Configure(new AmazonDynamoDBConfig()));
        var runner = new FixtureRunner();
        var app = new FargateBootstrapApplication(Settings, new FixtureClock());

        string endpoint = defect == "loopback-metadata" ? "http://127.0.0.1:44123/v4/fixture-context" : Metadata;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => app.RunAsync(endpoint, http, sts, ddb, runner,
            static (_, _) => throw new InvalidOperationException("Bootstrap registered with unverified identity."), token));

        if (defect == "loopback-metadata") Assert.Empty(metadata.Paths);
        Assert.Empty(ddbWire.Requests);
        Assert.Equal(0, runner.Starts);
        Assert.DoesNotContain(Sensitive, error.ToString(), StringComparison.Ordinal);
        if (defect is "role" or "role-id" or "session" or "account")
        {
            Assert.Contains("Action=GetCallerIdentity", Assert.Single(stsWire.Requests).Body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(stsWire.Requests);
        }
    }

    [Theory]
    [InlineData("valid", RunnerBootstrapOutcome.Completed)]
    [InlineData("wrong-revision", RunnerBootstrapOutcome.OfferRejected)]
    [InlineData("expired", RunnerBootstrapOutcome.OfferRejected)]
    [InlineData("tampered", RunnerBootstrapOutcome.OfferRejected)]
    [InlineData("lost-reply", RunnerBootstrapOutcome.OfferRejected)]
    public async Task VerifiedTaskConsumesOnlyItsEncryptedConditionalOfferAndNeverPublishesStatus(string caseName, RunnerBootstrapOutcome outcome)
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var metadata = new MetadataWire(caseName);
        using var http = new HttpClient(metadata);
        using var stsWire = new AwsWireTransport(_ => StsReply());
        using var sts = new AmazonSecurityTokenServiceClient(AwsWireTransport.Credentials, stsWire.Configure(new AmazonSecurityTokenServiceConfig()));
        bool helloExists = false;
        Dictionary<string, object>? attributes = null;
        using var ddbWire = new AwsWireTransport(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var body = document.RootElement;
            Assert.Equal(Settings.TableName, body.GetProperty("TableName").GetString());
            if (request.Target == "DynamoDB_20120810.PutItem")
            {
                var helloKey = LifecycleKey.Hello(UserId);
                var raw = body.GetProperty("Item");
                Assert.Equal(helloKey.PartitionKey, raw.GetProperty("PK").GetProperty("S").GetString());
                Assert.Equal("attribute_not_exists(PK)", body.GetProperty("ConditionExpression").GetString());
                if (helloExists)
                {
                    return AwsWireTransport.Json("""{"__type":"ConditionalCheckFailedException"}""", HttpStatusCode.BadRequest);
                }
                helloExists = true;
                var hello = BootstrapItems.DecodeHello(helloKey, ReadItem(raw), clock.GetUtcNow());
                var allocation = Guid.Parse("00012233-4455-6677-8899-aabbccddeeff");
                // Precomputed from the canonical task ARN, revision-7 definition ARN, and allocation.
                var incarnation = Guid.Parse("8333bcc2-7c30-5503-0f71-d9a70c81a309");
                var identity = new JitHandoffIdentity(Deployment, Settings.ProfileId, Settings.Validate().ScopeIdentity, 99,
                    allocation, TaskArn, incarnation, UserId);
                var binding = JitOfferBinding.Create(identity, hello.BootNonce, clock.GetUtcNow().AddSeconds(20));
                var offer = JitOfferCryptography.Seal(binding, hello, Encoding.UTF8.GetBytes(Sensitive), clock);
                if (caseName == "tampered")
                {
                    offer = offer with { Tag = offer.Tag.SetItem(0, (byte)(offer.Tag[0] ^ 1)) };
                }
                if (caseName == "expired")
                {
                    clock.Now = clock.Now.AddSeconds(21);
                }
                attributes = BootstrapItems.EncodeOffer(offer, BootstrapItems.HelloDigest(hello)).ToDictionary(pair => pair.Key,
                    pair => pair.Value.S is { } value ? (object)new { S = value } : new { N = pair.Value.N }, StringComparer.Ordinal);
                return AwsWireTransport.Json("{}");
            }

            Assert.Equal("DynamoDB_20120810.DeleteItem", request.Target);
            Assert.Equal(LifecycleKey.Offer(UserId).PartitionKey, body.GetProperty("Key").GetProperty("PK").GetProperty("S").GetString());
            Assert.Equal("ALL_OLD", body.GetProperty("ReturnValues").GetString());
            Assert.Contains("offerExpiresAt > :now", body.GetProperty("ConditionExpression").GetString(), StringComparison.Ordinal);
            if (caseName == "lost-reply")
            {
                throw new HttpRequestException("Fixture dropped the consume response.");
            }
            return AwsWireTransport.Json(JsonSerializer.Serialize(new { Attributes = attributes }));
        });
        using var ddb = new AmazonDynamoDBClient(AwsWireTransport.Credentials, ddbWire.Configure(new AmazonDynamoDBConfig()));
        var runner = new FixtureRunner();
        FargateBootstrapApplication Application() => new(Settings, clock);
        Task<LifecycleWriteOutcome> RegisterHelloAsync(JitHello hello, CancellationToken cancellation) =>
            BrokerHelloFixture.RegisterAsync(ddb, Settings.TableName, UserId, hello, clock, cancellation);

        Assert.Equal(outcome, await Application().RunAsync(Metadata, http, sts, ddb, runner, RegisterHelloAsync, token));
        Assert.Equal(outcome == RunnerBootstrapOutcome.Completed ? 1 : 0, runner.Starts);
        Assert.Equal(RunnerBootstrapOutcome.HelloRejected,
            await Application().RunAsync(Metadata, http, sts, ddb, runner, RegisterHelloAsync, token));
        Assert.Equal(outcome == RunnerBootstrapOutcome.Completed ? 1 : 0, runner.Starts);

        string[] expected = ["DynamoDB_20120810.PutItem", "DynamoDB_20120810.DeleteItem", "DynamoDB_20120810.PutItem"];
        Assert.Equal(expected, ddbWire.Requests.Select(request => request.Target));
        Assert.All(ddbWire.Requests, request =>
        {
            Assert.DoesNotContain("STATUS#", request.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(Sensitive, request.Body, StringComparison.Ordinal);
        });
        string[] paths = ["/v4/fixture-context", "/v4/fixture-context/task"];
        Assert.Equal(paths, metadata.Paths.Take(2));
    }

    [Fact]
    public void MemoryRootRequiresNonswappingTmpfsEvenWhenExecutionIsDisabled()
    {
        string[] mounts =
        [
            "21 10 0:1 / / ro,relatime - overlay overlay ro",
            "31 21 0:2 / /dev/shm rw,nosuid,nodev,noexec - tmpfs shm rw,size=65536k",
            "41 21 0:3 /new-root /sys/fs/cgroup ro,nosuid,nodev,noexec - cgroup2 cgroup rw",
        ];
        Assert.True(FargateMountLayout.HasVerifiedMemoryMount(mounts, cgroupSwapDisabled: true));
        Assert.False(FargateMountLayout.HasVerifiedMemoryMount(mounts, cgroupSwapDisabled: false));
        Assert.False(FargateMountLayout.HasVerifiedMemoryMount(mounts.Concat(
            ["32 31 0:4 / /dev/shm/nested rw,relatime - xfs scratch rw"]).ToArray(), cgroupSwapDisabled: true));
        Assert.Equal("/sys/fs/cgroup/memory.swap.max", FargateMountLayout.FindCgroupSwapLimit(mounts, ["0::/"]));
        Assert.Equal("/sys/fs/cgroup/sub/memory.swap.max", FargateMountLayout.FindCgroupSwapLimit(mounts, ["0::/new-root/sub"]));
        Assert.Null(FargateMountLayout.FindCgroupSwapLimit(mounts, ["0::/other/sub"]));
    }

    private static Dictionary<string, AttributeValue> ReadItem(JsonElement item) =>
        item.EnumerateObject().ToDictionary(field => field.Name, field =>
            field.Value.TryGetProperty("S", out var text) ? new AttributeValue { S = text.GetString() } :
                new AttributeValue { N = field.Value.GetProperty("N").GetString() }, StringComparer.Ordinal);

    private static HttpResponseMessage StsReply(string? defect = null)
    {
        string account = defect == "account" ? "999900001111" : Account;
        string role = defect == "role" ? "other-role" : Role;
        string roleId = defect == "role-id" ? "AROAFIXTUREWRONG000001" : RoleId;
        string session = defect == "session" ? "11112233445566778899aabbccddeeff" : TaskId;
        return AwsWireTransport.Xml($"""
            <GetCallerIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
              <GetCallerIdentityResult><Arn>arn:aws:sts::{account}:assumed-role/{role}/{session}</Arn>
                <UserId>{roleId}:{session}</UserId><Account>{account}</Account></GetCallerIdentityResult>
            </GetCallerIdentityResponse>
            """);
    }

    private sealed class MetadataWire(string defect) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            const string runtime = TaskId + "-1234567890";
            if (defect == "redirect")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("http://169.254.169.254/");
                return Task.FromResult(redirect);
            }

            string body;
            if (path.EndsWith("/task", StringComparison.Ordinal))
            {
                var containers = new List<object>
                {
                    new { DockerId = defect == "mismatched-runtime-id" ? TaskId + "-9876543210" : runtime, Name = "runner" },
                };
                if (defect is "sidecar" or "internal-looking-container")
                {
                    containers.Add(new
                    {
                        DockerId = TaskId + "-9876543210",
                        Name = defect == "sidecar" ? "extra" : "~internal~ecs~pause",
                        Type = defect == "sidecar" ? "NORMAL" : "CNI_PAUSE",
                    });
                }
                body = JsonSerializer.Serialize(new
                {
                    Cluster = defect == "cluster" ? Cluster + "-other" : Cluster,
                    TaskARN = defect == "task" ? TaskArn[..^1] + "0" : TaskArn,
                    Family = defect == "family" ? Settings.TaskFamily + "-other" : Settings.TaskFamily,
                    Revision = defect switch { "wrong-revision" => "8", "invalid-revision" => "07", _ => "7" },
                    LaunchType = "FARGATE",
                    Containers = containers,
                });
            }
            else
            {
                body = defect == "duplicate-properties" ? $$"""{"DockerId":"{{runtime}}","Name":"bogus","Name":"runner"}""" :
                    JsonSerializer.Serialize(new { DockerId = runtime, Name = defect == "container" ? "sidecar" : "runner" });
            }
            return Task.FromResult(AwsWireTransport.Json(defect == "oversize" ? new string('x', 33 * 1024) : body));
        }
    }

    private sealed class FixtureRunner : IOneJobRunner
    {
        internal int Starts { get; private set; }

        public Task<OneJobCompletion> RunAsync(JitPlaintext configuration, string expectedName, Func<long, CancellationToken, Task> ready,
            CancellationToken cancellationToken)
        {
            Assert.True(configuration.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(Sensitive)));
            Assert.StartsWith("ss-", expectedName, StringComparison.Ordinal);
            Starts++;
            return Task.FromResult(new OneJobCompletion(17, 0));
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2050, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
