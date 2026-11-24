using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public static class RunnerOwnershipTags
{
    public const string Controller = "scaleset-aws:controller";
    public const string ControllerValue = "scaleset-dotnet-aws";
    public const string Deployment = "scaleset-aws:deployment";
    public const string Profile = "scaleset-aws:profile";
    public const string Allocation = "scaleset-aws:allocation";
    public const string BootstrapProtocol = "scaleset-aws:bootstrap-protocol";

    public static string DeploymentIdentity(RuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return $"{configuration.Aws.Account}:{configuration.Aws.Region}:{configuration.EnvironmentName}";
    }
}
