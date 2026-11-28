using System.Collections.Immutable;
using Amazon.EC2;
using Amazon.EC2.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

/// <summary>Read-only EC2 API calls for Fargate subnet and security-group inspection.</summary>
public sealed class FargateNetworkReader
{
    private readonly IAmazonEC2 _client;

    public FargateNetworkReader(IAmazonEC2 client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    internal string? Region => _client.Config.RegionEndpoint?.SystemName ?? _client.Config.AuthenticationRegion;

    internal async Task<(ImmutableArray<FargateSubnet> Subnets, DateTimeOffset SubnetSampleStartedAt)> ReadAsync(
        NetworkConfiguration network, string account, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (network.Subnets.Length is 0 or > 16 || network.SecurityGroupIds.Length is 0 or > 5)
        {
            return default;
        }
        var subnetSampleStartedAt = clock.GetUtcNow();
        var subnets = await _client.DescribeSubnetsAsync(new DescribeSubnetsRequest
        {
            SubnetIds = network.Subnets.Select(subnet => subnet.Id).ToList(),
        }, cancellationToken).ConfigureAwait(false);
        if (subnets.Subnets?.Count != network.Subnets.Length || !string.IsNullOrEmpty(subnets.NextToken))
        {
            return default;
        }
        var byId = new Dictionary<string, Subnet>(StringComparer.Ordinal);
        foreach (var subnet in subnets.Subnets)
        {
            if (subnet is not { State.Value: "available", SubnetId: not null, MapPublicIpOnLaunch: false } || subnet.VpcId != network.VpcId ||
                subnet.AvailableIpAddressCount < 0 || !byId.TryAdd(subnet.SubnetId, subnet))
            {
                return default;
            }
        }
        var verified = ImmutableArray.CreateBuilder<FargateSubnet>(network.Subnets.Length);
        foreach (var configured in network.Subnets)
        {
            if (!byId.TryGetValue(configured.Id, out var subnet) || subnet.AvailabilityZone != configured.AvailabilityZone)
            {
                return default;
            }
            verified.Add(new FargateSubnet(configured.Id, configured.AvailabilityZone,
                subnet.Ipv6Native == true ? null : subnet.AvailableIpAddressCount));
        }
        var groups = await _client.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest
        {
            GroupIds = [.. network.SecurityGroupIds],
        }, cancellationToken).ConfigureAwait(false);
        if (groups.SecurityGroups?.Count != network.SecurityGroupIds.Length || !string.IsNullOrEmpty(groups.NextToken))
        {
            return default;
        }
        var missing = network.SecurityGroupIds.ToHashSet(StringComparer.Ordinal);
        foreach (var securityGroup in groups.SecurityGroups)
        {
            if (securityGroup is null || securityGroup.VpcId != network.VpcId || !missing.Remove(securityGroup.GroupId))
            {
                return default;
            }
        }
        if (!await Ec2NetworkEvidence.HasPrivateRoutesAsync(_client, network, account, cancellationToken).ConfigureAwait(false))
        {
            return default;
        }
        return (verified.MoveToImmutable(), subnetSampleStartedAt);
    }
}
