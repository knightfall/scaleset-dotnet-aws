using System.Net;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class CapturedEcsControllerTaskFenceObserverTests
{
    private const string Group = "arn:aws:logs:us-east-1:123456789012:log-group:/aws/events/controller-fence";
    private const string Rule = "arn:aws:events:us-east-1:123456789012:rule/controller-fence";
    private const string Cluster = "arn:aws:ecs:us-east-1:123456789012:cluster/controller";
    private const string TaskArn = "arn:aws:ecs:us-east-1:123456789012:task/controller/0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Acquired = Now.AddMinutes(-20);
    private static readonly ControllerPredecessorCapture Capture = new(Group, Rule, Now.AddHours(-1), Now.AddHours(1));
    private const string NativeStop = $$"""
        {"version":"0","detail-type":"ECS Task State Change","source":"aws.ecs","account":"123456789012",
         "id":"105f6bb1-4da6-c630-4965-35383018cbca","time":"2026-11-25T00:20:45Z","region":"us-east-1",
         "resources":["{{TaskArn}}"],"detail":{"taskArn":"{{TaskArn}}","clusterArn":"{{Cluster}}",
         "launchType":"FARGATE","lastStatus":"STOPPED","desiredStatus":"STOPPED","version":7,
         "createdAt":"2026-11-25T00:09:20.162Z","stoppedAt":"2026-11-25T00:20:45.320Z"}
        }
        """;

    [Fact]
    public async Task ExactNativeStopAfterAnEmptyPageAndIdenticalRepeatsProvesThePredecessor()
    {
        using var logs = new LogWire(Page("a"), Page("b", NativeStop), Page(null, NativeStop));
        var result = await Reader(logs).ObserveAsync(TaskArn, Acquired, TestContext.Current.CancellationToken);
        Assert.Equal(new ControllerTaskFenceObservation(ControllerTaskFenceStatus.Stopped, Capture.QualifiedUntil), result);
        Assert.Equal(new[] { null, "a", "b" }, logs.Requests.Select(request => request.NextToken));
        Assert.All(logs.Requests, request =>
        {
            Assert.Equal(Group, request.LogGroupIdentifier);
            Assert.Equal($"\"{TaskArn}\"", request.FilterPattern);
            Assert.Equal(Acquired.ToUnixTimeMilliseconds(), request.StartTime);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), request.EndTime);
            Assert.Equal(8, request.Limit);
        });
    }

    [Theory]
    [InlineData(ControllerTaskFenceStatus.Stopped)]
    [InlineData(ControllerTaskFenceStatus.Running)]
    [InlineData(ControllerTaskFenceStatus.Unknown)]
    public async Task OnlyUnambiguousMissingCanReadTheCapture(ControllerTaskFenceStatus state)
    {
        using var logs = new LogWire(Page(null, NativeStop));
        Assert.Equal(new ControllerTaskFenceObservation(state), await Reader(logs, state)
            .ObserveAsync(TaskArn, Acquired, TestContext.Current.CancellationToken));
        Assert.Empty(logs.Requests);
    }

    [Fact]
    public async Task LegacyBoundsWrongClusterAndExpiredQualificationKeepTheFenceClosed()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var logs = new LogWire(Page(null, NativeStop));
        var reader = Reader(logs);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, acquiredAt: null, cancellation)).Status);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn.Replace("/controller/", "/other/", StringComparison.Ordinal),
            Acquired, cancellation)).Status);
        var expired = Reader(logs, capture: Capture with { QualifiedUntil = Now });
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await expired.ObserveAsync(TaskArn, Acquired, cancellation)).Status);
        Assert.Empty(logs.Requests);
        Assert.Equal(ControllerTaskFenceStatus.Stopped, (await Reader(logs, ControllerTaskFenceStatus.Stopped)
            .ObserveAsync(TaskArn, acquiredAt: null, cancellation)).Status);
        Assert.Empty(logs.Requests);
    }

    [Theory]
    [InlineData("\"aws.ecs\"", "\"fixture.ecs\"")]
    [InlineData("{\"version\":\"0\"", "{\"replay-name\":\"older-archive\",\"version\":\"0\"")]
    [InlineData("123456789012\",", "000000000000\",")]
    [InlineData("us-east-1\",", "us-east-2\",")]
    [InlineData("cluster/controller\"", "cluster/other\"")]
    [InlineData("0123456789abcdef0123456789abcdef\",\"clusterArn", "0123456789abcdef0123456789abcdea\",\"clusterArn")]
    [InlineData("\"lastStatus\":\"STOPPED\"", "\"lastStatus\":\"STOPPING\"")]
    [InlineData("\"lastStatus\":\"STOPPED\"", "\"desiredStatus\":\"STOPPED\"")]
    [InlineData("\"FARGATE\"", "\"EC2\"")]
    [InlineData("\"stoppedAt\"", "\"executionStoppedAt\"")]
    [InlineData("00:20:45.320Z", "00:09:45.320Z")]
    [InlineData("00:20:45.320Z", "00:31:45.320Z")]
    [InlineData("00:09:20.162Z", "00:11:20.162Z")]
    [InlineData("\"version\":7", "\"version\":-7")]
    public async Task MalformedStaleAndMismatchedEventsCannotAuthorizeStop(string original, string replacement)
    {
        Assert.Contains(original, NativeStop, StringComparison.Ordinal);
        using var logs = new LogWire(Page(null, NativeStop.Replace(original, replacement, StringComparison.Ordinal)));
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await Reader(logs)
            .ObserveAsync(TaskArn, Acquired, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AccessFailureContradictoryPagesAndIncompletePaginationHoldAfterPositiveLookingEvents()
    {
        var token = TestContext.Current.CancellationToken;
        using var denied = new LogWire { Denied = true };
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await Reader(denied).ObserveAsync(TaskArn, Acquired, token)).Status);
        var other = NativeStop.Replace("\"version\":7", "\"version\":8", StringComparison.Ordinal);
        using var conflicting = new LogWire(Page("a", NativeStop), Page(null, other));
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await Reader(conflicting).ObserveAsync(TaskArn, Acquired, token)).Status);
        using var repeatedToken = new LogWire(Page("a", NativeStop), Page("a"));
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await Reader(repeatedToken).ObserveAsync(TaskArn, Acquired, token)).Status);
        using var unbounded = new LogWire(Page("a", NativeStop), Page("b"), Page("c"), Page("d"), Page("e"), Page("f"));
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await Reader(unbounded).ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(6, unbounded.Requests.Count);
    }

    private static CapturedEcsControllerTaskFenceObserver Reader(LogWire logs, ControllerTaskFenceStatus state = ControllerTaskFenceStatus.Missing,
        ControllerPredecessorCapture? capture = null) =>
        new(new ExactObserver(state), logs, new AwsEnvironment("123456789012", "us-east-1"), Cluster, capture ?? Capture, new FixedClock());

    private static FilterLogEventsResponse Page(string? token, params string[] messages) => new()
    {
        HttpStatusCode = HttpStatusCode.OK,
        NextToken = token,
        Events = messages.Select(message => new FilteredLogEvent { Message = message }).ToList(),
    };

    private sealed class ExactObserver(ControllerTaskFenceStatus status) : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) => Task.FromResult(status);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LogWire(params FilterLogEventsResponse[] pages) : AmazonCloudWatchLogsClient(
        new BasicAWSCredentials("capture-fixture", "fixture-not-a-credential"), new AmazonCloudWatchLogsConfig { ServiceURL = "http://127.0.0.1:1" })
    {
        private readonly Queue<FilterLogEventsResponse> _replies = new(pages);
        internal List<FilterLogEventsRequest> Requests { get; } = [];
        internal bool Denied { get; init; }

        public override Task<FilterLogEventsResponse> FilterLogEventsAsync(FilterLogEventsRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Denied ? Task.FromException<FilterLogEventsResponse>(new AmazonCloudWatchLogsException("Fixture denies the read.")) :
                Task.FromResult(_replies.Dequeue());
        }
    }
}
