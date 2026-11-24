using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public enum RetirementPreflightStatus
{
    CatalogUnavailable = 0,
    BindingMismatch = 1,
    RetirementRequired = 2,
    Verified = 3,
}

public sealed record RetirementPreflightResult(
    RetirementPreflightStatus Status, IReadOnlyList<string> Profiles);

public sealed partial class DynamoDbLifecycleStore
{
    /// <summary>Verifies that every profile losing its deployed permissions has a durable tombstone.</summary>
    public async Task<RetirementPreflightResult> VerifyRetirementAsync(
        RuntimeConfiguration current, long currentGeneration, RuntimeConfiguration candidate, long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        if (currentGeneration <= 0 || generation <= currentGeneration || current.Aws != candidate.Aws ||
            current.EnvironmentName != candidate.EnvironmentName || current.TrackingTable != candidate.TrackingTable ||
            _tableName != current.TrackingTable.Name)
        {
            return new RetirementPreflightResult(RetirementPreflightStatus.BindingMismatch, []);
        }

        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(current);
        var head = await ReadCatalogRolloutAsync(deployment, cancellationToken).ConfigureAwait(false);
        if (head?.Pending is not null ||
            (current.Mode == ControllerMode.Active &&
            (head is null || head.AppliedGeneration != currentGeneration || head.AppliedDigest != current.Sha256)) ||
            (current.Mode == ControllerMode.DryRun && head?.AppliedGeneration >= currentGeneration))
        {
            return new RetirementPreflightResult(RetirementPreflightStatus.CatalogUnavailable, []);
        }
        var rows = await ReadCatalogRowsAsync(deployment, cancellationToken).ConfigureAwait(false);
        if (await ReadCatalogRolloutAsync(deployment, cancellationToken).ConfigureAwait(false) != head ||
            (head is null && rows.Count != 0))
        {
            return new RetirementPreflightResult(RetirementPreflightStatus.CatalogUnavailable, []);
        }

        var previouslyGranted = GrantedProfiles(current).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var nextGranted = GrantedProfiles(candidate).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var blocked = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, profile) in previouslyGranted)
        {
            var stored = head is not null && rows.TryGetValue(id, out var row) ? row.Effective(head) : null;
            if (stored is null || !MatchesDeployedProfile(current, profile, stored))
            {
                return new RetirementPreflightResult(RetirementPreflightStatus.CatalogUnavailable, [id]);
            }
            if (stored.State != CatalogProfileState.Tombstoned &&
                (!nextGranted.TryGetValue(id, out var next) || !CoversDeployedGrants(current, profile, candidate, next)))
            {
                blocked.Add(id);
            }
        }

        foreach (var row in rows.Values)
        {
            if (head is not null && row.Effective(head) is { State: not CatalogProfileState.Tombstoned } stored &&
                !previouslyGranted.ContainsKey(stored.Profile.ProfileId))
            {
                // Extra live catalog rows make the deployed grant set incomplete.
                blocked.Add(stored.Profile.ProfileId);
            }
        }
        return blocked.Count == 0
            ? new RetirementPreflightResult(RetirementPreflightStatus.Verified, [])
            : new RetirementPreflightResult(RetirementPreflightStatus.RetirementRequired, [.. blocked]);
    }

    private static IEnumerable<RunnerProfile> GrantedProfiles(RuntimeConfiguration configuration) =>
        configuration.Mode == ControllerMode.Active
            ? configuration.Profiles.Where(static profile => profile.State != RunnerProfileState.Disabled)
            : [];

    private static bool MatchesDeployedProfile(RuntimeConfiguration current, RunnerProfile profile, CatalogProfile stored)
    {
        var expected = ConfigurationBinding(current, profile);
        return SameBinding(expected with
        {
            ScaleSetId = expected.ScaleSetId ?? stored.Binding.ScaleSetId,
            RunnerGroupId = stored.Binding.RunnerGroupId,
        }, stored.Binding);
    }

    private static bool CoversDeployedGrants(
        RuntimeConfiguration current, RunnerProfile profile, RuntimeConfiguration candidate, RunnerProfile next)
    {
        var before = profile.Provisioner;
        var after = next.Provisioner;
        var inherited = current.GitHub[profile.GitHub.Connection];
        var replacement = candidate.GitHub[next.GitHub.Connection];
        var oldNetwork = current.Networks[before.Network];
        var newNetwork = candidate.Networks[after.Network];
        if (before.BootstrapRoleArn != after.BootstrapRoleArn || before.BootstrapProtocol != after.BootstrapProtocol ||
            inherited.SecretArn != replacement.SecretArn ||
            oldNetwork.VpcId != newNetwork.VpcId || !oldNetwork.Subnets.All(newNetwork.Subnets.Contains) ||
            !oldNetwork.SecurityGroupIds.All(newNetwork.SecurityGroupIds.Contains))
        {
            return false;
        }
        return (before, after) switch
        {
            (Ec2ProvisionerConfiguration ec2, Ec2ProvisionerConfiguration nextEc2) =>
                ec2.LaunchTemplateId == nextEc2.LaunchTemplateId && ec2.InstanceProfileArn == nextEc2.InstanceProfileArn,
            (FargateProvisionerConfiguration fargate, FargateProvisionerConfiguration nextFargate) =>
                fargate.TaskDefinitionArn == nextFargate.TaskDefinitionArn && fargate.ExecutionRoleArn == nextFargate.ExecutionRoleArn &&
                ClusterScope(current, fargate) == ClusterScope(candidate, nextFargate),
            _ => false,
        };
    }

    private static (string Arn, bool Created) ClusterScope(RuntimeConfiguration configuration, FargateProvisionerConfiguration profile) =>
        configuration.EcsClusters[profile.Cluster] switch
        {
            ImportEcsClusterConfiguration imported => (imported.Arn, false),
            CreateEcsClusterConfiguration created => ($"arn:{profile.BootstrapRoleArn.Split(':')[1]}:ecs:" +
                $"{configuration.Aws.Region}:{configuration.Aws.Account}:cluster/{created.Name}", true),
            _ => throw new InvalidOperationException("The configured ECS cluster type is unsupported."),
        };
}
