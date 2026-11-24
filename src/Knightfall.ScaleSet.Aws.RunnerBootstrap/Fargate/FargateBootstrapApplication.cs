using Amazon.DynamoDBv2;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;

internal sealed record FargateBootstrapSettings(
    string Account,
    string Region,
    string Deployment,
    string ProfileId,
    string GitHubScope,
    string TableName,
    string ClusterArn,
    string TaskFamily,
    string ContainerName,
    string TaskRoleArn,
    string TaskRoleId,
    string RunnerDistribution,
    string RunnerDotnet,
    string RunnerChild,
    string WorkScratch,
    long? ScaleSetId = null)
{
    internal RunnerBootstrapTarget Validate()
    {
        var profile = new LifecycleProfile(Deployment, ProfileId);
        var cluster = ClusterArn.Split(':', 6);
        if (Account.Length != 12 || !Account.All(char.IsAsciiDigit) ||
            Region.Length is < 4 or > 32 || Region.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-') ||
            !profile.Deployment.StartsWith(Account + ":" + Region + ":", StringComparison.Ordinal) ||
            cluster is not ["arn", var partition, "ecs", var region, var account, var resource] || region != Region || account != Account ||
            (partition != "aws" && !partition.StartsWith("aws-", StringComparison.Ordinal)) ||
            partition.Any(character => !char.IsAsciiLetterLower(character) && character != '-') ||
            !resource.StartsWith("cluster/", StringComparison.Ordinal) || !IsEcsName(resource["cluster/".Length..]) ||
            !IsEcsName(TaskFamily) || !IsEcsName(ContainerName) ||
            !TaskRoleArn.StartsWith($"arn:{partition}:iam::{Account}:role/", StringComparison.Ordinal) ||
            !IsRolePath(TaskRoleArn.Split(':', 6)[5]["role/".Length..]) ||
            TaskRoleId.Length is < 16 or > 128 || !TaskRoleId.StartsWith("AROA", StringComparison.Ordinal) ||
            TaskRoleId.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)) ||
            TableName.Length is < 3 or > 255 || TableName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) ||
            ScaleSetId is <= 0)
        {
            throw new InvalidDataException("The Fargate bootstrap settings are invalid.");
        }
        return new RunnerBootstrapTarget(profile.Deployment, profile.ProfileId, GitHubRoutingIdentity.Scope(GitHubScope), ScaleSetId);
    }

    internal FargateIdentityExpectation Identity =>
        new(Account, Region, ClusterArn, TaskFamily, ContainerName, TaskRoleArn, TaskRoleId);

    private static bool IsEcsName(string value) =>
        value.Length is > 0 and <= 255 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsRolePath(string value) =>
        value.Length is > 0 and <= 512 && value.Split('/').All(segment => segment.Length > 0 &&
            segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '+' or '=' or ',' or '@'));
}

internal sealed class FargateBootstrapApplication(FargateBootstrapSettings settings, TimeProvider clock)
{
    private int _spent;

    internal async Task<RunnerBootstrapOutcome> RunAsync(string? metadataEndpoint, HttpClient metadata,
        IAmazonSecurityTokenService sts, IAmazonDynamoDB ddb, IOneJobRunner runner, CancellationToken cancellationToken)
    {
        var target = settings.Validate();
        var principal = await FargateBootstrapIdentity.ReadAsync(metadataEndpoint, metadata, sts, settings.Identity, clock, cancellationToken)
            .ConfigureAwait(false);
        var offers = new DynamoDbBootstrapOfferClient(ddb, settings.TableName, principal.AwsUserId, principal.TaskArn, clock);
        var bootstrap = new RunnerBootstrap(offers, target, principal.TaskArn, principal.AwsUserId,
            () => Interlocked.Exchange(ref _spent, 1) == 0, runner, clock, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1),
            validateProviderBinding: principal.MatchesOffer, telemetryProvider: RunnerProvisionerType.Fargate);
        return await bootstrap.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
