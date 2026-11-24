using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amazon.ECS;
using Amazon.ECS.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using EcsTask = Amazon.ECS.Model.Task;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

public sealed partial class FargateProfileInspector
{
    public Task<FargateInventorySnapshot> ReadInventoryAsync(
        RuntimeConfiguration configuration, string profileId, CancellationToken cancellationToken) =>
        ReadInventoryAsync(GetTarget(configuration, profileId), null, cancellationToken);

    public Task<FargateInventorySnapshot> ReadAllocationAsync(
        RuntimeConfiguration configuration, string profileId, Guid allocationId, CancellationToken cancellationToken) =>
        ReadInventoryAsync(GetTarget(configuration, profileId), allocationId, cancellationToken);

    public static string StartedBy(Guid allocationId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(allocationId, Guid.Empty);
        return "ss-" + allocationId.ToString("N");
    }

    private async Task<FargateInventorySnapshot> ReadInventoryAsync(
        InspectionTarget target, Guid? allocationId, CancellationToken cancellationToken)
    {
        string? startedBy = allocationId is Guid allocation ? StartedBy(allocation) : null;
        var observedAt = _clock.GetUtcNow();
        var tasks = new List<FargateTaskObservation>();
        try
        {
            if (!await IsClusterActiveAsync(target, cancellationToken).ConfigureAwait(false))
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

    private async Task<bool> ReadTaskIdsAsync(InspectionTarget target, string? startedBy, DesiredStatus? desiredStatus,
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
                if (arn?.StartsWith(target.TaskArnPrefix, StringComparison.Ordinal) != true ||
                    !IsHex(arn.AsSpan(target.TaskArnPrefix.Length), 32))
                {
                    complete = false;
                    continue;
                }
                complete &= seenInPass.Add(arn);
                destination.Add(arn);
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

    private async Task<bool> DescribeBatchAsync(InspectionTarget target, AllocationCorrelation? correlation, string[] batch, DateTimeOffset observedAt,
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

    private static bool IsUnrelatedTask(InspectionTarget target, EcsTask task, Dictionary<string, string>? tags, AllocationCorrelation correlation)
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

    private static bool ClaimsProfile(InspectionTarget target, Dictionary<string, string>? tags) =>
        tags?.GetValueOrDefault(RunnerOwnershipTags.Controller) == RunnerOwnershipTags.ControllerValue &&
        tags.GetValueOrDefault(RunnerOwnershipTags.Deployment) == target.Scope.Deployment &&
        tags.GetValueOrDefault(RunnerOwnershipTags.Profile) == target.Scope.ProfileId;

    private static FargateTaskObservation ParseTask(InspectionTarget target, EcsTask task, Dictionary<string, string>? tags,
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
        var attached = ReadAttachedSubnets(task.Attachments);
        bool inScope = attached.Any(subnet => target.Network.Subnets.Any(configured => configured.Id == subnet));
        exact = tags is not null && state != FargateTaskState.Unknown && !string.IsNullOrEmpty(task.TaskDefinitionArn);
        Guid? allocation = null;
        Guid? incarnation = null;
        if (ClaimsProfile(target, tags))
        {
            string? tagged = tags!.GetValueOrDefault(RunnerOwnershipTags.Allocation);
            if (Guid.TryParseExact(tagged, "D", out var id) && id != Guid.Empty && tagged == id.ToString("D"))
            {
                allocation = id;
                if (tags!.GetValueOrDefault(RunnerOwnershipTags.BootstrapProtocol) == target.Provisioner.BootstrapProtocol &&
                    task.TaskDefinitionArn == target.Scope.TaskDefinitionArn && task.LaunchType?.Value == "FARGATE" &&
                    task.CapacityProviderName != "FARGATE_SPOT" && task.EnableExecuteCommand == false &&
                    task.StartedBy == StartedBy(id) && task.Group?.StartsWith("service:", StringComparison.Ordinal) != true &&
                    ValidObservedOverrides(task.Overrides, target.Provisioner.RunnerContainer) &&
                    (state != FargateTaskState.Running || inScope))
                {
                    incarnation = ComputeIncarnation(task.TaskArn, task.TaskDefinitionArn, id);
                }
            }
            exact &= incarnation.HasValue;
        }
        return new FargateTaskObservation(task.TaskArn, task.TaskDefinitionArn, state, ReadCreatedAt(task.CreatedAt, observedAt),
            allocation, incarnation, attached, attached.IsEmpty ? null : attachmentObservedAt, inScope, task.EnableExecuteCommand);
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

    private static ImmutableArray<string> ReadAttachedSubnets(List<Attachment>? source)
    {
        var subnets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachment in source ?? [])
        {
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
                values.GetValueOrDefault("networkInterfaceId")?.StartsWith("eni-", StringComparison.Ordinal) == true &&
                IPAddress.TryParse(values.GetValueOrDefault("privateIPv4Address"), out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                subnets.Add(subnet);
            }
        }
        return [.. subnets.Order(StringComparer.Ordinal)];
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

    private static Guid ComputeIncarnation(string taskArn, string taskDefinitionArn, Guid allocationId)
    {
        string identity = $"{taskArn}\n{taskDefinitionArn}\n{allocationId:D}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    private sealed record AllocationCorrelation(Guid AllocationId, string StartedBy, HashSet<string> ListedTaskArns);
}
