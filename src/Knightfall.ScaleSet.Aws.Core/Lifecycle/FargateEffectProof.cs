using System.Security.Cryptography;
using System.Text;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record FargateLaunchOwnershipTags(
    string Controller, string Deployment, string Profile, string Allocation, string BootstrapProtocol);

public static class FargateExecutorIncarnation
{
    public static Guid Compute(string taskArn, string taskDefinitionArn, Guid allocationId)
    {
        string identity = $"{taskArn}\n{taskDefinitionArn}\n{allocationId:D}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}

internal static class FargateEffectProof
{
    internal static void ValidateOwnership(LifecycleProfile profile, FargateLaunchEffectRequest request)
    {
        var tags = request.OwnershipTags!;
        string[] arn = request.ClusterArn.Split(':', 6);
        if (tags.Controller != RunnerOwnershipTags.ControllerValue || tags.Deployment != profile.Deployment ||
            tags.Profile != profile.ProfileId || tags.Allocation != request.AllocationId.ToString("D") || tags.BootstrapProtocol != "v1" ||
            !profile.Deployment.StartsWith(arn[4] + ":" + arn[3] + ":", StringComparison.Ordinal))
        {
            throw new ArgumentException("Fargate ownership must identify the exact deployment, profile, and allocation.", nameof(request));
        }
    }

    internal static bool Matches(LifecycleProfile profile, FargateLaunchEffectRequest request,
        ExecutorBinding binding, ExecutorResourceObservation observation) =>
        MatchesBinding(profile, request.AllocationId, request.ClusterArn, request.TaskDefinitionArn, binding) &&
        ValidObservation(observation);

    internal static bool Matches(LifecycleProfile profile, FargateStopEffectRequest request,
        ExecutorBinding binding, ExecutorResourceObservation observation) =>
        binding.ResourceArn == request.TaskArn && (request.Incarnation is null || request.Incarnation == binding.Incarnation) &&
        MatchesBinding(profile, request.AllocationId, request.ClusterArn, request.TaskDefinitionArn, binding) &&
        observation.State == ExecutorResourceState.IrreversiblyTerminated && ValidObservation(observation);

    private static bool MatchesBinding(LifecycleProfile profile, Guid allocationId, string clusterArn, string taskDefinitionArn,
        ExecutorBinding binding)
    {
        if (binding.Profile != profile || binding.AllocationId != allocationId.ToString("D") ||
            binding.Provider is not FargateExecutorIdentity provider || provider.ClusterArn != clusterArn ||
            provider.TaskDefinitionArn != taskDefinitionArn || !EffectRequestIdentifiers.IsMatch(binding.ResourceArn, EffectRequestIdentifiers.EcsTaskArn))
        {
            return false;
        }

        string taskPrefix = clusterArn.Replace(":cluster/", ":task/", StringComparison.Ordinal) + "/";
        return binding.ResourceArn.StartsWith(taskPrefix, StringComparison.Ordinal) &&
            binding.Incarnation == FargateExecutorIncarnation.Compute(binding.ResourceArn, taskDefinitionArn, allocationId);
    }

    private static bool ValidObservation(ExecutorResourceObservation observation) =>
        Enum.IsDefined(observation.State) && observation.ObservedAt != default && observation.EvidenceId != Guid.Empty &&
        observation.ProviderVersion is not < 0 &&
        (observation.StartedAt is not { } start || (start != default && start <= observation.ObservedAt)) &&
        (observation.State != ExecutorResourceState.Starting || observation.StartedAt.HasValue);
}
