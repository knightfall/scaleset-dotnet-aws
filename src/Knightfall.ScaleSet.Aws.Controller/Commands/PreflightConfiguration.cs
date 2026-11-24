using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

internal sealed record PreflightConfiguration(string Path, string Digest)
{
    internal static PreflightConfiguration? Parse(string[] args, string operation)
    {
        if (args.Length != 6 || args[0] != "preflight" || args[1] != operation)
        {
            return null;
        }

        (string Path, string Digest)? values = (args[2], args[4]) switch
        {
            ("--config", "--sha256") => (args[3], args[5]),
            ("--sha256", "--config") => (args[5], args[3]),
            _ => null,
        };
        return values is { } supplied && !string.IsNullOrWhiteSpace(supplied.Path) &&
            !supplied.Path.StartsWith("--", StringComparison.Ordinal) && supplied.Digest.Length == 64 && supplied.Digest.All(Uri.IsHexDigit)
            ? new PreflightConfiguration(supplied.Path, supplied.Digest)
            : null;
    }

    internal Task<RuntimeConfiguration> LoadAsync(CancellationToken cancellationToken) =>
        RuntimeArtifactLoader.LoadAsync(RuntimeArtifact.FromLocal(Path, Digest), cancellationToken);
}
