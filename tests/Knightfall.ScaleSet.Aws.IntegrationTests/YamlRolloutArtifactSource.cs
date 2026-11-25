using System.Diagnostics;
using System.Globalization;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

/// <summary>Emits authored YAML and reads the exact uploaded runtime version back through the production loader.</summary>
internal sealed class YamlRolloutArtifactSource(DirectoryInfo repository, DirectoryInfo workspace, Uri endpoint, string bucket)
    : IDisposable
{
    internal const string Account = "000000000000";
    internal const string Region = "us-east-1";
    private const string Key = "runtime/controller.json";

    internal static async Task<YamlRolloutArtifactSource> CreateAsync(Uri endpoint, CancellationToken token)
    {
        string bucket = "t25-rollout-" + Guid.NewGuid().ToString("N");
        var workspace = Directory.CreateTempSubdirectory("scaleset-yaml-rollout-");
        try
        {
            var fixture = new YamlRolloutArtifactSource(FindRepository(), workspace, endpoint, bucket);
            string version = (await File.ReadAllTextAsync(Path.Combine(fixture.Repository, ".node-version"), token)).Trim();
            Assert.Equal("v" + version, await fixture.NodeAsync(["--version"], token));
            using var s3 = fixture.CreateS3();
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, token);
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            }, token);
            return fixture;
        }
        catch
        {
            workspace.Delete(recursive: true);
            throw;
        }
    }

    internal async Task<PublishedYaml[]> PublishAsync(string table, string githubSecretArn, CancellationToken token)
    {
        string fixture = (await File.ReadAllTextAsync(Path.Combine(Repository, "testdata", "config", "mixed-ec2-fargate.yaml"), token))
            .ReplaceLineEndings("\n");
        const string ec2Profile = "    - id: linux-ec2-build\n";
        const string shellProfile = "    - id: linux-shell\n";
        const string armProfile = "    - id: linux-ci-arm\n";
        string shared = fixture[..fixture.IndexOf("    - id: linux-ec2-docker\n", StringComparison.Ordinal)]
            .Replace("111122223333", Account, StringComparison.Ordinal)
            .Replace("ap-southeast-2", Region, StringComparison.Ordinal)
            .Replace("name: engineering-dev\n", $"name: {bucket}\n", StringComparison.Ordinal)
            .Replace("      type: create\n  networks:", $"      type: import\n      name: {table}\n  networks:", StringComparison.Ordinal)
            .Replace("https://github.com/example-engineering", "https://github.com/enterprises/engineering", StringComparison.Ordinal)
            .Replace("type: github-app", "type: enterprise-pat", StringComparison.Ordinal)
            .Replace($"arn:aws:secretsmanager:{Region}:{Account}:secret:github-app-engineering-a1b2c3", githubSecretArn, StringComparison.Ordinal);
        int ec2 = fixture.IndexOf(ec2Profile, StringComparison.Ordinal);
        int shell = fixture.IndexOf(shellProfile, StringComparison.Ordinal);
        int arm = fixture.IndexOf(armProfile, StringComparison.Ordinal);
        string Normalize(string text) => text.Replace("111122223333", Account, StringComparison.Ordinal)
            .Replace("ap-southeast-2", Region, StringComparison.Ordinal);
        string original = Normalize(fixture[ec2..shell]);
        original += """
                statusQualification:
                  imageId: ami-0123456789abcdef0
                  roleId: AROAFIXTUREBOOTSTRAP01
                  evidenceSha256: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
        """ + "\n";
        string adjusted = original.Replace("maxRunners: 16", "maxRunners: 18", StringComparison.Ordinal);
        string added = Normalize(fixture[shell..arm]);
        string draining = added.Replace("state: active", "state: draining", StringComparison.Ordinal);
        string[] variants = [original, adjusted + added, adjusted + draining, adjusted];

        using var s3 = CreateS3();
        var emitted = new List<Emitted>();
        for (int index = 0; index < variants.Length; index++)
        {
            string number = (index + 1).ToString(CultureInfo.InvariantCulture);
            string yaml = Path.Combine(workspace.FullName, $"generation-{number}.yaml");
            string runtime = Path.Combine(workspace.FullName, $"emitted-{number}.json");
            await File.WriteAllTextAsync(yaml, shared + variants[index], token);
            string digest = await NodeAsync(["cdk/bin/emit-runtime.ts", yaml, runtime], token);
            Assert.Matches("^[0-9a-f]{64}$", digest);
            var uploaded = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = Key, FilePath = runtime, ContentType = "application/json",
            }, token);
            var artifact = RuntimeArtifact.FromS3($"s3://{bucket}/{Key}", uploaded.VersionId, digest);
            emitted.Add(new Emitted(artifact, runtime, digest));
        }
        Assert.Equal(variants.Length, emitted.Select(item => item.Artifact.S3!.VersionId).Distinct(StringComparer.Ordinal).Count());

        var published = new List<PublishedYaml>();
        foreach (var item in emitted)
        {
            var configuration = await RuntimeArtifactLoader.LoadAsync(item.Artifact, _ => CreateS3(), token);
            Assert.Equal(item.Digest, configuration.Sha256);
            byte[] pinned = await RuntimeArtifactLoader.ReadS3Async(item.Artifact.S3!, Region, _ => CreateS3(), token);
            Assert.Equal(await File.ReadAllBytesAsync(item.LocalPath, token), pinned);
            string verified = item.LocalPath.Replace("emitted-", "readback-", StringComparison.Ordinal);
            await File.WriteAllBytesAsync(verified, pinned, token);
            published.Add(new PublishedYaml(configuration, verified));
        }
        return [.. published];
    }

    private string Repository => repository.FullName;

    private AmazonS3Client CreateS3()
    {
        var config = ControllerAwsClientConfiguration.Create<AmazonS3Config>(Region, endpoint);
        config.ForcePathStyle = true;
        return new AmazonS3Client(new BasicAWSCredentials(Account, "test-only-secret"), config);
    }

    private async Task<string> NodeAsync(IEnumerable<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = Repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The pinned Node.js emitter could not be started.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, $"Build the pinned YAML emitter dependencies with npm --prefix cdk ci. {await errors}");
            return (await output).Trim();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public void Dispose() => workspace.Delete(recursive: true);

    private static DirectoryInfo FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "cdk", "bin", "emit-runtime.ts")))
            {
                return directory;
            }
        }
        throw new InvalidOperationException("The rollout integration suite requires the repository's YAML emitter.");
    }

    private sealed record Emitted(RuntimeArtifact Artifact, string LocalPath, string Digest);
}

internal sealed record PublishedYaml(RuntimeConfiguration Configuration, string ReadbackPath);
