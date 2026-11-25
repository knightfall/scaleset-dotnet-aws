using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using OneJobBootstrap = Knightfall.ScaleSet.Aws.RunnerBootstrap.RunnerBootstrap;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class HelloEndpointRegistrationTests
{
    private const string Region = "ap-southeast-2";
    private const string Endpoint = "https://exampleid.lambda-url." + Region + ".on.aws/";

    [Theory]
    [InlineData("http://exampleid.lambda-url.ap-southeast-2.on.aws/")]
    [InlineData("https://exampleid.lambda-url.us-east-1.on.aws/")]
    [InlineData("https://untrusted.exampleid.lambda-url.ap-southeast-2.on.aws/")]
    [InlineData("https://exampleid.lambda-url.ap-southeast-2.on.aws:434/")]
    [InlineData("https://exampleid.lambda-url.ap-southeast-2.on.aws/?ignored=value")]
    public void OnlyAcceptsAnExactRegionalFunctionUrl(string endpoint)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() =>
            new HelloEndpointRegistration(http, endpoint, Region, new SessionAWSCredentials("fixture", "fixture", "fixture"), TimeProvider.System));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task TransientResponsesRetryTheSameSignedHello(HttpStatusCode transient)
    {
        var token = TestContext.Current.CancellationToken;
        using var recipient = JitRecipientKey.Create();
        using var wire = new SigningWire((attempt, _) => Respond(attempt == 1 ? transient : HttpStatusCode.NoContent));
        using var http = new HttpClient(wire);
        var clock = new RetryClock();
        var registration = Registration(http, clock).RegisterAsync(recipient.Hello, token);

        await clock.ExpectBudgetAsync(token);
        await clock.ReleaseRetryAsync(250, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await registration);
        Assert.Equal(2, wire.Attempts);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(recipient.Hello, BootstrapJson.Options), wire.FirstPayload);
    }

    [Fact]
    public async Task LostResponseAfterAcceptanceRetriesTheSameSignedHello()
    {
        var token = TestContext.Current.CancellationToken;
        using var recipient = JitRecipientKey.Create();
        using var wire = new SigningWire((attempt, _) => attempt == 1
            ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "Fixture lost the reply after accepting the request.")
            : Respond(HttpStatusCode.NoContent));
        using var http = new HttpClient(wire);
        var clock = new RetryClock();
        var registration = Registration(http, clock).RegisterAsync(recipient.Hello, token);

        await clock.ExpectBudgetAsync(token);
        await clock.ReleaseRetryAsync(250, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await registration);
        Assert.Equal(2, wire.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task PermanentResponseIsTerminal(HttpStatusCode rejection)
    {
        using var recipient = JitRecipientKey.Create();
        using var wire = new SigningWire((_, _) => Respond(rejection));
        using var http = new HttpClient(wire);
        var clock = new RetryClock();
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(LifecycleWriteOutcome.Conflict, await Registration(http, clock).RegisterAsync(recipient.Hello, token));
        await clock.ExpectBudgetAsync(token);
        Assert.Equal(1, wire.Attempts);
        Assert.False(clock.HasPendingTimer);
    }

    [Fact]
    public async Task SevenAttemptsExhaustOneEc2ReservationWithoutReadingAnOfferOrRegisteringAgain()
    {
        var token = TestContext.Current.CancellationToken;
        using var wire = new SigningWire((_, _) => Respond(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(wire);
        using var dynamoWire = new AwsWireTransport(_ => throw new InvalidOperationException("Unconfirmed HELLO reached the offer store."));
        using var ddb = new AmazonDynamoDBClient(AwsWireTransport.Credentials, dynamoWire.Configure(new AmazonDynamoDBConfig()));
        var clock = new RetryClock();
        const string resource = "arn:aws:ec2:ap-southeast-2:111122223333:instance/i-0123456789abcdef1";
        const string principal = "AROAFIXTUREROLE000001:i-0123456789abcdef1";
        int spent = 0;
        void CheckReserved() => Assert.Equal(1, Volatile.Read(ref spent));
        OneJobBootstrap Bootstrap() => new(new DynamoDbBootstrapOfferClient(ddb, "fixture-table", principal, resource, clock),
            new RunnerBootstrapTarget("111122223333:ap-southeast-2:fixture", "fixture", "github.com/fixture/repo", 17),
            resource, principal, () => Interlocked.Exchange(ref spent, 1) == 0, new UnexpectedRunner(), clock,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), (hello, cancellationToken) =>
            {
                CheckReserved();
                return Registration(http, clock).RegisterAsync(hello, cancellationToken);
            });

        var registration = Bootstrap().RunAsync(token);
        await clock.ExpectBudgetAsync(token);
        foreach (int ceiling in new[] { 250, 500, 1_000, 2_000, 4_000, 4_000 })
        {
            await clock.ReleaseRetryAsync(ceiling, token);
        }
        Assert.Equal(RunnerBootstrapOutcome.HelloRejected, await registration);
        Assert.Equal(RunnerBootstrapOutcome.AlreadySpent, await Bootstrap().RunAsync(token));
        Assert.Equal(7, wire.Attempts);
        Assert.False(clock.HasPendingTimer);
        Assert.Empty(dynamoWire.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeadlineAndCallerCancellationStopSendsAndJitter(bool callerCancels, bool duringRequest)
    {
        var token = TestContext.Current.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var recipient = JitRecipientKey.Create();
        using var wire = new SigningWire((_, signal) => duringRequest
            ? BlockRequestAsync(signal) : Respond(HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(wire);
        var clock = new RetryClock();
        var registration = Registration(http, clock).RegisterAsync(recipient.Hello, caller.Token);

        var budget = await clock.ExpectBudgetAsync(token);
        var backoff = duringRequest ? null : await clock.TakeTimerAsync(token);
        Assert.False(registration.IsCompleted);
        if (callerCancels)
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration);
        }
        else
        {
            budget.Fire();
            Assert.Equal(LifecycleWriteOutcome.Indeterminate, await registration);
        }
        backoff?.Fire();
        Assert.False(clock.HasPendingTimer);
        Assert.Equal(1, wire.Attempts);
    }

    [Fact]
    public async Task ConcurrentBurstReusesAvailableBrokerSlotsAfterThrottle()
    {
        var token = TestContext.Current.CancellationToken;
        using var first = JitRecipientKey.Create();
        using var second = JitRecipientKey.Create();
        using var third = JitRecipientKey.Create();
        using var fourth = JitRecipientKey.Create();
        JitHello[] hellos = [first.Hello, second.Hello, third.Hello, fourth.Hello];
        var clocks = hellos.Select(_ => new RetryClock()).ToArray();
        using var wire = new BurstWire();
        using var http = new HttpClient(wire);
        var tasks = hellos.Select((hello, index) => Registration(http, clocks[index]).RegisterAsync(hello, token)).ToArray();

        for (int index = 0; index < hellos.Length; index++)
        {
            await clocks[index].ExpectBudgetAsync(token);
        }
        Assert.Equal(2, wire.Throttles);
        Assert.Empty(wire.Accepted);
        Assert.All(tasks, task => Assert.False(task.IsCompleted));
        wire.ReleaseFirstSends();
        Assert.Equal(LifecycleWriteOutcome.Committed, await tasks[0]);
        Assert.Equal(LifecycleWriteOutcome.Committed, await tasks[1]);
        for (int index = 2; index < hellos.Length; index++)
        {
            await clocks[index].ReleaseRetryAsync(250, token);
            Assert.Equal(LifecycleWriteOutcome.Committed, await tasks[index]);
        }
        Assert.Equal(2, wire.Throttles);
        Assert.Equal(4, wire.Accepted.Count);
        Assert.Equal(hellos.Select(hello => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hello, BootstrapJson.Options))))
            .Order(StringComparer.Ordinal), wire.Accepted.Keys.Order(StringComparer.Ordinal));
    }

    private static HelloEndpointRegistration Registration(HttpClient http, TimeProvider clock) =>
        new(http, Endpoint, Region, new SessionAWSCredentials("fixture-key", "fixture-secret", "fixture-session"), clock);

    private static Task<HttpResponseMessage> Respond(HttpStatusCode code) => Task.FromResult(new HttpResponseMessage(code));

    private static Task<HttpResponseMessage> BlockRequestAsync(CancellationToken token) =>
        new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(token);

    private sealed class SigningWire(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public byte[]? FirstPayload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Endpoint, request.RequestUri?.AbsoluteUri);
            var content = Assert.IsType<ByteArrayContent>(request.Content);
            Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
            byte[] body = await content.ReadAsByteArrayAsync(cancellationToken);
            FirstPayload ??= body;
            Assert.Equal(FirstPayload, body);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)),
                Assert.Single(request.Headers.GetValues("X-Amz-Content-SHA256")));
            Assert.Equal("fixture-session", Assert.Single(request.Headers.GetValues("X-Amz-Security-Token")));
            Assert.Equal("20260102T030405Z", Assert.Single(request.Headers.GetValues("X-Amz-Date")));
            string authorization = Assert.Single(request.Headers.GetValues("Authorization"));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=fixture-key/20260102/ap-southeast-2/lambda/aws4_request, ", authorization);
            Assert.Contains("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token", authorization, StringComparison.Ordinal);
            return await respond(attempt, cancellationToken);
        }
    }

    private sealed class BurstWire : HttpMessageHandler
    {
        private readonly SemaphoreSlim _slots = new(2, 2);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _throttles;

        internal int Throttles => Volatile.Read(ref _throttles);

        internal ConcurrentDictionary<string, byte> Accepted { get; } = new(StringComparer.Ordinal);

        internal void ReleaseFirstSends() => _released.SetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_slots.Wait(0, cancellationToken))
            {
                Interlocked.Increment(ref _throttles);
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
            try
            {
                await _released.Task.WaitAsync(cancellationToken);
                byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Assert.True(Accepted.TryAdd(Convert.ToHexStringLower(SHA256.HashData(body)), 0));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            finally
            {
                _slots.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _slots.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class UnexpectedRunner : IOneJobRunner
    {
        public Task<OneJobCompletion> RunAsync(JitPlaintext configuration, string expectedName, Func<long, CancellationToken, Task> ready,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Unconfirmed HELLO started a runner.");
    }

    private sealed class RetryClock : TimeProvider
    {
        private readonly Channel<ScheduledTimer> _scheduled = Channel.CreateUnbounded<ScheduledTimer>();

        internal bool HasPendingTimer => _scheduled.Reader.TryPeek(out _);

        public override DateTimeOffset GetUtcNow() => new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ScheduledTimer(callback, state, dueTime);
            Assert.True(_scheduled.Writer.TryWrite(timer));
            return timer;
        }

        internal Task<ScheduledTimer> TakeTimerAsync(CancellationToken token) =>
            _scheduled.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);

        internal async Task<ScheduledTimer> ExpectBudgetAsync(CancellationToken token)
        {
            var timer = await TakeTimerAsync(token);
            Assert.Equal(TimeSpan.FromSeconds(25), timer.Delay);
            return timer;
        }

        internal async Task ReleaseRetryAsync(int ceiling, CancellationToken token)
        {
            var timer = await TakeTimerAsync(token);
            Assert.InRange(timer.Delay.TotalMilliseconds, ceiling / 2d, ceiling);
            timer.Fire();
        }
    }

    private sealed class ScheduledTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        private TimerCallback? _callback = callback;

        internal TimeSpan Delay { get; } = delay;

        internal void Fire() => Interlocked.Exchange(ref _callback, null)?.Invoke(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Interlocked.Exchange(ref _callback, null);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
