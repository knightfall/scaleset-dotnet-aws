using Amazon.ECS;
using Amazon.ECS.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

/// <summary>Reads the exact native MiniStack ECS predecessor, whose task ID uses a dashed UUID.</summary>
internal sealed class RestoreOwnerTask(IAmazonECS ecs, string clusterArn, string nativeArn, string controllerArn) : IControllerTaskFenceObserver
{
    internal string ControllerArn { get; } = controllerArn;

    internal static async Task<RestoreOwnerTask> StartAsync(IAmazonECS ecs, string subnet, CancellationToken cancellationToken)
    {
        string name = "restore-controller-" + Guid.NewGuid().ToString("N");
        var cluster = await ecs.CreateClusterAsync(new CreateClusterRequest { ClusterName = name }, cancellationToken);
        var definition = await ecs.RegisterTaskDefinitionAsync(new RegisterTaskDefinitionRequest
        {
            Family = name,
            NetworkMode = NetworkMode.Awsvpc,
            RequiresCompatibilities = [Compatibility.FARGATE],
            Cpu = "256",
            Memory = "512",
            ContainerDefinitions = [new ContainerDefinition { Name = "controller", Image = "fixture.invalid/controller:offline", Essential = true }],
        }, cancellationToken);
        var started = await ecs.RunTaskAsync(new RunTaskRequest
        {
            Cluster = cluster.Cluster.ClusterArn,
            TaskDefinition = definition.TaskDefinition.TaskDefinitionArn,
            LaunchType = LaunchType.FARGATE,
            Count = 1,
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration { Subnets = [subnet], AssignPublicIp = AssignPublicIp.DISABLED },
            },
        }, cancellationToken);
        Assert.Empty(started.Failures ?? []);
        var task = Assert.Single(started.Tasks);
        Assert.Equal(cluster.Cluster.ClusterArn, task.ClusterArn);
        int suffix = task.TaskArn.LastIndexOf('/') + 1;
        var uuid = Guid.Parse(task.TaskArn.AsSpan(suffix));
        return new RestoreOwnerTask(ecs, task.ClusterArn, task.TaskArn, task.TaskArn[..suffix] + uuid.ToString("N"));
    }

    internal System.Threading.Tasks.Task StopAsync(CancellationToken cancellationToken) =>
        ecs.StopTaskAsync(new StopTaskRequest { Cluster = clusterArn, Task = nativeArn }, cancellationToken);

    public async Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
    {
        if (taskArn != ControllerArn)
        {
            return ControllerTaskFenceStatus.Unknown;
        }
        var described = await ecs.DescribeTasksAsync(new DescribeTasksRequest
        {
            Cluster = clusterArn,
            Tasks = [nativeArn],
        }, cancellationToken);
        if (described.Failures is { Count: > 0 } || described.Tasks is not [{ } exact] || exact.TaskArn != nativeArn ||
            exact.ClusterArn != clusterArn)
        {
            return ControllerTaskFenceStatus.Unknown;
        }
        return exact.LastStatus switch
        {
            "STOPPED" when exact.DesiredStatus == "STOPPED" => ControllerTaskFenceStatus.Stopped,
            "RUNNING" => ControllerTaskFenceStatus.Running,
            _ => ControllerTaskFenceStatus.Unknown,
        };
    }
}
