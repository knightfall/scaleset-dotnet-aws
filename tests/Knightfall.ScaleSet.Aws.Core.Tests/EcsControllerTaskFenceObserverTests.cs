using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.ECS;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class EcsControllerTaskFenceObserverTests
{
    private const string ClusterArn = "arn:aws:ecs:us-east-1:123456789012:cluster/controller";
    private const string TaskArn = "arn:aws:ecs:us-east-1:123456789012:task/controller/0123456789abcdef0123456789abcdef";
    private const string OtherCluster = "arn:aws:ecs:us-east-1:123456789012:cluster/another";
    private const string OtherTask = "arn:aws:ecs:us-east-1:123456789012:task/controller/ffffffffffffffffffffffffffffffff";
    private const string WireEndpoint = "https://ecs.fixture.invalid/";
    private const string JsonMediaType = "application/x-amz-json-1.1";
    private const string StoppedTask = $$"""{"taskArn":"{{TaskArn}}","clusterArn":"{{ClusterArn}}","lastStatus":"STOPPED"}""";
    private const string MissingTask = $$"""{"arn":"{{TaskArn}}","reason":"MISSING"}""";

    public static TheoryData<string> AmbiguousResponses { get; } = new()
    {
        "{}",
        """{"tasks":[],"failures":[]}""",
        $$"""{"tasks":[{{StoppedTask}},{{StoppedTask}}]}""",
        $$"""{"tasks":[{{StoppedTask}}],"failures":[{{MissingTask}}]}""",
        $$"""{"tasks":[{{StoppedTask}}],"failures":[{"arn":"{{OtherTask}}","reason":"MISSING"}]}""",
        $$"""{"tasks":[{{StoppedTask.Replace(ClusterArn, OtherCluster, StringComparison.Ordinal)}}]}""",
        $$"""{"tasks":[{{StoppedTask.Replace(TaskArn, OtherTask, StringComparison.Ordinal)}}]}""",
        $$"""{"tasks":[{"taskArn":"{{TaskArn}}","lastStatus":"STOPPED"}]}""",
        $$"""{"tasks":[{"taskArn":"{{TaskArn}}","clusterArn":"{{ClusterArn}}","desiredStatus":"STOPPED"}]}""",
        $$"""{"tasks":[{"taskArn":"{{TaskArn}}","clusterArn":"{{ClusterArn}}","lastStatus":"STOPPED","desiredStatus":"RUNNING"}]}""",
        $$"""{"tasks":[{"taskArn":"{{TaskArn}}","clusterArn":"{{ClusterArn}}","lastStatus":"DELETED"}]}""",
        $$"""{"failures":[{"arn":"{{OtherTask}}","reason":"MISSING"}]}""",
        $$"""{"failures":[{"arn":"{{TaskArn}}","reason":"ACCESS_DENIED"}]}""",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactStoppedTaskInConfiguredClusterProvesStop(bool includeFailures)
    {
        string body = includeFailures
            ? $$"""
                {"tasks":[{"taskArn":"{{TaskArn}}","clusterArn":"{{ClusterArn}}","lastStatus":"STOPPED","desiredStatus":"STOPPED"}],
                 "failures":[]}
                """
            : $$"""{"tasks":[{{StoppedTask}}]}""";
        using var wire = Wire.RespondWith(body);
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);

        var status = await observer.ReadAsync(TaskArn, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerTaskFenceStatus.Stopped, status);
        AssertExactDescribeRequest(wire);
    }

    [Theory]
    [InlineData("PROVISIONING")]
    [InlineData("PENDING")]
    [InlineData("ACTIVATING")]
    [InlineData("RUNNING")]
    [InlineData("DEACTIVATING")]
    [InlineData("STOPPING")]
    [InlineData("DEPROVISIONING")]
    public async Task DesiredStopDoesNotProveNonterminalTaskStopped(string lastStatus)
    {
        using var wire = Wire.RespondWith($$"""
            {"tasks":[{"taskArn":"{{TaskArn}}","clusterArn":"{{ClusterArn}}","lastStatus":"{{lastStatus}}","desiredStatus":"STOPPED"}]}
            """);
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);

        var status = await observer.ReadAsync(TaskArn, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerTaskFenceStatus.Running, status);
        AssertExactDescribeRequest(wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoleExactMissingFailureIsReportedWithoutStopProof(bool includeTasks)
    {
        string body = includeTasks
            ? $$"""{"tasks":[],"failures":[{{MissingTask}}]}"""
            : $$"""{"failures":[{{MissingTask}}]}""";
        using var wire = Wire.RespondWith(body);
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);

        var status = await observer.ReadAsync(TaskArn, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerTaskFenceStatus.Missing, status);
        AssertExactDescribeRequest(wire);
    }

    [Theory]
    [MemberData(nameof(AmbiguousResponses))]
    public async Task IncompleteContradictoryOrMismatchedEvidenceIsUnknown(string body)
    {
        using var wire = Wire.RespondWith(body);
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);

        var status = await observer.ReadAsync(TaskArn, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerTaskFenceStatus.Unknown, status);
        AssertExactDescribeRequest(wire);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"__type":"ServerException","message":"fixture unavailable"}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"__type":"AccessDeniedException","message":"fixture denied"}""")]
    [InlineData(HttpStatusCode.OK, "{")]
    [InlineData(HttpStatusCode.Created, $$"""{"tasks":[{{StoppedTask}}]}""")]
    public async Task FailedOrMalformedResponsesAreUnknown(HttpStatusCode responseStatus, string body)
    {
        using var wire = Wire.RespondWith(body, responseStatus);
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);

        var status = await observer.ReadAsync(TaskArn, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerTaskFenceStatus.Unknown, status);
        AssertExactDescribeRequest(wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DroppedOrTimedOutRequestPreservesUncertainty(bool timeout)
    {
        Exception error = timeout
            ? new TaskCanceledException("Injected transport timeout")
            : new HttpRequestException("Injected connection loss");
        using var wire = new Wire(_ => Task.FromException<HttpResponseMessage>(error));
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);

        var status = await observer.ReadAsync(TaskArn, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerTaskFenceStatus.Unknown, status);
        AssertExactDescribeRequest(wire);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task/0123456789abcdef0123456789abcdef")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task/another/0123456789abcdef0123456789abcdef")]
    [InlineData("arn:aws:ecs:us-west-2:123456789012:task/controller/0123456789abcdef0123456789abcdef")]
    [InlineData("arn:aws:ecs:us-east-1:111111111111:task/controller/0123456789abcdef0123456789abcdef")]
    [InlineData("arn:aws-cn:ecs:us-east-1:123456789012:task/controller/0123456789abcdef0123456789abcdef")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task/controller/")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task/controller/0123")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task/controller/0123456789abcdef0123456789abcdeg")]
    public async Task MalformedOrForeignTaskArnsDoNotIssueRequests(string? taskArn)
    {
        using var wire = Wire.RespondWith($$"""{"tasks":[{{StoppedTask}}]}""");
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);

        var status = await observer.ReadAsync(taskArn!, TestContext.Current.CancellationToken);

        Assert.Equal(ControllerTaskFenceStatus.Unknown, status);
        Assert.Empty(wire.Requests);
    }

    [Theory]
    [InlineData("controller")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:cluster/")]
    [InlineData("arn:aws:ecs:us-east-1:1234:cluster/controller")]
    [InlineData(TaskArn)]
    public void RequiresCanonicalEcsClusterArn(string clusterArn)
    {
        using var wire = Wire.RespondWith("{}");
        using var client = wire.CreateClient();

        Assert.Throws<ArgumentException>("controllerClusterArn", () => new EcsControllerTaskFenceObserver(client, clusterArn));
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task CancellationOfInFlightDescribePropagatesToCaller()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wire = new Wire(async cancellationToken =>
        {
            sent.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected fixture cancellation.");
        });
        using var client = wire.CreateClient();
        var observer = new EcsControllerTaskFenceObserver(client, ClusterArn);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var reading = observer.ReadAsync(TaskArn, caller.Token);
        await sent.Task.WaitAsync(TestContext.Current.CancellationToken);
        await caller.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(caller.Token, error.CancellationToken);
        AssertExactDescribeRequest(wire);
    }

    private static void AssertExactDescribeRequest(Wire wire)
    {
        var request = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(WireEndpoint, request.Uri?.AbsoluteUri);
        Assert.Equal("AmazonEC2ContainerServiceV20141113.DescribeTasks", request.Target);
        Assert.Equal(JsonMediaType, request.MediaType);
        using var document = JsonDocument.Parse(request.Body);
        var body = document.RootElement;
        Assert.Equal(ClusterArn, body.GetProperty("cluster").GetString());
        Assert.Equal(TaskArn, Assert.Single(body.GetProperty("tasks").EnumerateArray()).GetString());
        Assert.Equal(2, body.EnumerateObject().Count());
    }

    private sealed record ObservedRequest(HttpMethod Method, Uri? Uri, string Target, string Body, string? MediaType);

    private sealed class Wire(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly List<ObservedRequest> _requests = [];

        internal IReadOnlyList<ObservedRequest> Requests => _requests;

        internal static Wire RespondWith(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(_ => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, JsonMediaType),
            }));

        internal AmazonECSClient CreateClient() => new(
            new BasicAWSCredentials("AKIDEXAMPLE", "offline-fixture-secret"),
            new AmazonECSConfig
            {
                ServiceURL = WireEndpoint,
                AuthenticationRegion = "us-east-1",
                RetryMode = RequestRetryMode.Standard,
                MaxErrorRetry = 0,
                MaxStaleConnectionRetries = 0,
                DisableClockSkewCorrection = true,
                HttpClientFactory = new WireClientFactory(this),
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content;
            Assert.NotNull(content);
            var body = await content.ReadAsStringAsync(cancellationToken);
            _requests.Add(new ObservedRequest(request.Method, request.RequestUri,
                request.Headers.GetValues("X-Amz-Target").Single(), body, content.Headers.ContentType?.MediaType));
            return await respond(cancellationToken);
        }
    }

    private sealed class WireClientFactory(Wire wire) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(wire, disposeHandler: false);
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
