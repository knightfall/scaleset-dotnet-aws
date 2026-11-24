using System.Security.Cryptography;
using System.Text;
using Amazon.ECS;
using Amazon.ECS.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using EcsTask = Amazon.ECS.Model.Task;
using EcsNetwork = Amazon.ECS.Model.NetworkConfiguration;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

public sealed partial class FargateProfileInspector
{
    /// <summary>Builds the immutable request to journal using approved task data and the selected subnets.</summary>
    public static FargateLaunchEffectRequest CreateLaunchRequest(FargateResolvedTask verified, Guid allocationId,
        IReadOnlyCollection<string> eligibleSubnetIds)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(eligibleSubnetIds);
        ArgumentOutOfRangeException.ThrowIfEqual(allocationId, Guid.Empty);
        var subnetIds = eligibleSubnetIds.Order(StringComparer.Ordinal).ToArray();
        if (subnetIds.Length is < 1 or > 16 || subnetIds.Distinct(StringComparer.Ordinal).Count() != subnetIds.Length ||
            !HasVerifiedSubnets(verified, subnetIds))
        {
            throw new ArgumentException("The launch requires distinct verified subnets with known and available IP capacity.",
                nameof(eligibleSubnetIds));
        }

        var scope = verified.Scope;
        var ownership = new FargateLaunchOwnershipTags(RunnerOwnershipTags.ControllerValue, scope.Deployment, scope.ProfileId,
            allocationId.ToString("D"), "v1");
        string tokenIdentity = $"fargate-launch-token\n{scope.ClusterArn}\n{scope.Deployment}\n{scope.ProfileId}\n{allocationId:D}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(tokenIdentity));
        return new FargateLaunchEffectRequest(allocationId, scope.ClusterArn, scope.TaskDefinitionArn, [.. subnetIds],
            [.. verified.SecurityGroupIds.Order(StringComparer.Ordinal)], verified.TaskRoleArn, verified.ExecutionRoleArn, verified.RunnerContainer,
            new Guid(digest.AsSpan(0, 16), bigEndian: true), ownership, verified.CpuUnits);
    }

    private static bool HasVerifiedSubnets(FargateResolvedTask verified, IReadOnlyCollection<string> subnetIds) =>
        subnetIds.All(id => verified.Subnets.Any(subnet => subnet.SubnetId == id && subnet.AvailableIpAddresses.HasValue)) &&
        verified.Subnets.Any(subnet => subnet.AvailableIpAddresses > 0 && subnetIds.Contains(subnet.SubnetId, StringComparer.Ordinal));

    /// <summary>Creates the journal's ECS effect adapter for one enabled profile of an active controller.</summary>
    public IFargateReplayEffectProvider CreateEffectProvider(RuntimeConfiguration configuration, LifecycleProfile profile,
        FargateContainerApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return CreateEffects(configuration, profile, approval);
    }

    /// <summary>Exposes exact readback and independently fenced cleanup while launch approval is unavailable.</summary>
    public IReconciliationEffectProvider CreateEffectObserver(RuntimeConfiguration configuration, LifecycleProfile profile) =>
        CreateEffects(configuration, profile, null);

    private EffectProvider CreateEffects(RuntimeConfiguration configuration, LifecycleProfile profile, FargateContainerApproval? approval)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(profile);
        if (configuration.Mode != ControllerMode.Active || profile.Deployment != RunnerOwnershipTags.DeploymentIdentity(configuration))
        {
            throw new ArgumentException("Fargate effects require the exact deployment of an active controller.", nameof(profile));
        }
        if (_ecs.Config.MaxErrorRetry != 0 || _ecs.Config.MaxStaleConnectionRetries != 0 ||
            !_ecs.Config.DisableClockSkewCorrection || _ecs.Config.AllowAutoRedirect)
        {
            throw new InvalidOperationException("Fargate effects require disabled SDK retries, clock-skew retries, and redirects.");
        }

        return new EffectProvider(this, configuration, profile, GetTarget(configuration, profile.ProfileId), approval);
    }

    private async Task<EcsTask?> DescribeExactTaskAsync(InventoryTarget target, string taskArn, CancellationToken cancellationToken)
    {
        if (!IsTaskArn(target, taskArn))
        {
            return null;
        }

        try
        {
            var reply = await _ecs.DescribeTasksAsync(new DescribeTasksRequest
            {
                Cluster = target.Scope.ClusterArn,
                Tasks = [taskArn],
                Include = ["TAGS"],
            }, cancellationToken).ConfigureAwait(false);

            return reply.Failures is not { Count: > 0 } &&
                reply.Tasks is [{ } task] && task.TaskArn == taskArn && task.ClusterArn == target.Scope.ClusterArn
                ? task : null;
        }
        catch (Exception error) when (IsReadUnavailable(error, cancellationToken))
        {
            return null;
        }
    }

    private static bool IsTaskArn(InventoryTarget target, string? arn) =>
        arn?.StartsWith(target.TaskArnPrefix, StringComparison.Ordinal) == true &&
        IsHex(arn.AsSpan(target.TaskArnPrefix.Length), 32);

    private static (ExecutorBinding Binding, ExecutorResourceObservation Resource) BindTask(LifecycleProfile profile,
        FargateProfileScope scope, FargateTaskObservation task, DateTimeOffset observedAt)
    {
        var state = task.State switch
        {
            FargateTaskState.Pending when task.CreatedAt.HasValue => ExecutorResourceState.Starting,
            FargateTaskState.Running => ExecutorResourceState.Running,
            FargateTaskState.Stopped => ExecutorResourceState.IrreversiblyTerminated,
            _ => ExecutorResourceState.Unknown,
        };
        return (new ExecutorBinding(profile, task.AllocationId!.Value.ToString("D"), task.ResourceArn, task.Incarnation!.Value,
                new FargateExecutorIdentity(scope.ClusterArn, scope.TaskDefinitionArn)),
            new ExecutorResourceObservation(state, observedAt, task.CreatedAt, Guid.NewGuid(), task.ProviderVersion));
    }

    private sealed class EffectProvider(FargateProfileInspector inspector, RuntimeConfiguration configuration,
        LifecycleProfile profile, InspectionTarget target, FargateContainerApproval? approval) : IFargateReplayEffectProvider
    {
        private readonly TelemetryProfile? _telemetryProfile = TelemetryProfile.From(target.Profile);

        public async Task<EffectDispatchPrecondition?> CheckAsync(EffectEnvelope envelope, CatalogBinding binding,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            ArgumentNullException.ThrowIfNull(binding);
            if (envelope.Profile != profile || binding.ProvisionerType != RunnerProvisionerType.Fargate ||
                binding.ProviderDefinition != target.Scope.TaskDefinitionArn || binding.BootstrapRoleArn != target.Provisioner.BootstrapRoleArn ||
                binding.BootstrapProtocol != target.Provisioner.BootstrapProtocol)
            {
                return null;
            }

            bool eligible = false;
            EffectEvidence? alreadyApplied = null;
            if (envelope.Request is FargateLaunchEffectRequest launch && IsScopedLaunch(launch))
            {
                eligible = await CheckLaunchAsync(launch, cancellationToken).ConfigureAwait(false);
            }
            else if (envelope.Request is FargateStopEffectRequest stop && IsScopedStop(stop))
            {
                var observed = await ReadOwnedTaskAsync(target.Inventory, stop.TaskArn, stop.AllocationId, cancellationToken).ConfigureAwait(false);
                if (observed is { Task.State: FargateTaskState.Stopped } terminal && terminal.Task.Incarnation == stop.Incarnation)
                {
                    eligible = true;
                    alreadyApplied = Evidence(envelope, target.Scope, terminal.Task, terminal.ObservedAt);
                }
                else
                {
                    eligible = observed is { Task.State: FargateTaskState.Pending or FargateTaskState.Running, DesiredRunning: true } live &&
                        live.Task.Incarnation == stop.Incarnation;
                }
            }
            return new EffectDispatchPrecondition(envelope.Sha256, envelope.Target, inspector._clock.GetUtcNow(), eligible, alreadyApplied);
        }

        public async Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            ArgumentNullException.ThrowIfNull(attempt);
            cancellationToken.ThrowIfCancellationRequested();
            var now = inspector._clock.GetUtcNow();
            if (envelope.Profile != profile || attempt.BeganAt < envelope.EarliestPossibleFirstSend || now >= attempt.Deadline)
            {
                return null;
            }

            try
            {
                return envelope.Request switch
                {
                    FargateLaunchEffectRequest launch when approval is not null && IsScopedLaunch(launch) &&
                        envelope.ProviderReplayDeadline is { } cutoff && now < cutoff && attempt.Deadline <= cutoff =>
                        await SendLaunchAsync(envelope, launch, cancellationToken).ConfigureAwait(false),
                    // The dispatcher has checked the AWS identity and consumed the cleanup attempt at its final durable fence.
                    FargateStopEffectRequest stop when IsScopedStop(stop) =>
                        await SendStopAsync(envelope, stop, cancellationToken).ConfigureAwait(false),
                    _ => null,
                };
            }
            catch (Exception error) when (IsReadUnavailable(error, cancellationToken))
            {
                return null;
            }
        }

        public async Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            ArgumentNullException.ThrowIfNull(attempt);
            if (envelope.Profile != profile)
            {
                return null;
            }
            if (envelope.Request is FargateStopEffectRequest stop && stop.Incarnation.HasValue &&
                stop.TaskDefinitionArn == target.Scope.TaskDefinitionArn)
            {
                var historic = new InventoryTarget(target.Scope with { ClusterArn = stop.ClusterArn },
                    target.Inventory.BootstrapProtocol, target.Inventory.RunnerContainer, target.Inventory.SubnetIds);
                return await ObserveStopAsync(envelope, historic, stop, cancellationToken).ConfigureAwait(false);
            }

            return (await ObserveForReplayAsync(envelope, attempt, cancellationToken).ConfigureAwait(false))?.Evidence;
        }

        public async Task<FargateReplayObservation?> ObserveForReplayAsync(EffectEnvelope envelope, EffectAttempt attempt,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            ArgumentNullException.ThrowIfNull(attempt);
            if (envelope.Profile != profile || envelope.Request is not FargateLaunchEffectRequest { OwnershipTags: { } tags } launch ||
                !launch.ClusterArn.AsSpan().StartsWith(target.Scope.ClusterArn.AsSpan(0, target.Scope.ClusterArn.LastIndexOf('/') + 1),
                    StringComparison.Ordinal))
            {
                return null;
            }

            using var operation = FargateDiagnostics.Start("launch_readback", _telemetryProfile, cancellationToken);
            // Observation follows the immutable original allocation, including after a compatible rollout changes its cluster or network.
            var historic = new InventoryTarget(target.Scope with { ClusterArn = launch.ClusterArn, TaskDefinitionArn = launch.TaskDefinitionArn },
                tags.BootstrapProtocol, launch.RunnerContainer, launch.SubnetIds);
            var snapshot = await inspector.ReadInventoryAsync(historic, launch.AllocationId, cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsComplete)
            {
                operation?.Complete("unknown", "inventory_incomplete");
                return new FargateReplayObservation(envelope.Sha256, envelope.Target, snapshot.ObservedAt, IsComplete: false);
            }

            var observedAt = inspector._clock.GetUtcNow();
            var evidence = snapshot.Tasks is [{ IsOwned: true } task] ? Evidence(envelope, snapshot.Scope, task, observedAt) : null;
            operation?.Complete(evidence is null ? "unknown" : "success", evidence is null ? "no_exact_task" : "exact_observation");
            return new FargateReplayObservation(envelope.Sha256, envelope.Target, snapshot.ObservedAt,
                IsComplete: evidence is not null || snapshot.Tasks.IsEmpty, evidence);
        }

        private bool IsScopedLaunch(FargateLaunchEffectRequest request) =>
            request.ClusterArn == target.Scope.ClusterArn && request.TaskDefinitionArn == target.Scope.TaskDefinitionArn &&
            request.TaskRoleArn == target.Provisioner.BootstrapRoleArn && request.ExecutionRoleArn == target.Provisioner.ExecutionRoleArn &&
            request.RunnerContainer == target.Provisioner.RunnerContainer && request.CpuUnits is > 0 &&
            request.OwnershipTags == new FargateLaunchOwnershipTags(RunnerOwnershipTags.ControllerValue, profile.Deployment,
                profile.ProfileId, request.AllocationId.ToString("D"), target.Provisioner.BootstrapProtocol);

        private bool IsScopedStop(FargateStopEffectRequest request) =>
            request.ClusterArn == target.Scope.ClusterArn && request.TaskDefinitionArn == target.Scope.TaskDefinitionArn &&
            request.Incarnation.HasValue && IsTaskArn(target.Inventory, request.TaskArn);

        private async Task<bool> CheckLaunchAsync(FargateLaunchEffectRequest request, CancellationToken cancellationToken)
        {
            if (approval is null)
            {
                return false;
            }
            var preflight = await inspector.ResolvePreflightAsync(configuration, profile.ProfileId, approval, cancellationToken).ConfigureAwait(false);
            return preflight is { IsReady: true, Task: { } verified } && verified.CpuUnits == request.CpuUnits &&
                request.SecurityGroupIds.SequenceEqual(verified.SecurityGroupIds.Order(StringComparer.Ordinal)) &&
                HasVerifiedSubnets(verified, request.SubnetIds);
        }

        private async Task<EffectEvidence?> SendLaunchAsync(EffectEnvelope envelope, FargateLaunchEffectRequest request,
            CancellationToken cancellationToken)
        {
            using var operation = FargateDiagnostics.Start("run_task", _telemetryProfile, cancellationToken);
            var tags = request.OwnershipTags!;
            // v1 starts the exact preflight-approved command from the pinned definition, with no runtime overrides.
            var reply = await inspector._ecs.RunTaskAsync(new RunTaskRequest
            {
                Cluster = request.ClusterArn,
                TaskDefinition = request.TaskDefinitionArn,
                LaunchType = LaunchType.FARGATE,
                Count = request.Count,
                StartedBy = request.StartedBy,
                ClientToken = request.ClientToken.ToString("N"),
                EnableExecuteCommand = request.EnableExecuteCommand,
                NetworkConfiguration = new EcsNetwork
                {
                    AwsvpcConfiguration = new AwsVpcConfiguration
                    {
                        AssignPublicIp = AssignPublicIp.DISABLED,
                        Subnets = [.. request.SubnetIds],
                        SecurityGroups = [.. request.SecurityGroupIds],
                    },
                },
                Tags =
                [
                    new() { Key = RunnerOwnershipTags.Controller, Value = tags.Controller },
                    new() { Key = RunnerOwnershipTags.Deployment, Value = tags.Deployment },
                    new() { Key = RunnerOwnershipTags.Profile, Value = tags.Profile },
                    new() { Key = RunnerOwnershipTags.Allocation, Value = tags.Allocation },
                    new() { Key = RunnerOwnershipTags.BootstrapProtocol, Value = tags.BootstrapProtocol },
                ],
            }, cancellationToken).ConfigureAwait(false);

            if (reply.Failures is { Count: > 0 } || reply.Tasks is not [{ TaskArn: { } arn } returned] ||
                (returned.ClusterArn is not null && returned.ClusterArn != target.Scope.ClusterArn))
            {
                operation?.Complete("unknown", "no_exact_task");
                return null;
            }
            var launched = target.Inventory with { SubnetIds = request.SubnetIds };
            var observed = await ReadOwnedTaskAsync(launched, arn, request.AllocationId, cancellationToken).ConfigureAwait(false);
            var evidence = observed is { } exact ? Evidence(envelope, target.Scope, exact.Task, exact.ObservedAt) : null;
            operation?.Complete(evidence is null ? "unknown" : "success", evidence is null ? "no_exact_task" : "exact_observation");
            return evidence;
        }

        private async Task<EffectEvidence?> SendStopAsync(EffectEnvelope envelope, FargateStopEffectRequest request,
            CancellationToken cancellationToken)
        {
            using var operation = FargateDiagnostics.Start("stop_task", _telemetryProfile, cancellationToken);
            _ = await inspector._ecs.StopTaskAsync(new StopTaskRequest
            {
                Cluster = request.ClusterArn,
                Task = request.TaskArn,
            }, cancellationToken).ConfigureAwait(false);
            var evidence = await ObserveStopAsync(envelope, target.Inventory, request, cancellationToken).ConfigureAwait(false);
            operation?.Complete(evidence is null ? "unknown" : "success", evidence is null ? "no_exact_terminal_state" : "exact_terminal_state");
            return evidence;
        }

        private async Task<EffectEvidence?> ObserveStopAsync(EffectEnvelope envelope, InventoryTarget inventory, FargateStopEffectRequest request,
            CancellationToken cancellationToken)
        {
            using var operation = FargateDiagnostics.Start("stop_readback", _telemetryProfile, cancellationToken);
            var observed = await ReadOwnedTaskAsync(inventory, request.TaskArn, request.AllocationId, cancellationToken).ConfigureAwait(false);
            var evidence = observed is { Task.State: FargateTaskState.Stopped } exact && exact.Task.Incarnation == request.Incarnation
                ? Evidence(envelope, inventory.Scope, exact.Task, exact.ObservedAt) : null;
            operation?.Complete(evidence is null ? "unknown" : "success", evidence is null ? "no_exact_terminal_state" : "exact_terminal_state");
            return evidence;
        }

        private async Task<OwnedTask?> ReadOwnedTaskAsync(InventoryTarget inventory, string taskArn, Guid allocation,
            CancellationToken cancellationToken)
        {
            var task = await inspector.DescribeExactTaskAsync(inventory, taskArn, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                return null;
            }
            var observedAt = inspector._clock.GetUtcNow();
            var observation = ParseTask(inventory, task, ReadOwnershipTags(task.Tags), observedAt, observedAt, out bool exact);
            return exact && observation.IsOwned && observation.AllocationId == allocation
                ? new OwnedTask(observation, observedAt, task.DesiredStatus == "RUNNING") : null;
        }

        private EffectEvidence Evidence(EffectEnvelope envelope, FargateProfileScope scope, FargateTaskObservation task, DateTimeOffset observedAt)
        {
            var (binding, resource) = BindTask(profile, scope, task, observedAt);
            return new EffectEvidence(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, observedAt)
            {
                ExecutorBinding = binding,
                ResourceObservation = resource,
            };
        }

        private sealed record OwnedTask(FargateTaskObservation Task, DateTimeOffset ObservedAt, bool DesiredRunning);
    }
}
