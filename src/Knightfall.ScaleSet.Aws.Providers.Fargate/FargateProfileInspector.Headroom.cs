namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

public sealed partial class FargateProfileInspector
{
    private static readonly TimeSpan ObservationFreshness = TimeSpan.FromMinutes(10);

    public FargateHeadroom EstimateHeadroom(FargateResolvedTask task,
        FargateInventorySnapshot inventory, FargateQuotaEvidence? quota, IReadOnlyCollection<FargateLaunchReservation>? outstanding)
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
        var attached = new Dictionary<Guid, FargateTaskObservation>();
        foreach (var observation in inventory.Tasks.Where(observation => observation.IsOwned && !observation.AttachedSubnetIds.IsDefaultOrEmpty))
        {
            if (observation.AllocationId is not { } allocation || !attached.TryAdd(allocation, observation))
            {
                return new FargateHeadroom(null, null, null);
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

            bool observedAttached = attached.TryGetValue(launch.AllocationId, out var resource) &&
                (launch.TaskArn is null || launch.TaskArn == resource.ResourceArn) &&
                resource.AttachedSubnetIds.All(launch.EligibleSubnetIds.Contains);
            DateTimeOffset? attachment = observedAttached && resource!.AttachmentObservedAt is { } observed &&
                observed >= inventory.ObservedAt && Fresh(observed, now) ? observed : null;
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
