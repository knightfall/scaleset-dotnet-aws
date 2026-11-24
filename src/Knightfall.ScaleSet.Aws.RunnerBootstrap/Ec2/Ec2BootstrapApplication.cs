using System.Runtime.Versioning;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

[SupportedOSPlatform("linux")]
internal sealed class Ec2BootstrapApplication(Ec2BootstrapSettings settings, TimeProvider clock)
{
    internal async Task<RunnerBootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        settings.Validate();
        Ec2SpentMarker.VerifyDirectory(settings.SpentDirectory);
        var boundary = LinuxRunnerBoundary.Verify(settings.Runner);
        using var http = Ec2ImdsV2.CreateHttpClient();
        var metadata = new Ec2ImdsV2(http, Ec2ImdsV2.InstanceEndpoint, clock);
        var verifier = new Ec2BootstrapIdentity(metadata, credentials => Ec2BootstrapIdentity.CreateSts(credentials, settings.Authority.Region));
        var identity = await verifier.VerifyAsync(settings.Authority, cancellationToken).ConfigureAwait(false);

        using var dynamodb = CreateDynamoDb(identity.Credentials, settings.Authority.Region);
        var offers = new DynamoDbBootstrapOfferClient(dynamodb, settings.TableName, identity.AwsUserId, identity.ResourceArn, clock);
        var spent = new Ec2SpentMarker(settings.SpentDirectory, identity.ResourceArn);
        var bootstrap = new RunnerBootstrap(offers, settings.Target, identity.ResourceArn, identity.AwsUserId, spent.TryReserve,
            new LinuxOneJobRunner(boundary), clock, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2),
            ReportAsync, telemetryProvider: RunnerProvisionerType.Ec2);
        return await bootstrap.RunAsync(cancellationToken).ConfigureAwait(false);

        async Task ReportAsync(BootstrapRunnerObservation observation, CancellationToken token)
        {
            Console.WriteLine(observation.Exited ? "One-job runner exited." : "One-job runner connected.");
            if (!settings.PublishStatus)
            {
                return;
            }

            try
            {
                var current = await verifier.VerifyAsync(settings.Authority, token).ConfigureAwait(false);
                if (current.ResourceArn != identity.ResourceArn || current.AwsUserId != identity.AwsUserId)
                {
                    Console.Error.WriteLine("Bootstrap status identity changed.");
                    return;
                }
                using var statusDynamoDb = CreateDynamoDb(current.Credentials, settings.Authority.Region);
                var status = new DynamoDbEc2BootstrapStatusClient(statusDynamoDb, settings.TableName, current.AwsUserId,
                    current.ResourceArn, clock);
                var outcome = await status.PublishAsync(observation.Hello,
                    new JitEc2BootstrapStatus(observation.OfferId, observation.RunnerId, observation.Exited ? 2 : 1,
                        observation.Exited, clock.GetUtcNow()), token).ConfigureAwait(false);
                if (outcome != LifecycleWriteOutcome.Committed)
                {
                    Console.Error.WriteLine("Bootstrap status was not confirmed.");
                }
            }
            catch (Exception error) when (error is IOException or AmazonClientException or AmazonServiceException or HttpRequestException)
            {
                Console.Error.WriteLine("Bootstrap status was unavailable.");
            }
        }
    }

    private static AmazonDynamoDBClient CreateDynamoDb(SessionAWSCredentials credentials, string region) =>
        new(credentials, new AmazonDynamoDBConfig
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region),
            IgnoreConfiguredEndpointUrls = true,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
            Timeout = TimeSpan.FromSeconds(5),
            HttpClientFactory = new BootstrapHttpFactory(),
        });
}

internal sealed record Ec2BootstrapSettings(
    string TableName, Ec2BootstrapAuthority Authority, RunnerBootstrapTarget Target, string SpentDirectory,
    LinuxRunnerSettings Runner, bool PublishStatus = false)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(TableName) || Authority is null || Target is null || Runner is null ||
            string.IsNullOrWhiteSpace(SpentDirectory) || string.IsNullOrWhiteSpace(Runner.RuntimeRoot) ||
            string.IsNullOrWhiteSpace(Runner.BoundaryProof) || Runner.ListenerSha256 is not { Length: 64 } ||
            !Runner.ListenerSha256.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            string.IsNullOrWhiteSpace(Target.ScopeIdentity) || string.IsNullOrWhiteSpace(Target.ProfileId) ||
            string.IsNullOrWhiteSpace(Target.Deployment) || Target.ScaleSetId is <= 0 ||
            !Target.Deployment.StartsWith(Authority.Account + ":" + Authority.Region + ":", StringComparison.Ordinal))
        {
            throw new ArgumentException("The EC2 bootstrap settings are incomplete.");
        }
        Authority.Validate();
    }
}
