using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Amazon.ECS;
using Amazon.ECS.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using EcsTask = Amazon.ECS.Model.Task;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

public sealed partial class FargateProfileInspector
{
    public Task<FargateInventorySnapshot> ReadInventoryAsync(
        RuntimeConfiguration configuration, string profileId, CancellationToken cancellationToken) =>
        ReadInventoryAsync(configuration, profileId, null, cancellationToken);

    public Task<FargateInventorySnapshot> ReadInventoryAsync(
        RuntimeConfiguration configuration, string profileId, FargateInventorySnapshot? previous, CancellationToken cancellationToken) =>
        ReadInventoryAsync(GetTarget(configuration, profileId), null, previous, cancellationToken);

    public Task<FargateInventorySnapshot> ReadAllocationAsync(
        RuntimeConfiguration configuration, string profileId, Guid allocationId, CancellationToken cancellationToken) =>
        ReadInventoryAsync(GetTarget(configuration, profileId), allocationId, null, cancellationToken);

    private async Task<FargateInventorySnapshot> ReadInventoryAsync(InspectionTarget target, Guid? allocationId,
        FargateInventorySnapshot? previous, CancellationToken cancellationToken)
    {
        using var operation = FargateDiagnostics.Start(allocationId.HasValue ? "allocation_inventory" : "inventory",
            TelemetryProfile.From(target.Profile), cancellationToken);
        var result = await ReadInventoryAsync(target.Inventory, allocationId, cancellationToken).ConfigureAwait(false);
        operation?.Complete(result.IsComplete ? "success" : "unknown", result.Status switch
        {
            FargateInventoryStatus.Complete => "inventory_complete",
            FargateInventoryStatus.AwsUnavailable => "aws_unavailable",
            _ => "inventory_incomplete",
        });
        return CarryRunningAttachments(result, previous);
    }

