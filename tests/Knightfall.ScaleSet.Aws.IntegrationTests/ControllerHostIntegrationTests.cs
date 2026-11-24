using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed class ControllerHostIntegrationTests(ITestOutputHelper output)
{
    private const string CreateScaleSetIntent = "github-scale-set-create";
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(60);
    private static readonly string[] AllowedAwsServices = ["dynamodb", "metadata", "ec2", "ecs-controller", "secretsmanager", "sts"];

    public static bool HasPosixSignals => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Theory(SkipUnless = nameof(HasPosixSignals), Skip = "The subprocess shutdown fixture sends a POSIX signal.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunRequiresExactPredecessorStopAndHandlesSigtermWithInFlightGitHubWork(bool holdEffect)
    {
        var token = TestContext.Current.CancellationToken;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        var setup = startup.Token;
        using var table = await MiniStackTable.CreateAsync(setup);
        var workspace = Directory.CreateTempSubdirectory("scaleset-controller-host-");
        try
        {
            var (configuration, path) = await WriteConfigurationAsync(table.Name, workspace.FullName, setup);
            string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
            var (predecessor, inherited) = await WriteExpiredPredecessorAsync(table, configuration, planLaunch: !holdEffect, setup);
            var store = new DynamoDbLifecycleStore(table.Client, table.Name, TimeProvider.System);
            var journal = new DynamoDbEffectJournal(store, TimeProvider.System);
            var profile = Assert.Single(configuration.Profiles);
            var profileKey = new LifecycleProfile(deployment, profile.Id);
            var simulator = new GitHubSimulator(profile.GitHub.RunnerGroup);
            if (inherited is not null)
            {
                simulator.SeedScaleSet(profile.GitHub.ScaleSet, profile.GitHub.Labels);
            }
            var operationStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operationAborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int heldRequests = 0;
            await using var github = await GitHubProtocolHost.StartAsync(simulator, setup,
                async context =>
                {
                    if (ShouldHold(context.Request, holdEffect))
                    {
                        Interlocked.Increment(ref heldRequests);
                        await HoldOperationAsync(context, operationStarted, operationAborted).ConfigureAwait(false);
                    }
                });
            await using var aws = await ControllerHostAwsFixture.StartAsync(setup);
            int healthPort = FreePort();
            using var process = StartController(github, aws.Endpoint, configuration.Sha256, path, healthPort);
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try
            {
                await ObserveWhileRunningAsync(aws.ObservedRunning, process, setup);
                var held = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(deployment, setup));
                Assert.Equal(ControllerLeaseMode.RecoveryOnly, held.Mode);
                Assert.Equal(predecessor.Epoch + 1, held.Epoch);
                Assert.Equal(ControllerHostAwsFixture.PreviousTask, held.RequiredFencingTaskArn);
                Assert.Equal(ControllerHostAwsFixture.Task, held.OwnerTaskArn);
                Assert.Empty(simulator.Requests);
                Assert.DoesNotContain(aws.Operations, request => request.Service == "secretsmanager");
                if (inherited is not null)
                {
                    var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profileKey, inherited.IntentId, setup));
                    Assert.Equal(inherited.Sha256, planned.Entry.Envelope.Sha256);
                    Assert.Equal(EffectState.Planned, planned.Entry.State);
                    Assert.Null(planned.Entry.Attempt);
                }

                using var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                {
                    BaseAddress = new Uri(FormattableString.Invariant($"http://127.0.0.1:{healthPort}/")),
                    Timeout = TimeSpan.FromSeconds(2),
                };
                Assert.Equal(HttpStatusCode.OK, await ProbeAsync(probe, "healthz", setup));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, await ProbeAsync(probe, "readyz", setup));

                aws.StopPreviousTask();
                await ObserveWhileRunningAsync(operationStarted.Task, process, setup);
                Assert.Equal("HTTP/2", await operationStarted.Task.WaitAsync(setup));
                Assert.False(operationAborted.Task.IsCompleted);
                Assert.Equal(ControllerLeaseMode.Active, (await store.ReadLeaseAsync(deployment, setup))?.Mode);
                Assert.Equal(HttpStatusCode.OK, await ProbeAsync(probe, "healthz", setup));

                Guid? sessionId = simulator.SessionId;
                EffectAttempt? attempt = null;
                if (holdEffect)
                {
                    Assert.Null(sessionId);
                    var effect = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profileKey, CreateScaleSetIntent, setup));
                    Assert.Equal(EffectState.Executing, effect.Entry.State);
                    attempt = Assert.IsType<EffectAttempt>(effect.Entry.Attempt);
                    Assert.Equal(held.Epoch, attempt.LeaseEpoch);
                    Assert.Equal(profile.GitHub.ScaleSet, Assert.IsType<GitHubScaleSetCreateEffectRequest>(effect.Entry.Envelope.Request).ScaleSetName);
                }
                else
                {
                    var priorPlan = Assert.IsType<EffectEnvelope>(inherited);
                    var settled = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profileKey, priorPlan.IntentId, setup));
                    Assert.Equal(priorPlan.Sha256, settled.Entry.Envelope.Sha256);
                    Assert.Equal(EffectState.Settled, settled.Entry.State);
                    Assert.Equal(EffectResolutionKind.CanceledBeforeSend, settled.Entry.Resolution?.Kind);
                    Assert.Null(settled.Entry.Attempt);
                    var pending = Assert.IsType<PendingEc2LaunchPage>(await store.ReadPendingEc2LaunchesAsync(profileKey, held.Fence, null, setup));
                    Assert.Empty(pending.Launches);
                    Assert.NotNull(sessionId);
                    var opened = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(profileKey, setup));
                    Assert.Equal(SessionPhase.Active, opened.Phase);
                    Assert.Equal(sessionId, opened.SessionId);
                    Assert.Equal(held.Fence, opened.Owner);
                }

                Assert.False(operationAborted.Task.IsCompleted);
                long signalSent = Stopwatch.GetTimestamp();
                await SendTerminateAsync(process.Id, token);
                await process.WaitForExitAsync(token).WaitAsync(ShutdownBudget, token);
                await operationAborted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.True(Stopwatch.GetElapsedTime(signalSent) < ShutdownBudget);
                Assert.Equal(0, process.ExitCode);

                Assert.Equal(1, heldRequests);
                Assert.Null(simulator.SessionId);
                var after = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(deployment, token));
                Assert.Equal(held.Fence, after.Fence);
                if (holdEffect)
                {
                    Assert.Equal(ControllerLeaseMode.Active, after.Mode);
                    Assert.DoesNotContain(simulator.Requests, request => request.Path == GitHubSimulator.Sessions);
                    var unresolved = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profileKey, CreateScaleSetIntent, token));
                    Assert.True(unresolved.Entry.State is EffectState.Executing or EffectState.Uncertain);
                    Assert.Null(unresolved.Entry.Resolution);
                    Assert.Equal(attempt?.Id, unresolved.Entry.Attempt?.Id);
                }
                else
                {
                    var expectedClose = GitHubSimulator.Sessions + $"/{sessionId:D}";
                    var close = Assert.Single(simulator.Requests, request => request is { Method: "DELETE", Host: GitHubProtocolHost.ServiceHost });
                    Assert.Equal(expectedClose, close.Path);
                    Assert.Equal(SessionPhase.Retired, (await store.ReadSessionAsync(profileKey, token))?.Phase);
                    Assert.Equal(ControllerLeaseMode.Released, after.Mode);
                }

                var requests = aws.Operations;
                Assert.Contains(requests, request => request.Service == "ec2");
                Assert.Contains(requests, request => request is ("ecs-controller", "DescribeTasks"));
                Assert.DoesNotContain(requests, request => request.Operation.Contains("RunInstances", StringComparison.Ordinal));
                Assert.All(requests, request => Assert.Contains(request.Service, AllowedAwsServices));
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                }
                output.WriteLine(await stdout);
                output.WriteLine(await stderr);
                output.WriteLine(string.Join(", ", aws.Operations.Select(request => $"{request.Service}/{request.Operation}")));
                output.WriteLine(string.Join(Environment.NewLine, aws.ForwardFailures));
                throw;
            }
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static bool ShouldHold(HttpRequest request, bool holdEffect) => holdEffect
        ? HttpMethods.IsPost(request.Method) && request.Path == GitHubSimulator.Sets
        : HttpMethods.IsGet(request.Method) && request.Path.StartsWithSegments("/messages");

    private static async Task HoldOperationAsync(HttpContext context, TaskCompletionSource<string> started, TaskCompletionSource aborted)
    {
        started.TrySetResult(context.Request.Protocol);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            aborted.TrySetResult();
        }
    }

    private static async Task ObserveWhileRunningAsync(Task evidence, Process process, CancellationToken cancellationToken)
    {
        var exit = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(evidence, exit).WaitAsync(cancellationToken).ConfigureAwait(false);
        Assert.Same(evidence, completed);
        await evidence.ConfigureAwait(false);
    }

    private static async Task<HttpStatusCode> ProbeAsync(HttpClient client, string route, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    private static Process StartController(GitHubProtocolHost github, Uri aws, string sha256, string path, int healthPort)
    {
        string testHost = Path.Combine(AppContext.BaseDirectory, typeof(ControllerHostIntegrationTests).Assembly.GetName().Name!);
        var command = new ProcessStartInfo(testHost)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] arguments =
        [
            ControllerIntegrationEntryPoint.ChildMode, github.Port.ToString(CultureInfo.InvariantCulture), github.Fingerprint,
            "run", "--config", path, "--sha256", sha256, "--generation", "1", "--health-addr", FormattableString.Invariant($"127.0.0.1:{healthPort}"),
        ];
        foreach (string argument in arguments)
        {
            command.ArgumentList.Add(argument);
        }
        command.Environment["AWS_ENDPOINT_URL"] = aws.AbsoluteUri;
        command.Environment["ECS_CONTAINER_METADATA_URI_V4"] = new Uri(aws, "v4/fixture").AbsoluteUri;
        foreach (string key in command.Environment.Keys.Where(key => key.StartsWith("OTEL_", StringComparison.Ordinal)).ToArray())
        {
            command.Environment.Remove(key);
        }
        return Process.Start(command) ?? throw new InvalidOperationException("Could not start the controller subprocess fixture.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<(RuntimeConfiguration Configuration, string Path)> WriteConfigurationAsync(string table, string workspace,
        CancellationToken cancellationToken)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "ec2-only-pinned.json");
        string template = await File.ReadAllTextAsync(fixture, cancellationToken).ConfigureAwait(false);
        // MiniStackTable creates the physical test table in this Region.
        var root = JsonNode.Parse(template.Replace("ap-southeast-2", ControllerHostAwsFixture.Region, StringComparison.Ordinal))!;
        var spec = root["spec"]!;
        spec["mode"] = "active";
        var github = spec["github"]!["engineering"]!;
        github["scopeURL"] = "https://github.com/enterprises/engineering";
        github["auth"]!["type"] = "enterprise-pat";
        github["auth"]!["secretArn"] = ControllerHostAwsFixture.Secret;
        var registration = spec["profiles"]![0]!["github"]!.AsObject();
        registration["registration"] = "create-if-missing";
        registration.Remove("scaleSetId");
        spec["tracking"]!["table"] = new JsonObject { ["type"] = "import", ["name"] = table };
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(root);
        string path = Path.Combine(workspace, "runtime.json");
        await File.WriteAllBytesAsync(path, document, cancellationToken).ConfigureAwait(false);
        return (RuntimeConfigurationLoader.Load(document), path);
    }

    private static async Task<(ControllerLease Lease, EffectEnvelope? InheritedPlan)> WriteExpiredPredecessorAsync(MiniStackTable table,
        RuntimeConfiguration configuration, bool planLaunch, CancellationToken cancellationToken)
    {
        var clock = new FormerClock(TimeProvider.System.GetUtcNow().AddMinutes(-2));
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var acquired = await store.TryAcquireLeaseAsync(deployment, "former-controller", ControllerHostAwsFixture.PreviousTask,
            ControllerLease.CurrentWriterProtocol, clock.Now, clock.Now.AddSeconds(45), cancellationToken).ConfigureAwait(false);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquired.Outcome);
        var recovery = new ControllerLeaseRecoveryCoordinator(store, new UnexpectedPredecessorObserver(), clock, TimeSpan.FromSeconds(5));
        var proof = new ControllerClockEvidence(clock.Now, clock.Now.AddSeconds(20), TimeSpan.FromMilliseconds(1));
        var active = await recovery.TryActivateAsync(Assert.IsType<ControllerLease>(acquired.Lease), configuration, proof, [], cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(LeaseActivationStatus.Activated, active.Status);
        var predecessor = Assert.IsType<ControllerLease>(active.Lease);
        var inherited = planLaunch
            ? await WriteInheritedLaunchAsync(configuration, store, predecessor, clock, cancellationToken).ConfigureAwait(false)
            : null;
        return (predecessor, inherited);
    }

    private static async Task<EffectEnvelope> WriteInheritedLaunchAsync(RuntimeConfiguration configuration, DynamoDbLifecycleStore store,
        ControllerLease predecessor, TimeProvider clock, CancellationToken cancellationToken)
    {
        var rollout = await store.AdvanceCatalogRolloutAsync(predecessor.Fence, configuration, 1, cancellationToken).ConfigureAwait(false);
        for (int pass = 0; pass < 3 && rollout.Status == CatalogRolloutStatus.Staging; pass++)
        {
            rollout = await store.AdvanceCatalogRolloutAsync(predecessor.Fence, configuration, 1, cancellationToken).ConfigureAwait(false);
        }
        Assert.Equal(CatalogRolloutStatus.Applied, rollout.Status);
        var profile = Assert.Single(configuration.Profiles);
        var key = new LifecycleProfile(predecessor.Deployment, profile.Id);
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, cancellationToken).ConfigureAwait(false));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(predecessor.Fence, catalog,
            GitHubSimulator.GroupId, GitHubSimulator.SetId, cancellationToken).ConfigureAwait(false));

        var ec2 = Assert.IsType<Ec2ProvisionerConfiguration>(profile.Provisioner);
        var numericVersion = Assert.IsType<PinnedLaunchTemplateVersion>(ec2.LaunchTemplateVersion);
        var network = configuration.Networks[ec2.Network];
        var subnet = network.Subnets[0];
        var allocation = Guid.NewGuid();
        var tags = new Ec2LaunchOwnershipTags(RunnerOwnershipTags.ControllerValue, key.Deployment, key.ProfileId,
            allocation.ToString("D"), ec2.BootstrapProtocol);
        var launch = new Ec2LaunchEffectRequest(allocation, configuration.Aws.Region, subnet.Id, subnet.AvailabilityZone,
            ec2.LaunchTemplateId, long.Parse(numericVersion.Version, CultureInfo.InvariantCulture), "c7g.xlarge", network.SecurityGroupIds,
            ec2.InstanceProfileArn, Guid.NewGuid(), Tenancy: Ec2Tenancy.Default, OwnershipTags: tags);
        var envelope = EffectEnvelope.Create(key, EffectIntentIdentity.ForAllocation(launch)!, 1, launch, clock.GetUtcNow(), null);
        var outcome = await new DynamoDbEffectJournal(store, clock).PlanAsync(predecessor, envelope, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(LifecycleWriteOutcome.Committed, outcome);
        return envelope;
    }

    private static async Task SendTerminateAsync(int processId, CancellationToken cancellationToken)
    {
        var command = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
        command.ArgumentList.Add("-TERM");
        command.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        using var sender = Process.Start(command) ?? throw new InvalidOperationException("Could not send SIGTERM.");
        await sender.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Assert.Equal(0, sender.ExitCode);
    }

    private sealed class FormerClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now => now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class UnexpectedPredecessorObserver : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The first owner has no predecessor.");
    }
}
