using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Health;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class RunCommandTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("9223372036854775808")]
    public async Task RejectsNonPositiveOrUnrepresentableGenerationBeforeOpeningTheArtifact(string generation)
    {
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);
        int exit = await RunCommand.RunAsync(
            ["run", "--config", "missing-runtime.json", "--sha256", new string('a', 64), "--generation", generation],
            TextWriter.Null, errors, NeverRun, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Contains("--generation <positive-integer>", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("missing-runtime.json", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsActiveArtifactWithMismatchedDigestWithoutComposingTheEngine()
    {
        using var runtime = new LocalRuntime(ConfigurationFixtures.Mixed);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var args = RunArguments(runtime.Path, new string('0', 64), 8080);

        int exit = await RunCommand.RunAsync(args, TextWriter.Null, errors, NeverRun, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.StartsWith("digest / ", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveEngineReceivesVerifiedSettingsAndSeparateOrderlyStopAndCleanupSignals()
    {
        using var runtime = new LocalRuntime(ConfigurationFixtures.Mixed);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var contextStarted = new TaskCompletionSource<ControllerRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int port = FreeLoopbackPort();
        const string ec2 = "https://sqs.ap-southeast-2.amazonaws.com/111122223333/ec2-events";
        const string ecs = "https://sqs.ap-southeast-2.amazonaws.com/111122223333/ecs-events";
        var args = RunArguments(runtime.Path, runtime.Digest, port,
            "--ec2-event-queue-url", ec2, "--ecs-event-queue-url", ecs);

        var run = RunCommand.RunAsync(args, TextWriter.Null, errors, async context =>
        {
            context.PublishHealth(new ControllerHealthSnapshot(true, true, true, true,
                context.Configuration.Profiles.Select(profile => new ControllerProfileHealth(profile.Id, true)).ToImmutableArray()));
            contextStarted.TrySetResult(context);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.Stopping);
            }
            catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested)
            {
                stopObserved.TrySetResult(context.ShutdownDeadline.IsCancellationRequested);
            }
            await finishCleanup.Task.WaitAsync(context.ShutdownDeadline);
        }, stopping.Token);
        using var client = HealthClient(port);
        try
        {
            var context = await contextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(ControllerMode.Active, context.Configuration.Mode);
            Assert.Equal(runtime.Digest, context.Configuration.Sha256);
            Assert.Equal(7, context.Generation);
            Assert.Equal(new ControllerEventQueues(ec2, ecs), context.EventQueues);

            using var ready = await client.GetAsync("readyz", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            await stopping.CancelAsync();
            Assert.False(await stopObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));

            // Lifecycle cleanup is still on the barrier, but health observes the same stop signal immediately.
            using var draining = await client.GetAsync("readyz", cancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, draining.StatusCode);
            using var response = JsonDocument.Parse(await draining.Content.ReadAsStringAsync(cancellationToken));
            Assert.False(response.RootElement.GetProperty("leaseActive").GetBoolean());

            finishCleanup.TrySetResult();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken));
            Assert.Empty(errors.ToString());
        }
        finally
        {
            finishCleanup.TrySetResult();
            await stopping.CancelAsync();
            _ = await run.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        }
    }

    [Fact]
    public async Task ReadOnlyRunRemainsLiveWithoutComposingTheActiveGraph()
    {
        using var runtime = new LocalRuntime(ConfigurationFixtures.Ec2Pinned);
        var cancellationToken = TestContext.Current.CancellationToken;
        int port = FreeLoopbackPort();
        using var client = HealthClient(port);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var run = RunCommand.RunAsync(RunArguments(runtime.Path, runtime.Digest, port),
            TextWriter.Null, errors, NeverRun, stopping.Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                Assert.False(run.IsCompleted, errors.ToString());
                try
                {
                    using var ready = await client.GetAsync("readyz", deadline.Token);
                    if (ready.StatusCode == HttpStatusCode.OK)
                    {
                        using var response = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(deadline.Token));
                        Assert.True(response.RootElement.GetProperty("configurationVerified").GetBoolean());
                        Assert.False(response.RootElement.GetProperty("ownershipRequired").GetBoolean());
                        break;
                    }
                }
                catch (HttpRequestException) when (!deadline.IsCancellationRequested)
                {
                    // Startup has not bound loopback yet.
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            Assert.False(run.IsCompleted);
        }
        finally
        {
            await stopping.CancelAsync();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken));
        }
        Assert.Empty(errors.ToString());
    }

    [Theory]
    [InlineData(ConfigurationFixtures.Ec2Pinned, "--ec2-event-queue-url", "https://sqs.ap-southeast-2.amazonaws.com/111122223333/ec2")]
    [InlineData(ConfigurationFixtures.Fargate, "--ec2-event-queue-url", "https://sqs.ap-southeast-2.amazonaws.com/111122223333/ec2")]
    [InlineData(ConfigurationFixtures.Mixed, "--ecs-event-queue-url", "https://sqs.ap-southeast-2.amazonaws.com/123456789012/ecs")]
    [InlineData(ConfigurationFixtures.Mixed, "--ecs-event-queue-url", "https://sqs.us-east-1.amazonaws.com/111122223333/ecs")]
    [InlineData(ConfigurationFixtures.Mixed, "--ecs-event-queue-url", "https://unknown.example/111122223333/ecs")]
    [InlineData(ConfigurationFixtures.Mixed, "--ecs-event-queue-url", "https://sqs.ap-southeast-2.amazonaws.com/111122223333/ecs?other=1")]
    public async Task RejectsQueuesOutsideTheSelectedActiveBackendAndVerifiedDeployment(string fixture, string flag, string url)
    {
        using var runtime = new LocalRuntime(fixture);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);

        int exit = await RunCommand.RunAsync(RunArguments(runtime.Path, runtime.Digest, 8080, flag, url),
            TextWriter.Null, errors, NeverRun, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.DoesNotContain(url, errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitLoopbackQueueMustMatchTheExactSelectedLocalAuthority()
    {
        var configuration = RuntimeConfigurationLoader.Load(ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed));
        var endpoint = new Uri("http://127.0.0.1:4566");
        var options = new RunOptions("runtime.json", null, null, new string('f', 64), 1, null, null,
            "http://127.0.0.1:4566/111122223333/ec2", "http://127.0.0.1:4566/111122223333/ecs");

        Assert.Equal(options.Ec2Queue, options.BindQueues(configuration, endpoint).Ec2);
        Assert.Equal(options.EcsQueue, options.BindQueues(configuration, endpoint).Ecs);
        Assert.Throws<ArgumentException>(() => (options with { Ec2Queue = "http://127.0.0.1:4567/ec2" })
            .BindQueues(configuration, endpoint));
    }

    private static Task NeverRun(ControllerRunContext context) =>
        throw new InvalidDataException($"Unexpected active runtime generation {context.Generation}.");

    private static string[] RunArguments(string path, string digest, int port, params string[] more) =>
        ["run", "--config", path, "--sha256", digest, "--generation", "7", "--health-addr", $"127.0.0.1:{port}", .. more];

    private static HttpClient HealthClient(int port) => new(new SocketsHttpHandler { UseProxy = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(2),
    };

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class LocalRuntime : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("scaleset-run-");

        internal LocalRuntime(string fixture)
        {
            byte[] bytes = ConfigurationFixtures.Runtime(fixture);
            Digest = ConfigurationFixtures.Sha256(bytes);
            Path = System.IO.Path.Combine(_directory.FullName, "runtime.json");
            File.WriteAllBytes(Path, bytes);
        }

        internal string Path { get; }
        internal string Digest { get; }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
