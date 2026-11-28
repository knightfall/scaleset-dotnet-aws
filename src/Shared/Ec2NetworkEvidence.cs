using System.Buffers;
using Amazon.EC2;
using Amazon.EC2.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Providers;

internal static class Ec2NetworkEvidence
{
    private const int MaximumRouteTablePages = 256;
    private static readonly SearchValues<char> LowercaseHex = SearchValues.Create("0123456789abcdef");

    internal static bool IsResourceId(string? value, string prefix) =>
        value?.StartsWith(prefix, StringComparison.Ordinal) == true &&
        value.Length - prefix.Length is 8 or 17 &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept(LowercaseHex) == -1;

    internal static async Task<bool> HasPrivateRoutesAsync(IAmazonEC2 ec2, NetworkConfiguration network, string account,
        CancellationToken cancellationToken)
    {
        var required = network.Subnets.Select(subnet => subnet.Id).ToHashSet(StringComparer.Ordinal);
        var explicitTables = new Dictionary<string, RouteTable>(StringComparer.Ordinal);
        var seenTables = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        RouteTable? mainTable = null;
        string? next = null;
        for (int page = 0; page < MaximumRouteTablePages; page++)
        {
            var response = await ec2.DescribeRouteTablesAsync(new DescribeRouteTablesRequest
            {
                Filters = [new Filter { Name = "vpc-id", Values = [network.VpcId] }],
                MaxResults = 100,
                NextToken = next,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var table in response.RouteTables ?? [])
            {
                if (table?.VpcId != network.VpcId || table.OwnerId != account ||
                    !IsResourceId(table.RouteTableId, "rtb-") || !seenTables.Add(table.RouteTableId))
                {
                    return false;
                }
                foreach (var association in table.Associations ?? [])
                {
                    if (association is null)
                    {
                        return false;
                    }
                    if (association.Main == true)
                    {
                        if (mainTable is not null || association.SubnetId is not null || association.GatewayId is not null ||
                            !IsCurrentAssociation(association, table))
                        {
                            return false;
                        }
                        mainTable = table;
                    }
                    else if (association.SubnetId is { } subnetId && required.Contains(subnetId) &&
                        (!IsCurrentAssociation(association, table) || association.GatewayId is not null ||
                         !explicitTables.TryAdd(subnetId, table)))
                    {
                        return false;
                    }
                }
            }
            next = response.NextToken;
            if (string.IsNullOrEmpty(next))
            {
                return required.All(subnetId => IsPrivate(explicitTables.GetValueOrDefault(subnetId) ?? mainTable));
            }
            if (string.IsNullOrWhiteSpace(next) || !seenTokens.Add(next))
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsCurrentAssociation(RouteTableAssociation association, RouteTable table) =>
        association.RouteTableId == table.RouteTableId && IsResourceId(association.RouteTableAssociationId, "rtbassoc-") &&
        (association.AssociationState is null || association.AssociationState.State?.Value == "associated");

    private static bool IsPrivate(RouteTable? table) =>
        table?.Routes is { Count: > 0 } routes && routes.All(route =>
            route is { CarrierGatewayId: null } && route.GatewayId?.StartsWith("igw-", StringComparison.Ordinal) != true &&
            (route.State?.Value is "blackhole" || (route.State?.Value is "active" && HasPrivateTarget(route))));

    private static bool HasPrivateTarget(Route route)
    {
        // EC2 represents local routes, gateways attached to the VPC and gateway endpoints in GatewayId.
        if (route.GatewayId is { } gateway)
        {
            return gateway == "local" || IsResourceId(gateway, "vgw-") || IsResourceId(gateway, "vpce-");
        }
        return !string.IsNullOrWhiteSpace(route.NatGatewayId) || !string.IsNullOrWhiteSpace(route.EgressOnlyInternetGatewayId) ||
            !string.IsNullOrWhiteSpace(route.TransitGatewayId) || !string.IsNullOrWhiteSpace(route.VpcPeeringConnectionId) ||
            !string.IsNullOrWhiteSpace(route.NetworkInterfaceId) || !string.IsNullOrWhiteSpace(route.InstanceId) ||
            !string.IsNullOrWhiteSpace(route.LocalGatewayId) ||
            !string.IsNullOrWhiteSpace(route.CoreNetworkArn) || !string.IsNullOrWhiteSpace(route.OdbNetworkArn) ||
            !string.IsNullOrWhiteSpace(route.IpAddress);
    }
}
