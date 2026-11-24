using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amazon.ECS;
using Amazon.ECS.Model;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

public sealed class EcsWireTests
{
    private const string JsonMediaType = "application/x-amz-json-1.1";
    private const string ClusterArn = "arn:aws:ecs:us-east-1:123456789012:cluster/runners";
    private const string TaskDefinitionArn = "arn:aws:ecs:us-east-1:123456789012:task-definition/runner:17";
    private const string TaskArn = "arn:aws:ecs:us-east-1:123456789012:task/runners/0123456789abcdef0123456789abcdef";
    private const string StartedBy = "ss-0123456789abcdef0123456789abcdef";
    private const string Token = "ecs-allocation-token";
    private const string RunResult = """
        {
          "tasks":[{
            "clusterArn":"arn:aws:ecs:us-east-1:123456789012:cluster/runners",
            "taskDefinitionArn":"arn:aws:ecs:us-east-1:123456789012:task-definition/runner:17",
            "taskArn":"arn:aws:ecs:us-east-1:123456789012:task/runners/0123456789abcdef0123456789abcdef",
            "lastStatus":"PROVISIONING"
          }],
          "failures":[{
            "arn":"arn:aws:ecs:us-east-1:123456789012:cluster/runners","reason":"RESOURCE:ENI",
            "detail":"Injected placement failure"
          }]
        }
        """;

