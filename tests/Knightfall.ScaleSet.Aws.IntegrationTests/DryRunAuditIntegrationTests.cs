using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.ECS;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using CreateClusterRequest = Amazon.ECS.Model.CreateClusterRequest;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed class DryRunAuditIntegrationTests
{
    private const string Account = "000000000000";
    private const string Region = "us-east-1";
    private const string RuntimeKey = "versions/runtime.json";
    private const string DemandKey = "versions/demand.json";
    private const string DemandProfile = "linux-ec2-build";

    [Theory(Timeout = 45_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileUsesPinnedS3VersionsAndOnlyReadCapabilities(bool useSnapshot)
    {
        var token = TestContext.Current.CancellationToken;
        var native = NativeEndpoint();
        using var table = await MiniStackTable.CreateAsync(token);
        var document = await LoadMixedAsync(table.Name, token);
        var spec = document["spec"]!;
        string environment = document["metadata"]!["name"]!.GetValue<string>();

        var options = ControllerAwsClientConfiguration.Create<AmazonS3Config>(Region, native);
        options.ForcePathStyle = true;
        using var s3 = new AmazonS3Client(FixtureCredentials(), options);
        string bucket = "scaleset-audit-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(bucket, token);
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, token);

        string? demandVersion = null;
        var clusters = new List<string>();
        spec["dryRun"]!["inspectAws"] = true;
        if (useSnapshot)
        {
            var demand = DemandDocument(environment, assignedJobs: 3);
            demandVersion = await PutVersionPairAsync(s3, bucket, DemandKey, demand, DemandDocument(environment, assignedJobs: 9), token);
            spec["dryRun"]!["demand"] = new JsonObject
            {
                ["type"] = "snapshot",
                ["s3Uri"] = $"s3://{bucket}/{DemandKey}",
                ["versionId"] = demandVersion,
                ["sha256"] = Sha256(demand),
            };
        }
        else
        {
            using var ecs = new AmazonECSClient(FixtureCredentials(), ControllerAwsClientConfiguration.Create<AmazonECSConfig>(Region, native));
            foreach (string clusterKey in new[] { "runner-arm", "runner-shell" })
            {
                var created = await ecs.CreateClusterAsync(new CreateClusterRequest { ClusterName = environment + "-" + clusterKey }, token);
                string arn = Assert.IsType<string>(created.Cluster.ClusterArn);
                Assert.Equal("ACTIVE", created.Cluster.Status);
                spec["ecsClusters"]![clusterKey]!["arn"] = arn;
                clusters.Add(arn);
            }
        }

        byte[] runtime = JsonSerializer.SerializeToUtf8Bytes(document);
        var configuration = RuntimeConfigurationLoader.Load(runtime);
        Assert.Equal(ControllerMode.Active, configuration.Mode);
        spec["profiles"]![1]!["capacity"]!["maxRunners"] = 15;
        byte[] latest = JsonSerializer.SerializeToUtf8Bytes(document);
        _ = RuntimeConfigurationLoader.Load(latest);
        Assert.NotEqual(Sha256(runtime), Sha256(latest));
        string version = await PutVersionPairAsync(s3, bucket, RuntimeKey, runtime, latest, token);

        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var fence = new DeploymentLeaseFence(deployment, "dry-run-existing", 9, ControllerLease.CurrentWriterProtocol, MiniStackTable.FirstTask);
        var leaseKey = LifecycleKey.Lease(deployment);
        await table.SetLeaseAsync(fence, TimeProvider.System.GetUtcNow().AddMinutes(5), token);
        var before = await table.ReadAsync(leaseKey, token);

        await using var proxy = await LoopbackAwsAuditProxy.StartAsync(native, token);
        await CheckEgressRecorderAsync(proxy, token);
        var result = await RunCliAsync(proxy.Endpoint, $"s3://{bucket}/{RuntimeKey}", version, configuration.Sha256, token);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Error);

        using var stdout = JsonDocument.Parse(result.Output);
        AssertReport(stdout.RootElement, configuration.Sha256, environment, useSnapshot);

        var requests = proxy.Requests.Skip(1).ToArray(); // The first request is the egress-recorder control.
        Assert.NotEmpty(requests);
        Assert.DoesNotContain(requests, request => request.Service is "dynamodb" or "secretsmanager" or "sqs" or "egress");
        Assert.DoesNotContain(requests, request =>
            request.Operation is "RunInstances" or "RunTask" or "TerminateInstances" or "StopTask");
        Assert.All(requests, request => Assert.True(request.Forwarded, $"{request.Service}/{request.Operation}"));

        AssertVersionedRead(requests[0], bucket, RuntimeKey, version);
        if (useSnapshot)
        {
            Assert.Equal(2, requests.Length);
            AssertVersionedRead(requests[1], bucket, DemandKey, demandVersion!);
        }
        else
        {
            Assert.Single(requests, request => request.Service == "s3");
            AssertNativeInspection(requests, clusters);
        }

        var after = await table.ReadAsync(leaseKey, token);
        Assert.Equal(before.Count, after.Count);
        Assert.All(before, attribute =>
        {
            Assert.Equal(attribute.Value.S, after[attribute.Key].S);
            Assert.Equal(attribute.Value.N, after[attribute.Key].N);
        });
    }

    private static async Task<JsonObject> LoadMixedAsync(string table, CancellationToken token)
    {
        string fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mixed-ec2-fargate.json"), token);
        var root = JsonNode.Parse(fixture.Replace("111122223333", Account, StringComparison.Ordinal)
            .Replace("ap-southeast-2", Region, StringComparison.Ordinal))!.AsObject();
        root["metadata"]!["name"] = "audit-" + Guid.NewGuid().ToString("N");
        root["spec"]!["tracking"]!["table"] = new JsonObject { ["type"] = "import", ["name"] = table };
        return root;
    }

    private static byte[] DemandDocument(string environment, int assignedJobs) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = "scaleset-aws/v1alpha1",
            kind = "RunnerDemandSnapshot",
            environment,
            aws = new { account = Account, region = Region },
            records = new[]
            {
                new
                {
                    profileId = DemandProfile,
                    scopeURL = "https://github.com/example-engineering",
                    runnerGroup = "Linux-EC2-Arm",
                    scaleSet = DemandProfile,
                    scaleSetId = "9223372036854775807",
                    observedAt = TimeProvider.System.GetUtcNow().AddSeconds(-2),
                    demand = new { totalAssignedJobs = assignedJobs },
                },
            },
        });

    private static async Task<string> PutVersionPairAsync(AmazonS3Client s3, string bucket, string key,
        byte[] selected, byte[] newer, CancellationToken token)
    {
        var original = await PutAsync(s3, bucket, key, selected, token);
        var latest = await PutAsync(s3, bucket, key, newer, token);
        Assert.NotEqual(original, latest);
        using var current = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token);
        Assert.Equal(latest, current.VersionId);
        return original;
    }

    private static async Task<string> PutAsync(AmazonS3Client s3, string bucket, string key, byte[] bytes, CancellationToken token)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        var receipt = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/json",
        }, token);
        string version = Assert.IsType<string>(receipt.VersionId);
        Assert.NotEmpty(version);
        Assert.NotEqual("null", version);
        return version;
    }

    private static void AssertReport(JsonElement report, string digest, string environment, bool usedSnapshot)
    {
        Assert.Equal("RunnerDryRunReport", report.GetProperty("kind").GetString());
        Assert.Equal(environment, report.GetProperty("environment").GetString());
        Assert.Equal(digest, report.GetProperty("runtimeSha256").GetString());
        Assert.Equal(usedSnapshot ? "snapshot" : "none", report.GetProperty("demandSource").GetString());
        var inspection = report.GetProperty("inspection");
        Assert.True(inspection.GetProperty("requested").GetBoolean());
        Assert.Equal(!usedSnapshot, inspection.GetProperty("performed").GetBoolean());

        var profiles = report.GetProperty("profiles").EnumerateArray().ToDictionary(
            profile => profile.GetProperty("profileId").GetString()!, StringComparer.Ordinal);
        Assert.Equal(["linux-ci-arm", "linux-ec2-build", "linux-ec2-docker", "linux-shell"], profiles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("ec2", profiles["linux-ec2-docker"].GetProperty("provisioner").GetString());
        Assert.Equal("ec2", profiles[DemandProfile].GetProperty("provisioner").GetString());
        Assert.Equal("fargate", profiles["linux-ci-arm"].GetProperty("provisioner").GetString());
        Assert.Equal("fargate", profiles["linux-shell"].GetProperty("provisioner").GetString());
        var pinnedProfile = profiles[DemandProfile].GetProperty("capacity");
        Assert.Equal(16, pinnedProfile.GetProperty("effectiveMax").GetInt32());
        if (usedSnapshot)
        {
            Assert.Equal(3, pinnedProfile.GetProperty("totalAssignedJobs").GetInt32());
            Assert.Equal("fresh", pinnedProfile.GetProperty("demandStatus").GetString());
        }
        else
        {
            Assert.Equal("unknown", pinnedProfile.GetProperty("demandStatus").GetString());
            Assert.Equal(JsonValueKind.Null, pinnedProfile.GetProperty("totalAssignedJobs").ValueKind);
            Assert.All(profiles.Values, profile =>
                Assert.NotEqual(JsonValueKind.Null, profile.GetProperty("inventoryObservedAt").ValueKind));
        }
        Assert.All(profiles.Values, profile => Assert.Equal(0, profile.GetProperty("capacity").GetProperty("proposedStarts").GetInt32()));
    }

    private static void AssertNativeInspection(IReadOnlyList<AuditedAwsRequest> requests, IReadOnlyCollection<string> clusters)
    {
        Assert.Contains(requests, request => (request.Service, request.Operation) == ("sts", "GetCallerIdentity"));
        Assert.Equal(2, requests.Count(request => (request.Service, request.Operation) == ("ec2", "DescribeInstances")));
        Assert.Equal(2, requests.Count(request => (request.Service, request.Operation) == ("ec2", "DescribeLaunchTemplateVersions")));
        string[] described = requests.Where(request => (request.Service, request.Operation) == ("ecs", "DescribeClusters"))
            .Select(request =>
            {
                using var body = JsonDocument.Parse(request.Body);
                return Assert.Single(body.RootElement.GetProperty("clusters").EnumerateArray()).GetString()!;
            }).ToArray();
        Assert.Equal(clusters.Order(StringComparer.Ordinal), described.Order(StringComparer.Ordinal));
        foreach (string cluster in clusters)
        {
            Assert.Contains(requests, request =>
            {
                if ((request.Service, request.Operation) != ("ecs", "ListTasks"))
                {
                    return false;
                }
                using var body = JsonDocument.Parse(request.Body);
                return body.RootElement.GetProperty("cluster").ValueEquals(cluster);
            });
        }
    }

    private static void AssertVersionedRead(AuditedAwsRequest request, string bucket, string key, string version)
    {
        Assert.Equal(("s3", "GetObject", "GET"), (request.Service, request.Operation, request.Method));
        var parts = request.Target.Split('?', 2);
        Assert.Equal($"/{bucket}/{key}", parts[0]);
        Assert.Equal(version, request.VersionId);
        Assert.Equal(version, Assert.Single(QueryHelpers.ParseQuery(parts[1])["versionId"]));
    }

    private static async Task CheckEgressRecorderAsync(LoopbackAwsAuditProxy proxy, CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(proxy.Endpoint) });
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var unexpected = await client.GetAsync(new Uri("https://fixture-github.invalid/session"), token);
        });
        var control = Assert.Single(proxy.Requests);
        Assert.Equal(("egress", "CONNECT", false), (control.Service, control.Operation, control.Forwarded));
        Assert.Equal("fixture-github.invalid:443", control.Target);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        Uri proxy, string source, string version, string digest, CancellationToken token)
    {
        var command = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] arguments =
        [
            Path.Combine(AppContext.BaseDirectory, "Knightfall.ScaleSet.Aws.Controller.dll"),
            "reconcile", "--config-s3-uri", source, "--config-version-id", version, "--sha256", digest, "--dry-run", "--once",
        ];
        foreach (string argument in arguments)
        {
            command.ArgumentList.Add(argument);
        }
        foreach (string key in command.Environment.Keys.Where(key =>
            key.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _ = command.Environment.Remove(key);
        }
        command.Environment["AWS_ENDPOINT_URL"] = proxy.AbsoluteUri;
        command.Environment["AWS_REGION"] = Region;
        command.Environment["AWS_DEFAULT_REGION"] = Region;
        command.Environment["AWS_ACCESS_KEY_ID"] = Account;
        command.Environment["AWS_SECRET_ACCESS_KEY"] = "test-only-secret";
        command.Environment["AWS_EC2_METADATA_DISABLED"] = "true";
        _ = command.Environment.Remove(FargateRuntimeEvidence.EnvironmentVariable);
        command.Environment["OTEL_SDK_DISABLED"] = "true";
        command.Environment["HTTP_PROXY"] = proxy.AbsoluteUri;
        command.Environment["HTTPS_PROXY"] = proxy.AbsoluteUri;
        command.Environment["NO_PROXY"] = "127.0.0.1,localhost";

        using var process = Process.Start(command) ?? throw new InvalidOperationException("The controller CLI did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
    }

    private static Uri NativeEndpoint()
    {
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("SCALESET_MINISTACK_ENDPOINT"), UriKind.Absolute, out var native) &&
            native is { Scheme: "http", Host: "127.0.0.1", IsDefaultPort: false, PathAndQuery: "/", UserInfo.Length: 0, Fragment.Length: 0 } &&
            Uri.TryCreate(Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL"), UriKind.Absolute, out var configured) && native == configured)
        {
            return native;
        }
        throw new InvalidOperationException("Use make check-integration for the isolated MiniStack instance.");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static BasicAWSCredentials FixtureCredentials() => new(Account, "test-only-secret");
}
