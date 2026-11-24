using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

public delegate void ProvisionerRegistration(
    IServiceCollection services,
    RuntimeConfiguration configuration,
    ImmutableArray<RunnerProfile> profiles);

public sealed record ActiveRuntimeRegistrations(
    Action<IServiceCollection, RuntimeConfiguration> OwnershipReader,
    Action<IServiceCollection, RuntimeConfiguration> LifecycleWrites,
    Action<IServiceCollection, RuntimeConfiguration> GitHubCredentials,
    Action<IServiceCollection, RuntimeConfiguration> GitHubSessions,
    Action<IServiceCollection, RuntimeConfiguration> EventConsumers,
    ProvisionerRegistration Ec2,
    ProvisionerRegistration Fargate);

public sealed record DryRunRuntimeRegistrations(
    ProvisionerRegistration Ec2Inventory,
    ProvisionerRegistration FargateInventory);

public sealed record RuntimeComposition(RuntimeConfiguration Configuration, IServiceCollection Services);

public static class RuntimeServiceComposition
{
    public static RuntimeComposition Compose(
        ReadOnlyMemory<byte> json,
        ActiveRuntimeRegistrations active,
        DryRunRuntimeRegistrations dryRun,
        string? expectedSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(dryRun);

        var configuration = RuntimeConfigurationLoader.Load(json, expectedSha256);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        if (configuration.Mode == ControllerMode.Active)
        {
            // The ownership reader serves the controller's ECS host.
            active.OwnershipReader(services, configuration);
            active.LifecycleWrites(services, configuration);
            active.GitHubCredentials(services, configuration);
            active.GitHubSessions(services, configuration);
            active.EventConsumers(services, configuration);
            RegisterSelectedBackends(services, configuration, active.Ec2, active.Fargate);
        }
        else if (configuration.DryRun.InspectAws)
        {
            RegisterSelectedBackends(services, configuration, dryRun.Ec2Inventory, dryRun.FargateInventory);
        }

        return new RuntimeComposition(configuration, services);
    }

    private static void RegisterSelectedBackends(
        IServiceCollection services,
        RuntimeConfiguration configuration,
        ProvisionerRegistration ec2,
        ProvisionerRegistration fargate)
    {
        var enabled = configuration.Profiles.Where(
            static profile => profile.State is RunnerProfileState.Active or RunnerProfileState.Draining);
        var ec2Profiles = enabled.Where(
            static profile => profile.Provisioner.Type == RunnerProvisionerType.Ec2).ToImmutableArray();
        if (!ec2Profiles.IsEmpty)
        {
            ec2(services, configuration, ec2Profiles);
        }

        var fargateProfiles = enabled.Where(
            static profile => profile.Provisioner.Type == RunnerProvisionerType.Fargate).ToImmutableArray();
        if (!fargateProfiles.IsEmpty)
        {
            fargate(services, configuration, fargateProfiles);
        }
    }
}
