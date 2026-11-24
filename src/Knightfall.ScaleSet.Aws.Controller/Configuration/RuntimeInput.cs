using Amazon.S3;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

public static class RuntimeInput
{
    public static Task<(RuntimeConfiguration Configuration, TelemetrySettings Telemetry)> LoadAsync(
        RuntimeArtifact artifact, string? otlpEndpoint, CancellationToken cancellationToken) =>
        LoadAsync(artifact, otlpEndpoint, RuntimeArtifactLoader.CreateS3Client, null, cancellationToken);

    internal static async Task<(RuntimeConfiguration Configuration, TelemetrySettings Telemetry)> LoadAsync(
        RuntimeArtifact artifact, string? otlpEndpoint, Func<string?, IAmazonS3> createS3Client,
        Func<string, string?>? readEnvironment, CancellationToken cancellationToken)
    {
        var telemetry = TelemetrySettings.Load(otlpEndpoint, readEnvironment: readEnvironment);
        var configuration = await RuntimeArtifactLoader.LoadAsync(artifact, createS3Client, cancellationToken);
        return (configuration, telemetry.WithRuntimeConfiguration(configuration));
    }
}
