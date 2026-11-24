using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;

[SupportedOSPlatform("linux")]
internal static class FargateBootstrapCommand
{
    internal static async Task<int> RunAsync(string settingsPath)
    {
        using var stopping = new CancellationTokenSource();
        using var stop = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });
        try
        {
            if (LinuxFiles.IsRoot() || !LinuxFiles.IsTrustedFile(settingsPath, ownerOnly: false))
            {
                throw new IOException("The Fargate bootstrap settings file or user is invalid.");
            }
            LinuxFiles.DisableCoreDumps();
            LinuxFiles.ProhibitPrivilegeGain();
            var settings = ReadSettings(settingsPath);
            _ = settings.Validate();

            string? metadataEndpoint = Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4");
            _ = FargateBootstrapIdentity.ParseMetadataEndpoint(metadataEndpoint);
            if (!FargateBootstrapIdentity.IsTaskCredentialsUri(Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE")))
            {
                throw new InvalidDataException("The Fargate task credential source is invalid.");
            }

            using var boundary = FargateRunnerBoundary.Create(settings);
            using var metadata = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                MaxResponseHeadersLength = 16,
            }) { Timeout = Timeout.InfiniteTimeSpan };
            var taskCredentials = new GenericContainerCredentials();
            var region = RegionEndpoint.GetBySystemName(settings.Region);
            var transport = new BootstrapHttpFactory();
            using var sts = new AmazonSecurityTokenServiceClient(taskCredentials, Configure(new AmazonSecurityTokenServiceConfig(), region, transport));
            using var ddb = new AmazonDynamoDBClient(taskCredentials, Configure(new AmazonDynamoDBConfig(), region, transport));
            var application = new FargateBootstrapApplication(settings, TimeProvider.System);
            var runner = new LinuxOneJobRunner(boundary, stopGrace: TimeSpan.FromSeconds(20));
            var outcome = await application.RunAsync(metadataEndpoint, metadata, sts, ddb, runner, stopping.Token)
                .ConfigureAwait(false);
            Console.WriteLine(outcome);
            return outcome == RunnerBootstrapOutcome.Completed ? 0 : 1;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Console.Error.WriteLine("Fargate bootstrap stopped.");
            return 1;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or ArgumentException or JsonException or Win32Exception or
                                      AmazonClientException or AmazonServiceException or InvalidOperationException or UnauthorizedAccessException or
                                      OperationCanceledException)
        {
            Console.Error.WriteLine("Fargate bootstrap failed.");
            return 1;
        }
    }

    private static FargateBootstrapSettings ReadSettings(string path)
    {
        using var document = File.OpenRead(path);
        if (document.Length is <= 0 or > 16 * 1024)
        {
            throw new IOException("The Fargate bootstrap settings file size is invalid.");
        }
        return JsonSerializer.Deserialize<FargateBootstrapSettings>(document, BootstrapJson.Options) ??
            throw new IOException("The Fargate bootstrap settings file is empty.");
    }

    private static T Configure<T>(T configuration, RegionEndpoint region, BootstrapHttpFactory transport) where T : ClientConfig
    {
        configuration.RegionEndpoint = region;
        configuration.MaxErrorRetry = 0;
        configuration.MaxStaleConnectionRetries = 0;
        configuration.DisableClockSkewCorrection = true;
        configuration.AllowAutoRedirect = false;
        configuration.IgnoreConfiguredEndpointUrls = true;
        configuration.HttpClientFactory = transport;
        return configuration;
    }
}
