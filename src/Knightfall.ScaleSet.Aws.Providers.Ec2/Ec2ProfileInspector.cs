using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

/// <summary>Reads EC2, IAM, and STS evidence for one configured EC2 runner profile.</summary>
public sealed partial class Ec2ProfileInspector : IDisposable
{
    private static readonly SearchValues<char> LowercaseHex = SearchValues.Create("0123456789abcdef");

    private readonly AmazonEC2Client _ec2;
    private readonly AmazonIdentityManagementServiceClient _iam;
    private readonly AmazonSecurityTokenServiceClient _sts;
    private readonly TimeProvider _clock;

    public Ec2ProfileInspector(AWSCredentials credentials, AmazonEC2Config ec2, AmazonIdentityManagementServiceConfig iam,
        AmazonSecurityTokenServiceConfig sts, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(ec2);
        ArgumentNullException.ThrowIfNull(iam);
        ArgumentNullException.ThrowIfNull(sts);
        ArgumentNullException.ThrowIfNull(clock);
        _ec2 = new AmazonEC2Client(credentials, ec2);
        _iam = new AmazonIdentityManagementServiceClient(credentials, iam);
        _sts = new AmazonSecurityTokenServiceClient(credentials, sts);
        _clock = clock;
    }

    public void Dispose()
    {
        _sts.Dispose();
        _iam.Dispose();
        _ec2.Dispose();
    }

    public async Task<Ec2PreflightResult> ResolvePreflightAsync(
        RuntimeConfiguration configuration, string profileId, CancellationToken cancellationToken)
    {
        var target = GetTarget(configuration, profileId);
        cancellationToken.ThrowIfCancellationRequested();
        var provisioner = target.Provisioner;
        if (!GetSelector(provisioner.LaunchTemplateVersion, out string selector, out long? pinnedVersion))
        {
            return Failed(Ec2PreflightStatus.TemplateIdentityMismatch);
        }

        long? observedVersion = null;
        try
        {
            if (!await MatchesCallerIdentityAsync(target, cancellationToken).ConfigureAwait(false))
            {
                return Failed(Ec2PreflightStatus.AwsIdentityMismatch);
            }
            var response = await _ec2.DescribeLaunchTemplateVersionsAsync(new DescribeLaunchTemplateVersionsRequest
            {
                LaunchTemplateId = provisioner.LaunchTemplateId,
                Versions = [selector],
                ResolveAlias = false,
            }, cancellationToken).ConfigureAwait(false);
            if (response.LaunchTemplateVersions is not { Count: > 0 } versions)
            {
                return Failed(Ec2PreflightStatus.TemplateUnavailable);
            }
            if (versions.Count != 1 || !string.IsNullOrEmpty(response.NextToken) ||
                versions[0] is not { VersionNumber: > 0, LaunchTemplateData: { } data } version ||
                version.LaunchTemplateId != provisioner.LaunchTemplateId ||
                (pinnedVersion is not null && version.VersionNumber != pinnedVersion) ||
                (selector == "$Default" && version.DefaultVersion != true))
            {
                return Failed(Ec2PreflightStatus.TemplateIdentityMismatch);
            }
            observedVersion = version.VersionNumber;
            if (data.MetadataOptions is not { HttpEndpoint.Value: "enabled", HttpTokens.Value: "required" })
            {
                return Failed(Ec2PreflightStatus.ImdsV2Required, observedVersion);
            }
            if (!HasBootstrapProtocol(data, provisioner.BootstrapProtocol))
            {
                return Failed(Ec2PreflightStatus.BootstrapProtocolMismatch, observedVersion);
            }
            if (!MatchesInstanceProfileReference(data, provisioner.InstanceProfileArn))
            {
                return Failed(Ec2PreflightStatus.InstanceProfileMismatch, observedVersion);
            }
            if (!MatchesTemplateNetwork(data, target.Network))
            {
                return Failed(Ec2PreflightStatus.NetworkIncompatible, observedVersion);
            }
            if (!TryGetTenancy(data, provisioner, out var tenancy))
            {
                return Failed(Ec2PreflightStatus.PlacementIncompatible, observedVersion);
            }

            var image = await ReadCompatibleImageAsync(data.ImageId, target.Profile.Platform, cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                return Failed(Ec2PreflightStatus.ImageIncompatible, observedVersion);
            }
            if (!HasEncryptedRoot(data, image.RootDeviceName))
            {
                return Failed(Ec2PreflightStatus.RootEncryptionUnverified, observedVersion);
            }
            string? instanceType = provisioner.InstanceType ?? data.InstanceType?.Value;
            if (string.IsNullOrWhiteSpace(instanceType) || data.InstanceRequirements is not null ||
                !await IsInstanceTypeCompatibleAsync(instanceType, image.Architecture.Value, cancellationToken).ConfigureAwait(false))
            {
                return Failed(Ec2PreflightStatus.InstanceTypeIncompatible, observedVersion);
            }
            var (roleStatus, roleId) = await ReadBootstrapRoleAsync(provisioner, cancellationToken).ConfigureAwait(false);
            if (roleStatus != Ec2PreflightStatus.Verified)
            {
                return Failed(roleStatus, observedVersion);
            }
            var subnets = await ReadNetworkAsync(target.Network, cancellationToken).ConfigureAwait(false);
            if (subnets.IsDefault || !await HasPrivateRoutesAsync(target, cancellationToken).ConfigureAwait(false))
            {
                return Failed(Ec2PreflightStatus.NetworkIncompatible, observedVersion);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var approved = new Ec2ResolvedTemplate(provisioner.LaunchTemplateId, observedVersion!.Value, image.ImageId,
                provisioner.InstanceProfileArn, provisioner.BootstrapRoleArn, roleId!, instanceType,
                tenancy, subnets, target.Network.SecurityGroupIds);
            return new Ec2PreflightResult(Ec2PreflightStatus.Verified, observedVersion, approved);
        }
        catch (AmazonEC2Exception error) when (observedVersion is null &&
                                               error.ErrorCode?.StartsWith("InvalidLaunchTemplate", StringComparison.Ordinal) == true)
        {
            return Failed(Ec2PreflightStatus.TemplateUnavailable);
        }
        catch (Exception error) when (IsReadFailure(error, cancellationToken))
        {
            return Failed(Ec2PreflightStatus.AwsUnavailable, observedVersion);
        }
    }

    private async Task<bool> MatchesCallerIdentityAsync(InspectionTarget target, CancellationToken cancellationToken)
    {
        var identity = await _sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken).ConfigureAwait(false);
        return identity.Account == target.Aws.Account &&
            identity.Arn?.Split(':', 6) is ["arn", var partition, "iam" or "sts", "", var arnAccount, { Length: > 0 }] &&
            partition == target.Partition && arnAccount == target.Aws.Account;
    }

