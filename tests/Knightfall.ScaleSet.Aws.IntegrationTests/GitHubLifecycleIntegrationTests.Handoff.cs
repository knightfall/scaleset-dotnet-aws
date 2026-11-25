using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Handoff;
using ScaleSet.Listener;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class GitHubLifecycleIntegrationTests
{
    private static readonly GitHubJitTiming HandoffTiming = new(WriteReserve, SendWindow, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmEvidenceExpiryPreventsAcquireAndReleasesOnlyAConfirmedUnsentReservation(bool committed)
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        var (executor, runnerId) = await IssueConsumedWarmRunnerAsync(rig, token);
        rig.Simulator.MarkRunnerListening(runnerId);
        var freshness = TimeSpan.FromSeconds(5);
        await PublishWarmInventoryAsync(rig, token);
        using var lane = await OpenWarmLaneAsync(rig, freshness, token);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        const long expired = 810;
        rig.Simulator.Offer(expired);
        var acquisition = LifecycleKey.Acquisition(rig.Profile, expired);
        var pause = committed ? rig.Table.Wire.PauseAfterNextSuccessfulTransactionWrite(acquisition) : rig.Table.Wire.PauseNextRead(acquisition);
        var running = lane.StepAsync(token);
        try
        {
            await pause.Captured.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(committed ? 1 : 0, await rig.ReadHeldAsync(token));
            rig.Clock.Now += freshness + TimeSpan.FromSeconds(1);
        }
        finally
        {
            pause.Resume();
        }
        var result = await running;
        Assert.Equal(committed ? ScaleSetListenerStepStatus.MessageAcknowledged : ScaleSetListenerStepStatus.Deferred, result.Step?.Status);
        if (!committed)
        {
            Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        }
        Assert.Empty(rig.Simulator.AcquiredRequests);
        Assert.DoesNotContain(rig.Simulator.Requests, IsAcquisition);
        Assert.Equal(0, await rig.ReadHeldAsync(token));
        var abandoned = await rig.Store.ReadRecordAsync(acquisition, token);
        Assert.Equal(committed, abandoned is not null);
        if (abandoned is not null)
        {
            using var payload = JsonDocument.Parse(abandoned.Payload!);
            Assert.Equal(3, payload.RootElement.GetProperty("state").GetInt32());
            Assert.Equal(3, payload.RootElement.GetProperty("evidence").GetProperty("kind").GetInt32());
        }

        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.ObserveResourceAsync(rig.Lease.Fence, executor,
            new ExecutorResourceObservation(ExecutorResourceState.Running, rig.Clock.Now, null, Guid.NewGuid()), token));
        await PublishWarmInventoryAsync(rig, token);
        // Refresh the prior demand sample before the next poll advertises its maximum.
        rig.Simulator.Offer();
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        rig.Simulator.Offer(expired);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        Assert.Equal([expired], rig.Simulator.AcquiredRequests);
        Assert.Equal(1, await rig.ReadHeldAsync(token));
    }

    [Fact]
    public async Task QueuedIdleStatisticsCannotAdmitWorkAfterRunnerTurnoverToAnOfflineReplacement()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        var (former, formerId) = await IssueConsumedWarmRunnerAsync(rig, token);
        rig.Simulator.MarkRunnerListening(formerId);
        await PublishWarmInventoryAsync(rig, token);
        using var lane = await OpenWarmLaneAsync(rig, TimeSpan.FromMinutes(1), token);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);

        const long staleOffer = 812;
        rig.Simulator.Offer(staleOffer); // Captures one registered idle runner before turnover.
        rig.Clock.Now += TimeSpan.FromSeconds(1);
        rig.Simulator.ForgetRunnerRegistration(formerId);
        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.ObserveResourceAsync(rig.Lease.Fence, former,
            new ExecutorResourceObservation(ExecutorResourceState.IrreversiblyTerminated, rig.Clock.Now, null, Guid.NewGuid()), token));
        var (replacement, replacementId) = await IssueConsumedWarmRunnerAsync(rig, token, "fedcba9876543210fedcba9876543210");
        Assert.NotEqual(formerId, replacementId);
        await PublishWarmInventoryAsync(rig, token);
        var inventory = await rig.Store.ReadExecutorInventoryAsync(rig.Profile, token);
        Assert.Equal(replacement, Assert.Single(inventory.Executors).Binding);

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        Assert.Contains(rig.Simulator.Requests, request => request.Method == "GET" && request.Host == GitHubProtocolHost.PublicApi &&
            request.Path == FormattableString.Invariant($"/enterprises/engineering/actions/runners/{replacementId}"));
        Assert.DoesNotContain(rig.Simulator.Requests, IsAcquisition);
        Assert.Null(await rig.Store.ReadRecordAsync(LifecycleKey.Acquisition(rig.Profile, staleOffer), token));
        Assert.Equal(0, await rig.ReadHeldAsync(token));

        const long currentOffer = 813;
        rig.Simulator.Offer(currentOffer); // This queue sample still reports zero idle runners.
        rig.Simulator.MarkRunnerListening(replacementId);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        Assert.Single(rig.Simulator.Requests, IsAcquisition);
        Assert.Equal([currentOffer], rig.Simulator.AcquiredRequests);
        Assert.Equal(1, await rig.ReadHeldAsync(token));
    }

    private static async Task<(ExecutorBinding Executor, long RunnerId)> IssueConsumedWarmRunnerAsync(
        SessionRig rig, CancellationToken token, string taskId = "0123456789abcdef0123456789abcdef")
    {
        using var registration = await rig.ConnectAsync(token);
        var (executor, identity) = await BindHandoffExecutorAsync(rig, registration, token, taskId);
        using var recipient = JitRecipientKey.Create();
        var bootstrap = new DynamoDbBootstrapOfferClient(rig.Table.Client, rig.Table.Name, identity.AwsUserId, identity.ResourceArn, rig.Clock);
        Assert.Equal(LifecycleWriteOutcome.Committed, await BrokerHelloFixture.RegisterAsync(rig.Table.Client,
            rig.Table.Name, identity.AwsUserId, recipient.Hello, rig.Clock, token));
        var issued = await new GitHubJitHandoff(rig.Store, rig.Clock, HandoffTiming).IssueAsync(
            new ActiveControllerAuthority(rig.Lease, ClockEvidence(rig.Clock)), identity, rig.Clock.Now,
            registration, CancellationToken.None, token);
        Assert.Equal(GitHubJitStatus.Offered, issued.Status);
        Assert.Equal(BootstrapOfferStatus.Consumed, (await bootstrap.TryConsumeOfferAsync(recipient.Hello, token)).Status);
        return (executor, Assert.IsType<long>(issued.RunnerId));
    }

    private static async Task<GitHubListenerLane> OpenWarmLaneAsync(SessionRig rig, TimeSpan freshness, CancellationToken token)
    {
        var catalog = Assert.IsType<CatalogProfile>(await rig.Store.ReadCatalogProfileAsync(rig.Profile, token));
        var connection = await rig.ConnectAsync(token);
        var binding = new GitHubScaleSetBinding(rig.Configuration.GitHub[ConnectionId].ScopeUrl, catalog.Binding.RunnerGroup,
            Assert.IsType<long>(catalog.Binding.RunnerGroupId), catalog.Binding.ScaleSet,
            Assert.IsType<long>(catalog.Binding.ScaleSetId), catalog.Binding.Labels);
        var definition = Definition(rig.Configuration, rig.Lease, rig.Clock, binding, connection, capacity: 1) with
        {
            ReadCapacity = cancellation => rig.Store.ReadListenerCapacityAsync(rig.Profile, maximum: 1, freshness,
                logFlushGrace: TimeSpan.FromSeconds(30), cancellation),
        };
        return new GitHubListenerSupervisor(rig.Store, rig.Clock, ListenerTiming).Register(definition);
    }

    private static async Task PublishWarmInventoryAsync(SessionRig rig, CancellationToken token)
    {
        var inventory = await rig.Store.ReadExecutorInventoryAsync(rig.Profile, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.PublishExecutorInventoryAsync(rig.Profile, rig.Lease.Fence,
            inventory.Revision, new ExecutorInventoryObservation(rig.Clock.Now, IsComplete: true, BackendCapacityCeiling: 1), token));
    }

    [Fact]
    public async Task DroppedGenerateCommitsOneRemoteRunnerAndExactFencedRecoveryNeverRemintsOrOffers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        using var originalClient = await rig.ConnectAsync(token);
        var (executor, identity) = await BindHandoffExecutorAsync(rig, originalClient, token);
        using var recipient = JitRecipientKey.Create();
        var bootstrap = new DynamoDbBootstrapOfferClient(rig.Table.Client, rig.Table.Name, identity.AwsUserId, identity.ResourceArn, rig.Clock);
        Assert.Equal(LifecycleWriteOutcome.Committed, await BrokerHelloFixture.RegisterAsync(rig.Table.Client,
            rig.Table.Name, identity.AwsUserId, recipient.Hello, rig.Clock, token));
        string offerId = JitOfferBinding.GetOfferId(identity);
        string intent = "jit-" + offerId;
        rig.Simulator.FailNext(SimulatorOperation.GenerateJit, SimulatorFault.DropAfterCommit);
        var first = await new GitHubJitHandoff(rig.Store, rig.Clock, HandoffTiming).IssueAsync(
            new ActiveControllerAuthority(rig.Lease, ClockEvidence(rig.Clock)), identity, rig.Clock.Now,
            originalClient, CancellationToken.None, token);
        Assert.Equal(GitHubJitStatus.RecoveryRequired, first.Status);
        long remoteRunner = Assert.IsType<long>(rig.Simulator.RegisteredRunnerId);
        var pending = Assert.IsType<EffectJournalSnapshot>(await new DynamoDbEffectJournal(rig.Store, rig.Clock)
            .ReadAsync(rig.Profile, intent, token));
        Assert.Equal(EffectState.Uncertain, pending.Entry.State);
        Assert.Equal(rig.Lease.Epoch, pending.Entry.Attempt?.LeaseEpoch);
        Assert.Equal(JitHandoffState.Staged, (await rig.Store.ReadJitHandoffAsync(rig.Profile, identity.AllocationId, token))?.State);
        Assert.Equal(1, (await rig.Store.ReadProfileHandoffObservationAsync(rig.Profile, token)).OutstandingOffers);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, (await bootstrap.TryConsumeOfferAsync(recipient.Hello, token)).Status);
        Assert.Single(rig.Simulator.Requests, IsJitPost);

        var restartedStore = new DynamoDbLifecycleStore(rig.Table.Client, rig.Table.Name, rig.Clock);
        var successor = await AcquireHandoffSuccessorAsync(rig, restartedStore, token);
        var recovery = new ControllerLeaseRecoveryCoordinator(restartedStore, new StoppedController(), rig.Clock, WriteReserve);
        var proof = ClockEvidence(rig.Clock);
        Assert.Equal(LeaseActivationStatus.ProfileRecoveryPending,
            (await recovery.TryActivateAsync(successor, rig.Configuration, proof, [], token)).Status);
        using var nextClient = await rig.ConnectAsync(token);
        var restarted = new GitHubJitHandoff(restartedStore, rig.Clock, HandoffTiming);
        Assert.Equal(LifecycleWriteOutcome.Committed, await restarted.RecoverFencedAsync(successor, proof, pending,
            new EffectRecoveryAuthority(recovery, []), nextClient, token));
        var active = await recovery.TryActivateAsync(successor, rig.Configuration, proof, [], token);
        Assert.Equal(LeaseActivationStatus.Activated, active.Status);
        var recovered = await restarted.IssueAsync(new ActiveControllerAuthority(Assert.IsType<ControllerLease>(active.Lease), proof),
            identity, rig.Clock.Now, nextClient, CancellationToken.None, token);
        Assert.Equal(new GitHubJitResult(GitHubJitStatus.RegisteredWithoutOffer, remoteRunner), recovered);

        var retained = Assert.IsType<JitHandoffSnapshot>(await restartedStore.ReadJitHandoffAsync(rig.Profile, identity.AllocationId, token));
        Assert.Equal(JitHandoffState.RegisteredWithoutOffer, retained.State);
        Assert.Equal(pending.Entry.Attempt?.Id, retained.JitAttemptId);
        Assert.Equal(remoteRunner, retained.RunnerId);
        Assert.Null(retained.OfferSha256);
        Assert.Equal(remoteRunner, (await restartedStore.ReadExecutorAsync(rig.Profile, executor.AllocationId, token))?.State.RunnerId);
        var settled = Assert.IsType<EffectJournalSnapshot>(await new DynamoDbEffectJournal(restartedStore, rig.Clock)
            .ReadAsync(rig.Profile, intent, token));
        Assert.Equal(EffectResolutionKind.Applied, settled.Entry.Resolution?.Kind);
        Assert.Equal(pending.Entry.Attempt, settled.Entry.Attempt);
        var reconstructedBootstrap = new DynamoDbBootstrapOfferClient(rig.Table.Client, rig.Table.Name, identity.AwsUserId, identity.ResourceArn, rig.Clock);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, (await reconstructedBootstrap.TryConsumeOfferAsync(recipient.Hello, token)).Status);

        string exactRunnerPath = RunnerPath(remoteRunner);
        var jitRequests = rig.Simulator.Requests.Where(request => IsJitPost(request) ||
            request.Path == GitHubSimulator.Runners || request.Path == exactRunnerPath).ToArray();
        Assert.Collection(jitRequests, request => Assert.True(IsJitPost(request)),
            request => Assert.Equal(("GET", GitHubSimulator.Runners), (request.Method, request.Path)),
            request => Assert.Equal(("GET", exactRunnerPath), (request.Method, request.Path)));
        Assert.DoesNotContain(rig.Table.Wire.Requests, request =>
            request.Request.GetRawText().Contains(GitHubSimulator.Jit, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DrainingAdvertisesZeroAndRemoteAssignmentRefusesOneExactRemovalAfterExecutorDeath()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        using var originalClient = await rig.ConnectAsync(token);
        var (executor, identity) = await BindHandoffExecutorAsync(rig, originalClient, token);
        using var recipient = JitRecipientKey.Create();
        var bootstrap = new DynamoDbBootstrapOfferClient(rig.Table.Client, rig.Table.Name, identity.AwsUserId, identity.ResourceArn, rig.Clock);
        Assert.Equal(LifecycleWriteOutcome.Committed, await BrokerHelloFixture.RegisterAsync(rig.Table.Client,
            rig.Table.Name, identity.AwsUserId, recipient.Hello, rig.Clock, token));
        var handoff = new GitHubJitHandoff(rig.Store, rig.Clock, HandoffTiming);
        var issued = await handoff.IssueAsync(new ActiveControllerAuthority(rig.Lease, ClockEvidence(rig.Clock)),
            identity, rig.Clock.Now, originalClient, CancellationToken.None, token);
        Assert.Equal(GitHubJitStatus.Offered, issued.Status);
        long runnerId = Assert.IsType<long>(issued.RunnerId);
        var delivered = await bootstrap.TryConsumeOfferAsync(recipient.Hello, token);
        Assert.Equal(BootstrapOfferStatus.Consumed, delivered.Status);
        using (var config = recipient.Open(Assert.IsType<JitOfferEnvelope>(delivered.Offer), identity, rig.Clock))
        {
            Assert.True(config.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(GitHubSimulator.Jit)));
        }

        var draining = await DrainingConfigurationAsync(rig.Configuration, token);
        var owner = await AcquireHandoffSuccessorAsync(rig, rig.Store, token);
        var ownership = new ControllerLeaseRecoveryCoordinator(rig.Store, new StoppedController(), rig.Clock, WriteReserve);
        var activated = await ownership.TryActivateAsync(owner, draining, ClockEvidence(rig.Clock), [], token);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        var lease = Assert.IsType<ControllerLease>(activated.Lease);
        var catalog = await ApplyDrainingGenerationAsync(rig, draining, lease, token);
        var connection = await rig.ConnectAsync(token);
        var binding = new GitHubScaleSetBinding(draining.GitHub[ConnectionId].ScopeUrl, catalog.Binding.RunnerGroup,
            Assert.IsType<long>(catalog.Binding.RunnerGroupId), catalog.Binding.ScaleSet,
            Assert.IsType<long>(catalog.Binding.ScaleSetId), catalog.Binding.Labels);
        var definition = Definition(draining, lease, rig.Clock, binding, connection, capacity: 2) with
        {
            ReadOwnership = _ => Task.FromResult<GitHubRegistrationLease?>(new GitHubRegistrationLease(lease, 2, ClockEvidence(rig.Clock))),
        };
        using var lane = new GitHubListenerSupervisor(rig.Store, rig.Clock, ListenerTiming).Register(definition);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        rig.Simulator.Offer(871);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        Assert.Equal("0", Assert.Single(rig.Simulator.Requests, request =>
            request.Host == GitHubProtocolHost.QueueHost && request.Method == "GET").Capacity);
        Assert.DoesNotContain(rig.Simulator.Requests, IsAcquisition);
        Assert.Equal(0, await rig.ReadHeldAsync(token));

        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.ObserveResourceAsync(lease.Fence, executor,
            new ExecutorResourceObservation(ExecutorResourceState.Running, rig.Clock.Now, null, Guid.NewGuid()), token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.ObserveProcessAsync(lease.Fence, executor,
            new ExecutorProcessObservation(runnerId, ExecutorProcessState.Ready, rig.Clock.Now, Guid.NewGuid()), token));
        var authority = new ActiveControllerAuthority(lease, ClockEvidence(rig.Clock));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await handoff.RemoveTerminatedRunnerAsync(
            authority, rig.Profile, identity.AllocationId, connection, CancellationToken.None, token));
        Assert.DoesNotContain(rig.Simulator.Requests, IsRunnerRemoval);

        rig.Simulator.AssignBeforeRunnerReportsStart(runnerId);
        var connected = Assert.IsType<ExecutorSnapshot>(await rig.Store.ReadExecutorAsync(rig.Profile, executor.AllocationId, token));
        Assert.Equal(ExecutorResourceState.Running, connected.State.Resource?.State);
        Assert.Equal(ExecutorProcessState.Ready, connected.State.Process?.State);
        Assert.Null(connected.State.Job);
        Assert.Equal(LifecycleWriteOutcome.Conflict, await handoff.RemoveTerminatedRunnerAsync(
            authority, rig.Profile, identity.AllocationId, connection, CancellationToken.None, token));
        Assert.DoesNotContain(rig.Simulator.Requests, IsRunnerRemoval);

        rig.Clock.Now = rig.Clock.Now.AddSeconds(1);
        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.ObserveResourceAsync(lease.Fence, executor,
            new ExecutorResourceObservation(ExecutorResourceState.IrreversiblyTerminated, rig.Clock.Now, null, Guid.NewGuid()), token));
        var terminal = Assert.IsType<ExecutorSnapshot>(await rig.Store.ReadExecutorAsync(rig.Profile, executor.AllocationId, token));
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, terminal.State.Resource?.State);
        Assert.Null(terminal.State.Job);

        Assert.Equal(LifecycleWriteOutcome.Indeterminate, await handoff.RemoveTerminatedRunnerAsync(
            authority, rig.Profile, identity.AllocationId, connection, CancellationToken.None, token));
        Assert.Equal(RunnerPath(runnerId), Assert.Single(rig.Simulator.Requests, IsRunnerRemoval).Path);
        Assert.Equal(runnerId, rig.Simulator.RegisteredRunnerId);
        string removeIntent = "remove-" + JitOfferBinding.GetOfferId(identity);
        var journal = new DynamoDbEffectJournal(rig.Store, rig.Clock);
        var ambiguous = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(rig.Profile, removeIntent, token));
        Assert.Equal(EffectState.Uncertain, ambiguous.Entry.State);
        Assert.Null(ambiguous.Entry.Resolution);
        Assert.Equal(runnerId, Assert.IsType<GitHubRunnerRemoveEffectRequest>(ambiguous.Entry.Envelope.Request).RunnerId);

        rig.Clock.Now = Assert.IsType<EffectAttempt>(ambiguous.Entry.Attempt).Deadline.AddSeconds(2);
        Assert.Equal(LifecycleWriteOutcome.Indeterminate, await handoff.RemoveTerminatedRunnerAsync(
            new ActiveControllerAuthority(lease, ClockEvidence(rig.Clock)), rig.Profile, identity.AllocationId,
            connection, CancellationToken.None, token));
        Assert.Single(rig.Simulator.Requests, IsRunnerRemoval);
        Assert.Equal(runnerId, rig.Simulator.RegisteredRunnerId);
        Assert.Equal(ambiguous.TransitionId, (await journal.ReadAsync(rig.Profile, removeIntent, token))?.TransitionId);
    }

    private static async Task<(ExecutorBinding Executor, JitHandoffIdentity Identity)> BindHandoffExecutorAsync(
        SessionRig rig, GitHubScaleSetConnection connection, CancellationToken token, string taskId = "0123456789abcdef0123456789abcdef")
    {
        var provisioner = Assert.IsType<FargateProvisionerConfiguration>(Assert.Single(rig.Configuration.Profiles).Provisioner);
        var cluster = Assert.IsType<ImportEcsClusterConfiguration>(rig.Configuration.EcsClusters[provisioner.Cluster]);
        string taskArn = cluster.Arn.Replace(":cluster/", ":task/", StringComparison.Ordinal) + "/" + taskId;
        var allocation = Guid.NewGuid();
        var executor = new ExecutorBinding(rig.Profile, allocation.ToString("D"), taskArn, Guid.NewGuid(),
            new FargateExecutorIdentity(cluster.Arn, provisioner.TaskDefinitionArn));
        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.BindExecutorAsync(rig.Lease.Fence, executor, token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await rig.Store.ObserveResourceAsync(rig.Lease.Fence, executor,
            new ExecutorResourceObservation(ExecutorResourceState.Running, rig.Clock.Now, rig.Clock.Now, Guid.NewGuid()), token));
        var identity = new JitHandoffIdentity(rig.Profile.Deployment, rig.Profile.ProfileId, connection.ScopeIdentity, GitHubSimulator.SetId,
            allocation, taskArn, executor.Incarnation, "AROAFIXTUREBOOTSTRAP21:" + taskId);
        return (executor, identity);
    }

    private static async Task<ControllerLease> AcquireHandoffSuccessorAsync(
        SessionRig rig, DynamoDbLifecycleStore store, CancellationToken token)
    {
        rig.Clock.Now = rig.Lease.ValidUntil.AddSeconds(2);
        var acquired = await store.TryAcquireLeaseAsync(rig.Lease.Deployment, SuccessorTask, SuccessorTask, ControllerLease.CurrentWriterProtocol,
            rig.Clock.Now.AddSeconds(-1), rig.Clock.Now.AddMinutes(5), token);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquired.Outcome);
        var successor = Assert.IsType<ControllerLease>(acquired.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, successor.Mode);
        return successor;
    }

    private static async Task<RuntimeConfiguration> DrainingConfigurationAsync(RuntimeConfiguration original, CancellationToken token)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), token))!;
        var saved = original.GitHub[ConnectionId];
        var changed = document.ToJsonString().Replace("111122223333", original.Aws.Account, StringComparison.Ordinal);
        document = JsonNode.Parse(changed)!;
        var scope = document["spec"]!["github"]![ConnectionId]!;
        scope["scopeURL"] = saved.ScopeUrl;
        scope["auth"]!["type"] = "enterprise-pat";
        scope["auth"]!["secretArn"] = saved.SecretArn;
        document["spec"]!["profiles"]![0]!["state"] = "draining";
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(document));
    }

    private static async Task<CatalogProfile> ApplyDrainingGenerationAsync(
        SessionRig rig, RuntimeConfiguration configuration, ControllerLease lease, CancellationToken token)
    {
        for (int pass = 0; pass < 10; pass++)
        {
            var update = await rig.Store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, 2, token);
            if (update.Status == CatalogRolloutStatus.Applied)
            {
                var row = Assert.IsType<CatalogProfile>(await rig.Store.ReadCatalogProfileAsync(rig.Profile, token));
                Assert.Equal(CatalogProfileState.Draining, row.State);
                return row;
            }
            Assert.Equal(CatalogRolloutStatus.Staging, update.Status);
        }
        throw new InvalidOperationException("The fixture draining catalog did not publish.");
    }

    private static bool IsJitPost(SimulatorRequest request) => request.Method == "POST" &&
        request.Path == FormattableString.Invariant($"{GitHubSimulator.Sets}/{GitHubSimulator.SetId}/generatejitconfig");

    private static bool IsRunnerRemoval(SimulatorRequest request) => request.Method == "DELETE" &&
        request.Path.StartsWith(GitHubSimulator.Runners + "/", StringComparison.Ordinal);

    private static string RunnerPath(long id) => FormattableString.Invariant($"{GitHubSimulator.Runners}/{id}");
}
