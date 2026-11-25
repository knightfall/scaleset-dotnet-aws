using System.Collections.Immutable;

namespace Knightfall.ScaleSet.Aws.Core.Configuration;

public sealed record RunnerProfile(
    string Id,
    RunnerProfileState State,
    RunnerProfilePurpose Purpose,
    RunnerPlatformConfiguration Platform,
    RunnerCapacityConfiguration Capacity,
    RunnerGitHubConfiguration GitHub,
    RunnerProvisionerConfiguration Provisioner);

public enum RunnerProfileState
{
    Active = 0,
    Draining = 1,
    Disabled = 2,
}

public enum RunnerProfilePurpose
{
    CopilotCloudAgent = 0,
    FargateCompatible = 1,
    Ec2General = 2,
}

public enum RunnerOperatingSystem
{
    Linux = 0,
    Windows = 1,
}

public enum RunnerArchitecture
{
    X64 = 0,
    Arm64 = 1,
}

public sealed record RunnerPlatformConfiguration(RunnerOperatingSystem OperatingSystem, RunnerArchitecture Architecture);

public sealed record RunnerCapacityConfiguration(int IdleReserve, int MaxRunners, int StartupTimeoutSeconds);

public enum GitHubRegistration
{
    Existing = 0,
    CreateIfMissing = 1,
}

public sealed record RunnerGitHubConfiguration(
    string Connection,
    string RunnerGroup,
    string ScaleSet,
    long? ScaleSetId,
    ImmutableArray<string> Labels,
    GitHubRegistration Registration);

public enum RunnerProvisionerType
{
    Ec2 = 0,
    Fargate = 1,
}

public abstract record RunnerProvisionerConfiguration(
    RunnerProvisionerType Type,
    string Network,
    string BootstrapRoleArn,
    string BootstrapProtocol);

public sealed record Ec2ProvisionerConfiguration(
    string Network,
    string BootstrapRoleArn,
    string BootstrapProtocol,
    string LaunchTemplateId,
    LaunchTemplateVersionConfiguration LaunchTemplateVersion,
    string InstanceProfileArn,
    string? InstanceType,
    Ec2Tenancy? Tenancy,
    Ec2SubnetStrategy? SubnetStrategy)
    : RunnerProvisionerConfiguration(RunnerProvisionerType.Ec2, Network, BootstrapRoleArn, BootstrapProtocol)
{
    public Ec2StatusQualification? StatusQualification { get; init; }
}

public sealed record Ec2StatusQualification(string ImageId, string RoleId, string EvidenceSha256);

public sealed record FargateProvisionerConfiguration(
    string Network,
    string BootstrapRoleArn,
    string BootstrapProtocol,
    string Cluster,
    string TaskDefinitionArn,
    string RunnerContainer,
    string ExecutionRoleArn)
    : RunnerProvisionerConfiguration(RunnerProvisionerType.Fargate, Network, BootstrapRoleArn, BootstrapProtocol);

public abstract record LaunchTemplateVersionConfiguration;

public sealed record PinnedLaunchTemplateVersion(string Version) : LaunchTemplateVersionConfiguration;

public sealed record AliasLaunchTemplateVersion(LaunchTemplateAlias Name) : LaunchTemplateVersionConfiguration;

public enum LaunchTemplateAlias
{
    Latest = 0,
    Default = 1,
}

public enum Ec2Tenancy
{
    Default = 0,
    Dedicated = 1,
}

public enum Ec2SubnetStrategy
{
    RoundRobin = 0,
    FailureWeighted = 1,
}
