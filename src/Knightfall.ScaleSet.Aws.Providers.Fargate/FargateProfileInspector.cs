using System.Buffers;
using System.Globalization;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using RunnerNetwork = Knightfall.ScaleSet.Aws.Core.Configuration.NetworkConfiguration;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

public sealed partial class FargateProfileInspector
{
    private const int MaximumPages = 256;
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private readonly IAmazonECS _ecs;
    private readonly IAmazonIdentityManagementService _iam;
    private readonly FargateNetworkReader _network;
    private readonly TimeProvider _clock;

    public FargateProfileInspector(IAmazonECS ecs, IAmazonIdentityManagementService iam,
        FargateNetworkReader network, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(ecs);
        ArgumentNullException.ThrowIfNull(iam);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(clock);
        _ecs = ecs;
        _iam = iam;
        _network = network;
        _clock = clock;
    }

    public async Task<FargatePreflightResult> ResolvePreflightAsync(
        RuntimeConfiguration configuration, string profileId, FargateContainerApproval approval, CancellationToken cancellationToken)
    {
        var target = GetTarget(configuration, profileId);
        using var operation = FargateDiagnostics.Start("task_definition", TelemetryProfile.From(target.Profile), cancellationToken);
        var result = await ResolvePreflightAsync(target, approval, cancellationToken).ConfigureAwait(false);
        string outcome = result.Status switch
        {
            FargatePreflightStatus.Verified => "success",
            FargatePreflightStatus.AwsUnavailable => "unknown",
            _ => "rejected",
        };
        operation?.Complete(outcome, PreflightReason(result.Status));
        return result;
    }

    private static string PreflightReason(FargatePreflightStatus status) => status switch
    {
        FargatePreflightStatus.Verified => "task_definition_verified",
        FargatePreflightStatus.AwsUnavailable => "aws_unavailable",
        FargatePreflightStatus.ClusterMismatch => "cluster_mismatch",
        FargatePreflightStatus.LongTaskArnsUnverified => "long_arns_unverified",
        FargatePreflightStatus.RoleMismatch => "role_mismatch",
        FargatePreflightStatus.NetworkIncompatible => "network_incompatible",
        FargatePreflightStatus.ContainerUnapproved => "container_unapproved",
        _ => "task_definition_incompatible",
    };

