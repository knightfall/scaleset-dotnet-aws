using System.Globalization;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class DryRunCommandTests
{
    public static TheoryData<string[]> InvalidArguments { get; } = new()
    {
        Array.Empty<string>(),
        new[] { "run", "--config", "runtime.json", "--dry-run", "--once" },
        new[] { "reconcile", "--config", "runtime.json", "--dry-run" },
        new[] { "reconcile", "--config", "runtime.json", "--once" },
        new[] { "reconcile", "--dry-run", "--once", "--config" },
        new[] { "reconcile", "--dry-run", "--once", "--config", "--sha256", "digest" },
        new[] { "reconcile", "--dry-run", "--once", "--config", "runtime.json", "--once" },
        new[] { "reconcile", "--dry-run", "--once", "--config-s3-uri", "s3://runtime-bucket/runtime.json" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task RequiresTheExplicitOfflineOneShotCommand(string[] args)
    {
        var result = await RunAsync(args);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("reconcile --config <runtime.json> --dry-run --once", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesTheSameAdvisoryJsonForTheSameInputs()
    {
        using var files = new TemporaryDirectory();
        var path = files.Write("runtime.json", ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed));
        string[] args = ["reconcile", "--once", "--dry-run", "--config", path];

        var first = await RunAsync(args);
        var second = await RunAsync(args);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first, second);
        Assert.Empty(first.Error);
        Assert.EndsWith("\n", first.Output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(first.Output);
        var report = document.RootElement;
        Assert.Equal("RunnerDryRunReport", report.GetProperty("kind").GetString());
        Assert.Equal("none", report.GetProperty("demandSource").GetString());
        Assert.False(report.GetProperty("inspection").GetProperty("performed").GetBoolean());
        Assert.All(report.GetProperty("profiles").EnumerateArray(), profile =>
        {
            var capacity = profile.GetProperty("capacity");
            Assert.Equal("unknown", capacity.GetProperty("demandStatus").GetString());
            Assert.Equal(0, capacity.GetProperty("proposedStarts").GetInt32());
        });
    }

    [Theory]
    [InlineData("snapshot", "RunnerDemandSnapshot")]
    [InlineData("replay", "RunnerDemandReplay")]
    public async Task ReadsEvidenceRelativeToRuntimeAndReportsOfflineInspection(string source, string kind)
    {
        using var files = new TemporaryDirectory();
        var config = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Ec2Latest,
            ConfigurationEdit.Set("/spec/dryRun/demand", new { type = source, path = "../evidence/demand.json" }));
        var path = files.Write("config/runtime.json", config);
        files.Write("evidence/demand.json", Encoding.UTF8.GetBytes($$"""
            {
              "apiVersion": "scaleset-aws/v1alpha1",
              "kind": "{{kind}}",
              "environment": "engineering-dev",
              "aws": { "account": "111122223333", "region": "ap-southeast-2" },
              "records": [{
                "profileId": "linux-ec2-docker",
                "scopeURL": "https://github.com/example-engineering",
                "runnerGroup": "Linux-EC2",
                "scaleSet": "linux-ec2-docker",
                "observedAt": "2026-01-01T00:00:00Z",
                "demand": { "totalAssignedJobs": 5 },
                "inventory": { "isComplete": true, "slots": [], "backendLaunchHeadroom": 3 }
              }]
            }
            """));
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 3, 0, TimeSpan.Zero));
        string digest = ConfigurationFixtures.Sha256(config);

        var result = await RunAsync(["reconcile", "--config", path, "--dry-run", "--once", "--sha256", digest], clock);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var report = document.RootElement;
        Assert.Equal(digest, report.GetProperty("runtimeSha256").GetString());
        Assert.Equal(source, report.GetProperty("demandSource").GetString());
        var inspection = report.GetProperty("inspection");
        Assert.True(inspection.GetProperty("requested").GetBoolean());
        Assert.False(inspection.GetProperty("performed").GetBoolean());
        Assert.Equal("offline", inspection.GetProperty("reason").GetString());
        var profile = Assert.Single(report.GetProperty("profiles").EnumerateArray());
        Assert.Equal("linux-ec2-docker", profile.GetProperty("profileId").GetString());
        Assert.Equal("ec2", profile.GetProperty("provisioner").GetString());
        var capacity = profile.GetProperty("capacity");
        Assert.Equal("fresh", capacity.GetProperty("demandStatus").GetString());
        Assert.Equal(5, capacity.GetProperty("target").GetInt32());
        Assert.Equal(3, capacity.GetProperty("proposedStarts").GetInt32());
        Assert.Equal(source == "replay" ? 1 : 0, report.GetProperty("replay").GetArrayLength());
    }

    [Fact]
    public async Task MalformedEvidenceProducesAnErrorWithoutAReport()
    {
        using var files = new TemporaryDirectory();
        var config = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Ec2Pinned,
            ConfigurationEdit.Set("/spec/dryRun/demand", new { type = "snapshot", path = "invalid.json" }));
        var path = files.Write("runtime.json", config);
        files.Write("invalid.json", "{"u8.ToArray());

        var result = await RunAsync(["reconcile", "--config", path, "--dry-run", "--once"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Demand evidence must be valid UTF-8 JSON", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifiesDigestBeforeReadingTheConfiguredEvidenceFile()
    {
        using var files = new TemporaryDirectory();
        var config = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Ec2Pinned,
            ConfigurationEdit.Set("/spec/dryRun/demand", new { type = "snapshot", path = "missing.json" }));
        var path = files.Write("runtime.json", config);

        var result = await RunAsync(
            ["reconcile", "--config", path, "--dry-run", "--once", "--sha256", new string('0', 64)]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.StartsWith("digest / ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("missing.json", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecognizesVersionPinnedS3DemandAsUnavailableOffline()
    {
        using var files = new TemporaryDirectory();
        var config = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Ec2Pinned,
            ConfigurationEdit.Set("/spec/dryRun/demand", new
            {
                type = "snapshot",
                s3Uri = "s3://runner-snapshots/demand.json",
                versionId = "version-123",
                sha256 = new string('a', 64),
            }));
        var path = files.Write("runtime.json", config);

        var result = await RunAsync(
            ["reconcile", "--config", path, "--dry-run", "--once", "--sha256", ConfigurationFixtures.Sha256(config)]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("S3 snapshots require", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("runner-snapshots", result.Error, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] args,
        TimeProvider? timeProvider = null)
    {
        await using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        await using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = await DryRunCommand.RunAsync(args, stdout, stderr, timeProvider, TestContext.Current.CancellationToken);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("scaleset-dry-run-");

        public string Write(string path, byte[] bytes)
        {
            var target = Path.Combine(_directory.FullName, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            return target;
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
