using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Listener;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed class FargateNativeHandoffTests
{
    private static readonly GitHubListenerTiming ListenerTiming = new(
        FargateNativeFixture.Reserve, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(30));
    private static readonly GitHubJitTiming JitTiming = new(
        FargateNativeFixture.Reserve, TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
    private const long FirstJob = 511;
    private const long SecondJob = 512;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FargateDrainOrStartupTimeoutRevokesTheExactUnconsumedOfferAndStopsOnce(bool drain)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = await FargateNativeFixture.CreateAsync(token);
        using var secrets = aws.CreateSecretsClient();
        var owner = await aws.StartOwnerAsync(token);
        var simulator = new GitHubSimulator(FargateNativeFixture.Group);
        await using var protocol = await GitHubProtocolHost.StartAsync(simulator, token);
        using var http = protocol.CreateClient();
        var factory = new GitHubScaleSetClientFactory(http, timeProvider: aws.Clock);
        var credential = Assert.IsType<GitHubSecretVersion>(
            await new GitHubSecretLoader(secrets).LoadAsync(aws.Configuration, FargateNativeFixture.Connection, token));
        using var github = factory.Create(credential);
        var registration = await new GitHubScaleSetRegistration(aws.Store, aws.Clock, FargateNativeFixture.Reserve,
            TimeSpan.FromSeconds(12)).ResolveAsync(aws.Configuration, FargateNativeFixture.ProfileId, github,
            RegistrationLease(aws, owner), null, token);
        var binding = Assert.IsType<GitHubScaleSetBinding>(registration.Binding);
        await aws.BindScaleSetAsync(owner, binding.RunnerGroupId, binding.ScaleSetId, token);
        using var ownership = new ControllerLeaseOwnership(aws.Store, owner, aws.Clock.Proof(), aws.Clock,
            TimeSpan.FromHours(2), FargateNativeFixture.Reserve);
        var task = await LaunchAsync(aws, owner, aws.Provider(), binding.ScaleSetId, token);
        using var recipient = JitRecipientKey.Create();
        Assert.Equal(LifecycleWriteOutcome.Committed, await BrokerHelloFixture.RegisterAsync(aws.Table.Client,
            aws.Table.Name, task.Identity.AwsUserId, recipient.Hello, aws.Clock, token));
        var coordinator = new ControllerJitCoordinator(aws.Store, new AwsBootstrapPrincipalResolver(aws.Iam),
            new GitHubJitHandoff(aws.Store, aws.Clock, JitTiming), NullLogger<ControllerJitCoordinator>.Instance);
        _ = await OfferAsync(aws, ownership, coordinator, task, github, token);

        var runtime = drain ? aws.ConfigurationFor(aws.DefinitionArn, drainingWarmReserve: true) : aws.Configuration;
        var profile = Assert.Single(runtime.Profiles);
        if (drain)
        {
            var current = Assert.IsType<ControllerLease>(await aws.Store.ReadLeaseAsync(aws.Profile.Deployment, token));
            Assert.Equal(LifecycleWriteOutcome.Committed, (await aws.Store.ReleaseLeaseAsync(current, aws.Clock.Now, token)).Outcome);
            owner = await aws.StartOwnerAsync(token, FargateNativeFixture.SecondController, runtime, generation: 2);
            Assert.Equal(1, profile.Capacity.IdleReserve);
            Assert.Equal(1, profile.Capacity.MaxRunners);
        }
        else
        {
            var started = Assert.IsType<DateTimeOffset>(task.Executor.State.StartedAt);
            aws.Clock.Now = started.AddSeconds(profile.Capacity.StartupTimeoutSeconds);
        }
        var catalog = Assert.IsType<CatalogProfile>(await aws.Store.ReadCatalogProfileAsync(aws.Profile, token));
        var approved = new Dictionary<string, FargateContainerApproval>(StringComparer.Ordinal) { [profile.Id] = FargateNativeFixture.Approval };
        var evidence = new FargateRuntimeEvidence(approved.ToFrozenDictionary(StringComparer.Ordinal), null);
        var quota = new FargateQuotaReader(runtime.Aws, evidence, null, aws.Clock);
        var backend = new FargateRunnerBackend(runtime, profile, aws.Profile, aws.Inspector, evidence, quota,
            [aws.Profile], new ConcurrentDictionary<string, FargateInventorySnapshot>(StringComparer.Ordinal), aws.Store,
            aws.Journal, aws.Dispatcher, aws.Clock, FargateNativeFixture.Timing.ObservationFreshness);
        var inventory = await backend.ReadAsync(owner, token);
        Assert.True(inventory.IsComplete);
        var resource = Assert.Single(inventory.Resources);
        Assert.Equal(task.Executor.Binding, resource.Binding);
        Assert.Equal(LifecycleWriteOutcome.Committed, await aws.Store.ObserveResourceAsync(owner.Fence, task.Executor.Binding,
            Assert.IsType<ExecutorResourceObservation>(resource.Observation), token));
        var durable = await aws.Store.ReadExecutorInventoryAsync(aws.Profile, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await aws.Store.PublishExecutorInventoryAsync(aws.Profile, owner.Fence,
            durable.Revision, new ExecutorInventoryObservation(inventory.ObservedAt, IsComplete: true, BackendCapacityCeiling: 1), token));
        var authority = new ActiveControllerAuthority(owner, aws.Clock.Proof());
        await backend.CleanupAsync(authority, catalog, await ReadExecutorAsync(aws, task, token), CancellationToken.None, token);
        var stopped = await ReadExecutorAsync(aws, task, token);
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, stopped.State.Resource?.State);
        Assert.Equal(FargateAdmissionClosure.OfferRevoked, stopped.State.AdmissionClosure);
        Assert.Equal(JitHandoffState.Revoked, (await aws.Store.ReadJitHandoffAsync(aws.Profile, task.Identity.AllocationId, token))?.State);
        await backend.CleanupAsync(authority, catalog, stopped, CancellationToken.None, token);
        Assert.Single(aws.Wire.Requests, request => request.Operation == "StopTask");
        Assert.Equal("STOPPED", (await aws.ReadNativeTaskAsync(task.Identity.ResourceArn, token)).LastStatus);
    }

    [Fact(Timeout = 75_000)]
    public async Task TwoTasksConsumeSeparateOffersAndKeepWorkingAcrossControllerReplacement()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = await FargateNativeFixture.CreateAsync(token);
        using var secrets = aws.CreateSecretsClient();
        var owner = await aws.StartOwnerAsync(token);
        var simulator = new GitHubSimulator(FargateNativeFixture.Group) { TotalAssignedJobs = 2 };
        await using var protocol = await GitHubProtocolHost.StartAsync(simulator, token);
        using var http = protocol.CreateClient();
        var factory = new GitHubScaleSetClientFactory(http, timeProvider: aws.Clock);
        var credential = Assert.IsType<GitHubSecretVersion>(
            await new GitHubSecretLoader(secrets).LoadAsync(aws.Configuration, FargateNativeFixture.Connection, token));
        using var jitConnection = factory.Create(credential);
        var registration = await new GitHubScaleSetRegistration(aws.Store, aws.Clock, FargateNativeFixture.Reserve,
            TimeSpan.FromSeconds(12)).ResolveAsync(aws.Configuration, FargateNativeFixture.ProfileId, jitConnection,
            RegistrationLease(aws, owner), null, token);
        Assert.Equal(GitHubRegistrationStatus.Bound, registration.Status);
        var github = Assert.IsType<GitHubScaleSetBinding>(registration.Binding);
        await aws.BindScaleSetAsync(owner, github.RunnerGroupId, github.ScaleSetId, token);
        using var ownership = new ControllerLeaseOwnership(aws.Store, owner, aws.Clock.Proof(), aws.Clock,
            TimeSpan.FromHours(2), FargateNativeFixture.Reserve);
        using var lane = StartListener(aws, owner, github, factory.Create(credential));
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var originalSession = Assert.IsType<Guid>(simulator.SessionId);

        var provider = aws.Provider();
        var first = await LaunchAsync(aws, owner, provider, github.ScaleSetId, token);
        var second = await LaunchAsync(aws, owner, provider, github.ScaleSetId, token);
        Assert.NotEqual(first.Identity.ResourceArn, second.Identity.ResourceArn);
        Assert.NotEqual(first.Identity.AwsUserId, second.Identity.AwsUserId);
        Assert.All(new[] { first, second }, task => Assert.StartsWith(aws.TaskRole.RoleId + ":", task.Identity.AwsUserId));
        Assert.Equal(2, aws.Wire.NativeLaunches);

        await using var metadata = await FargateMetadataFixture.StartAsync([first.Executor.Binding, second.Executor.Binding], aws.Family, token);
        using var metadataHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var settings = new FargateBootstrapSettings(FargateNativeFixture.Account, FargateNativeFixture.Region,
            aws.Profile.Deployment, aws.Profile.ProfileId, FargateNativeFixture.Scope, aws.Table.Name, aws.ClusterArn, aws.Family,
            FargateNativeFixture.Runner, aws.TaskRole.Arn, aws.TaskRole.RoleId, "/opt/scaleset/runner", "/opt/scaleset/dotnet",
            "/opt/scaleset/runner-child", "/work", github.ScaleSetId);
        var firstCredentials = await aws.AssumeTaskAsync(first.Identity.ResourceArn, token);
        var secondCredentials = await aws.AssumeTaskAsync(second.Identity.ResourceArn, token);
        using var firstSts = aws.TaskSts(firstCredentials);
        using var secondSts = aws.TaskSts(secondCredentials);
        using var firstDdb = aws.TaskDynamoDb(firstCredentials);
        using var secondDdb = aws.TaskDynamoDb(secondCredentials);
        string firstMetadata = metadata.ForTask(first.Identity.ResourceArn).AbsoluteUri;
        string secondMetadata = metadata.ForTask(second.Identity.ResourceArn).AbsoluteUri;
        await Assert.ThrowsAsync<InvalidDataException>(() => new FargateBootstrapApplication(settings, aws.Clock)
            .RunAsync(firstMetadata, metadataHttp, secondSts, secondDdb, first.Job,
                static (_, _) => throw new InvalidOperationException("Bootstrap registered with unverified identity."), token));
        Assert.DoesNotContain(aws.Wire.Requests, request => request.Operation is "PutItem" or "DeleteItem");

        var firstApp = new FargateBootstrapApplication(settings, aws.Clock);
        var secondApp = new FargateBootstrapApplication(settings, aws.Clock);
        using var bootstrapCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstExit = firstApp.RunAsync(firstMetadata, metadataHttp, firstSts, firstDdb, first.Job,
            (hello, cancellation) => BrokerHelloFixture.RegisterAsync(aws.Table.Client, aws.Table.Name,
                first.Identity.AwsUserId, hello, aws.Clock, cancellation), bootstrapCancellation.Token);
        var secondExit = secondApp.RunAsync(secondMetadata, metadataHttp, secondSts, secondDdb, second.Job,
            (hello, cancellation) => BrokerHelloFixture.RegisterAsync(aws.Table.Client, aws.Table.Name,
                second.Identity.AwsUserId, hello, aws.Clock, cancellation), bootstrapCancellation.Token);
        try
        {
            await Task.WhenAll(WaitForHelloAsync(aws, first, firstExit, token), WaitForHelloAsync(aws, second, secondExit, token));
            var logs = new HandoffLog();
            var coordinator = new ControllerJitCoordinator(aws.Store, new AwsBootstrapPrincipalResolver(aws.Iam),
                new GitHubJitHandoff(aws.Store, aws.Clock, JitTiming), logs);
            long firstRunner = await OfferAsync(aws, ownership, coordinator, first, jitConnection, token);
            long secondRunner = await OfferAsync(aws, ownership, coordinator, second, jitConnection, token);
            Assert.NotEqual(firstRunner, secondRunner);
            var processes = await Task.WhenAll(first.Job.Running, second.Job.Running)
                .WaitAsync(TimeSpan.FromSeconds(8), token);
            Assert.Empty(await aws.Table.ReadAsync(LifecycleKey.Offer(first.Identity.AwsUserId), token));
            Assert.Empty(await aws.Table.ReadAsync(LifecycleKey.Offer(second.Identity.AwsUserId), token));

            await StepJobAsync(aws, simulator.StartJob(FirstJob, firstRunner), lane, first, FirstJob, firstRunner, token);
            await StepJobAsync(aws, simulator.StartJob(SecondJob, secondRunner), lane, second, SecondJob, secondRunner, token);
            Assert.Equal(GitHubListenerLaneStatus.Stopped, (await lane.StopAsync(token)).Status);
            Assert.Null(simulator.SessionId);
            Assert.Equal(LifecycleWriteOutcome.Committed, (await aws.Store.ReleaseLeaseAsync(owner, aws.Clock.Now, token)).Outcome);

            var successor = await aws.StartOwnerAsync(token, FargateNativeFixture.SecondController);
            Assert.Equal(owner.Epoch + 1, successor.Epoch);
            Assert.Equal(FargateNativeFixture.SecondController, successor.OwnerTaskArn);
            using var newLane = StartListener(aws, successor, github, factory.Create(credential));
            Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await newLane.StepAsync(token)).Step?.Status);
            Assert.NotEqual(originalSession, Assert.IsType<Guid>(simulator.SessionId));
            Assert.All(processes, process => Assert.False(process.HasExited));
            foreach (var task in new[] { first, second })
            {
                var live = await aws.ReadNativeTaskAsync(task.Identity.ResourceArn, token);
                Assert.Equal(task.Identity.ResourceArn, live.TaskArn);
                Assert.Equal("RUNNING", live.LastStatus);
                Assert.Equal(ExecutorJobPhase.Started, (await ReadExecutorAsync(aws, task, token)).State.Job?.Phase);
            }

            await using var events = await MiniStackRunnerEventRoute.CreateAsync(token);
            using var consumer = new FargateTaskEventConsumer(events.Sqs, aws.Inspector, aws.Configuration, events.QueueUrl, aws.Clock);
            await CompleteAsync(aws, successor, simulator, newLane, first, firstExit, FirstJob, firstRunner, events, consumer, token);
            await CompleteAsync(aws, successor, simulator, newLane, second, secondExit, SecondJob, secondRunner, events, consumer, token);
            Assert.Equal(GitHubListenerLaneStatus.Stopped, (await newLane.StopAsync(token)).Status);
            await AssertHandoffBoundaryAsync(aws, simulator, logs, [first, second], token);
        }
        finally
        {
            await bootstrapCancellation.CancelAsync();
            try
            {
                await Task.WhenAll(firstExit, secondExit).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (OperationCanceledException) when (bootstrapCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static GitHubRegistrationLease RegistrationLease(FargateNativeFixture aws, ControllerLease lease) =>
        new(lease, 1, aws.Clock.Proof());

    private static GitHubListenerLane StartListener(FargateNativeFixture aws, ControllerLease owner,
        GitHubScaleSetBinding github, GitHubScaleSetConnection connection) =>
        new GitHubListenerSupervisor(aws.Store, aws.Clock, ListenerTiming).Register(new GitHubListenerProfile(
            aws.Configuration, FargateNativeFixture.ProfileId, github, connection, owner.Fence,
            _ => Task.FromResult<GitHubRegistrationLease?>(RegistrationLease(aws, owner)),
            _ => Task.FromResult(new ListenerCapacity(2, 0)), (_, _) => Task.FromResult(true), CancellationToken.None));

    private static async Task<TaskAllocation> LaunchAsync(
        FargateNativeFixture aws, ControllerLease owner, IFargateReplayEffectProvider provider, long scaleSetId, CancellationToken token)
    {
        var launch = await aws.PlanLaunchAsync(owner, token);
        Assert.Equal(EffectDispatchStatus.Settled, await aws.DispatchAsync(owner, launch, provider, token));
        var executor = Assert.IsType<ExecutorSnapshot>(await aws.Store.ReadExecutorAsync(aws.Profile, launch.AllocationId.ToString("D"), token));
        Assert.Equal(ExecutorResourceState.Running, executor.State.Resource?.State);
        string principal = aws.TaskRole.RoleId + ":" + FargateMetadataFixture.ShortId(executor.Binding.ResourceArn);
        var identity = new JitHandoffIdentity(aws.Profile.Deployment, aws.Profile.ProfileId,
            GitHubRoutingIdentity.Scope(FargateNativeFixture.Scope), scaleSetId, launch.AllocationId, executor.Binding.ResourceArn,
            executor.Binding.Incarnation, principal);
        return new TaskAllocation(executor, identity, new SimulatedFargateJob("ss-" + JitOfferBinding.GetOfferId(identity)[..48]));
    }

    private static async Task WaitForHelloAsync(FargateNativeFixture aws, TaskAllocation task, Task<RunnerBootstrapOutcome> exit,
        CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(8));
        while ((await aws.Table.ReadAsync(LifecycleKey.Hello(task.Identity.AwsUserId), wait.Token)).Count == 0)
        {
            if (exit.IsCompleted)
            {
                throw new InvalidOperationException($"Bootstrap exited before HELLO ({await exit}).");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), wait.Token);
        }
    }

    private static async Task<long> OfferAsync(FargateNativeFixture aws, ControllerLeaseOwnership ownership,
        ControllerJitCoordinator coordinator, TaskAllocation task, GitHubScaleSetConnection connection, CancellationToken token)
    {
        var offer = await coordinator.AdvanceAsync(aws.Configuration, ownership,
            new BootstrapProviderObservation(task.Executor.Binding, aws.TaskRole.Arn, task.Executor.State.Resource!.ObservedAt), connection, token);
        Assert.Equal(GitHubJitStatus.Offered, offer.Status);
        long runner = Assert.IsType<long>(offer.RunnerId);
        var durable = Assert.IsType<JitHandoffSnapshot>(await aws.Store.ReadJitHandoffAsync(aws.Profile, task.Identity.AllocationId, token));
        Assert.Equal(task.Identity, durable.Identity);
        task.Job.BindRunner(runner);
        return runner;
    }

    private static async Task StepJobAsync(FargateNativeFixture aws, long messageId, GitHubListenerLane lane, TaskAllocation task,
        long job, long runner, CancellationToken token)
    {
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        var record = Assert.IsType<ExecutorJobObservation>((await ReadExecutorAsync(aws, task, token)).State.Job);
        Assert.Equal((job, runner, messageId, ExecutorJobPhase.Started), (record.RequestId, record.RunnerId, record.MessageId, record.Phase));
    }

    private static async Task CompleteAsync(FargateNativeFixture aws, ControllerLease successor, GitHubSimulator simulator,
        GitHubListenerLane listener, TaskAllocation task, Task<RunnerBootstrapOutcome> exit, long requestId, long runnerId,
        MiniStackRunnerEventRoute events, FargateTaskEventConsumer consumer, CancellationToken token)
    {
        simulator.Complete(requestId, runnerId);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await listener.StepAsync(token)).Step?.Status);
        Assert.Equal(ExecutorJobPhase.Completed, (await ReadExecutorAsync(aws, task, token)).State.Job?.Phase);
        task.Job.AllowExit();
        Assert.Equal(RunnerBootstrapOutcome.Completed, await exit.WaitAsync(token));

        await aws.RecordProcessExitAsync(task.Identity.ResourceArn, token);
        var native = await aws.ReadNativeTaskAsync(task.Identity.ResourceArn, token);
        Assert.Equal("STOPPED", native.LastStatus);
        long version = Assert.IsType<long>(native.Version);
        aws.Clock.Now += TimeSpan.FromSeconds(1);
        await events.PublishAsync(new JsonObject
        {
            ["version"] = "0",
            ["source"] = "aws.ecs",
            ["detail-type"] = "ECS Task State Change",
            ["account"] = FargateNativeFixture.Account,
            ["region"] = FargateNativeFixture.Region,
            ["resources"] = new JsonArray(task.Identity.ResourceArn),
            ["detail"] = new JsonObject
            {
                ["clusterArn"] = aws.ClusterArn,
                ["taskArn"] = task.Identity.ResourceArn,
                ["taskDefinitionArn"] = aws.DefinitionArn,
                ["version"] = version,
                ["lastStatus"] = "STOPPED",
            },
        }.ToJsonString(), token);
        int applied = 0;
        await consumer.PollAsync(async (binding, observed, applyToken) =>
        {
            Assert.Equal(task.Executor.Binding, binding);
            Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, observed.State);
            Assert.Equal(version, observed.ProviderVersion);
            var stillHeld = await events.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = events.QueueUrl, AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
            }, applyToken);
            Assert.Equal("1", stillHeld.Attributes["ApproximateNumberOfMessagesNotVisible"]);
            Assert.Equal(LifecycleWriteOutcome.Committed, await aws.Store.ObserveResourceAsync(successor.Fence, binding, observed, applyToken));
            applied++;
            return true;
        }, token);
        Assert.Equal(1, applied);
        var state = (await ReadExecutorAsync(aws, task, token)).State;
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, state.Resource?.State);
        Assert.Equal(version, state.Resource?.ProviderVersion);
        Assert.Null(state.Process);
        var queue = await events.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = events.QueueUrl, AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
        }, token);
        Assert.Equal("0", queue.Attributes["ApproximateNumberOfMessages"]);
        Assert.Equal("0", queue.Attributes["ApproximateNumberOfMessagesNotVisible"]);
    }

    private static async Task AssertHandoffBoundaryAsync(FargateNativeFixture aws, GitHubSimulator simulator, HandoffLog logs,
        IReadOnlyCollection<TaskAllocation> tasks, CancellationToken token)
    {
        Assert.Equal(2, aws.Wire.NativeLaunches);
        Assert.Equal(2, simulator.Requests.Count(request => request.Method == "POST" &&
            request.Path.EndsWith("/generatejitconfig", StringComparison.Ordinal)));
        Assert.DoesNotContain(aws.Wire.Requests, request => request.Operation == "StopTask");
        foreach (var task in tasks)
        {
            var helloKey = LifecycleKey.Hello(task.Identity.AwsUserId);
            var offerKey = LifecycleKey.Offer(task.Identity.AwsUserId);
            Assert.DoesNotContain(aws.Wire.Requests, request => request.Operation == "PutItem" &&
                HasBootstrapKey(request.Body, "Item", helloKey));
            Assert.Equal("iam-url-v1", (await aws.Table.ReadAsync(helloKey, token))["helloIssuer"].S);
            Assert.Contains(aws.Wire.Requests, request => request.Operation == "DeleteItem" &&
                HasBootstrapKey(request.Body, "Key", offerKey, "ALL_OLD"));
            var encryptedOffer = Assert.Single(aws.Table.Wire.Requests.Where(request => request.Operation == "TransactWriteItems")
                .SelectMany(request => request.Request.GetProperty("TransactItems").EnumerateArray()),
                action => action.TryGetProperty("Put", out var put) && HasKey(put.GetProperty("Item"), offerKey))
                .GetProperty("Put").GetProperty("Item");
            Assert.Equal(task.Identity.ResourceArn, encryptedOffer.GetProperty("resourceArn").GetProperty("S").GetString());
            using var offerPayload = JsonDocument.Parse(encryptedOffer.GetProperty("payload").GetProperty("S").GetString()!);
            var payload = offerPayload.RootElement;
            Assert.Equal(JitOfferBinding.GetOfferId(task.Identity), payload.GetProperty("binding").GetProperty("offerId").GetString());
            Assert.NotEmpty(payload.GetProperty("ciphertext").GetBytesFromBase64());
            Assert.Equal(16, payload.GetProperty("tag").GetBytesFromBase64().Length);
            Assert.Empty(await aws.Table.ReadAsync(LifecycleKey.Status(task.Identity.AwsUserId), token));
        }
        Assert.DoesNotContain(aws.Wire.Requests, request => request.Body.Contains("STATUS#", StringComparison.Ordinal) ||
            request.Body.Contains(GitHubSimulator.Jit, StringComparison.Ordinal));
        Assert.DoesNotContain(aws.Table.Wire.Requests, request =>
            request.Operation != "GetItem" && request.Request.GetRawText().Contains("STATUS#", StringComparison.Ordinal));
        Assert.DoesNotContain(aws.Table.Wire.Requests, request => request.Request.GetRawText().Contains(GitHubSimulator.Jit, StringComparison.Ordinal));
        Assert.All(aws.Wire.Requests.Where(request => request.Operation == "RunTask"), request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            Assert.False(body.RootElement.TryGetProperty("overrides", out _));
        });
        Assert.Equal(2, logs.Messages.Count);
        Assert.All(logs.Messages, message => Assert.Equal("Telemetry operation advance finished with success", message));
    }

    private static bool HasBootstrapKey(string body, string location, LifecycleKey expected, string? returnValues = null)
    {
        using var document = JsonDocument.Parse(body);
        var request = document.RootElement;
        return HasKey(request.GetProperty(location), expected) && (returnValues is null ||
            (request.TryGetProperty("ReturnValues", out var value) && value.ValueEquals(returnValues)));
    }

    private static bool HasKey(JsonElement item, LifecycleKey expected) =>
        item.GetProperty("PK").GetProperty("S").ValueEquals(expected.PartitionKey) &&
        item.GetProperty("SK").GetProperty("S").ValueEquals(expected.SortKey);

    private static async Task<ExecutorSnapshot> ReadExecutorAsync(FargateNativeFixture aws, TaskAllocation task, CancellationToken token) =>
        Assert.IsType<ExecutorSnapshot>(await aws.Store.ReadExecutorAsync(aws.Profile, task.Executor.Binding.AllocationId, token));

    private sealed record TaskAllocation(ExecutorSnapshot Executor, JitHandoffIdentity Identity, SimulatedFargateJob Job);

    private sealed class HandoffLog : ILogger<ControllerJitCoordinator>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }
}
