using System.Security.Cryptography;
using System.Text;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

public sealed partial class Ec2EffectProvider
{
    public async Task<Ec2LaunchEffectRequest?> CreateLaunchRequestAsync(
        Guid allocationId, int attemptOrdinal, long placementSequence, IReadOnlySet<string> excludedAvailabilityZones,
        IReadOnlyDictionary<string, int>? recentZonalFailures, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(allocationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(placementSequence);
        ArgumentNullException.ThrowIfNull(excludedAvailabilityZones);

        var preflight = await _inspector.ResolvePreflightAsync(_configuration, _profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (preflight is not { IsReady: true, Template: { } template })
        {
            return null;
        }

        var eligible = template.Subnets.Where(subnet => subnet.AvailableIpAddresses is > 0 &&
                !excludedAvailabilityZones.Contains(subnet.AvailabilityZone))
            .OrderBy(subnet => subnet.AvailabilityZone, StringComparer.Ordinal)
            .ThenBy(subnet => subnet.SubnetId, StringComparer.Ordinal).ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        int offset = (int)(placementSequence % eligible.Length);
        var selected = eligible[offset];
        if (_provisioner.SubnetStrategy == Ec2SubnetStrategy.FailureWeighted && recentZonalFailures is not null)
        {
            selected = eligible.Skip(offset).Concat(eligible.Take(offset))
                .MinBy(subnet => recentZonalFailures.GetValueOrDefault(subnet.AvailabilityZone))!;
        }

        string identity = FormattableString.Invariant(
            $"{_profile.Deployment}/{_profile.ProfileId}/{allocationId:D}/{attemptOrdinal}/{selected.AvailabilityZone}/{selected.SubnetId}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var clientToken = new Guid(hash.AsSpan(0, 16), bigEndian: true);
        return new Ec2LaunchEffectRequest(allocationId, _configuration.Aws.Region, selected.SubnetId, selected.AvailabilityZone,
            template.LaunchTemplateId, template.VersionNumber, template.InstanceType, template.SecurityGroupIds, template.InstanceProfileArn,
            clientToken, attemptOrdinal, template.Tenancy, OwnershipTags(allocationId));
    }
}