    private async Task<FargatePreflightResult> ResolvePreflightAsync(InspectionTarget target, FargateContainerApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (!HasImageDigest(approval.Image) || approval.EntryPoint.IsDefault || approval.Command.IsDefault)
        {
            return Failed(FargatePreflightStatus.ContainerUnapproved);
        }

        try
        {
            if (!await IsClusterActiveAsync(target.Scope.ClusterArn, cancellationToken).ConfigureAwait(false))
            {
                return Failed(FargatePreflightStatus.ClusterMismatch);
            }
            if (!await HasLongArnsAsync(target, cancellationToken).ConfigureAwait(false))
            {
                return Failed(FargatePreflightStatus.LongTaskArnsUnverified);
            }

            var described = await _ecs.DescribeTaskDefinitionAsync(
                new DescribeTaskDefinitionRequest { TaskDefinition = target.Scope.TaskDefinitionArn }, cancellationToken).ConfigureAwait(false);
            var definition = described.TaskDefinition;
            if (definition is not { Status.Value: "ACTIVE" } || definition.TaskDefinitionArn != target.Scope.TaskDefinitionArn ||
                definition.Revision is not int revision || revision <= 0 ||
                !target.Scope.TaskDefinitionArn.EndsWith(":" + revision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return Failed(FargatePreflightStatus.TaskDefinitionMismatch);
            }
            if (!ValidPlatform(definition, target.Profile.Platform))
            {
                return Failed(FargatePreflightStatus.TaskPlatformMismatch);
            }
            if (!int.TryParse(definition.Cpu, NumberStyles.None, CultureInfo.InvariantCulture, out int cpu) ||
                !int.TryParse(definition.Memory, NumberStyles.None, CultureInfo.InvariantCulture, out int memory) || !ValidTaskSize(cpu, memory))
            {
                return Failed(FargatePreflightStatus.InvalidTaskSize);
            }
            if (!ValidContainer(definition, target.Provisioner.RunnerContainer, approval))
            {
                return Failed(FargatePreflightStatus.ContainerUnapproved);
            }
            if (definition.TaskRoleArn != target.Provisioner.BootstrapRoleArn || definition.ExecutionRoleArn != target.Provisioner.ExecutionRoleArn)
            {
                return Failed(FargatePreflightStatus.RoleMismatch);
            }
            string? taskRoleId = await ReadRoleIdAsync(definition.TaskRoleArn, cancellationToken).ConfigureAwait(false);
            string? executionRoleId = await ReadRoleIdAsync(definition.ExecutionRoleArn, cancellationToken).ConfigureAwait(false);
            if (taskRoleId is null || executionRoleId is null)
            {
                return Failed(FargatePreflightStatus.RoleMismatch);
            }
            var (subnets, subnetSampleStartedAt) = await _network.ReadAsync(target.Network, _clock, cancellationToken)
                .ConfigureAwait(false);
            return subnets.IsDefault ? Failed(FargatePreflightStatus.NetworkIncompatible)
                : new FargatePreflightResult(FargatePreflightStatus.Verified, new FargateResolvedTask(target.Scope,
                    _clock.GetUtcNow(), revision, cpu, memory, target.Provisioner.RunnerContainer, definition.TaskRoleArn,
                    taskRoleId, definition.ExecutionRoleArn, executionRoleId, subnets, target.Network.SecurityGroupIds,
                    subnetSampleStartedAt));
        }
        catch (Exception error) when (IsReadUnavailable(error, cancellationToken))
        {
            return Failed(FargatePreflightStatus.AwsUnavailable);
        }
    }

    private async Task<bool> IsClusterActiveAsync(string clusterArn, CancellationToken cancellationToken)
    {
        var reply = await _ecs.DescribeClustersAsync(new DescribeClustersRequest { Clusters = [clusterArn] }, cancellationToken)
            .ConfigureAwait(false);
        return reply.Failures is not { Count: > 0 } && reply.Clusters is [{ Status: "ACTIVE" } cluster] &&
            cluster.ClusterArn == clusterArn;
    }

    private async Task<bool> HasLongArnsAsync(InspectionTarget target, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        bool found = false;
        for (int page = 0; page < MaximumPages; page++)
        {
            var reply = await _ecs.ListAccountSettingsAsync(new ListAccountSettingsRequest
            {
                Name = SettingName.TaskLongArnFormat, EffectiveSettings = true, NextToken = next,
            }, cancellationToken).ConfigureAwait(false);
            if (reply.Settings is null)
            {
                return false;
            }
            foreach (var setting in reply.Settings)
            {
                if (found || setting is not { Name.Value: "taskLongArnFormat", Value: "enabled" } ||
                    (setting.PrincipalArn is { } principal && !principal.StartsWith(target.PrincipalArnPrefix, StringComparison.Ordinal)))
                {
                    return false;
                }
                found = true;
            }
            next = reply.NextToken;
            if (string.IsNullOrEmpty(next))
            {
                return found;
            }
            if (!visited.Add(next))
            {
                return false;
            }
        }
        return false;
    }

    private async Task<string?> ReadRoleIdAsync(string arn, CancellationToken cancellationToken)
    {
        string roleName = arn[(arn.LastIndexOf('/') + 1)..];
        var role = (await _iam.GetRoleAsync(new GetRoleRequest { RoleName = roleName }, cancellationToken).ConfigureAwait(false)).Role;
        return role is { RoleId.Length: > 0 } && role.Arn == arn && role.RoleName == roleName ? role.RoleId : null;
    }

    private InspectionTarget GetTarget(RuntimeConfiguration configuration, string profileId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var profile = configuration.Profiles.SingleOrDefault(candidate => candidate.Id == profileId);
        if (profile?.Provisioner is not FargateProvisionerConfiguration provisioner || profile.State == RunnerProfileState.Disabled ||
            (configuration.Mode == ControllerMode.DryRun && !configuration.DryRun.InspectAws))
        {
            throw new ArgumentException("Fargate inspection requires an enabled Fargate profile with AWS inspection authorized.", nameof(profileId));
        }

        var aws = configuration.Aws;
        string? ecsRegion = _ecs.Config.RegionEndpoint?.SystemName ?? _ecs.Config.AuthenticationRegion;
        if (ecsRegion != aws.Region || _network.Region != aws.Region)
        {
            throw new ArgumentException("Fargate inspection must use the configured AWS Region.", nameof(configuration));
        }
        string partition = provisioner.TaskDefinitionArn.Split(':')[1];
        string regionScope = $"arn:{partition}:ecs:{aws.Region}:{aws.Account}:";
        string cluster = configuration.EcsClusters[provisioner.Cluster] switch
        {
            CreateEcsClusterConfiguration created => regionScope + "cluster/" + created.Name,
            ImportEcsClusterConfiguration imported => imported.Arn,
            _ => throw new ArgumentException("A Fargate runner cluster is required.", nameof(configuration)),
        };
        var scope = new FargateProfileScope(aws.Account, aws.Region, RunnerOwnershipTags.DeploymentIdentity(configuration),
            profile.Id, cluster, provisioner.TaskDefinitionArn);
        return new InspectionTarget(scope, profile, provisioner, configuration.Networks[provisioner.Network],
            $"arn:{partition}:iam::{aws.Account}:");
    }

    private static bool ValidPlatform(TaskDefinition definition, RunnerPlatformConfiguration platform)
    {
        string approvedArchitecture = platform.Architecture == RunnerArchitecture.Arm64 ? "ARM64" : "X86_64";
        return platform.OperatingSystem == RunnerOperatingSystem.Linux && definition.NetworkMode == NetworkMode.Awsvpc &&
            definition.RequiresCompatibilities?.Contains("FARGATE", StringComparer.Ordinal) == true &&
            (definition.RuntimePlatform?.OperatingSystemFamily?.Value ?? "LINUX") == "LINUX" &&
            (definition.RuntimePlatform?.CpuArchitecture?.Value ?? "X86_64") == approvedArchitecture;
    }

    private static bool ValidTaskSize(int cpu, int memory) => cpu switch
    {
        256 => memory is 512 or 1024 or 2048,
        512 => InIncrements(memory, 1024, 4096, 1024),
        1024 => InIncrements(memory, 2048, 8192, 1024),
        2048 => InIncrements(memory, 4096, 16384, 1024),
        4096 => InIncrements(memory, 8192, 30720, 1024),
        8192 => InIncrements(memory, 16384, 61440, 4096),
        16384 => InIncrements(memory, 32768, 122880, 8192),
        32768 => memory is 61440 or 122880 or 249856,
        _ => false,
    };

    private static bool InIncrements(int value, int minimum, int maximum, int step) =>
        value >= minimum && value <= maximum && value % step == 0;

    private static bool ValidContainer(TaskDefinition definition, string runnerContainer, FargateContainerApproval approval)
    {
        if (definition.PidMode is not null ||
            definition.ContainerDefinitions is not [var container] || container is null || container.Name != runnerContainer ||
            container.Essential == false || container.Privileged == true || container.RestartPolicy?.Enabled == true ||
            container.HealthCheck is not null || container.LinuxParameters?.InitProcessEnabled == true ||
            container.ReadonlyRootFilesystem != true || container.Image != approval.Image ||
            !(container.EntryPoint ?? []).SequenceEqual(approval.EntryPoint, StringComparer.Ordinal) ||
            !(container.Command ?? []).SequenceEqual(approval.Command, StringComparer.Ordinal))
        {
            return false;
        }
        if (container.Environment is { Count: > 0 } || container.EnvironmentFiles is { Count: > 0 } ||
            container.Secrets is { Count: > 0 } || container.LinuxParameters?.Capabilities?.Add is { Count: > 0 } ||
            container.LinuxParameters?.Devices is { Count: > 0 } || container.DockerSecurityOptions is { Count: > 0 })
        {
            return false;
        }
        if (container.User is { } user && user.Split(':', 2)[0] is "0" or "root")
        {
            return false;
        }
        return ValidScratchMounts(definition, container, approval) && ValidLogging(container.LogConfiguration);
    }

    private static bool ValidScratchMounts(TaskDefinition definition, ContainerDefinition container, FargateContainerApproval approval)
    {
        var mounts = container.MountPoints ?? [];
        var volumes = definition.Volumes ?? [];
        if (container.VolumesFrom is { Count: > 0 } || mounts.Count != volumes.Count)
        {
            return false;
        }
        if (mounts.Count == 0)
        {
            return true;
        }
        if (approval.ScratchMountPaths.IsDefaultOrEmpty || approval.EntryPoint.IsDefaultOrEmpty ||
            approval.EntryPoint[0]?.StartsWith('/') != true)
        {
            return false;
        }

        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var volume in volumes)
        {
            if (volume is not { Name.Length: > 0, ConfiguredAtLaunch: not true, DockerVolumeConfiguration: null,
                    EfsVolumeConfiguration: null, FsxWindowsFileServerVolumeConfiguration: null, S3filesVolumeConfiguration: null } ||
                !string.IsNullOrEmpty(volume.Host?.SourcePath) || !sources.Add(volume.Name))
            {
                return false;
            }
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            if (mount is not { ContainerPath: { } path, SourceVolume: { } source } || !IsSafeMountPath(path) ||
                !approval.ScratchMountPaths.Contains(path, StringComparer.Ordinal) || !paths.Add(path) || !sources.Remove(source) ||
                approval.EntryPoint.Any(argument => ShadowsPath(path, argument)) ||
                approval.Command.Any(argument => ShadowsPath(path, argument)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ShadowsPath(string mountPath, string? argument) =>
        argument == mountPath || argument?.StartsWith(mountPath + "/", StringComparison.Ordinal) == true;

    private static bool IsSafeMountPath(string? path)
    {
        if (path?.StartsWith('/') != true)
        {
            return false;
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains('\\')) ||
            path != "/" + string.Join('/', segments))
        {
            return false;
        }
        return !ShadowsPath(path, "/run/docker.sock") && !ShadowsPath(path, "/var/run/docker.sock") &&
            !ShadowsPath("/run/docker.sock", path) && !ShadowsPath("/var/run/docker.sock", path);
    }

    private static bool ValidLogging(LogConfiguration? configuration) => configuration is null ||
        (configuration.LogDriver?.Value == "awslogs" && configuration.SecretOptions is not { Count: > 0 } &&
         configuration.Options?.Keys.All(key => key is "awslogs-group" or "awslogs-region" or "awslogs-stream-prefix" or
            "awslogs-create-group" or "awslogs-multiline-pattern" or "awslogs-datetime-format" or "mode" or "max-buffer-size") != false);

    private static bool HasImageDigest(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Any(char.IsWhiteSpace))
        {
            return false;
        }
        int at = image.IndexOf("@sha256:", StringComparison.Ordinal);
        return at > 0 && IsHex(image.AsSpan(at + "@sha256:".Length), 64);
    }

    private static bool IsHex(ReadOnlySpan<char> value, int length) => value.Length == length && value.IndexOfAnyExcept(LowerHex) < 0;

    private static FargatePreflightResult Failed(FargatePreflightStatus status) => new(status, null);

    private static bool IsReadUnavailable(Exception error, CancellationToken cancellationToken) =>
        error is AmazonServiceException or HttpRequestException or TimeoutException or IOException ||
        (error is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private sealed record InspectionTarget(FargateProfileScope Scope, RunnerProfile Profile, FargateProvisionerConfiguration Provisioner,
        RunnerNetwork Network, string PrincipalArnPrefix)
    {
        public InventoryTarget Inventory { get; } = new(Scope, Provisioner.BootstrapProtocol, Provisioner.RunnerContainer,
            [.. Network.Subnets.Select(subnet => subnet.Id)]);
    }
}
