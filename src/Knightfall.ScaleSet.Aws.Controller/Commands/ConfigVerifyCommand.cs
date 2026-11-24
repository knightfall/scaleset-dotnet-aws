using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static class ConfigVerifyCommand
{
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        string? path = null;
        string? sha256 = null;
        bool valid = false;
        if (args is ["config", "verify", ..] && args.Length is 4 or 6)
        {
            valid = true;
            for (int index = 2; index < args.Length; index += 2)
            {
                string value = args[index + 1];
                switch (args[index])
                {
                    case "--file" when path is null && HasValue(value):
                        path = value;
                        break;
                    case "--sha256" when sha256 is null && HasValue(value):
                        sha256 = value;
                        break;
                    default:
                        valid = false;
                        break;
                }
            }
        }

        if (!valid || path is null)
        {
            await stderr.WriteLineAsync("Use config verify --file <path> [--sha256 <hex>].".AsMemory(), cancellationToken);
            return 2;
        }

        try
        {
            var configuration = await RuntimeArtifactLoader.LoadAsync(RuntimeArtifact.FromLocal(path, sha256), cancellationToken);
            await stdout.WriteLineAsync(configuration.Sha256.AsMemory(), cancellationToken);
            return 0;
        }
        catch (RuntimeConfigurationException error)
        {
            await WriteErrorAsync(error, stderr, cancellationToken);
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await stderr.WriteLineAsync("The local runtime configuration could not be read.".AsMemory(), cancellationToken);
            return 1;
        }
    }

    internal static bool HasValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);

    internal static Task WriteErrorAsync(RuntimeConfigurationException error, TextWriter stderr, CancellationToken cancellationToken)
    {
        string path = error.Path.Length == 0 ? "/" : error.Path;
        return stderr.WriteLineAsync(($"{error.Code} {path} {error.Message}").AsMemory(), cancellationToken);
    }
}
