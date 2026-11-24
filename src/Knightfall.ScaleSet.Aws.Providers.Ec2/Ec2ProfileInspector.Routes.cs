using Amazon.EC2.Model;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

public sealed partial class Ec2ProfileInspector
{
    private async Task<bool> HasPrivateRoutesAsync(InspectionTarget target, CancellationToken cancellationToken)
    {
        var required = target.Network.Subnets.Select(subnet => subnet.Id).ToHashSet(StringComparer.Ordinal);
        var explicitTables = new Dictionary<string, RouteTable>(StringComparer.Ordinal);
        var seenTables = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        RouteTable? mainTable = null;
        string? next = null;
        do
        {
            var response = await _ec2.DescribeRouteTablesAsync(new DescribeRouteTablesRequest
            {
                Filters = [new Filter { Name = "vpc-id", Values = [target.Network.VpcId] }],
                MaxResults = 100,
                NextToken = next,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var table in response.RouteTables ?? [])
            {
                if (table?.VpcId != target.Network.VpcId || table.OwnerId != target.Aws.Account ||
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
            if (next is { Length: > 0 } && (string.IsNullOrWhiteSpace(next) || !seenTokens.Add(next)))
            {
                return false;
            }
        }
        while (!string.IsNullOrEmpty(next));

        return required.All(subnetId => IsPrivate(explicitTables.GetValueOrDefault(subnetId) ?? mainTable));
    }

    private static bool IsCurrentAssociation(RouteTableAssociation association, RouteTable table) =>
        association.RouteTableId == table.RouteTableId && IsResourceId(association.RouteTableAssociationId, "rtbassoc-") &&
        (association.AssociationState is null || association.AssociationState.State?.Value == "associated");

    private static bool IsPrivate(RouteTable? table) =>
        table?.Routes is { Count: > 0 } routes && routes.All(route =>
            route?.State?.Value is "blackhole" ||
            (route?.State?.Value is "active" && route.GatewayId?.StartsWith("igw-", StringComparison.Ordinal) != true));
}
