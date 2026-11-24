using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.DryRun;
using Knightfall.ScaleSet.Aws.Core.Planning;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static class DryRunCommand
{
    private const string Usage = "Use reconcile --config <runtime.json> --dry-run --once [--sha256 <hex>] or " +
        "reconcile --config-s3-uri <s3://bucket/key> --config-version-id <version> --sha256 <hex> --dry-run --once.";
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, stdout, stderr, RuntimeArtifactLoader.CreateS3Client, null, null, timeProvider, cancellationToken);

    public static Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<RuntimeConfiguration, CancellationToken, Task<IReadOnlyDictionary<string, InventorySample>?>> inspectInventory,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, stdout, stderr, RuntimeArtifactLoader.CreateS3Client, null, inspectInventory, timeProvider, cancellationToken);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string?, IAmazonS3> createS3Client,
        Func<string, string?>? readEnvironment,
        Func<RuntimeConfiguration, CancellationToken, Task<IReadOnlyDictionary<string, InventorySample>?>>? inspectInventory,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        if (options is null)
        {
            await stderr.WriteLineAsync(Usage.AsMemory(), cancellationToken);
            return 2;
        }

        try
        {
            var artifact = options.ConfigPath is { } path
                ? RuntimeArtifact.FromLocal(path, options.ExpectedSha256)
                : RuntimeArtifact.FromS3(options.S3Uri!, options.VersionId!, options.ExpectedSha256!);
            var (configuration, telemetry) = await RuntimeInput.LoadAsync(
                artifact, null, createS3Client, readEnvironment, cancellationToken);
            await using var pipeline = TelemetryPipeline.Start(telemetry, stderr);
            var report = await CreateReportAsync(configuration, artifact, createS3Client, inspectInventory,
                timeProvider, cancellationToken);
            string output = JsonSerializer.Serialize(report, ReportJson) + "\n";
            await stdout.WriteAsync(output.AsMemory(), cancellationToken);
            return 0;
        }
        catch (RuntimeConfigurationException error)
        {
            await ConfigVerifyCommand.WriteErrorAsync(error, stderr, cancellationToken);
            return 1;
        }
        catch (Exception error) when (
            error is TelemetrySettingsException or DryRunEvidenceException)
        {
            await stderr.WriteLineAsync(error.Message.AsMemory(), cancellationToken);
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            AmazonServiceException or AmazonClientException)
        {
            await stderr.WriteLineAsync("The requested runtime or read-only evidence could not be loaded.".AsMemory(), cancellationToken);
            return 1;
        }
    }

    internal static async Task<DryRunReport> CreateReportAsync(
        RuntimeConfiguration configuration, RuntimeArtifact artifact, Func<string?, IAmazonS3> createS3Client,
        Func<RuntimeConfiguration, CancellationToken, Task<IReadOnlyDictionary<string, InventorySample>?>>? inspectInventory,
        TimeProvider? timeProvider, CancellationToken cancellationToken)
    {
        var evidence = await ReadEvidenceAsync(configuration, artifact.LocalPath, createS3Client, cancellationToken);
        IReadOnlyDictionary<string, InventorySample>? inventory = null;
        if (inspectInventory is not null && configuration.DryRun.InspectAws &&
            configuration.DryRun.Demand is NoDryRunDemandConfiguration)
        {
            inventory = await inspectInventory(configuration, cancellationToken);
        }
        return DryRunEngine.CreateReport(configuration, evidence, timeProvider, inventory);
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadEvidenceAsync(
        RuntimeConfiguration configuration,
        string? configPath,
        Func<string?, IAmazonS3> createS3Client,
        CancellationToken cancellationToken)
    {
        if (configuration.DryRun.Demand is S3SnapshotDemandConfiguration remote)
        {
            var artifact = VersionedS3Artifact.Parse(remote.S3Uri, remote.VersionId);
            return await RuntimeArtifactLoader.ReadS3Async(artifact, configuration.Aws.Region, createS3Client, cancellationToken);
        }

        string? path = configuration.DryRun.Demand switch
        {
            NoDryRunDemandConfiguration => null,
            LocalSnapshotDemandConfiguration snapshot => snapshot.Path,
            LocalReplayDemandConfiguration replay => replay.Path,
            _ => throw new InvalidOperationException("Unexpected offline demand configuration."),
        };

        if (path is null)
        {
            return null;
        }
        if (configPath is null)
        {
            throw new DryRunEvidenceException("Local demand evidence requires a local runtime file.");
        }

        string directory = Path.GetDirectoryName(configPath)!;
        string evidencePath = Path.GetFullPath(path, directory);
        return await File.ReadAllBytesAsync(evidencePath, cancellationToken);
    }

    private static Options? ParseOptions(string[] args)
    {
        if (args.Length == 0 || args[0] != "reconcile")
        {
            return null;
        }

        string? configPath = null;
        string? s3Uri = null;
        string? versionId = null;
        string? sha256 = null;
        bool dryRun = false;
        bool once = false;

        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config" when configPath is null && HasValue(args, index):
                    configPath = args[++index];
                    break;
                case "--config-s3-uri" when s3Uri is null && HasValue(args, index):
                    s3Uri = args[++index];
                    break;
                case "--config-version-id" when versionId is null && HasValue(args, index):
                    versionId = args[++index];
                    break;
                case "--sha256" when sha256 is null && HasValue(args, index):
                    sha256 = args[++index];
                    break;
                case "--dry-run" when !dryRun:
                    dryRun = true;
                    break;
                case "--once" when !once:
                    once = true;
                    break;
                default:
                    return null;
            }
        }

        if (!dryRun || !once)
        {
            return null;
        }
        bool local = configPath is not null && s3Uri is null && versionId is null;
        bool remote = configPath is null && s3Uri is not null && versionId is not null && sha256 is not null;
        return local || remote ? new Options(configPath, s3Uri, versionId, sha256) : null;
    }

    private static bool HasValue(string[] args, int index) =>
        index + 1 < args.Length && ConfigVerifyCommand.HasValue(args[index + 1]);

    private sealed record Options(string? ConfigPath, string? S3Uri, string? VersionId, string? ExpectedSha256);
}
