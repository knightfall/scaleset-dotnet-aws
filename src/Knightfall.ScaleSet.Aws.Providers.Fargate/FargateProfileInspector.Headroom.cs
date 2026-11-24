namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

public sealed partial class FargateProfileInspector
{
    private static readonly TimeSpan ObservationFreshness = TimeSpan.FromMinutes(10);

    public FargateHeadroom EstimateHeadroom(FargateResolvedTask task,
        FargateInventorySnapshot inventory, FargateQuotaEvidence? quota, IReadOnlyCollection<FargateLaunchReservation>? outstanding) =>
        EstimateHeadroom(task, inventory, quota, outstanding, []);

    public FargateHeadroom EstimateHeadroom(FargateResolvedTask task,
        FargateInventorySnapshot inventory, FargateQuotaEvidence? quota, IReadOnlyCollection<FargateLaunchReservation>? outstanding,
        IReadOnlyCollection<FargateInventorySnapshot> otherProfiles)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(inventory);
        var now = _clock.GetUtcNow();
        if (!inventory.IsProfileComplete || inventory.Scope != task.Scope || !Fresh(inventory.ObservedAt, now) ||
            inventory.Tasks.IsDefault || outstanding is null || task.Subnets.IsDefaultOrEmpty || task.CpuUnits <= 0)
        {
            return new FargateHeadroom(null, null, null);
        }

        var requested = new HashSet<Guid>();
        var attached = new Dictionary<(FargateProfileScope Scope, Guid Allocation), (FargateTaskObservation Task, DateTimeOffset InventoryAt)>();
        var snapshots = otherProfiles.Where(sample => sample.IsProfileComplete && !sample.Tasks.IsDefault &&
            sample.Scope.Account == task.Scope.Account && sample.Scope.Region == task.Scope.Region &&
            sample.Scope.Deployment == task.Scope.Deployment && sample.Scope.ProfileId != task.Scope.ProfileId &&
            Fresh(sample.ObservedAt, now)).Prepend(inventory);
        foreach (var sample in snapshots)
        {
            foreach (var observation in sample.Tasks)
            {
                if (!observation.IsOwned || observation.AttachedSubnetIds.IsDefaultOrEmpty || observation.State == FargateTaskState.Stopped)
                {
                    continue;
                }
                if (observation.AllocationId is not { } allocation ||
                    !attached.TryAdd((sample.Scope, allocation), (observation, sample.ObservedAt)))
                {
                    return new FargateHeadroom(null, null, null);
                }
            }
        }
        var scopeSubnets = task.Subnets.Select(subnet => subnet.SubnetId).ToHashSet(StringComparer.Ordinal);
        long reservedIp = 0;
        long reservedCpu = 0;
        foreach (var launch in outstanding)
        {
            if (launch is null || launch.AllocationId == Guid.Empty || launch.CpuUnits <= 0 ||
                launch.EligibleSubnetIds.IsDefaultOrEmpty || !requested.Add(launch.AllocationId))
            {
                return new FargateHeadroom(null, null, null);
            }

            var launchScope = launch.Scope ?? inventory.Scope;
            bool observedAttached = attached.TryGetValue((launchScope, launch.AllocationId), out var resource) &&
                resource.Task.TaskDefinitionArn == launchScope.TaskDefinitionArn &&
                (launch.TaskArn is null || launch.TaskArn == resource.Task.ResourceArn) &&
                resource.Task.AttachedSubnetIds.All(launch.EligibleSubnetIds.Contains);
            DateTimeOffset? attachment = observedAttached && resource.Task.AttachmentObservedAt is { } observed &&
                observed >= resource.InventoryAt && Fresh(observed, now) ? observed : null;
            // Only an attachment observed before sampling is already represented in that sample.
            if (launch.EligibleSubnetIds.Any(scopeSubnets.Contains) &&
                (attachment is not { } attachedForIp || attachedForIp >= task.SubnetSampleStartedAt))
            {
                reservedIp++;
            }
            if (attachment is not { } attachedForCpu || quota is null || attachedForCpu >= quota.ObservedAt)
            {
                reservedCpu += launch.CpuUnits;
            }
        }

        int? ipSlots = Fresh(task.SubnetSampleStartedAt, now) && task.Subnets.All(subnet => subnet.AvailableIpAddresses is >= 0)
            ? ClampSlots(task.Subnets.Sum(subnet => (long)subnet.AvailableIpAddresses!.Value) - reservedIp) : null;
        int? cpuSlots = quota is { RemainingCpuUnits: >= 0, ReservedCpuUnits: >= 0 } &&
            quota.Account == task.Scope.Account && quota.Region == task.Scope.Region &&
            Fresh(quota.ObservedAt, now) && quota.ExpiresAt > now
            ? ClampSlots(Math.Max(0, Math.Max(0, quota.RemainingCpuUnits - quota.ReservedCpuUnits) - reservedCpu) / task.CpuUnits) : null;
        int? launchSlots = ipSlots.HasValue && cpuSlots.HasValue ? Math.Min(ipSlots.Value, cpuSlots.Value) : null;
        return new FargateHeadroom(ipSlots, cpuSlots, launchSlots);
    }

    private static bool Fresh(DateTimeOffset observedAt, DateTimeOffset now) =>
        observedAt != default && observedAt <= now && now - observedAt <= ObservationFreshness;

    private static int ClampSlots(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}
