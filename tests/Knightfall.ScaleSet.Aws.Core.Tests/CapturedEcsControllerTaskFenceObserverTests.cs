using System.Globalization;
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

    [Theory]
    [InlineData("950", ControllerTaskFenceStatus.Stopped)]
    [InlineData("900", ControllerTaskFenceStatus.Missing)]
    public async Task EnvelopeSecondIsIncludedButTerminalTimeMustReachAcquisition(string milliseconds, ControllerTaskFenceStatus expected)
    {
        var acquired = Acquired.AddMilliseconds(901);
        string message = NativeStop.Replace("00:20:45Z", "00:10:00Z", StringComparison.Ordinal)
            .Replace("00:20:45.320Z", $"00:10:00.{milliseconds}Z", StringComparison.Ordinal);
        using var logs = new LogWire(Page(null, message));
        var observed = await Reader(logs).ObserveAsync(TaskArn, acquired, TestContext.Current.CancellationToken);
        Assert.Equal(expected, observed.Status);
        Assert.Equal(Acquired.ToUnixTimeMilliseconds(), Assert.Single(logs.Requests).StartTime);
    }

    [Fact]
    public async Task StableScanResumesAndRechecksItsWitnessAndCompletionBeforeEachReuse()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        using var logs = new LogWire(Page("a"), Page("b", NativeStop), Page("c"), Page("d"), Page("e"), Page("f"),
            Page("g", NativeStop), Page(null), Page(null), Page("g-2", NativeStop),
            Page(null), Page("g-3"), Page(null));
        var reader = Reader(logs, clock: clock);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(6, logs.Requests.Count);

        clock.Now = Now.AddSeconds(5);
        var stopped = new ControllerTaskFenceObservation(ControllerTaskFenceStatus.Stopped, Capture.QualifiedUntil);
        Assert.Equal(stopped, await reader.ObserveAsync(TaskArn, Acquired, token));
        Assert.Equal(stopped, await reader.ObserveAsync(TaskArn, Acquired, token));
        Assert.Equal(new[] { null, "a", "b", "c", "d", "e", "f", "g", "g", "f" },
            logs.Requests.Select(request => request.NextToken));
        Assert.All(logs.Requests, request => Assert.Equal(Now.ToUnixTimeMilliseconds(), request.EndTime));

        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(["g", "f"], logs.Requests.Skip(10).Select(request => request.NextToken));
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Null(logs.Requests[^1].NextToken);
        Assert.Equal(clock.Now.ToUnixTimeMilliseconds(), logs.Requests[^1].EndTime);
    }

    [Fact]
    public async Task FiniteScanContinuesBeyondOneHundredTwentyPagesAndFiveMinutes()
    {
        const int continuations = 132;
        var pages = Enumerable.Range(1, continuations).Select(page =>
            Page(page.ToString(CultureInfo.InvariantCulture), page == 1 ? [NativeStop] : []));
        using var logs = new LogWire([.. pages, Page(null, NativeStop)]);
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var reader = Reader(logs, clock: clock);

        for (int page = 0; page < continuations; page += 6)
        {
            Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
            Assert.Equal(page + 6, logs.Requests.Count);
            clock.Now = clock.Now.AddSeconds(20);
        }

        Assert.True(clock.Now - Now > TimeSpan.FromMinutes(5));
        var stopped = new ControllerTaskFenceObservation(ControllerTaskFenceStatus.Stopped, Capture.QualifiedUntil);
        Assert.Equal(stopped, await reader.ObserveAsync(TaskArn, Acquired, token));
        Assert.Equal(continuations + 1, logs.Requests.Count);
        Assert.Null(logs.Requests[0].NextToken);
        Assert.Equal(Enumerable.Range(1, continuations).Select(page => page.ToString(CultureInfo.InvariantCulture)),
            logs.Requests.Skip(1).Select(request => request.NextToken));
        Assert.All(logs.Requests, request => Assert.Equal(Now.ToUnixTimeMilliseconds(), request.EndTime));
    }

    [Fact]
    public async Task NonAdjacentCursorCycleDiscardsWitnessWithoutCompletingTheFence()
    {
        using var logs = new LogWire(Page("a", NativeStop), Page("b"), Page("c"), Page("d"), Page("e"), Page("f"),
            Page("g"), Page("h"), Page("i"), Page("j"), Page("g"), Page(null));
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var reader = Reader(logs, clock: clock);

        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(6, logs.Requests.Count);
        clock.Now = Now.AddSeconds(5);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(["f", "g", "h", "i", "j"], logs.Requests.Skip(6).Select(request => request.NextToken));

        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Null(logs.Requests[^1].NextToken);
        Assert.Equal(clock.Now.ToUnixTimeMilliseconds(), logs.Requests[^1].EndTime);
    }

    [Fact]
    public async Task CompletedReuseExpiresAfterFiveMinutesWithoutAValidatedRead()
    {
        using var logs = new LogWire(Page("a"), Page("b"), Page("c"), Page("d"), Page("e"), Page("f"),
            Page(null, NativeStop), Page(null, NativeStop), Page(null, NativeStop), Page(null));
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var reader = Reader(logs, clock: clock);

        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(ControllerTaskFenceStatus.Stopped, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        clock.Now = Now.AddMinutes(4);
        Assert.Equal(ControllerTaskFenceStatus.Stopped, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        clock.Now = Now.AddMinutes(8);
        Assert.Equal(ControllerTaskFenceStatus.Stopped, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(["f", "f", "f"], logs.Requests.Skip(6).Select(request => request.NextToken));

        clock.Now = Now.AddMinutes(13);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Null(logs.Requests[^1].NextToken);
        Assert.Equal(clock.Now.ToUnixTimeMilliseconds(), logs.Requests[^1].EndTime);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("repeated token")]
    [InlineData("invalid token")]
    [InlineData("qualification expiry")]
    public async Task UnsafeContinuationOrExpiredWindowDiscardsEarlierEvidence(string interruption)
    {
        string conflicting = NativeStop.Replace("\"version\":7", "\"version\":8", StringComparison.Ordinal);
        var pending = new[] { Page("a", NativeStop), Page("b"), Page("c"), Page("d"), Page("e"), Page("f") };
        FilterLogEventsResponse[] remaining = interruption switch
        {
            "conflict" => [Page(null, conflicting), Page(null)],
            "repeated token" => [Page("f"), Page(null)],
            _ => [Page(null)],
        };
        using var logs = new LogWire([.. pending, .. remaining]);
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var reader = Reader(logs, clock: clock);

        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(6, logs.Requests.Count);
        clock.Now = interruption == "qualification expiry" ? Capture.QualifiedUntil : Now.AddSeconds(5);
        if (interruption == "invalid token")
        {
            logs.BeforeResponse = (request, _) =>
            {
                Assert.Equal("f", request.NextToken);
                throw new InvalidParameterException("Fixture rejects an expired continuation.");
            };
        }
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        logs.BeforeResponse = null;

        if (interruption is "conflict" or "repeated token" or "invalid token")
        {
            Assert.Equal("f", logs.Requests[^1].NextToken);
            Assert.Equal(Now.ToUnixTimeMilliseconds(), logs.Requests[^1].EndTime);
            Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        }
        if (interruption == "qualification expiry")
        {
            Assert.Equal(6, logs.Requests.Count);
            clock.Now = Now.AddSeconds(5);
            Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        }
        Assert.Null(logs.Requests[^1].NextToken);
        Assert.Equal(clock.Now.ToUnixTimeMilliseconds(), logs.Requests[^1].EndTime);
    }

    [Fact]
    public async Task ClockRollbackAfterProgressStartsAWholeScan()
    {
        using var logs = new LogWire(Page("a", NativeStop), Page("b"), Page("c"), Page("d"), Page("e"), Page("f"),
            Page("g"), Page("h"), Page("i"), Page("j"), Page("k"), Page("l"), Page(null));
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var reader = Reader(logs, clock: clock);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        clock.Now = Now.AddSeconds(5);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal("f", logs.Requests[6].NextToken);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), logs.Requests[6].EndTime);

        clock.Now = Now.AddSeconds(4);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Null(logs.Requests[^1].NextToken);
        Assert.Equal(clock.Now.ToUnixTimeMilliseconds(), logs.Requests[^1].EndTime);
    }

    [Fact]
    public async Task DirectEcsChangeDiscardsTheExactPredecessorsProgress()
    {
        using var logs = new LogWire(Page("a", NativeStop), Page("b"), Page("c"), Page("d"), Page("e"), Page("f"), Page(null));
        var token = TestContext.Current.CancellationToken;
        var direct = new ExactObserver(ControllerTaskFenceStatus.Missing);
        var reader = Reader(logs, current: direct);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);

        direct.Status = ControllerTaskFenceStatus.Running;
        Assert.Equal(ControllerTaskFenceStatus.Running, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(6, logs.Requests.Count);

        direct.Status = ControllerTaskFenceStatus.Missing;
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Null(logs.Requests[^1].NextToken);
    }

    [Fact]
    public async Task TransientServiceFailureCanResumeItsLastValidatedCursor()
    {
        using var logs = new LogWire(Page("a", NativeStop), Page(null));
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        logs.BeforeResponse = (request, _) =>
        {
            if (request.NextToken == "a")
            {
                throw new ServiceUnavailableException("Fixture times out upstream.") { StatusCode = HttpStatusCode.ServiceUnavailable };
            }
        };
        var reader = Reader(logs, clock: clock);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);

        logs.BeforeResponse = null;
        clock.Now = Now.AddSeconds(5);
        Assert.Equal(ControllerTaskFenceStatus.Stopped, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(new[] { null, "a", "a" }, logs.Requests.Select(request => request.NextToken));
        Assert.All(logs.Requests, request => Assert.Equal(Now.ToUnixTimeMilliseconds(), request.EndTime));
    }

    [Fact]
    public async Task SeparatePredecessorsKeepTheirOwnCursorsAndChangedAcquisitionStartsAWholeScan()
    {
        const string otherTask = "arn:aws:ecs:us-east-1:123456789012:task/controller/0123456789abcdef0123456789abcdea";
        using var logs = new LogWire(Page("a"), Page("b"), Page("c"), Page("d"), Page("e"), Page("f"),
            Page("a"), Page("b"), Page("c"), Page("d"), Page("e"), Page("f"),
            Page(null, NativeStop), Page("g"), Page(null));
        var token = TestContext.Current.CancellationToken;
        var clock = new FixtureClock();
        var reader = Reader(logs, clock: clock);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        clock.Now = Now.AddSeconds(5);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(otherTask, Acquired, token)).Status);
        Assert.Equal(ControllerTaskFenceStatus.Stopped, (await reader.ObserveAsync(TaskArn, Acquired, token)).Status);
        Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(otherTask, Acquired.AddSeconds(1), token)).Status);

        foreach (var offset in new[] { 0, 6, 13 })
        {
            Assert.Null(logs.Requests[offset].NextToken);
        }
        Assert.Equal("f", logs.Requests[12].NextToken);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), logs.Requests[12].EndTime);
        Assert.Equal($"\"{otherTask}\"", logs.Requests[13].FilterPattern);
        Assert.Equal(Acquired.AddSeconds(1).ToUnixTimeMilliseconds(), logs.Requests[13].StartTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCanOnlyResumeAfterTheLastFullyValidatedPage(bool cancelCaller)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var logs = new LogWire(Page("a", NativeStop), Page("b"), Page(null));
        var clock = new FixtureClock();
        logs.BeforeResponse = (request, _) =>
        {
            if (request.NextToken == "a")
            {
                if (cancelCaller)
                {
                    caller.Cancel();
                }
                else
                {
                    clock.ExpireLookup();
                }
            }
        };
        var reader = Reader(logs, clock: clock);
        if (cancelCaller)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ObserveAsync(TaskArn, Acquired, caller.Token));
        }
        else
        {
            Assert.Equal(ControllerTaskFenceStatus.Missing, (await reader.ObserveAsync(TaskArn, Acquired, caller.Token)).Status);
        }

        logs.BeforeResponse = null;
        clock.Now = Now.AddSeconds(5);
        Assert.Equal(ControllerTaskFenceStatus.Stopped,
            (await reader.ObserveAsync(TaskArn, Acquired, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(new[] { null, "a", "a", "b" }, logs.Requests.Select(request => request.NextToken));
        Assert.All(logs.Requests, request => Assert.Equal(Now.ToUnixTimeMilliseconds(), request.EndTime));
    }

    private static CapturedEcsControllerTaskFenceObserver Reader(LogWire logs, ControllerTaskFenceStatus state = ControllerTaskFenceStatus.Missing,
        ControllerPredecessorCapture? capture = null, FixtureClock? clock = null, ExactObserver? current = null) =>
        new(current ?? new ExactObserver(state), logs, new AwsEnvironment("123456789012", "us-east-1"), Cluster, capture ?? Capture, clock ?? new FixtureClock());

    private static FilterLogEventsResponse Page(string? token, params string[] messages) => new()
    {
        HttpStatusCode = HttpStatusCode.OK,
        NextToken = token,
        Events = messages.Select(message => new FilteredLogEvent { Message = message }).ToList(),
    };

    private sealed class ExactObserver(ControllerTaskFenceStatus status) : IControllerTaskFenceObserver
    {
        internal ControllerTaskFenceStatus Status { get; set; } = status;

        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) => Task.FromResult(Status);
    }

    private sealed class FixtureClock : TimeProvider
    {
        private Action? _expireLookup;
        internal DateTimeOffset Now { get; set; } = CapturedEcsControllerTaskFenceObserverTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(2), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            _expireLookup = () => callback(state);
            return new LookupTimer();
        }

        internal void ExpireLookup() => Assert.IsType<Action>(_expireLookup)();

        private sealed class LookupTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class LogWire(params FilterLogEventsResponse[] pages) : AmazonCloudWatchLogsClient(
        new BasicAWSCredentials("capture-fixture", "fixture-not-a-credential"), new AmazonCloudWatchLogsConfig { ServiceURL = "http://127.0.0.1:1" })
    {
        private readonly Queue<FilterLogEventsResponse> _replies = new(pages);
        internal List<FilterLogEventsRequest> Requests { get; } = [];
        internal bool Denied { get; init; }
        internal Action<FilterLogEventsRequest, CancellationToken>? BeforeResponse { get; set; }

        public override Task<FilterLogEventsResponse> FilterLogEventsAsync(FilterLogEventsRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            BeforeResponse?.Invoke(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Denied ? Task.FromException<FilterLogEventsResponse>(new AmazonCloudWatchLogsException("Fixture denies the read.")) :
                Task.FromResult(_replies.Dequeue());
        }
    }
}