    [Fact]
    public async Task RunTaskSerializesSingleFargateIntentAndPreservesPartialFailures()
    {
        using var wire = new AwsWireTransport(_ => JsonReply(RunResult));
        using var client = CreateClient(wire);

        var response = await client.RunTaskAsync(CreateRunRequest(Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        var placed = Assert.Single(response.Tasks);
        Assert.Equal(ClusterArn, placed.ClusterArn);
        Assert.Equal(TaskDefinitionArn, placed.TaskDefinitionArn);
        Assert.Equal(TaskArn, placed.TaskArn);
        Assert.Equal("PROVISIONING", placed.LastStatus);
        var failure = Assert.Single(response.Failures);
        Assert.Equal(ClusterArn, failure.Arn);
        Assert.Equal("RESOURCE:ENI", failure.Reason);
        Assert.Equal("Injected placement failure", failure.Detail);

        var body = ReadBody(Assert.Single(wire.Requests), "RunTask");
        Assert.Equal(ClusterArn, body.GetProperty("cluster").GetString());
        Assert.Equal(TaskDefinitionArn, body.GetProperty("taskDefinition").GetString());
        Assert.Equal("FARGATE", body.GetProperty("launchType").GetString());
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal(Token, body.GetProperty("clientToken").GetString());
        Assert.Equal(StartedBy, body.GetProperty("startedBy").GetString());
        Assert.False(body.GetProperty("enableExecuteCommand").GetBoolean());
        Assert.False(body.TryGetProperty("capacityProviderStrategy", out _));

        var network = body.GetProperty("networkConfiguration").GetProperty("awsvpcConfiguration");
        Assert.Equal("DISABLED", network.GetProperty("assignPublicIp").GetString());
        Assert.Equal(
            ["subnet-0123456789abcdef0", "subnet-0123456789abcdef1"],
            network.GetProperty("subnets").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            "sg-0123456789abcdef0",
            Assert.Single(network.GetProperty("securityGroups").EnumerateArray()).GetString());

        var tag = Assert.Single(body.GetProperty("tags").EnumerateArray());
        Assert.Equal("Allocation", tag.GetProperty("key").GetString());
        Assert.Equal("profile-a", tag.GetProperty("value").GetString());
        var container = Assert.Single(body.GetProperty("overrides").GetProperty("containerOverrides").EnumerateArray());
        Assert.Equal("runner", container.GetProperty("name").GetString());
        var environment = Assert.Single(container.GetProperty("environment").EnumerateArray());
        Assert.Equal("ALLOCATION_ID", environment.GetProperty("name").GetString());
        Assert.Equal("allocation-a", environment.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("""{"tasks":[],"failures":[{"reason":"RESOURCE:ENI"}]}""", true)]
    [InlineData("""{"failures":[{"reason":"RESOURCE:ENI"}]}""", false)]
    public async Task RunTaskPreservesFailuresWithEmptyOrAbsentTasks(string payload, bool tasksPresent)
    {
        using var wire = new AwsWireTransport(_ => JsonReply(payload));
        using var client = CreateClient(wire);

        var response = await client.RunTaskAsync(CreateRunRequest(Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        Assert.Equal("RESOURCE:ENI", Assert.Single(response.Failures).Reason);
        if (tasksPresent)
        {
            Assert.Empty(response.Tasks);
        }
        else
        {
            Assert.Null(response.Tasks);
        }

        ReadBody(Assert.Single(wire.Requests), "RunTask");
    }

    [Fact]
    public async Task RunTaskGeneratesANewWireTokenOnSeparateInvocationsWithoutAToken()
    {
        using var wire = new AwsWireTransport(_ => JsonReply(RunResult));
        using var client = CreateClient(wire);
        var request = CreateRunRequest(null);
        var token = TestContext.Current.CancellationToken;

        await client.RunTaskAsync(request, token);
        await client.RunTaskAsync(request, token);

        Assert.Null(request.ClientToken);
        Assert.Equal(2, wire.Requests.Count);
        var first = ReadBody(wire.Requests[0], "RunTask").GetProperty("clientToken").GetString();
        var second = ReadBody(wire.Requests[1], "RunTask").GetProperty("clientToken").GetString();
        Assert.True(Guid.TryParse(first, out var firstToken));
        Assert.True(Guid.TryParse(second, out var secondToken));
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public async Task StopTaskSerializesExactTaskAndCluster()
    {
        using var wire = new AwsWireTransport(_ => JsonReply("""
            {
              "task":{
                "taskArn":"arn:aws:ecs:us-east-1:123456789012:task/runners/0123456789abcdef0123456789abcdef",
                "clusterArn":"arn:aws:ecs:us-east-1:123456789012:cluster/runners",
                "lastStatus":"DEACTIVATING","desiredStatus":"STOPPED"
              }
            }
            """));
        using var client = CreateClient(wire);

        var response = await client.StopTaskAsync(CreateStopRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(TaskArn, response.Task.TaskArn);
        Assert.Equal(ClusterArn, response.Task.ClusterArn);
        Assert.Equal("DEACTIVATING", response.Task.LastStatus);
        Assert.Equal("STOPPED", response.Task.DesiredStatus);
        var body = ReadBody(Assert.Single(wire.Requests), "StopTask");
        Assert.Equal(TaskArn, body.GetProperty("task").GetString());
        Assert.Equal(ClusterArn, body.GetProperty("cluster").GetString());
        Assert.Equal("allocation revoked", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ListTasksUsesClusterScopedStartedByAndOpaqueContinuationToken()
    {
        using var wire = new AwsWireTransport(request =>
        {
            var body = ReadBody(request, "ListTasks");
            if (body.TryGetProperty("nextToken", out _))
            {
                return JsonReply("""
                    {"taskArns":["arn:aws:ecs:us-east-1:123456789012:task/runners/0123456789abcdef0123456789abcdef"]}
                    """);
            }

            return JsonReply("""{"taskArns":[],"nextToken":"page+2=opaque"}""");
        });
        using var client = CreateClient(wire);
        var token = TestContext.Current.CancellationToken;

        var first = await client.ListTasksAsync(new ListTasksRequest { Cluster = ClusterArn, StartedBy = StartedBy }, token);
        Assert.Equal("page+2=opaque", first.NextToken);
        Assert.Empty(first.TaskArns);
        var second = await client.ListTasksAsync(
            new ListTasksRequest { Cluster = ClusterArn, StartedBy = StartedBy, NextToken = first.NextToken },
            token);

        Assert.Equal(TaskArn, Assert.Single(second.TaskArns));
        Assert.Null(second.NextToken);
        Assert.Collection(wire.Requests,
            firstRequest =>
            {
                var body = ReadBody(firstRequest, "ListTasks");
                Assert.Equal(ClusterArn, body.GetProperty("cluster").GetString());
                Assert.Equal(StartedBy, body.GetProperty("startedBy").GetString());
                Assert.Equal(2, body.EnumerateObject().Count());
            },
            secondRequest =>
            {
                var body = ReadBody(secondRequest, "ListTasks");
                Assert.Equal(ClusterArn, body.GetProperty("cluster").GetString());
                Assert.Equal(StartedBy, body.GetProperty("startedBy").GetString());
                Assert.Equal("page+2=opaque", body.GetProperty("nextToken").GetString());
                Assert.Equal(3, body.EnumerateObject().Count());
            });
    }

    [Fact]
    public async Task DescribeTasksBatchesIdsAndReturnsStoppedTaskAndMissingTaskSeparately()
    {
        const string missing = "arn:aws:ecs:us-east-1:123456789012:task/runners/ffffffffffffffffffffffffffffffff";
        using var wire = new AwsWireTransport(_ => JsonReply("""
            {
              "tasks":[{
                "taskArn":"arn:aws:ecs:us-east-1:123456789012:task/runners/0123456789abcdef0123456789abcdef",
                "lastStatus":"STOPPED","tags":[{"key":"Allocation","value":"profile-a"}]
              }],
              "failures":[{
                "arn":"arn:aws:ecs:us-east-1:123456789012:task/runners/ffffffffffffffffffffffffffffffff",
                "reason":"MISSING"
              }]
            }
            """));
        using var client = CreateClient(wire);

        var response = await client.DescribeTasksAsync(
            new DescribeTasksRequest { Cluster = ClusterArn, Tasks = [TaskArn, missing], Include = ["TAGS"] },
            TestContext.Current.CancellationToken);

        var stopped = Assert.Single(response.Tasks);
        Assert.Equal(TaskArn, stopped.TaskArn);
        Assert.Equal("STOPPED", stopped.LastStatus);
        var tag = Assert.Single(stopped.Tags);
        Assert.Equal("Allocation", tag.Key);
        Assert.Equal("profile-a", tag.Value);
        var failure = Assert.Single(response.Failures);
        Assert.Equal(missing, failure.Arn);
        Assert.Equal("MISSING", failure.Reason);
        var body = ReadBody(Assert.Single(wire.Requests), "DescribeTasks");
        Assert.Equal(ClusterArn, body.GetProperty("cluster").GetString());
        Assert.Equal([TaskArn, missing], body.GetProperty("tasks").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("TAGS", Assert.Single(body.GetProperty("include").EnumerateArray()).GetString());
        Assert.False(body.TryGetProperty("nextToken", out _));
    }

    [Fact]
    public async Task DescribeClustersPreservesCanonicalClusterIdentity()
    {
        using var wire = new AwsWireTransport(_ => JsonReply("""
            {"clusters":[{
              "clusterArn":"arn:aws:ecs:us-east-1:123456789012:cluster/runners","status":"ACTIVE"
            }]}
            """));
        using var client = CreateClient(wire);

        var response = await client.DescribeClustersAsync(
            new DescribeClustersRequest { Clusters = [ClusterArn] },
            TestContext.Current.CancellationToken);

        var cluster = Assert.Single(response.Clusters);
        Assert.Equal(ClusterArn, cluster.ClusterArn);
        Assert.Equal("ACTIVE", cluster.Status);
        Assert.Null(response.Failures);
        var body = ReadBody(Assert.Single(wire.Requests), "DescribeClusters");
        Assert.Equal(ClusterArn, Assert.Single(body.GetProperty("clusters").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task DescribeTaskDefinitionUsesPinnedRevisionAndReadsFargatePlatform()
    {
        using var wire = new AwsWireTransport(_ => JsonReply("""
            {"taskDefinition":{
              "taskDefinitionArn":"arn:aws:ecs:us-east-1:123456789012:task-definition/runner:17",
              "revision":17,"networkMode":"awsvpc","requiresCompatibilities":["FARGATE"],
              "runtimePlatform":{"cpuArchitecture":"ARM64","operatingSystemFamily":"LINUX"},
              "taskRoleArn":"arn:aws:iam::123456789012:role/runner-task",
              "executionRoleArn":"arn:aws:iam::123456789012:role/runner-execution",
              "cpu":"1024","memory":"2048","containerDefinitions":[{
                "name":"runner","essential":true,"privileged":false,
                "image":"123456789012.dkr.ecr.us-east-1.amazonaws.com/runner@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
              }]
            }}
            """));
        using var client = CreateClient(wire);

        var response = await client.DescribeTaskDefinitionAsync(
            new DescribeTaskDefinitionRequest { TaskDefinition = TaskDefinitionArn },
            TestContext.Current.CancellationToken);

        var definition = response.TaskDefinition;
        Assert.Equal(TaskDefinitionArn, definition.TaskDefinitionArn);
        Assert.Equal(17, definition.Revision);
        Assert.Equal(NetworkMode.Awsvpc, definition.NetworkMode);
        Assert.Equal("FARGATE", Assert.Single(definition.RequiresCompatibilities));
        Assert.Equal("ARM64", definition.RuntimePlatform.CpuArchitecture.Value);
        Assert.Equal("LINUX", definition.RuntimePlatform.OperatingSystemFamily.Value);
        Assert.Equal("1024", definition.Cpu);
        Assert.Equal("2048", definition.Memory);
        Assert.Equal("arn:aws:iam::123456789012:role/runner-task", definition.TaskRoleArn);
        Assert.Equal("arn:aws:iam::123456789012:role/runner-execution", definition.ExecutionRoleArn);
        var container = Assert.Single(definition.ContainerDefinitions);
        Assert.Equal("runner", container.Name);
        Assert.True(container.Essential);
        Assert.False(container.Privileged);
        Assert.Equal(
            "123456789012.dkr.ecr.us-east-1.amazonaws.com/runner@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            container.Image);
        var body = ReadBody(Assert.Single(wire.Requests), "DescribeTaskDefinition");
        Assert.Equal(TaskDefinitionArn, body.GetProperty("taskDefinition").GetString());
    }

    [Fact]
    public async Task ListAccountSettingsRequestsEffectiveLongArnConfiguration()
    {
        using var wire = new AwsWireTransport(_ => JsonReply("""
            {"settings":[{"name":"taskLongArnFormat","value":"enabled","principalArn":"arn:aws:iam::123456789012:root"}]}
            """));
        using var client = CreateClient(wire);

        var response = await client.ListAccountSettingsAsync(
            new ListAccountSettingsRequest { Name = SettingName.TaskLongArnFormat, EffectiveSettings = true },
            TestContext.Current.CancellationToken);

        var setting = Assert.Single(response.Settings);
        Assert.Equal(SettingName.TaskLongArnFormat, setting.Name);
        Assert.Equal("enabled", setting.Value);
        Assert.Equal("arn:aws:iam::123456789012:root", setting.PrincipalArn);
        var body = ReadBody(Assert.Single(wire.Requests), "ListAccountSettings");
        Assert.Equal("taskLongArnFormat", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("effectiveSettings").GetBoolean());
    }

    [Theory]
    [InlineData("RunTask")]
    [InlineData("StopTask")]
    public async Task JournalOwnedWritesSendOneAttemptOnUnavailableService(string operation)
    {
        using var wire = new AwsWireTransport(_ => JsonReply(
            """{"__type":"ServerException","message":"Injected HTTP 503"}""",
            HttpStatusCode.ServiceUnavailable));
        using var client = CreateClient(wire);

        var exception = await Assert.ThrowsAsync<ServerException>(
            () => SendMutation(client, operation, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        ReadBody(Assert.Single(wire.Requests), operation);
    }

    [Theory]
    [InlineData("RunTask")]
    [InlineData("StopTask")]
    public async Task JournalOwnedWritesSendOneAttemptOnConnectionReset(string operation)
    {
        using var wire = new AwsWireTransport(_ => throw new HttpRequestException(
            "Injected connection reset",
            new SocketException((int)SocketError.ConnectionReset)));
        using var client = CreateClient(wire);

        var exception = await Record.ExceptionAsync(
            () => SendMutation(client, operation, TestContext.Current.CancellationToken));

        Assert.NotNull(exception);
        Assert.IsType<SocketException>(exception.GetBaseException());
        ReadBody(Assert.Single(wire.Requests), operation);
    }

    private static AmazonECSClient CreateClient(AwsWireTransport wire)
    {
        return new AmazonECSClient(AwsWireTransport.Credentials, wire.Configure(new AmazonECSConfig()));
    }

    private static Task SendMutation(AmazonECSClient client, string operation, CancellationToken token)
    {
        return operation switch
        {
            "RunTask" => client.RunTaskAsync(CreateRunRequest(Token), token),
            "StopTask" => client.StopTaskAsync(CreateStopRequest(), token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static RunTaskRequest CreateRunRequest(string? token)
    {
        return new RunTaskRequest
        {
            Cluster = ClusterArn,
            TaskDefinition = TaskDefinitionArn,
            LaunchType = LaunchType.FARGATE,
            Count = 1,
            ClientToken = token,
            StartedBy = StartedBy,
            EnableExecuteCommand = false,
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration
                {
                    AssignPublicIp = AssignPublicIp.DISABLED,
                    SecurityGroups = ["sg-0123456789abcdef0"],
                    Subnets = ["subnet-0123456789abcdef0", "subnet-0123456789abcdef1"],
                },
            },
            Overrides = new TaskOverride
            {
                ContainerOverrides =
                [
                    new ContainerOverride
                    {
                        Name = "runner",
                        Environment = [new Amazon.ECS.Model.KeyValuePair { Name = "ALLOCATION_ID", Value = "allocation-a" }],
                    },
                ],
            },
            Tags = [new Tag { Key = "Allocation", Value = "profile-a" }],
        };
    }

    private static StopTaskRequest CreateStopRequest()
    {
        return new StopTaskRequest { Cluster = ClusterArn, Task = TaskArn, Reason = "allocation revoked" };
    }

    private static HttpResponseMessage JsonReply(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return AwsWireTransport.Json(body, status, JsonMediaType);
    }

    private static JsonElement ReadBody(AwsWireRequest request, string operation)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/", request.Uri.AbsolutePath);
        Assert.Equal(JsonMediaType, request.ContentType);
        Assert.Equal("AmazonEC2ContainerServiceV20141113." + operation, request.Target);
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.Clone();
    }
}
