using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amazon;
using Amazon.ECS;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using DescribeTasksRequest = Amazon.ECS.Model.DescribeTasksRequest;
using DescribeTasksResponse = Amazon.ECS.Model.DescribeTasksResponse;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

public sealed class EcsControllerTaskFenceObserver : IControllerTaskFenceObserver
{
    private const string ClusterPrefix = "cluster/";
    private readonly IAmazonECS _ecs;
    private readonly string _clusterArn;
    private readonly string _taskArnPrefix;

    public EcsControllerTaskFenceObserver(IAmazonECS ecs, string controllerClusterArn)
    {
        ArgumentNullException.ThrowIfNull(ecs);
        if (!Arn.TryParse(controllerClusterArn, out var cluster) ||
            cluster.Service != "ecs" ||
            !IsResourceSegment(cluster.Partition) ||
            !IsResourceSegment(cluster.Region) ||
            cluster.AccountId is not { Length: 12 } account ||
            !account.All(char.IsAsciiDigit) ||
            !cluster.Resource.StartsWith(ClusterPrefix, StringComparison.Ordinal) ||
            !IsResourceSegment(cluster.Resource.AsSpan(ClusterPrefix.Length)))
        {
            throw new ArgumentException("Supply the canonical controller ECS cluster ARN.", nameof(controllerClusterArn));
        }

        _ecs = ecs;
        _clusterArn = controllerClusterArn;
        var name = cluster.Resource[ClusterPrefix.Length..];
        _taskArnPrefix = $"arn:{cluster.Partition}:ecs:{cluster.Region}:{account}:task/{name}/";
    }

    public async Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (taskArn?.StartsWith(_taskArnPrefix, StringComparison.Ordinal) != true ||
            !IsTaskId(taskArn.AsSpan(_taskArnPrefix.Length)))
        {
            return ControllerTaskFenceStatus.Unknown;
        }

        DescribeTasksResponse response;
        try
        {
            response = await _ecs.DescribeTasksAsync(
                new DescribeTasksRequest { Cluster = _clusterArn, Tasks = [taskArn] }, cancellationToken);
        }
        catch (Exception error) when (error is
            AmazonClientException or AmazonServiceException or HttpRequestException or SocketException or
            IOException or JsonException or TimeoutException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ControllerTaskFenceStatus.Unknown;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (response is not { HttpStatusCode: HttpStatusCode.OK })
        {
            return ControllerTaskFenceStatus.Unknown;
        }

        if (response.Failures is { Count: > 0 } failures)
        {
            return response.Tasks is null or { Count: 0 } &&
                failures is [{ Reason: "MISSING", Arn: var missingArn }] &&
                string.Equals(missingArn, taskArn, StringComparison.Ordinal)
                ? ControllerTaskFenceStatus.Missing
                : ControllerTaskFenceStatus.Unknown;
        }

        if (response.Tasks is not [{ } task] ||
            !string.Equals(task.TaskArn, taskArn, StringComparison.Ordinal) ||
            !string.Equals(task.ClusterArn, _clusterArn, StringComparison.Ordinal))
        {
            return ControllerTaskFenceStatus.Unknown;
        }

        return task.LastStatus switch
        {
            "STOPPED" when task.DesiredStatus is null or "STOPPED" => ControllerTaskFenceStatus.Stopped,
            "PROVISIONING" or "PENDING" or "ACTIVATING" or "RUNNING" or
                "DEACTIVATING" or "STOPPING" or "DEPROVISIONING" => ControllerTaskFenceStatus.Running,
            _ => ControllerTaskFenceStatus.Unknown,
        };
    }

    private static bool IsResourceSegment(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTaskId(ReadOnlySpan<char> value)
    {
        if (value.Length != 32)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
