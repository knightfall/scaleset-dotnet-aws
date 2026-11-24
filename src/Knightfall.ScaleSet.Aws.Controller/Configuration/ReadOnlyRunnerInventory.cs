using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.Providers.Fargate;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

internal static class ReadOnlyRunnerInventory
{
    internal static async Task<IReadOnlyDictionary<string, InventorySample>?> ReadAsync(
        RuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.DryRun.InspectAws)
        {
            return null;
        }

        var clock = TimeProvider.System;
        var enabled = configuration.Profiles.Where(profile => profile.State != RunnerProfileState.Disabled).ToLookup(profile => profile.Provisioner.Type);
        var fargate = enabled[RunnerProvisionerType.Fargate];
        FargateRuntimeEvidence? evidence = fargate.Any()
            ? FargateRuntimeEvidence.Load(configuration, Environment.GetEnvironmentVariable(FargateRuntimeEvidence.EnvironmentVariable))
            : null;
        var endpoint = ControllerAwsClientConfiguration.ReadLoopbackEndpoint(Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL"));
        using var clients = await ControllerCloudClients.ConnectAsync(configuration.Aws, endpoint, cancellationToken).ConfigureAwait(false);
        var snapshots = new Dictionary<string, InventorySample>(StringComparer.Ordinal);

        var ec2 = enabled[RunnerProvisionerType.Ec2];
        if (ec2.Any())
        {
            var inspector = clients.Ec2Inventory(clock);
            foreach (var profile in ec2)
            {
                var inventory = await inspector.ReadInventoryAsync(configuration, profile.Id, cancellationToken).ConfigureAwait(false);
                var preflight = await inspector.ResolvePreflightAsync(configuration, profile.Id, cancellationToken).ConfigureAwait(false);
                snapshots.Add(profile.Id, ProjectEc2(inventory, preflight.Template));
            }
        }
        if (evidence is not null)
        {
            var inspector = new FargateProfileInspector(clients.Ecs(), clients.Iam(), new FargateNetworkReader(clients.Ec2Network()), clock);
            foreach (var profile in fargate)
            {
                var inventory = await inspector.ReadInventoryAsync(configuration, profile.Id, cancellationToken).ConfigureAwait(false);
                var headroom = new FargateHeadroom(null, null, null);
                if (evidence.ForProfile(profile.Id) is { } approval)
                {
                    var preflight = await inspector.ResolvePreflightAsync(configuration, profile.Id, approval, cancellationToken).ConfigureAwait(false);
                    if (preflight is { IsReady: true, Task: { } approved })
                    {
                        // This graph does not attest pending launch reservations. Known exhaustion still bounds capacity.
                        var sampled = inspector.EstimateHeadroom(approved, inventory, evidence.Quota, outstanding: []);
                        headroom = sampled with { LaunchSlots = sampled.LaunchSlots is 0 ? 0 : null };
                    }
                }
                snapshots.Add(profile.Id, inventory.ToPlanningInventory(headroom));
            }
        }
        return snapshots;
    }

    private static InventorySample ProjectEc2(Ec2InventorySnapshot inventory, Ec2ResolvedTemplate? template)
    {
        var slots = inventory.Instances.Where(instance => instance.State != Ec2InstanceState.Terminated)
            .Select(instance =>
            {
                bool verified = instance.IsAuthoritative && instance.Incarnation.HasValue && instance.AllocationId.HasValue;
                var start = instance.OriginalLaunchTime <= inventory.ObservedAt ? instance.OriginalLaunchTime : null;
                return new RunnerSlot(instance.InstanceId, instance.State == Ec2InstanceState.Pending && start.HasValue
                    ? RunnerSlotState.Starting : RunnerSlotState.Unknown, verified, start);
            }).ToImmutableArray();
        int? headroom = template is { Subnets.IsDefaultOrEmpty: false } && template.Subnets.All(subnet => subnet.AvailableIpAddresses is >= 0)
            ? (int)Math.Min(int.MaxValue, template.Subnets.Sum(subnet => (long)subnet.AvailableIpAddresses!.Value)) : null;
        return new InventorySample(inventory.ObservedAt, inventory.IsComplete, null, headroom, slots);
    }
}
