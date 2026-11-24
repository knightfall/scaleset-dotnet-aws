using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Knightfall.ScaleSet.Aws.Core.Configuration;

public sealed class RuntimeConfiguration
{
    internal RuntimeConfiguration(
        string environmentName,
        string sha256,
        AwsEnvironment aws,
        ControllerMode mode,
        DryRunConfiguration dryRun,
        ControllerHostingConfiguration controller,
        TrackingTableConfiguration trackingTable,
        FrozenDictionary<string, NetworkConfiguration> networks,
        FrozenDictionary<string, EcsClusterConfiguration> ecsClusters,
        FrozenDictionary<string, GitHubConnectionConfiguration> gitHub,
        ImmutableArray<RunnerProfile> profiles)
    {
        EnvironmentName = environmentName;
        Sha256 = sha256;
        Aws = aws;
        Mode = mode;
        DryRun = dryRun;
        Controller = controller;
        TrackingTable = trackingTable;
        Networks = networks;
        EcsClusters = ecsClusters;
        GitHub = gitHub;
        Profiles = profiles;
    }

    public string EnvironmentName { get; }
    public string Sha256 { get; }
    public AwsEnvironment Aws { get; }
    public ControllerMode Mode { get; }
    public DryRunConfiguration DryRun { get; }
    public ControllerHostingConfiguration Controller { get; }
    public TrackingTableConfiguration TrackingTable { get; }
    public FrozenDictionary<string, NetworkConfiguration> Networks { get; }
    public FrozenDictionary<string, EcsClusterConfiguration> EcsClusters { get; }
    public FrozenDictionary<string, GitHubConnectionConfiguration> GitHub { get; }
    public ImmutableArray<RunnerProfile> Profiles { get; }
}

public enum ControllerMode
{
    DryRun = 0,
    Active = 1,
}

public sealed record AwsEnvironment(string Account, string Region);

public sealed record DryRunConfiguration(DryRunDemandConfiguration Demand, bool InspectAws);

public abstract record DryRunDemandConfiguration;

public sealed record NoDryRunDemandConfiguration : DryRunDemandConfiguration;

public sealed record LocalSnapshotDemandConfiguration(string Path) : DryRunDemandConfiguration;

public sealed record S3SnapshotDemandConfiguration(string S3Uri, string VersionId, string Sha256)
    : DryRunDemandConfiguration;

public sealed record LocalReplayDemandConfiguration(string Path) : DryRunDemandConfiguration;

public sealed record ControllerHostingConfiguration(
    string Image,
    string Network,
    string Cluster,
    RunnerArchitecture CpuArchitecture,
    int Cpu,
    int MemoryMiB);

public abstract record TrackingTableConfiguration(string Name);

public sealed record CreateTrackingTableConfiguration(string Name) : TrackingTableConfiguration(Name);

public sealed record ImportTrackingTableConfiguration(string Name) : TrackingTableConfiguration(Name);

public sealed record NetworkConfiguration(
    string VpcId,
    ImmutableArray<SubnetConfiguration> Subnets,
    ImmutableArray<string> SecurityGroupIds);

public sealed record SubnetConfiguration(string Id, string AvailabilityZone);

public abstract record EcsClusterConfiguration;

public sealed record CreateEcsClusterConfiguration(string Name) : EcsClusterConfiguration;

public sealed record ImportEcsClusterConfiguration(string Arn) : EcsClusterConfiguration;

public enum GitHubAuthType
{
    GitHubApp = 0,
    EnterprisePat = 1,
}

public sealed record GitHubConnectionConfiguration(string ScopeUrl, GitHubAuthType AuthType, string SecretArn);