    private async Task<bool> IsInstanceTypeCompatibleAsync(string instanceType, string architecture, CancellationToken cancellationToken)
    {
        var response = await _ec2.DescribeInstanceTypesAsync(new DescribeInstanceTypesRequest
        {
            InstanceTypes = [instanceType],
        }, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(response.NextToken) &&
            response.InstanceTypes is [{ InstanceType.Value: var actualType, SupportedInRegion: not false,
                ProcessorInfo.SupportedArchitectures: { } architectures }] &&
            actualType == instanceType && architectures.Contains(architecture, StringComparer.Ordinal);
    }

    private async Task<Image?> ReadCompatibleImageAsync(
        string? imageId, RunnerPlatformConfiguration platform, CancellationToken cancellationToken)
    {
        // A numeric template version can still refer to a mutable SSM AMI parameter.
        if (!IsResourceId(imageId, "ami-"))
        {
            return null;
        }
        var response = await _ec2.DescribeImagesAsync(new DescribeImagesRequest { ImageIds = [imageId!] }, cancellationToken)
            .ConfigureAwait(false);
        if (response.Images is not { Count: 1 } images || !string.IsNullOrEmpty(response.NextToken) ||
            images[0] is not { State.Value: "available", RootDeviceType.Value: "ebs" } image ||
            image.ImageId != imageId || image.ImageAllowed == false || string.IsNullOrWhiteSpace(image.RootDeviceName))
        {
            return null;
        }

        var architecture = platform.Architecture switch
        {
            RunnerArchitecture.X64 => "x86_64",
            RunnerArchitecture.Arm64 => "arm64",
            _ => null,
        };
        bool matchesOs = platform.OperatingSystem switch
        {
            RunnerOperatingSystem.Windows => image.Platform?.Value == "windows",
            RunnerOperatingSystem.Linux => image.Platform is null && image.PlatformDetails is { } details &&
                (details.Contains("Linux", StringComparison.OrdinalIgnoreCase) || details.StartsWith("Ubuntu", StringComparison.Ordinal)),
            _ => false,
        };
        return architecture is not null && image.Architecture?.Value == architecture && matchesOs ? image : null;
    }

    private async Task<(Ec2PreflightStatus Status, string? RoleId)> ReadBootstrapRoleAsync(
        Ec2ProvisionerConfiguration provisioner, CancellationToken cancellationToken)
    {
        string profileName = ArnName(provisioner.InstanceProfileArn);
        string roleName = ArnName(provisioner.BootstrapRoleArn);
        var instance = await _iam.GetInstanceProfileAsync(new GetInstanceProfileRequest { InstanceProfileName = profileName },
            cancellationToken).ConfigureAwait(false);
        if (instance.InstanceProfile is not { } profile || profile.Arn != provisioner.InstanceProfileArn ||
            profile.InstanceProfileName != profileName)
        {
            return (Ec2PreflightStatus.InstanceProfileMismatch, null);
        }
        if (profile.Roles is not { Count: 1 } roles || roles[0] is not { } attached ||
            attached.Arn != provisioner.BootstrapRoleArn || attached.RoleName != roleName || string.IsNullOrWhiteSpace(attached.RoleId))
        {
            return (Ec2PreflightStatus.BootstrapRoleMismatch, null);
        }

        var response = await _iam.GetRoleAsync(new GetRoleRequest { RoleName = roleName }, cancellationToken).ConfigureAwait(false);
        return response.Role is { } role && role.Arn == attached.Arn && role.RoleName == roleName && role.RoleId == attached.RoleId
            ? (Ec2PreflightStatus.Verified, role.RoleId) : (Ec2PreflightStatus.BootstrapRoleMismatch, null);
    }

    private async Task<ImmutableArray<Ec2PreflightSubnet>> ReadNetworkAsync(NetworkConfiguration network, CancellationToken cancellationToken)
    {
        var subnets = await _ec2.DescribeSubnetsAsync(new DescribeSubnetsRequest
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
            if (subnet is not { State.Value: "available", MapPublicIpOnLaunch: false } ||
                subnet.VpcId != network.VpcId || subnet.AvailableIpAddressCount < 0 ||
                !byId.TryAdd(subnet.SubnetId, subnet))
            {
                return default;
            }
        }
        var verified = ImmutableArray.CreateBuilder<Ec2PreflightSubnet>(network.Subnets.Length);
        foreach (var configured in network.Subnets)
        {
            if (!byId.TryGetValue(configured.Id, out var subnet) || subnet.AvailabilityZone != configured.AvailabilityZone)
            {
                return default;
            }
            verified.Add(new Ec2PreflightSubnet(configured.Id, configured.AvailabilityZone, subnet.AvailableIpAddressCount));
        }

        var groups = await _ec2.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest
        {
            GroupIds = [.. network.SecurityGroupIds],
        }, cancellationToken).ConfigureAwait(false);
        if (groups.SecurityGroups?.Count != network.SecurityGroupIds.Length || !string.IsNullOrEmpty(groups.NextToken))
        {
            return default;
        }
        var missing = network.SecurityGroupIds.ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups.SecurityGroups)
        {
            if (group is null || group.VpcId != network.VpcId || !missing.Remove(group.GroupId))
            {
                return default;
            }
        }
        return verified.MoveToImmutable();
    }

    private InspectionTarget GetTarget(RuntimeConfiguration configuration, string profileId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var profile = configuration.Profiles.SingleOrDefault(profile => profile.Id == profileId);
        if (profile?.Provisioner is not Ec2ProvisionerConfiguration provisioner || profile.State == RunnerProfileState.Disabled ||
            (configuration.Mode == ControllerMode.DryRun && !configuration.DryRun.InspectAws))
        {
            throw new ArgumentException("EC2 inspection requires an enabled EC2 profile with AWS inspection authorized.", nameof(profileId));
        }

        string? region = _ec2.Config.RegionEndpoint?.SystemName ?? _ec2.Config.AuthenticationRegion;
        if (region != configuration.Aws.Region)
        {
            throw new ArgumentException("EC2 inspection must use the configured AWS Region.", nameof(configuration));
        }
        string partition = provisioner.BootstrapRoleArn.Split(':')[1];
        return new InspectionTarget(configuration.Aws, profile, provisioner, configuration.Networks[provisioner.Network],
            RunnerOwnershipTags.DeploymentIdentity(configuration), partition);
    }

    private static bool GetSelector(LaunchTemplateVersionConfiguration version, out string selector, out long? pinned)
    {
        pinned = null;
        switch (version)
        {
            case PinnedLaunchTemplateVersion value when TryPositiveDecimal(value.Version, out long parsed):
                pinned = parsed;
                selector = value.Version;
                return true;
            case AliasLaunchTemplateVersion { Name: LaunchTemplateAlias.Latest }:
                selector = "$Latest";
                return true;
            case AliasLaunchTemplateVersion { Name: LaunchTemplateAlias.Default }:
                selector = "$Default";
                return true;
            default:
                selector = string.Empty;
                return false;
        }
    }

    private static bool HasBootstrapProtocol(ResponseLaunchTemplateData data, string protocol)
    {
        var tags = data.TagSpecifications?.Where(specification => specification.ResourceType?.Value == "instance")
            .SelectMany(specification => specification.Tags ?? [])
            .Where(tag => tag.Key == RunnerOwnershipTags.BootstrapProtocol)
            .Take(2).ToArray();
        return tags is [{ } tag] && tag.Value == protocol;
    }

    private static bool HasEncryptedRoot(ResponseLaunchTemplateData data, string rootDevice)
    {
        var mappings = data.BlockDeviceMappings?.Where(mapping => mapping.DeviceName == rootDevice).Take(2).ToArray();
        return mappings is [{ NoDevice: null, Ebs.Encrypted: true }];
    }

    private static bool MatchesInstanceProfileReference(ResponseLaunchTemplateData data, string expectedArn) =>
        data.IamInstanceProfile is { } profile &&
        (profile.Arn is not null || profile.Name is not null) &&
        (profile.Arn is null || profile.Arn == expectedArn) &&
        (profile.Name is null || profile.Name == ArnName(expectedArn));

    private static bool MatchesTemplateNetwork(ResponseLaunchTemplateData data, NetworkConfiguration network) =>
        data.NetworkInterfaces is not { Count: > 0 } && data.SecondaryInterfaces is not { Count: > 0 } &&
        data.SecurityGroups is not { Count: > 0 } &&
        data.SecurityGroupIds?.All(id => network.SecurityGroupIds.Contains(id)) != false &&
        data.Placement?.AvailabilityZoneId is null &&
        (data.Placement?.AvailabilityZone is not { } zone || network.Subnets.All(subnet => subnet.AvailabilityZone == zone));

    private static bool TryGetTenancy(ResponseLaunchTemplateData data, Ec2ProvisionerConfiguration configuration, out Ec2Tenancy? tenancy)
    {
        string? templateValue = data.Placement?.Tenancy?.Value;
        tenancy = configuration.Tenancy ?? (templateValue == "dedicated" ? Ec2Tenancy.Dedicated : null);
        return templateValue is null or "default" or "dedicated" &&
            (configuration.Tenancy is null || templateValue is null ||
             templateValue == (configuration.Tenancy == Ec2Tenancy.Dedicated ? "dedicated" : "default"));
    }

    private static bool TryPositiveDecimal(string? value, out long parsed) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
        parsed > 0 && value == parsed.ToString(CultureInfo.InvariantCulture);

    private static bool IsResourceId(string? value, string prefix) =>
        value?.StartsWith(prefix, StringComparison.Ordinal) == true &&
        value.Length - prefix.Length is 8 or 17 &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept(LowercaseHex) == -1;

    private static string ArnName(string arn) => arn[(arn.LastIndexOf('/') + 1)..];

    private static bool IsReadFailure(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        error is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or TimeoutException or OperationCanceledException;

    private static Ec2PreflightResult Failed(Ec2PreflightStatus status, long? observedVersion = null) =>
        new(status, observedVersion, null);

    private sealed record InspectionTarget(AwsEnvironment Aws, RunnerProfile Profile, Ec2ProvisionerConfiguration Provisioner,
        NetworkConfiguration Network, string Deployment, string Partition);
}
