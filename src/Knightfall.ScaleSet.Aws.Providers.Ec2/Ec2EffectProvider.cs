using System.Globalization;
using System.Net;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

/// <summary>Implements the SDK wire effects for one active controller EC2 profile.</summary>
public sealed partial class Ec2EffectProvider : IReconciliationEffectProvider, IDisposable
{
    private readonly RuntimeConfiguration _configuration;
    private readonly LifecycleProfile _profile;
    private readonly Ec2ProvisionerConfiguration _provisioner;
    private readonly AmazonEC2Client _operations;
    private readonly Ec2ProfileInspector _inspector;
    private readonly TimeProvider _clock;
    private readonly string _instanceArnPrefix;

    public Ec2EffectProvider(RuntimeConfiguration configuration, LifecycleProfile profile, AWSCredentials credentials,
        AmazonEC2Config ec2, AmazonIdentityManagementServiceConfig iam, AmazonSecurityTokenServiceConfig sts, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(ec2);
        if (configuration.Mode != ControllerMode.Active || RunnerOwnershipTags.DeploymentIdentity(configuration) != profile.Deployment ||
            configuration.Profiles.SingleOrDefault(item => item.Id == profile.ProfileId) is not
                { State: RunnerProfileState.Active or RunnerProfileState.Draining, Provisioner: Ec2ProvisionerConfiguration provisioner })
        {
            throw new ArgumentException("EC2 effects require the exact enabled profile of an active controller.", nameof(profile));
        }
        if (ec2.MaxErrorRetry != 0 || ec2.MaxStaleConnectionRetries != 0 || !ec2.DisableClockSkewCorrection || ec2.AllowAutoRedirect ||
            (ec2.RegionEndpoint?.SystemName ?? ec2.AuthenticationRegion) != configuration.Aws.Region)
        {
            throw new ArgumentException("EC2 effects require the configured Region and disabled SDK retries, clock-skew retries, and redirects.",
                nameof(ec2));
        }

        _configuration = configuration;
        _profile = profile;
        _provisioner = provisioner;
        _clock = clock;
        string partition = provisioner.BootstrapRoleArn.Split(':')[1];
        _instanceArnPrefix = $"arn:{partition}:ec2:{configuration.Aws.Region}:{configuration.Aws.Account}:instance/";
        _inspector = new Ec2ProfileInspector(credentials, ec2, iam, sts, clock);
        _operations = new AmazonEC2Client(credentials, ec2);
    }

    public void Dispose()
    {
        _operations.Dispose();
        _inspector.Dispose();
    }

    public async Task<EffectDispatchPrecondition?> CheckAsync(
        EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(binding);
        if (envelope.Profile != _profile || binding.ProvisionerType != RunnerProvisionerType.Ec2 ||
            binding.ProviderDefinition != _provisioner.LaunchTemplateId || binding.BootstrapRoleArn != _provisioner.BootstrapRoleArn ||
            binding.BootstrapProtocol != _provisioner.BootstrapProtocol)
        {
            return null;
        }
        if (envelope.Request is Ec2LaunchEffectRequest launch && IsScopedLaunch(launch))
        {
            return await CheckLaunchAsync(envelope, launch, cancellationToken).ConfigureAwait(false);
        }
        bool eligible = envelope.Request is Ec2TerminateEffectRequest termination && IsScopedTermination(termination, out string instanceId) &&
            await ReadTerminationTargetAsync(termination, instanceId, cancellationToken).ConfigureAwait(false) is { State: Ec2InstanceState.Running };
        return new EffectDispatchPrecondition(envelope.Sha256, envelope.Target, _clock.GetUtcNow(), eligible);
    }

