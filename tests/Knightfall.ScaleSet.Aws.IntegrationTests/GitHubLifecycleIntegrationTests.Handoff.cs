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
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class GitHubLifecycleIntegrationTests
{
    private static readonly GitHubJitTiming HandoffTiming = new(WriteReserve, SendWindow, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

    [Fact]
    public async Task DroppedGenerateCommitsOneRemoteRunnerAndExactFencedRecoveryNeverRemintsOrOffers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var rig = await SessionRig.CreateAsync(token);
        using var originalClient = await rig.ConnectAsync(token);
        var (executor, identity) = await BindHandoffExecutorAsync(rig, originalClient, token);
        using var recipient = JitRecipientKey.Create();
        var bootstrap = new DynamoDbBootstrapOfferClient(rig.Table.Client, rig.Table.Name, identity.AwsUserId, identity.ResourceArn, rig.Clock);
        Assert.Equal(LifecycleWriteOutcome.Committed, await bootstrap.RegisterHelloAsync(recipient.Hello, token));
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
        Assert.Equal(LifecycleWriteOutcome.Committed, await bootstrap.RegisterHelloAsync(recipient.Hello, token));
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
        SessionRig rig, GitHubScaleSetConnection connection, CancellationToken token)
    {
        var provisioner = Assert.IsType<FargateProvisionerConfiguration>(Assert.Single(rig.Configuration.Profiles).Provisioner);
        var cluster = Assert.IsType<ImportEcsClusterConfiguration>(rig.Configuration.EcsClusters[provisioner.Cluster]);
        const string taskId = "0123456789abcdef0123456789abcdef";
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
