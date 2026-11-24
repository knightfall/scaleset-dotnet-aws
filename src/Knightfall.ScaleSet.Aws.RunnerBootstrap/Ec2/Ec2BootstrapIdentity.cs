using System.Net;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

internal sealed record Ec2BootstrapAuthority(string Account, string Region, string RoleArn, string RoleId)
{
    internal string Partition => RoleArn.Split(':', 6)[1];

    internal string RoleName => RoleArn[(RoleArn.LastIndexOf('/') + 1)..];

    internal void Validate()
    {
        var arn = RoleArn?.Split(':', 6) ?? [];
        if (arn.Length != 6 || arn[0] != "arn" || arn[1] is not ("aws" or "aws-us-gov" or "aws-cn") ||
            arn[2] != "iam" || arn[3].Length != 0 || arn[4] != Account || !arn[5].StartsWith("role/", StringComparison.Ordinal) ||
            !Ec2BootstrapIdentity.AccountPattern().IsMatch(Account) || !Ec2BootstrapIdentity.RegionPattern().IsMatch(Region) ||
            !Ec2BootstrapIdentity.RoleNamePattern().IsMatch(RoleName) || !Ec2BootstrapIdentity.RoleIdPattern().IsMatch(RoleId))
        {
            throw new ArgumentException("The EC2 bootstrap authority is invalid.");
        }
    }
}

internal sealed record VerifiedEc2BootstrapIdentity(string ResourceArn, string AwsUserId, SessionAWSCredentials Credentials)
{
    public override string ToString() => nameof(VerifiedEc2BootstrapIdentity);
}

internal sealed partial class Ec2BootstrapIdentity(Ec2ImdsV2 imds, Func<SessionAWSCredentials, IAmazonSecurityTokenService> createSts)
{
    internal async Task<VerifiedEc2BootstrapIdentity> VerifyAsync(Ec2BootstrapAuthority authority, CancellationToken cancellationToken)
    {
        authority.Validate();
        var metadata = await imds.ReadAsync(authority.RoleName, cancellationToken).ConfigureAwait(false);
        var instance = metadata.Document;
        if (instance.Account != authority.Account || instance.Region != authority.Region ||
            !InstanceIdPattern().IsMatch(instance.InstanceId))
        {
            throw InvalidIdentity();
        }

        string principal = authority.RoleId + ":" + instance.InstanceId;
        string callerArn = $"arn:{authority.Partition}:sts::{authority.Account}:assumed-role/{authority.RoleName}/{instance.InstanceId}";
        try
        {
            using var sts = createSts(metadata.Credentials);
            var response = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken).ConfigureAwait(false);
            if (response.HttpStatusCode != HttpStatusCode.OK || response.Account != authority.Account ||
                response.Arn != callerArn || response.UserId != principal)
            {
                throw InvalidIdentity();
            }
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException or TimeoutException ||
                                      (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw InvalidIdentity();
        }

        string resource = $"arn:{authority.Partition}:ec2:{authority.Region}:{authority.Account}:instance/{instance.InstanceId}";
        return new VerifiedEc2BootstrapIdentity(resource, principal, metadata.Credentials);
    }

    internal static AmazonSecurityTokenServiceClient CreateSts(SessionAWSCredentials credentials, string region) =>
        new(credentials, new AmazonSecurityTokenServiceConfig
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region),
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
            IgnoreConfiguredEndpointUrls = true,
            HttpClientFactory = new BootstrapHttpFactory(),
            Timeout = TimeSpan.FromSeconds(5),
        });

    [GeneratedRegex(@"\A[0-9]{12}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex AccountPattern();

    [GeneratedRegex(@"\A[a-z]{2}(?:-[a-z]+)+-[0-9]+\z", RegexOptions.CultureInvariant)]
    internal static partial Regex RegionPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_+=,.@-]{1,64}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex RoleNamePattern();

    [GeneratedRegex(@"\AAROA[A-Z0-9]{12,124}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex RoleIdPattern();

    [GeneratedRegex(@"\Ai-(?:[a-f0-9]{8}|[a-f0-9]{17})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceIdPattern();

    private static IOException InvalidIdentity() => new("EC2 bootstrap role and instance identity could not be verified.");
}