    public async Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(attempt);
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope.Profile != _profile || _clock.GetUtcNow() >= attempt.Deadline)
        {
            return null;
        }
        return envelope.Request switch
        {
            Ec2LaunchEffectRequest request when IsScopedLaunch(request) =>
                await SendLaunchAsync(envelope, request, attempt, cancellationToken).ConfigureAwait(false),
            Ec2TerminateEffectRequest request when IsScopedTermination(request, out string instanceId) =>
                await SendTerminationAsync(envelope, request, instanceId, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    public async Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(attempt);
        if (envelope.Profile != _profile)
        {
            return null;
        }
        return envelope.Request switch
        {
            Ec2LaunchEffectRequest request when IsScopedLaunch(request) =>
                await ObserveLaunchAsync(envelope, request, null, cancellationToken).ConfigureAwait(false),
            Ec2TerminateEffectRequest request when IsScopedTermination(request, out string instanceId) =>
                await ObserveTerminationAsync(envelope, request, instanceId, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task<EffectDispatchPrecondition> CheckLaunchAsync(
        EffectEnvelope envelope, Ec2LaunchEffectRequest request, CancellationToken cancellationToken)
    {
        var preflight = await _inspector.ResolvePreflightAsync(_configuration, _profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (!preflight.IsReady || preflight.Template is not { } template || template.LaunchTemplateId != request.LaunchTemplateId)
        {
            return new EffectDispatchPrecondition(envelope.Sha256, envelope.Target, _clock.GetUtcNow(), false);
        }
        bool matchesVersion = template.VersionNumber == request.LaunchTemplateVersion;
        bool eligible = matchesVersion &&
            template.InstanceType == request.InstanceType && template.InstanceProfileArn == request.InstanceProfileArn &&
            template.Tenancy == request.Tenancy && template.SecurityGroupIds.Order(StringComparer.Ordinal).SequenceEqual(request.SecurityGroupIds) &&
            template.Subnets.Any(subnet => subnet.SubnetId == request.SubnetId &&
                subnet.AvailabilityZone == request.AvailabilityZone && subnet.AvailableIpAddresses is > 0);
        return new EffectDispatchPrecondition(envelope.Sha256, envelope.Target, _clock.GetUtcNow(), eligible)
        {
            DefinitivelyObsolete = !matchesVersion && _provisioner.LaunchTemplateVersion is AliasLaunchTemplateVersion,
        };
    }

    private bool IsScopedLaunch(Ec2LaunchEffectRequest request) =>
        request.Region == _configuration.Aws.Region && request.LaunchTemplateId == _provisioner.LaunchTemplateId &&
        request.InstanceProfileArn == _provisioner.InstanceProfileArn && request.InstanceType is not null &&
        request.OwnershipTags == OwnershipTags(request.AllocationId);

    private bool IsScopedTermination(Ec2TerminateEffectRequest request, out string instanceId)
    {
        instanceId = string.Empty;
        if (request.LaunchTemplateId != _provisioner.LaunchTemplateId ||
            !request.InstanceArn.StartsWith(_instanceArnPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        instanceId = request.InstanceArn[_instanceArnPrefix.Length..];
        return Ec2ProfileInspector.IsResourceId(instanceId, "i-");
    }

    private Ec2LaunchOwnershipTags OwnershipTags(Guid allocation) =>
        new(RunnerOwnershipTags.ControllerValue, RunnerOwnershipTags.DeploymentIdentity(_configuration), _profile.ProfileId,
            allocation.ToString("D"), _provisioner.BootstrapProtocol);

    private static List<Tag> WireTags(Ec2LaunchOwnershipTags ownership) =>
    [
        new Tag(RunnerOwnershipTags.Controller, ownership.Controller),
        new Tag(RunnerOwnershipTags.Deployment, ownership.Deployment),
        new Tag(RunnerOwnershipTags.Profile, ownership.Profile),
        new Tag(RunnerOwnershipTags.Allocation, ownership.Allocation),
        new Tag(RunnerOwnershipTags.BootstrapProtocol, ownership.BootstrapProtocol),
    ];

    private static RunInstancesRequest WireRequest(Ec2LaunchEffectRequest request) => new()
    {
        MinCount = request.MinCount,
        MaxCount = request.MaxCount,
        ClientToken = request.ClientToken.ToString("D"),
        LaunchTemplate = new LaunchTemplateSpecification
        {
            LaunchTemplateId = request.LaunchTemplateId,
            Version = request.LaunchTemplateVersion.ToString(CultureInfo.InvariantCulture),
        },
        InstanceType = request.InstanceType,
        IamInstanceProfile = new IamInstanceProfileSpecification { Arn = request.InstanceProfileArn },
        SubnetId = request.SubnetId,
        SecurityGroupIds = [.. request.SecurityGroupIds],
        Placement = new Placement
        {
            AvailabilityZone = request.AvailabilityZone,
            Tenancy = request.Tenancy switch
            {
                Ec2Tenancy.Default => Tenancy.Default,
                Ec2Tenancy.Dedicated => Tenancy.Dedicated,
                _ => null,
            },
        },
        TagSpecifications =
        [
            new TagSpecification { ResourceType = ResourceType.Instance, Tags = WireTags(request.OwnershipTags!) },
            new TagSpecification { ResourceType = ResourceType.Volume, Tags = WireTags(request.OwnershipTags!) },
        ],
    };

    private async Task<EffectEvidence?> SendLaunchAsync(EffectEnvelope envelope, Ec2LaunchEffectRequest request,
        EffectAttempt attempt, CancellationToken cancellationToken)
    {
        string instanceId;
        try
        {
            var result = await _operations.RunInstancesAsync(WireRequest(request), cancellationToken).ConfigureAwait(false);
            if (result.Reservation?.Instances is not [{ InstanceId: { } returnedId }] ||
                !Ec2ProfileInspector.IsResourceId(returnedId, "i-"))
            {
                return null;
            }
            instanceId = returnedId;
        }
        catch (AmazonEC2Exception error) when (!cancellationToken.IsCancellationRequested && ClassifyRejection(error) is not null)
        {
            return new EffectEvidence(EffectResolutionKind.Rejected, envelope.Sha256, envelope.Target, _clock.GetUtcNow(), attempt.Id)
            {
                Ec2LaunchRejection = ClassifyRejection(error),
            };
        }
        catch (Exception error) when (Ec2ProfileInspector.IsReadFailure(error, cancellationToken))
        {
            return null;
        }

        return await ObserveLaunchAsync(envelope, request, instanceId, cancellationToken).ConfigureAwait(false);
    }

    private static Ec2LaunchRejection? ClassifyRejection(AmazonEC2Exception error) =>
        (int)error.StatusCode is >= 400 and < 600 && !string.IsNullOrWhiteSpace(error.RequestId)
            ? error.ErrorCode switch
            {
                "InsufficientInstanceCapacity" => Ec2LaunchRejection.InsufficientInstanceCapacity,
                "InsufficientFreeAddressesInSubnet" => Ec2LaunchRejection.InsufficientFreeAddressesInSubnet,
                _ => null,
            } : null;
}
