using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

/// <param name="Binding">Exact executor identity from the provider's owned inventory.</param>
/// <param name="CredentialSourceArn">Observed EC2 instance profile or effective ECS task role.</param>
/// <param name="ObservedAt">Local time of the provider read.</param>
public sealed record BootstrapProviderObservation(
    ExecutorBinding Binding, string CredentialSourceArn, DateTimeOffset ObservedAt);

public sealed class AwsBootstrapPrincipalResolver
{
    private readonly IAmazonIdentityManagementService _iam;

    public AwsBootstrapPrincipalResolver(IAmazonIdentityManagementService iam)
    {
        ArgumentNullException.ThrowIfNull(iam);
        _iam = iam;
    }

    public async Task<JitHandoffIdentity?> ResolveAsync(
        RuntimeConfiguration configuration, CatalogProfile catalog, BootstrapProviderObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(observation);
        var binding = observation.Binding;
        if (configuration.Mode != ControllerMode.Active ||
            catalog.State is not (CatalogProfileState.Active or CatalogProfileState.Draining) ||
            catalog.Binding is not { BootstrapProtocol: "v1", ScaleSetId: > 0 } ||
            binding.Profile != catalog.Profile ||
            binding.Profile.Deployment != ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration) ||
            configuration.Profiles.SingleOrDefault(profile => profile.Id == binding.Profile.ProfileId) is not
                { State: RunnerProfileState.Active or RunnerProfileState.Draining } configured ||
            configured.Provisioner.BootstrapRoleArn != catalog.Binding.BootstrapRoleArn ||
            configured.Provisioner.Type != catalog.Binding.ProvisionerType)
        {
            return null;
        }
        binding.Validate();
        var resourceArn = binding.ResourceArn.Split(':', 6);
        string expectedPartition = catalog.Binding.BootstrapRoleArn.Split(':', 6)[1];
        if (resourceArn[1] != expectedPartition || resourceArn[3] != configuration.Aws.Region || resourceArn[4] != configuration.Aws.Account)
        {
            return null;
        }

        try
        {
            string? roleId = (configured.Provisioner, binding.Provider) switch
            {
                (Ec2ProvisionerConfiguration ec2, Ec2ExecutorIdentity instance)
                    when instance.LaunchTemplateId == ec2.LaunchTemplateId &&
                         ec2.LaunchTemplateId == catalog.Binding.ProviderDefinition &&
                         observation.CredentialSourceArn == ec2.InstanceProfileArn =>
                    await ReadInstanceRoleAsync(ec2, cancellationToken).ConfigureAwait(false),
                (FargateProvisionerConfiguration fargate, FargateExecutorIdentity task)
                    when task.TaskDefinitionArn == fargate.TaskDefinitionArn &&
                         fargate.TaskDefinitionArn == catalog.Binding.ProviderDefinition &&
                         MatchesCluster(configuration, fargate.Cluster, task.ClusterArn, expectedPartition) &&
                         observation.CredentialSourceArn == fargate.BootstrapRoleArn =>
                    await ReadRoleIdAsync(fargate.BootstrapRoleArn, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
            return roleId is null ? null : new JitHandoffIdentity(binding.Profile.Deployment, binding.Profile.ProfileId,
                catalog.Binding.ScopeIdentity, catalog.Binding.ScaleSetId.Value, Guid.ParseExact(binding.AllocationId, "D"),
                binding.ResourceArn, binding.Incarnation, roleId + ":" + ResourceName(binding.ResourceArn));
        }
        catch (Exception error) when (error is AmazonServiceException or HttpRequestException or IOException or TimeoutException)
        {
            return null;
        }
    }

    private async Task<string?> ReadInstanceRoleAsync(Ec2ProvisionerConfiguration configured, CancellationToken cancellationToken)
    {
        var profile = await _iam.GetInstanceProfileAsync(
            new GetInstanceProfileRequest { InstanceProfileName = ResourceName(configured.InstanceProfileArn) }, cancellationToken)
            .ConfigureAwait(false);
        if (profile.InstanceProfile is not { Roles.Count: 1 } found || found.Arn != configured.InstanceProfileArn ||
            found.Roles[0].Arn != configured.BootstrapRoleArn)
        {
            return null;
        }
        string? roleId = await ReadRoleIdAsync(configured.BootstrapRoleArn, cancellationToken).ConfigureAwait(false);
        return roleId == found.Roles[0].RoleId ? roleId : null;
    }

    private async Task<string?> ReadRoleIdAsync(string roleArn, CancellationToken cancellationToken)
    {
        var response = await _iam.GetRoleAsync(new GetRoleRequest { RoleName = ResourceName(roleArn) }, cancellationToken)
            .ConfigureAwait(false);
        var role = response.Role;
        return role?.Arn == roleArn && role.RoleId is { Length: >= 16 and <= 128 } id &&
            id.StartsWith("AROA", StringComparison.Ordinal) && id.All(static value => char.IsAsciiLetterUpper(value) || char.IsAsciiDigit(value))
            ? id : null;
    }

    private static string ResourceName(string arn) => arn[(arn.LastIndexOf('/') + 1)..];

    private static bool MatchesCluster(RuntimeConfiguration configuration, string clusterKey, string observedArn, string partition) =>
        configuration.EcsClusters[clusterKey] switch
        {
            ImportEcsClusterConfiguration import => import.Arn == observedArn,
            CreateEcsClusterConfiguration create =>
                $"arn:{partition}:ecs:{configuration.Aws.Region}:{configuration.Aws.Account}:cluster/{create.Name}" == observedArn,
            _ => false,
        };
}