    private static FargateInventorySnapshot CarryRunningAttachments(FargateInventorySnapshot current,
        FargateInventorySnapshot? previous)
    {
        if (!current.IsProfileComplete || previous is not { IsProfileComplete: true } || previous.Scope != current.Scope ||
            previous.Tasks.IsDefaultOrEmpty || current.Tasks.IsEmpty || previous.ObservedAt >= current.ObservedAt ||
            !Fresh(previous.ObservedAt, current.ObservedAt))
        {
            return current;
        }

        var priorTasks = previous.Tasks.GroupBy(task => task.ResourceArn, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var tasks = current.Tasks.Select(task => priorTasks.TryGetValue(task.ResourceArn, out var prior)
            ? CarryRunningAttachment(task, prior, current.ObservedAt, previous.ObservedAt) : task).ToImmutableArray();
        return current with { Tasks = tasks };
    }

    private static FargateTaskObservation CarryRunningAttachment(FargateTaskObservation task, FargateTaskObservation prior,
        DateTimeOffset inventoryAt, DateTimeOffset priorInventoryAt)
    {
        if (task is not { IsOwned: true, State: FargateTaskState.Running, AttachmentObservedAt: { } latest } ||
            latest < inventoryAt || prior.State != FargateTaskState.Running || prior.Incarnation != task.Incarnation ||
            prior.AllocationId != task.AllocationId || prior.TaskDefinitionArn != task.TaskDefinitionArn ||
            prior.CreatedAt != task.CreatedAt)
        {
            return task;
        }

        if (task.ProviderVersion < prior.ProviderVersion || task.ProviderVersion.HasValue != prior.ProviderVersion.HasValue)
        {
            return task with { Attachment = null, AttachmentObservedAt = null };
        }

        if (task.Attachment is not { } attachment || prior.Attachment != attachment || prior.AttachmentObservedAt is not { } seen ||
            seen < priorInventoryAt || !Fresh(seen, inventoryAt))
        {
            return task;
        }

        // Use the earliest running observation only when this full poll re-confirms the exact attachment.
        return task with { RunningAttachmentObservedSince = prior.RunningAttachmentObservedSince ?? seen };
    }

    public static string StartedBy(Guid allocationId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(allocationId, Guid.Empty);
        return "ss-" + allocationId.ToString("N");
    }

    private async Task<FargateInventorySnapshot> ReadInventoryAsync(
        InventoryTarget target, Guid? allocationId, CancellationToken cancellationToken)
    {
        string? startedBy = allocationId is Guid allocation ? StartedBy(allocation) : null;
        var observedAt = _clock.GetUtcNow();
        var tasks = new List<FargateTaskObservation>();
        try
        {
            if (!await IsClusterActiveAsync(target.Scope.ClusterArn, cancellationToken).ConfigureAwait(false))
            {
                return new FargateInventorySnapshot(target.Scope, FargateInventoryStatus.Incomplete, observedAt, [], startedBy);
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            bool complete = await ReadTaskIdsAsync(target, startedBy, startedBy is null ? DesiredStatus.RUNNING : null,
                ids, cancellationToken).ConfigureAwait(false);
            var correlation = allocationId is Guid requested
                ? new AllocationCorrelation(requested, startedBy!, new HashSet<string>(ids, StringComparer.Ordinal))
                : null;
            // StartedBy must be the only filter and defaults to desired RUNNING. Find stopped candidates separately.
            complete &= await ReadTaskIdsAsync(target, null, DesiredStatus.STOPPED, ids, cancellationToken).ConfigureAwait(false);
            foreach (var batch in ids.Order(StringComparer.Ordinal).Chunk(100))
            {
                complete &= await DescribeBatchAsync(target, correlation, batch, observedAt, tasks, cancellationToken).ConfigureAwait(false);
            }
            var duplicateAllocations = tasks.Where(task => task.IsOwned).GroupBy(task => task.AllocationId)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            if (duplicateAllocations.Count > 0)
            {
                complete = false;
                for (int index = 0; index < tasks.Count; index++)
                {
                    if (duplicateAllocations.Contains(tasks[index].AllocationId))
                    {
                        tasks[index] = tasks[index] with { Incarnation = null };
                    }
                }
            }
            return new FargateInventorySnapshot(target.Scope, complete ? FargateInventoryStatus.Complete : FargateInventoryStatus.Incomplete,
                observedAt, [.. tasks], startedBy);
        }
        catch (Exception error) when (IsReadUnavailable(error, cancellationToken))
        {
            return new FargateInventorySnapshot(target.Scope, FargateInventoryStatus.AwsUnavailable, observedAt, [], startedBy);
        }
    }

    private async Task<bool> ReadTaskIdsAsync(InventoryTarget target, string? startedBy, DesiredStatus? desiredStatus,
        HashSet<string> destination, CancellationToken cancellationToken)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var seenInPass = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        bool complete = true;
        for (int page = 0; page < MaximumPages; page++)
        {
            var reply = await _ecs.ListTasksAsync(new ListTasksRequest
            {
                Cluster = target.Scope.ClusterArn, StartedBy = startedBy, DesiredStatus = desiredStatus, NextToken = next,
            }, cancellationToken).ConfigureAwait(false);
            if (reply.TaskArns is null || reply.TaskArns.Count > 100)
            {
                return false;
            }
            foreach (var arn in reply.TaskArns)
            {
                if (!IsTaskArn(target, arn))
                {
                    complete = false;
                    continue;
                }
                complete &= seenInPass.Add(arn!);
                destination.Add(arn!);
            }
            next = reply.NextToken;
            if (string.IsNullOrEmpty(next))
            {
                return complete;
            }
            if (!tokens.Add(next))
            {
                return false;
            }
        }
        return false;
    }

    private async Task<bool> DescribeBatchAsync(InventoryTarget target, AllocationCorrelation? correlation, string[] batch, DateTimeOffset observedAt,
        List<FargateTaskObservation> destination, CancellationToken cancellationToken)
    {
        var reply = await _ecs.DescribeTasksAsync(new DescribeTasksRequest
        {
            Cluster = target.Scope.ClusterArn, Tasks = [.. batch], Include = ["TAGS"],
        }, cancellationToken).ConfigureAwait(false);
        var attachmentObservedAt = _clock.GetUtcNow();
        var missing = batch.ToHashSet(StringComparer.Ordinal);
        bool complete = reply.Failures is not { Count: > 0 };
        foreach (var task in reply.Tasks ?? [])
        {
            if (task is not { TaskArn: { } arn } || !missing.Remove(arn) || task.ClusterArn != target.Scope.ClusterArn)
            {
                complete = false;
                continue;
            }
            var tags = ReadOwnershipTags(task.Tags);
            if (correlation is { ListedTaskArns: var listed } && !listed.Contains(arn) &&
                IsUnrelatedTask(target, task, tags, correlation))
            {
                continue;
            }
            var observation = ParseTask(target, task, tags, observedAt, attachmentObservedAt, out bool exact);
            if (correlation is not null && (task.StartedBy != correlation.StartedBy ||
                observation.AllocationId != correlation.AllocationId || !observation.IsOwned))
            {
                exact = false;
                observation = observation with { Incarnation = null };
            }
            destination.Add(observation);
            complete &= exact;
        }
        return complete && missing.Count == 0;
    }

    private static bool IsUnrelatedTask(InventoryTarget target, EcsTask task, Dictionary<string, string>? tags, AllocationCorrelation correlation)
    {
        if (tags is null || task.StartedBy == correlation.StartedBy ||
            string.IsNullOrEmpty(task.TaskDefinitionArn) || string.IsNullOrEmpty(task.LastStatus))
        {
            return false;
        }
        return Guid.TryParse(tags.GetValueOrDefault(RunnerOwnershipTags.Allocation), out var allocation) && allocation != Guid.Empty
            ? allocation != correlation.AllocationId
            : !ClaimsProfile(target, tags);
    }

    private static bool ClaimsProfile(InventoryTarget target, Dictionary<string, string>? tags) =>
        tags?.GetValueOrDefault(RunnerOwnershipTags.Controller) == RunnerOwnershipTags.ControllerValue &&
        tags.GetValueOrDefault(RunnerOwnershipTags.Deployment) == target.Scope.Deployment &&
        tags.GetValueOrDefault(RunnerOwnershipTags.Profile) == target.Scope.ProfileId;

    private static bool IsForeignProfileTask(InventoryTarget target, EcsTask task, Dictionary<string, string> tags)
    {
        var deployment = tags.GetValueOrDefault(RunnerOwnershipTags.Deployment);
        var profile = tags.GetValueOrDefault(RunnerOwnershipTags.Profile);
        if (profile == target.Scope.ProfileId && (deployment is null || deployment == target.Scope.Deployment))
        {
            return false;
        }
        if (task.TaskDefinitionArn != target.Scope.TaskDefinitionArn ||
            task.Group?.StartsWith("service:", StringComparison.Ordinal) == true)
        {
            return true;
        }
        // A standalone task using the same definition and with incomplete ownership evidence is ambiguous.
        return tags.GetValueOrDefault(RunnerOwnershipTags.Controller) == RunnerOwnershipTags.ControllerValue &&
            !string.IsNullOrWhiteSpace(deployment) && !string.IsNullOrWhiteSpace(profile) &&
            Guid.TryParseExact(tags.GetValueOrDefault(RunnerOwnershipTags.Allocation), "D", out var allocation) &&
            allocation != Guid.Empty && task.StartedBy == StartedBy(allocation);
    }

    private static FargateTaskObservation ParseTask(InventoryTarget target, EcsTask task, Dictionary<string, string>? tags,
        DateTimeOffset observedAt, DateTimeOffset attachmentObservedAt, out bool exact)
    {
        var state = task.LastStatus switch
        {
            "PROVISIONING" or "PENDING" or "ACTIVATING" => FargateTaskState.Pending,
            "RUNNING" => FargateTaskState.Running,
            "DEACTIVATING" or "STOPPING" or "DEPROVISIONING" => FargateTaskState.Stopping,
            "STOPPED" => FargateTaskState.Stopped,
            _ => FargateTaskState.Unknown,
        };
        var (attached, attachment) = ReadAttachments(task.Attachments);
        bool inScope = attached.Any(subnet => target.SubnetIds.Contains(subnet, StringComparer.Ordinal));
        exact = tags is not null && state != FargateTaskState.Unknown && !string.IsNullOrEmpty(task.TaskDefinitionArn) &&
            task.Version is not < 0;
        Guid? allocation = null;
        Guid? incarnation = null;
        if (ClaimsProfile(target, tags))
        {
            string? tagged = tags!.GetValueOrDefault(RunnerOwnershipTags.Allocation);
            if (Guid.TryParseExact(tagged, "D", out var id) && id != Guid.Empty && tagged == id.ToString("D"))
            {
                allocation = id;
                if (tags!.GetValueOrDefault(RunnerOwnershipTags.BootstrapProtocol) == target.BootstrapProtocol &&
                    task.TaskDefinitionArn == target.Scope.TaskDefinitionArn && task.LaunchType?.Value == "FARGATE" &&
                    task.CapacityProviderName != "FARGATE_SPOT" && task.EnableExecuteCommand == false &&
                    task.StartedBy == StartedBy(id) && task.Group?.StartsWith("service:", StringComparison.Ordinal) != true &&
                    ValidObservedOverrides(task.Overrides, target.RunnerContainer) &&
                    (state != FargateTaskState.Running || inScope) && task.Version is not < 0)
                {
                    incarnation = FargateExecutorIncarnation.Compute(task.TaskArn, task.TaskDefinitionArn, id);
                }
            }
            exact &= incarnation.HasValue;
        }
        return new FargateTaskObservation(task.TaskArn, task.TaskDefinitionArn, state, ReadCreatedAt(task.CreatedAt, observedAt),
            allocation, incarnation, attached, attachment is null ? null : attachmentObservedAt, inScope, task.EnableExecuteCommand,
            task.Version is >= 0 ? task.Version : null)
        {
            Attachment = attachment,
            IsForeign = exact && tags is not null && IsForeignProfileTask(target, task, tags),
        };
    }

    /// <summary>ECS may echo an empty override or only the container name for an unchanged launch.</summary>
    private static bool ValidObservedOverrides(TaskOverride? overrides, string runnerContainer) =>
        overrides is null ||
        (overrides is { Cpu: null, Memory: null, EphemeralStorage: null, TaskRoleArn: null, ExecutionRoleArn: null,
                InferenceAcceleratorOverrides: not { Count: > 0 } } &&
            (overrides.ContainerOverrides is not { Count: > 0 } ||
                (overrides.ContainerOverrides is [{ Cpu: null, Memory: null, MemoryReservation: null,
                    Command: not { Count: > 0 }, Environment: not { Count: > 0 }, EnvironmentFiles: not { Count: > 0 },
                    ResourceRequirements: not { Count: > 0 } } runner] && runner.Name == runnerContainer)));

    private static Dictionary<string, string>? ReadOwnershipTags(List<Tag>? source)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in source ?? [])
        {
            if (tag is null)
            {
                return null;
            }
            if ((tag.Key is RunnerOwnershipTags.Controller or RunnerOwnershipTags.Deployment or RunnerOwnershipTags.Profile or
                    RunnerOwnershipTags.Allocation or RunnerOwnershipTags.BootstrapProtocol) &&
                (tag.Value is null || !tags.TryAdd(tag.Key, tag.Value)))
            {
                return null;
            }
        }
        return tags;
    }

    private static (ImmutableArray<string> Subnets, FargateNetworkAttachment? Exact) ReadAttachments(List<Attachment>? source)
    {
        var subnets = new HashSet<string>(StringComparer.Ordinal);
        FargateNetworkAttachment? network = null;
        int possibleInterfaces = 0;
        foreach (var attachment in source ?? [])
        {
            if (string.IsNullOrEmpty(attachment?.Type) || attachment.Type == "ElasticNetworkInterface")
            {
                possibleInterfaces++;
            }
            if (attachment is not { Type: "ElasticNetworkInterface", Status: "ATTACHED", Details: { } details })
            {
                continue;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var detail in details)
            {
                if (detail is null || detail.Name is null || detail.Value is null || !values.TryAdd(detail.Name, detail.Value))
                {
                    values.Clear();
                    break;
                }
            }
            if (values.TryGetValue("subnetId", out var subnet) && subnet.StartsWith("subnet-", StringComparison.Ordinal) &&
                values.GetValueOrDefault("networkInterfaceId") is { } interfaceId && interfaceId.StartsWith("eni-", StringComparison.Ordinal) &&
                IPAddress.TryParse(values.GetValueOrDefault("privateIPv4Address"), out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                subnets.Add(subnet);
                network = new FargateNetworkAttachment(attachment.Id, interfaceId, subnet, address.ToString());
            }
        }
        return ([.. subnets.Order(StringComparer.Ordinal)], possibleInterfaces == 1 ? network : null);
    }

    private static DateTimeOffset? ReadCreatedAt(DateTime? time, DateTimeOffset observedAt)
    {
        if (time is not { } raw || raw == default)
        {
            return null;
        }
        DateTimeOffset stamp = raw.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(raw, DateTimeKind.Utc) : raw.ToUniversalTime();
        return stamp <= observedAt ? stamp : null;
    }

    private sealed record AllocationCorrelation(Guid AllocationId, string StartedBy, HashSet<string> ListedTaskArns);

    private sealed record InventoryTarget(FargateProfileScope Scope, string BootstrapProtocol, string RunnerContainer,
        ImmutableArray<string> SubnetIds)
    {
        public string TaskArnPrefix { get; } = Scope.ClusterArn.Replace(":cluster/", ":task/", StringComparison.Ordinal) + "/";
    }
}
