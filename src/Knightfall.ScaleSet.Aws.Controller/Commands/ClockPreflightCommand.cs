using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static class ClockPreflightCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        var artifact = PreflightConfiguration.Parse(args, "clock");
        if (artifact is null)
        {
            await stderr.WriteLineAsync("Use preflight clock --config <runtime.json> --sha256 <hex>.".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return 2;
        }

        try
        {
            var configuration = await artifact.LoadAsync(cancellationToken).ConfigureAwait(false);
            using var metadata = new EcsControllerMetadata(configuration, TimeProvider.System);
            if (await metadata.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                await stdout.WriteLineAsync("Controller clock preflight verified.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or RuntimeConfigurationException or ArgumentException)
        {
            // Report the unverified preflight without printing metadata, endpoint details or configuration content.
        }

        await stderr.WriteLineAsync("Controller clock preflight could not verify a fresh synchronized ECS task clock.".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        return 1;
    }
}
