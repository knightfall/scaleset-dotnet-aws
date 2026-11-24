using System.Security.Cryptography;
using System.Text;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record Ec2LaunchOwnershipTags(
    string Controller, string Deployment, string Profile, string Allocation, string BootstrapProtocol);

public enum Ec2LaunchRejection
{
    InsufficientInstanceCapacity = 1,
    InsufficientFreeAddressesInSubnet = 2,
}

public static class Ec2ExecutorIncarnation
{
    public static Guid Compute(string account, string region, string instanceId, DateTimeOffset launchTime, long numericVersion)
    {
        string tuple = FormattableString.Invariant($"{account}/{region}/{instanceId}/{launchTime.UtcTicks}/{numericVersion}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(tuple));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}

internal static class Ec2LaunchProof
{
    internal static void ValidateOwnership(this Ec2LaunchEffectRequest launch, LifecycleProfile profile)
    {
        var tags = launch.OwnershipTags!;
        var arn = launch.InstanceProfileArn.Split(':', 6);
        if (tags.Controller != RunnerOwnershipTags.ControllerValue || tags.Deployment != profile.Deployment ||
            tags.Profile != profile.ProfileId || tags.Allocation != launch.AllocationId.ToString("D") || tags.BootstrapProtocol != "v1" ||
            !profile.Deployment.StartsWith(arn[4] + ":" + launch.Region + ":", StringComparison.Ordinal))
        {
            throw new ArgumentException("The EC2 launch ownership tags must identify the exact deployment, profile, and allocation.");
        }
    }

    internal static bool Matches(
        LifecycleProfile profile, Ec2LaunchEffectRequest launch, ExecutorBinding binding, ExecutorResourceObservation observation)
    {
        if (binding.Profile != profile || binding.AllocationId != launch.AllocationId.ToString("D") ||
            binding.Provider is not Ec2ExecutorIdentity ec2 || ec2.LaunchTemplateId != launch.LaunchTemplateId ||
            ec2.LaunchTemplateVersion != launch.LaunchTemplateVersion || !ValidObservation(ec2, observation))
        {
            return false;
        }

        var instanceProfile = launch.InstanceProfileArn.Split(':', 6);
        var expectedArn = $"arn:{instanceProfile[1]}:ec2:{launch.Region}:{instanceProfile[4]}:instance/";
        return EffectRequestIdentifiers.IsMatch(binding.ResourceArn, @"\A" + EffectRequestIdentifiers.Ec2InstanceArn + @"\z") &&
            binding.ResourceArn.StartsWith(expectedArn, StringComparison.Ordinal) &&
            binding.Incarnation == Ec2ExecutorIncarnation.Compute(instanceProfile[4], launch.Region,
                binding.ResourceArn[expectedArn.Length..], ec2.InstanceLaunchTime, ec2.LaunchTemplateVersion);
    }

    internal static bool Matches(
        LifecycleProfile profile, Ec2TerminateEffectRequest terminate, ExecutorBinding binding, ExecutorResourceObservation observation) =>
        binding.Profile == profile && binding.AllocationId == terminate.AllocationId.ToString("D") &&
        binding.ResourceArn == terminate.InstanceArn && binding.Provider is Ec2ExecutorIdentity ec2 &&
        ec2.InstanceLaunchTime == terminate.InstanceLaunchTime && ec2.LaunchTemplateId == terminate.LaunchTemplateId &&
        ec2.LaunchTemplateVersion == terminate.LaunchTemplateVersion && observation.State == ExecutorResourceState.IrreversiblyTerminated &&
        ValidObservation(ec2, observation);

    private static bool ValidObservation(Ec2ExecutorIdentity ec2, ExecutorResourceObservation observation) =>
        ec2.InstanceLaunchTime != default && observation.EvidenceId != Guid.Empty && Enum.IsDefined(observation.State) &&
        observation.StartedAt == ec2.InstanceLaunchTime && observation.ObservedAt >= ec2.InstanceLaunchTime;
}
