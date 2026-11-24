using System.Text.Json;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.DryRun;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static class DryRunCommand
{
    private const string Usage = "Use reconcile --config <runtime.json> --dry-run --once [--sha256 <hex>].";
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var options = ParseOptions(args);
        if (options is null)
        {
            await stderr.WriteLineAsync(Usage.AsMemory(), cancellationToken);
            return 2;
        }

        try
        {
            string configPath = Path.GetFullPath(options.ConfigPath);
            byte[] json = await File.ReadAllBytesAsync(configPath, cancellationToken);
            var configuration = RuntimeConfigurationLoader.Load(json, options.ExpectedSha256);

            if (configuration.DryRun.Demand is S3SnapshotDemandConfiguration)
            {
                await stderr.WriteLineAsync(
                    "Offline reconcile requires a local demand snapshot file. S3 snapshots require the S3-enabled read-only runtime."
                        .AsMemory(),
                    cancellationToken);
                return 1;
            }

            var evidence = await ReadEvidenceAsync(configuration.DryRun.Demand, configPath, cancellationToken);
            var report = DryRunEngine.CreateReport(configuration, evidence, timeProvider);
            string output = JsonSerializer.Serialize(report, ReportJson) + "\n";
            await stdout.WriteAsync(output.AsMemory(), cancellationToken);
            return 0;
        }
        catch (RuntimeConfigurationException error)
        {
            string path = error.Path.Length == 0 ? "/" : error.Path;
            await stderr.WriteLineAsync(($"{error.Code} {path} {error.Message}").AsMemory(), cancellationToken);
            return 1;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or DryRunEvidenceException)
        {
            await stderr.WriteLineAsync(error.Message.AsMemory(), cancellationToken);
            return 1;
        }
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadEvidenceAsync(
        DryRunDemandConfiguration demand,
        string configPath,
        CancellationToken cancellationToken)
    {
        string? path = demand switch
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

        return configPath is not null && dryRun && once ? new Options(configPath, sha256) : null;
    }

    private static bool HasValue(string[] args, int index) =>
        index + 1 < args.Length &&
        !string.IsNullOrWhiteSpace(args[index + 1]) &&
        !args[index + 1].StartsWith("--", StringComparison.Ordinal);

    private sealed record Options(string ConfigPath, string? ExpectedSha256);
}
