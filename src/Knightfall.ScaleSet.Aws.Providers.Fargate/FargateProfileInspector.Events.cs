using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

public sealed partial class FargateProfileInspector
{
    internal async Task<FargateTaskEventRead?> ReadEventAsync(RuntimeConfiguration configuration, FargateTaskEvent hint,
        CancellationToken cancellationToken)
    {
        var targets = configuration.Profiles.Where(item => item.State != RunnerProfileState.Disabled &&
                item.Provisioner is FargateProvisionerConfiguration candidate && candidate.TaskDefinitionArn == hint.TaskDefinitionArn)
            .Select(item => GetTarget(configuration, item.Id).Inventory)
            .Where(target => target.Scope.ClusterArn == hint.ClusterArn && IsTaskArn(target, hint.TaskArn)).ToArray();
        if (targets.Length == 0)
        {
            return new FargateTaskEventRead();
        }

        var task = await DescribeExactTaskAsync(targets[0], hint.TaskArn, cancellationToken).ConfigureAwait(false);
        if (task is null || task.TaskDefinitionArn != hint.TaskDefinitionArn || task.Version is not long version || version < hint.Version)
        {
            return null;
        }

        var tags = ReadOwnershipTags(task.Tags);
        if (tags is null)
        {
            return null;
        }

        var owner = targets.SingleOrDefault(target => ClaimsProfile(target, tags));
        if (owner is null)
        {
            return new FargateTaskEventRead();
        }

        var observedAt = _clock.GetUtcNow();
        var observation = ParseTask(owner, task, tags, observedAt, observedAt, out bool exact);
        if (!exact || !observation.IsOwned)
        {
            return null;
        }

        var profile = new LifecycleProfile(owner.Scope.Deployment, owner.Scope.ProfileId);
        var (binding, resource) = BindTask(profile, owner.Scope, observation, observedAt);
        return new FargateTaskEventRead(version, binding, resource);
    }
}

internal sealed record FargateTaskEventRead(long? Version = null,
    ExecutorBinding? Binding = null, ExecutorResourceObservation? Resource = null);
