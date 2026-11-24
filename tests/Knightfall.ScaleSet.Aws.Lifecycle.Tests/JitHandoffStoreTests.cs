using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class JitHandoffStoreTests
{
    private const string ProfileId = "linux-shell";
    private const string FargateShortId = "00000000000000000000000000000009";
    private const string FargateUserId = "AROAFIXTUREHANDOFF001:" + FargateShortId;
    private const string ClusterArn = "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-shell";
    private const string FargateArn = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-shell/" + FargateShortId;
    private const string Ec2UserId = "AROAFIXTUREHANDOFF002:i-00000000000000001";
    private const string Ec2Arn = "arn:aws:ec2:ap-southeast-2:111122223333:instance/i-00000000000000001";
    private const long RunnerId = 6501;
    private static ReadOnlySpan<byte> SyntheticJit => "synthetic-jit-that-must-never-appear-in-durable-storage"u8;

    [Fact]
    public async Task DuplicateAndForeignHellosCannotReplaceTheKeyBoundToAnOffer()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        using var recipient = JitRecipientKey.Create();
        using var replacement = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Bootstrap.RegisterHelloAsync(recipient.Hello, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await fixture.Bootstrap.RegisterHelloAsync(replacement.Hello, cancellation));
        Assert.Throws<ArgumentException>(() => new DynamoDbBootstrapOfferClient(table.Client, table.Name,
            FargateUserId, Ec2Arn, fixture.Clock));

        var foreign = fixture.Identity with { AwsUserId = "AROAFIXTUREFOREIGN001:" + FargateShortId };
        var notStaged = await fixture.Store.StageJitHandoffAsync(fixture.Lease, fixture.Catalog, fixture.Executor,
            foreign, RunnerName(foreign), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, notStaged.Outcome);
        Assert.Null(notStaged.Handoff);
        var offer = await StageAsync(fixture, cancellation);
        Assert.True(offer.Hello.RecipientPublicKey.AsSpan().SequenceEqual(recipient.Hello.RecipientPublicKey.AsSpan()));
        Assert.Equal(1, (await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, cancellation)).OutstandingOffers);

        // Simulate a principal rewriting its own row without using our conditional HELLO API.
        var helloKey = LifecycleKey.Hello(fixture.Identity.AwsUserId);
        var row = await table.ReadAsync(helloKey, cancellation);
        row["payload"] = new AttributeValue { S = BootstrapItems.Serialize(replacement.Hello) };
        await table.Client.PutItemAsync(table.Name, row, cancellation);
        var planned = await PlanAsync(fixture, offer, cancellation);
        var blocked = await fixture.Journal.BeginJitAsync(fixture.Lease, fixture.Catalog, fixture.Executor, offer, planned,
            fixture.Clock.Now, fixture.Clock.Now.AddSeconds(40), TimeSpan.FromSeconds(1), cancellation);
        Assert.Null(blocked.Permission);
        Assert.Equal(EffectState.Planned, (await fixture.Journal.ReadAsync(fixture.Catalog.Profile, offer.IntentId, cancellation))?.Entry.State);
    }

    [Fact]
    public async Task KnownPublishAfterLostTransactionResponseLetsOnlyTheExactBootKeyConsume()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        using var recipient = JitRecipientKey.Create();
        using var replacement = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, cancellation);
        var (staged, attempt) = await StageAndBeginAsync(fixture, recipient, cancellation);
        table.Wire.Clear();
        table.Wire.DropNextSuccessfulTransactionResponse();

        var published = await PublishAsync(fixture, staged, attempt, cancellation);
        Assert.Equal(JitHandoffState.Offered, published.State);
        Assert.Equal(RunnerId, published.RunnerId);
        var runner = await fixture.Store.ReadExecutorAsync(fixture.Catalog.Profile, fixture.Executor.Binding.AllocationId, cancellation);
        Assert.Equal(RunnerId, runner?.State.RunnerId);
        foreach (var call in table.Wire.Requests)
        {
            Assert.DoesNotContain("synthetic-jit-that-must-never-appear-in-durable-storage", call.Request.GetRawText(), StringComparison.Ordinal);
        }

        table.Wire.Clear();
        var wrong = await fixture.Bootstrap.TryConsumeOfferAsync(replacement.Hello, cancellation);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, wrong.Status);
        var consumed = await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation);
        Assert.Equal(BootstrapOfferStatus.Consumed, consumed.Status);
        using var plaintext = recipient.Open(Assert.IsType<JitOfferEnvelope>(consumed.Offer), fixture.Identity, fixture.Clock);
        Assert.True(plaintext.Bytes.SequenceEqual(SyntheticJit));
        var retry = await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation);
        Assert.Equal(BootstrapOfferStatus.AlreadyConsumed, retry.Status);
        Assert.Equal(2, table.Wire.Requests.Count);
        Assert.All(table.Wire.Requests, call =>
        {
            Assert.Equal("DeleteItem", call.Operation);
            Assert.Equal("ALL_OLD", call.Request.GetProperty("ReturnValues").GetString());
        });
    }

    [Fact]
    public async Task ConsumeAndRevokeCompeteOnTheSameExactOffer()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        using var recipient = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, cancellation);
        var (staged, attempt) = await StageAndBeginAsync(fixture, recipient, cancellation);
        var published = await PublishAsync(fixture, staged, attempt, cancellation);
        var pause = table.Wire.PauseNextTransactionWrite(LifecycleKey.Handoff(fixture.Catalog.Profile, fixture.Identity.AllocationId));
        var revoking = fixture.Store.RevokeJitHandoffAsync(fixture.Lease, published, cancellation);
        try
        {
            await pause.Captured.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            var receipt = await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation);
            Assert.Equal(BootstrapOfferStatus.Consumed, receipt.Status);
        }
        finally
        {
            pause.Resume();
        }
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await revoking).Outcome);
        var durable = await fixture.Store.ReadJitHandoffAsync(fixture.Catalog.Profile, fixture.Identity.AllocationId, cancellation);
        Assert.Equal(JitHandoffState.Offered, durable?.State);
        Assert.Equal(1, (await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, cancellation)).OutstandingOffers);
    }

    [Fact]
    public async Task RevocationWinsAndRetainsOccupancyUntilIndependentCleanup()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        using var recipient = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, cancellation);
        var (staged, attempt) = await StageAndBeginAsync(fixture, recipient, cancellation);
        var published = await PublishAsync(fixture, staged, attempt, cancellation);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(31);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, (await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation)).Status);

        var revoked = await fixture.Store.RevokeJitHandoffAsync(fixture.Lease, published, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, revoked.Outcome);
        var witness = Assert.IsType<JitHandoffSnapshot>(revoked.Handoff);
        Assert.Equal(JitHandoffState.Revoked, witness.State);
        Assert.Equal(witness.TransitionId, (await fixture.Store.RevokeJitHandoffAsync(fixture.Lease, published, cancellation)).Handoff?.TransitionId);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, (await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation)).Status);
        Assert.Equal(1, (await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, cancellation)).OutstandingOffers);
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await fixture.Store.CompleteTerminatedJitHandoffAsync(fixture.Lease,
            witness, null, null, cancellation)).Outcome);

        var generation = await ResolveAppliedAsync(fixture, attempt, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(fixture.Lease.Fence,
            fixture.Executor.Binding, new ExecutorResourceObservation(ExecutorResourceState.IrreversiblyTerminated, fixture.Clock.Now,
                fixture.Executor.State.StartedAt, Guid.NewGuid()), cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            (await fixture.Store.CompleteTerminatedJitHandoffAsync(fixture.Lease, witness, generation, null, cancellation)).Outcome);
        var removal = await SettleRemovalAsync(fixture, witness, cancellation);
        var completed = await fixture.Store.CompleteTerminatedJitHandoffAsync(fixture.Lease, witness, generation, removal, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, completed.Outcome);
        Assert.Equal(JitHandoffState.ResourceTerminated, completed.Handoff?.State);
        var clear = await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, cancellation);
        Assert.Equal(0, clear.OutstandingOffers);
        Assert.Equal(new LifecycleRecordCondition(LifecycleKey.HandoffHead(fixture.Catalog.Profile), 2), Assert.Single(clear.ReadGuards));
        var replayed = await fixture.Store.CompleteTerminatedJitHandoffAsync(fixture.Lease, witness, generation, removal, cancellation);
        Assert.Equal(completed.Handoff?.TransitionId, replayed.Handoff?.TransitionId);
    }

    [Fact]
    public async Task LostConsumeReceiptDoesNotAuthorizeAnotherOffer()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        using var recipient = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, cancellation);
        var (staged, attempt) = await StageAndBeginAsync(fixture, recipient, cancellation);
        var published = await PublishAsync(fixture, staged, attempt, cancellation);
        table.Wire.Clear();
        table.Wire.DropNextSuccessfulBootstrapDeleteResponse();

        var receipt = await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation);
        Assert.Equal(BootstrapOfferStatus.Indeterminate, receipt.Status);
        Assert.Null(receipt.Offer);
        Assert.Equal(BootstrapOfferStatus.Indeterminate, (await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation)).Status);
        Assert.Equal("DeleteItem", Assert.Single(table.Wire.Requests).Operation);
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            (await fixture.Store.RevokeJitHandoffAsync(fixture.Lease, published, cancellation)).Outcome);
        var replay = await fixture.Store.PublishJitHandoffAsync(fixture.Lease, fixture.Catalog, staged, attempt, RunnerId,
            SealedOffer(fixture, staged), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, replay.Outcome);
        Assert.Equal(1, (await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, cancellation)).OutstandingOffers);
    }

    [Fact]
    public async Task RediscoveredRegistrationWithoutPlaintextRemainsUnissuedAndCannotBeMintedAgain()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        using var recipient = JitRecipientKey.Create();
        var fixture = await CreateFixtureAsync(table, cancellation);
        var (staged, attempt) = await StageAndBeginAsync(fixture, recipient, cancellation);

        var reconciled = await fixture.Store.RecordUnissuedJitAsync(fixture.Lease, staged, attempt, RunnerId, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, reconciled.Outcome);
        Assert.Equal(JitHandoffState.RegisteredWithoutOffer, reconciled.Handoff?.State);
        Assert.Equal(RunnerId, reconciled.Handoff?.RunnerId);
        Assert.Null(reconciled.Handoff?.OfferSha256);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, (await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, cancellation)).Status);
        Assert.Equal(LifecycleWriteOutcome.Conflict, (await fixture.Store.StageJitHandoffAsync(fixture.Lease, fixture.Catalog,
            fixture.Executor, fixture.Identity, staged.RunnerName, cancellation)).Outcome);
        Assert.Equal(1, (await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, cancellation)).OutstandingOffers);
    }

    [Fact]
    public async Task OnlyEc2CapabilityWritesStatusAndExitCannotBeOverwritten()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        using var recipient = JitRecipientKey.Create();
        var clock = new ContractClock { Now = MiniStackTable.Now };
        Assert.Throws<ArgumentException>(() => new DynamoDbEc2BootstrapStatusClient(table.Client, table.Name, FargateUserId, FargateArn, clock));
        var client = new DynamoDbEc2BootstrapStatusClient(table.Client, table.Name, Ec2UserId, Ec2Arn, clock);
        var ready = new JitEc2BootstrapStatus(new string('a', 64), RunnerId, 1, Exited: false, clock.Now);
        Assert.Equal(LifecycleWriteOutcome.Committed, await client.PublishAsync(recipient.Hello, ready, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await client.PublishAsync(recipient.Hello, ready, cancellation));
        var exit = ready with { Sequence = 2, Exited = true };
        Assert.Equal(LifecycleWriteOutcome.Committed, await client.PublishAsync(recipient.Hello, exit, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await client.PublishAsync(recipient.Hello, ready with { Sequence = 3 }, cancellation));
        var row = await table.ReadAsync(LifecycleKey.Status(Ec2UserId), cancellation);
        Assert.True(row["exited"].BOOL);
        Assert.Equal("2", row["sequence"].N);
    }

    internal static async Task<TestContextFixture> CreateFixtureAsync(
        MiniStackTable table, CancellationToken cancellationToken, RuntimeConfiguration? configuration = null)
    {
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        configuration ??= RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken));
        var deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var acquired = await store.TryAcquireLeaseAsync(deployment, "handoff-fixture", MiniStackTable.FirstTask,
            ControllerLease.CurrentWriterProtocol, clock.Now, clock.Now.AddHours(1), cancellationToken);
        var promoted = await store.PromoteLeaseAsync(Assert.IsType<ControllerLease>(acquired.Lease), clock.Now, cancellationToken);
        var lease = Assert.IsType<ControllerLease>(promoted.Lease);
        for (int pass = 0; pass < 4; pass++)
        {
            var rollout = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, 1, cancellationToken);
            if (rollout.Status == CatalogRolloutStatus.Applied)
            {
                break;
            }
            Assert.Equal(CatalogRolloutStatus.Staging, rollout.Status);
        }
        var profile = new LifecycleProfile(deployment, ProfileId);
        var unbound = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(lease.Fence, unbound, 42, 84, cancellationToken));
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellationToken));
        var index = await store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellationToken);
        Assert.Equal(EffectIndexState.Ready, index.Head?.State);
        var allocationId = Guid.NewGuid();
        var incarnation = Guid.NewGuid();
        var binding = new ExecutorBinding(profile, allocationId.ToString("D"), FargateArn, incarnation,
            new FargateExecutorIdentity(ClusterArn, catalog.Binding.ProviderDefinition));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindExecutorAsync(lease.Fence, binding, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(lease.Fence, binding,
            new ExecutorResourceObservation(ExecutorResourceState.Running, clock.Now, clock.Now.AddMinutes(-1), Guid.NewGuid()),
            cancellationToken));
        var executor = Assert.IsType<ExecutorSnapshot>(await store.ReadExecutorAsync(profile, binding.AllocationId, cancellationToken));
        var identity = new JitHandoffIdentity(deployment, ProfileId, catalog.Binding.ScopeIdentity, 84, allocationId, FargateArn,
            incarnation, FargateUserId);
        var bootstrap = new DynamoDbBootstrapOfferClient(table.Client, table.Name, FargateUserId, FargateArn, clock);
        return new TestContextFixture(store, new DynamoDbEffectJournal(store, clock), lease, catalog, executor, identity, bootstrap, clock);
    }

    private static async Task<JitHandoffSnapshot> StageAsync(TestContextFixture fixture, CancellationToken cancellationToken)
    {
        var staged = await fixture.Store.StageJitHandoffAsync(fixture.Lease, fixture.Catalog, fixture.Executor, fixture.Identity,
            RunnerName(fixture.Identity), cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, staged.Outcome);
        return Assert.IsType<JitHandoffSnapshot>(staged.Handoff);
    }

    private static string RunnerName(JitHandoffIdentity identity) => "ss-" + JitOfferBinding.GetOfferId(identity)[..48];

    private static async Task<EffectJournalSnapshot> PlanAsync(TestContextFixture fixture, JitHandoffSnapshot handoff,
        CancellationToken cancellationToken)
    {
        var identity = fixture.Identity;
        var request = new GitHubJitGenerateEffectRequest(identity.ScopeIdentity, identity.ScaleSetId, identity.AllocationId,
            identity.ResourceArn, identity.Incarnation, identity.AwsUserId, handoff.HelloSha256, handoff.OfferId, handoff.RunnerName);
        var envelope = EffectEnvelope.Create(fixture.Catalog.Profile, handoff.IntentId, fixture.Catalog.AppliedGeneration,
            request, fixture.Clock.Now, null);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Journal.PlanAsync(fixture.Lease, envelope, Guid.NewGuid(), cancellationToken));
        return Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(fixture.Catalog.Profile, handoff.IntentId, cancellationToken));
    }

    private static async Task<(JitHandoffSnapshot Handoff, EffectJournalSnapshot Attempt)> StageAndBeginAsync(
        TestContextFixture fixture, JitRecipientKey recipient, CancellationToken cancellationToken)
    {
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Bootstrap.RegisterHelloAsync(recipient.Hello, cancellationToken));
        var staged = await StageAsync(fixture, cancellationToken);
        var planned = await PlanAsync(fixture, staged, cancellationToken);
        var begun = await fixture.Journal.BeginJitAsync(fixture.Lease, fixture.Catalog, fixture.Executor, staged, planned,
            fixture.Clock.Now, fixture.Clock.Now.AddSeconds(40), TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, begun.Outcome);
        Assert.True(Assert.IsType<EffectSendAuthorization>(begun.Permission).TryConsume(out _));
        var attempt = Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(fixture.Catalog.Profile, staged.IntentId, cancellationToken));
        return (staged, attempt);
    }

    private static async Task<JitHandoffSnapshot> PublishAsync(TestContextFixture fixture, JitHandoffSnapshot staged,
        EffectJournalSnapshot attempt, CancellationToken cancellationToken)
    {
        var published = await fixture.Store.PublishJitHandoffAsync(fixture.Lease, fixture.Catalog, staged, attempt, RunnerId,
            SealedOffer(fixture, staged), cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, published.Outcome);
        return Assert.IsType<JitHandoffSnapshot>(published.Handoff);
    }

    private static JitOfferEnvelope SealedOffer(TestContextFixture fixture, JitHandoffSnapshot staged) =>
        JitOfferCryptography.Seal(JitOfferBinding.Create(fixture.Identity, staged.Hello.BootNonce, fixture.Clock.Now.AddSeconds(30)),
            staged.Hello, SyntheticJit, fixture.Clock);

    private static async Task<EffectJournalSnapshot> SettleRemovalAsync(
        TestContextFixture fixture, JitHandoffSnapshot handoff, CancellationToken cancellationToken)
    {
        var profile = fixture.Catalog.Profile;
        var identity = fixture.Identity;
        var request = new GitHubRunnerRemoveEffectRequest(identity.ScopeIdentity, identity.ScaleSetId,
            identity.AllocationId, identity.ResourceArn, identity.Incarnation, handoff.RunnerName, RunnerId);
        var envelope = EffectEnvelope.Create(profile, "remove-" + handoff.OfferId, fixture.Catalog.AppliedGeneration,
            request, fixture.Clock.Now, null);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Journal.PlanAsync(fixture.Lease, envelope, Guid.NewGuid(), cancellationToken));
        var intent = Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(profile, envelope.IntentId, cancellationToken));
        var executor = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(profile, fixture.Executor.Binding.AllocationId,
            cancellationToken));
        var begin = await fixture.Journal.BeginRunnerRemovalAsync(fixture.Lease, fixture.Catalog, executor, handoff, intent,
            fixture.Clock.Now, fixture.Clock.Now.AddSeconds(40), TimeSpan.FromSeconds(1), cancellationToken);
        Assert.True(Assert.IsType<EffectSendAuthorization>(begin.Permission).TryConsume(out _));
        var attempt = Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(profile, envelope.IntentId, cancellationToken));
        return await ResolveAppliedAsync(fixture, attempt, cancellationToken);
    }

    private static async Task<EffectJournalSnapshot> ResolveAppliedAsync(
        TestContextFixture fixture, EffectJournalSnapshot attempt, CancellationToken cancellationToken)
    {
        var envelope = attempt.Entry.Envelope;
        var evidence = new EffectEvidence(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, fixture.Clock.Now);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await fixture.Journal.ResolveAsync(fixture.Lease, attempt, Guid.NewGuid(), evidence, cancellationToken));
        return Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken));
    }

    internal sealed record TestContextFixture(DynamoDbLifecycleStore Store, DynamoDbEffectJournal Journal, ControllerLease Lease,
        CatalogProfile Catalog, ExecutorSnapshot Executor, JitHandoffIdentity Identity, DynamoDbBootstrapOfferClient Bootstrap,
        ContractClock Clock);
}
