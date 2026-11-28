using System.Buffers;
using System.Runtime.Versioning;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

[SupportedOSPlatform("linux")]
internal sealed class Ec2BootstrapApplication(Ec2BootstrapSettings settings, TimeProvider clock, ILogger? logger = null)
{
    internal async Task<RunnerBootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        settings.Validate();
        Ec2SpentMarker.VerifyDirectory(settings.SpentDirectory);
        var qualification = settings.StatusQualification;
        var boundary = LinuxRunnerBoundary.Verify(settings.Runner, qualification?.EvidenceSha256);
        using var http = Ec2ImdsV2.CreateHttpClient();
        var metadata = new Ec2ImdsV2(http, Ec2ImdsV2.InstanceEndpoint, clock);
        var verifier = new Ec2BootstrapIdentity(metadata, credentials => Ec2BootstrapIdentity.CreateSts(credentials, settings.Authority.Region));
        var identity = await verifier.VerifyAsync(settings.Authority, cancellationToken, qualification?.ImageId).ConfigureAwait(false);

        using var dynamodb = CreateDynamoDb(identity.Credentials, settings.Authority.Region);
        using var helloTransport = HelloEndpointRegistration.CreateTransport();
        var registration = new HelloEndpointRegistration(helloTransport, settings.HelloEndpoint, settings.Authority.Region,
            identity.Credentials, clock);
        var offers = new DynamoDbBootstrapOfferClient(dynamodb, settings.TableName, identity.AwsUserId, identity.ResourceArn, clock);
        var spent = new Ec2SpentMarker(settings.SpentDirectory, identity.ResourceArn);
        var reporter = qualification is null ? null : new Ec2BootstrapStatusReporter(identity, qualification.EvidenceSha256, clock,
            token => verifier.VerifyAsync(settings.Authority, token, qualification.ImageId), PublishStatusAsync);
        await using var logQueue = BootstrapLogQueue.Start(logger, clock);
        var diagnosticsLogger = logQueue ?? logger;
        var bootstrap = new RunnerBootstrap(offers, settings.Target, identity.ResourceArn, identity.AwsUserId, spent.TryReserve,
            new LinuxOneJobRunner(boundary), clock, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2),
            registration.RegisterAsync, ReportAsync, validateProviderBinding: null, RunnerProvisionerType.Ec2, diagnosticsLogger);
        return await bootstrap.RunAsync(cancellationToken).ConfigureAwait(false);

        async Task ReportAsync(BootstrapRunnerObservation observation, CancellationToken token)
        {
            BootstrapLog.Write(diagnosticsLogger, observation.Exited ? BootstrapLog.RunnerExited : BootstrapLog.RunnerConnected);
            if (reporter is not null &&
                await reporter.ReportAsync(observation, token).ConfigureAwait(false) != LifecycleWriteOutcome.Committed)
            {
                BootstrapLog.Write(diagnosticsLogger, BootstrapLog.StatusUnconfirmed);
            }
        }

        async Task<LifecycleWriteOutcome> PublishStatusAsync(VerifiedEc2BootstrapIdentity current, JitHello hello,
            JitEc2BootstrapStatus status, CancellationToken token)
        {
            using var statusDynamoDb = CreateDynamoDb(current.Credentials, settings.Authority.Region);
            var client = new DynamoDbEc2BootstrapStatusClient(statusDynamoDb, settings.TableName, current.AwsUserId,
                current.ResourceArn, clock);
            return await client.PublishAsync(hello, status, token).ConfigureAwait(false);
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
    LinuxRunnerSettings Runner, bool PublishStatus = false, Ec2StatusQualification? StatusQualification = null,
    string? HelloEndpoint = null)
{
    private static readonly SearchValues<char> LowercaseHex = SearchValues.Create("0123456789abcdef");

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
        if (PublishStatus != (StatusQualification is not null) ||
            (StatusQualification is { } qualification &&
             (qualification.RoleId != Authority.RoleId || !IsImageId(qualification.ImageId) ||
              qualification.EvidenceSha256 is not { Length: 64 } ||
              qualification.EvidenceSha256.AsSpan().IndexOfAnyExcept(LowercaseHex) >= 0)))
        {
            throw new ArgumentException("EC2 supervisor STATUS requires a matching image and role qualification.");
        }
    }

    private static bool IsImageId(string? id) => id is { Length: 12 or 21 } && id.StartsWith("ami-", StringComparison.Ordinal) &&
        id.AsSpan(4).IndexOfAnyExcept(LowercaseHex) < 0;
}
