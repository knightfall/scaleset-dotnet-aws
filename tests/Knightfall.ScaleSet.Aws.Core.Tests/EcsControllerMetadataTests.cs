using System.Net;
using System.Net.Sockets;
using System.Text;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class EcsControllerMetadataTests
{
    private const string Cluster = "engineering-dev-controller";
    private const string ClusterArn = "arn:aws:ecs:ap-southeast-2:111122223333:cluster/" + Cluster;
    private const string TaskArn = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-dev-controller/0123456789abcdef0123456789abcdef";
    private const string Reference = "2026-11-24T11:30:00Z";
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 11, 30, 5, TimeSpan.Zero);
    private const string ValidDocument = $$$"""
        {"Cluster":"{{{ClusterArn}}}","TaskARN":"{{{TaskArn}}}","LaunchType":"FARGATE",
         "ClockDrift":{"ClockErrorBound":0.446931,"ReferenceTimestamp":"{{{Reference}}}","ClockSynchronizationStatus":"SYNCHRONIZED"}}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactFargateMetadataYieldsABoundedClockUsingMilliseconds(bool useShortCluster)
    {
        string body = useShortCluster ? ValidDocument.Replace(ClusterArn, Cluster, StringComparison.Ordinal) : ValidDocument;
        var result = Assert.IsType<ControllerTaskAuthority>(await ReadFixtureAsync(body));

        Assert.Equal(TaskArn, result.TaskArn);
        Assert.Equal(ClusterArn, result.ClusterArn);
        Assert.Equal(Now, result.Clock.ObservedAt);
        Assert.Equal(Now.AddSeconds(15), result.Clock.ValidUntil);
        Assert.Equal(TimeSpan.FromTicks(4470), result.Clock.MaximumAbsoluteSkew);
        Assert.True(result.Clock.TryGetUpperBound(Now, Now.AddSeconds(30), TimeSpan.FromSeconds(10), out var upper));
        Assert.Equal(Now.AddTicks(4470), upper);
    }

    [Theory]
    [InlineData("ap-southeast-2", "us-east-1")]
    [InlineData("111122223333", "999999999999")]
    [InlineData("engineering-dev-controller", "runner-cluster")]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("FARGATE", "EC2")]
    [InlineData("SYNCHRONIZED", "NOT_SYNCHRONIZED")]
    [InlineData("2026-11-24T11:30:00Z", "2026-11-24T11:27:00Z")]
    [InlineData("2026-11-24T11:30:00Z", "2026-11-24T11:30:06Z")]
    [InlineData("2026-11-24T11:30:00Z", "2026-11-24T11:30:00+00:00")]
    [InlineData("ReferenceTimestamp", "ReferenceTime")]
    [InlineData("0.446931", "1000")]
    [InlineData("0.446931", "-0.1")]
    [InlineData("0.446931", "\"0.446931\"")]
    [InlineData("0.446931", "1e999")]
    [InlineData("0.446931", "0,\"ClockErrorBound\":0")]
    public async Task ForeignTaskAndUnusableClockNeverYieldAuthority(string value, string replacement) =>
        Assert.Null(await ReadFixtureAsync(ValidDocument.Replace(value, replacement, StringComparison.Ordinal)));

    [Fact]
    public async Task AgedReferenceLimitsProofEvenWhenTheMetadataReadWasRecent()
    {
        var result = Assert.IsType<ControllerTaskAuthority>(await ReadFixtureAsync(
            ValidDocument.Replace(Reference, "2026-11-24T11:28:06Z", StringComparison.Ordinal)));

        Assert.Equal(Now.AddSeconds(1), result.Clock.ValidUntil);
        Assert.False(result.Clock.TryGetUpperBound(Now.AddSeconds(1), Now.AddSeconds(30), TimeSpan.Zero, out _));
    }

    [Fact]
    public async Task RedirectsAndOversizedResponsesGiveNoClockEvidence()
    {
        Assert.Null(await ReadFixtureAsync(ValidDocument, "302 Found\r\nLocation: /redirect"));
        Assert.Null(await ReadFixtureAsync(new string(' ', (64 * 1024) + 1)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyHeartbeatReportsTheMetadataReadDeadlineAsATimeout(bool heartbeat)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var token = budget.Token;
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = ServeAsync(server, "200 OK", ValidDocument, token, async cancellation =>
        {
            received.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        });
        string path = Path.Combine(AppContext.BaseDirectory, "ConfigFixtures", "runtime", "fargate-only.json");
        var configuration = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(path, token));
        var clock = new FixtureClock { HoldReadDeadline = true };
        using var metadata = new EcsControllerMetadata(configuration, clock, $"http://127.0.0.1:{port}/v4/context", allowLoopback: true);

        Task read = heartbeat ? Assert.ThrowsAsync<TimeoutException>(() => metadata.ReadClockAsync(TaskArn, token))
            : ObserveNoAcquisitionEvidenceAsync();
        try
        {
            await received.Task.WaitAsync(token);
            Assert.False(read.IsCompleted);
            Assert.IsType<Action>(clock.ExpireReadDeadline)();
            await read.WaitAsync(token);
        }
        finally
        {
            await budget.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        }

        async Task ObserveNoAcquisitionEvidenceAsync() => Assert.Null(await metadata.ReadAsync(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:9000/v4/test")]
    [InlineData("http://localhost/v4/test")]
    [InlineData("http://169.254.169.254/v4/test")]
    [InlineData("http://169.254.170.2:9000/v4/test")]
    [InlineData("https://169.254.170.2/v4/test")]
    [InlineData("http://user@169.254.170.2/v4/test")]
    [InlineData("http://169.254.170.2/v4/test?extra")]
    [InlineData("http://169.254.170.2/v4/test#fragment")]
    [InlineData("http://169.254.170.2/v4/%74est")]
    [InlineData("http://169.254.170.2/v4/test/task")]
    public void ProductionRejectsNoncanonicalOrNonEcsAuthorities(string? endpoint) =>
        Assert.Null(EcsControllerMetadata.ParseTaskEndpoint(endpoint, allowLoopback: false));

    [Fact]
    public void ProductionUsesOnlyTheCanonicalEcsAddressAndTaskPath()
    {
        var endpoint = EcsControllerMetadata.ParseTaskEndpoint("http://169.254.170.2/v4/fixture_context-01", allowLoopback: false);
        Assert.Equal("http://169.254.170.2/v4/fixture_context-01/task", endpoint?.AbsoluteUri);
    }

    private static async Task<ControllerTaskAuthority?> ReadFixtureAsync(string body, string status = "200 OK")
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var token = budget.Token;
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var exchange = ServeAsync(server, status, body, token);
        string path = Path.Combine(AppContext.BaseDirectory, "ConfigFixtures", "runtime", "fargate-only.json");
        var configuration = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(path, token));
        using var metadata = new EcsControllerMetadata(configuration, new FixtureClock(), $"http://127.0.0.1:{port}/v4/context", allowLoopback: true);

        var result = await metadata.ReadAsync(token);
        await exchange;
        Assert.False(server.Pending());
        return result;
    }

    private static async Task ServeAsync(TcpListener listener, string status, string body, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeResponse = null)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("GET /v4/context/task HTTP/1.1", await reader.ReadLineAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 })
        {
        }
        if (beforeResponse is not null)
        {
            await beforeResponse(cancellationToken);
        }

        string reply = $"HTTP/1.1 {status}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), cancellationToken);
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal bool HoldReadDeadline { get; init; }
        internal Action? ExpireReadDeadline { get; private set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => 0;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!HoldReadDeadline)
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }
            ExpireReadDeadline = () => callback(state);
            return System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
