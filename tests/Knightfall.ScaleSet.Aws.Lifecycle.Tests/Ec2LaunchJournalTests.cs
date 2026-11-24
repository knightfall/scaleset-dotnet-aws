using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class Ec2LaunchJournalTests
{
    private const string Account = "111122223333";
    private const string Region = "ap-southeast-2";
    private const string InstanceId = "i-0123456789abcdef2";
    private const string InstanceArn = "arn:aws:ec2:" + Region + ":" + Account + ":instance/" + InstanceId;
    private const string NextTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const long ScaleSetId = long.MaxValue;
    private static readonly TimeSpan Reserve = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(3);
    private static readonly EffectDispatcherTiming Timing = new(TimeSpan.FromSeconds(30), Reserve, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ExactFastZonalRejectionAllowsAtMostOneAtomicAlternativeWithAFreshToken()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var first = Launch(fixture, Guid.NewGuid(), 0, 'a', Guid.NewGuid());
        var original = Assert.IsType<Ec2LaunchEffectRequest>(first.Request);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Journal.PlanAsync(fixture.Lease, first, Guid.NewGuid(), cancellation));
        var provider = new Provider(fixture.Clock)
        {
            Send = (envelope, attempt) => new EffectEvidence(EffectResolutionKind.Rejected, envelope.Sha256,
                envelope.Target, fixture.Clock.Now, attempt.Id) { Ec2LaunchRejection = Ec2LaunchRejection.InsufficientInstanceCapacity },
        };

        Assert.Equal(EffectDispatchStatus.Settled, await DispatchAsync(fixture, first, provider, cancellation));
        Assert.Equal(first.Sha256, Assert.Single(provider.Sends));
        var rejected = await ReadAsync(fixture, first, cancellation);
        var begin = Assert.IsType<EffectAttempt>(rejected.Entry.Attempt).BeganAt;
        var receipt = Assert.IsType<EffectEvidence>(rejected.Entry.Resolution);
        Assert.True(receipt.ObservedAt < begin);
        Assert.True(receipt.LatestObservedAt >= begin);
        Assert.Equal(Ec2LaunchRejection.InsufficientInstanceCapacity, receipt.Ec2LaunchRejection);
        Assert.Equal(0, (await fixture.Store.ReadEffectIndexAsync(fixture.Profile, cancellation))?.UnfinishedCount);

        var sameZone = Launch(fixture, original.AllocationId, 1, 'a', Guid.NewGuid());
        var sameToken = Launch(fixture, original.AllocationId, 1, 'b', original.ClientToken);
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            await fixture.Journal.PlanNextEc2LaunchAsync(fixture.Lease, rejected, sameZone, Guid.NewGuid(), cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            await fixture.Journal.PlanNextEc2LaunchAsync(fixture.Lease, rejected, sameToken, Guid.NewGuid(), cancellation));
        var newAllocationSameToken = Launch(fixture, Guid.NewGuid(), 0, 'b', original.ClientToken);
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            await fixture.Journal.PlanAsync(fixture.Lease, newAllocationSameToken, Guid.NewGuid(), cancellation));

        var blue = Launch(fixture, original.AllocationId, 1, 'b', Guid.NewGuid());
        var green = Launch(fixture, original.AllocationId, 1, 'c', Guid.NewGuid());
        Assert.Equal(LifecycleWriteOutcome.Conflict, await fixture.Journal.PlanAsync(fixture.Lease, blue, Guid.NewGuid(), cancellation));
        table.Wire.HoldNextTwoTransactions();
        var candidates = new[] { blue, green };
        var results = await Task.WhenAll(candidates.Select(candidate =>
            fixture.Journal.PlanNextEc2LaunchAsync(fixture.Lease, rejected, candidate, Guid.NewGuid(), cancellation)));
        Assert.Single(results, outcome => outcome == LifecycleWriteOutcome.Committed);
        Assert.Single(results, outcome => outcome == LifecycleWriteOutcome.Conflict);
        var winner = candidates[Array.IndexOf(results, LifecycleWriteOutcome.Committed)];
        var lineage = Assert.IsType<Ec2LaunchLineageSnapshot>(await fixture.Store.ReadEc2LaunchLineageAsync(fixture.Profile,
            original.AllocationId, cancellation));
        var planned = Assert.IsType<Ec2LaunchEffectRequest>(winner.Request);
        Assert.Equal(new[] { original.AvailabilityZone, planned.AvailabilityZone }, lineage.TriedAvailabilityZones);
        Assert.Equal(new[] { original.ClientToken, planned.ClientToken }, lineage.ClientTokens);
        Assert.Equal(1, lineage.AttemptOrdinal);
        Assert.Equal(winner.Sha256, lineage.EnvelopeSha256);
        Assert.True(winner.Matches((await ReadAsync(fixture, winner, cancellation)).Entry.Envelope));
        Assert.Equal(1, (await fixture.Store.ReadEffectIndexAsync(fixture.Profile, cancellation))?.UnfinishedCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExactPositiveReadbackBindsExecutorWithSettlementWhileAnUnresolvedLaunchCannotFailOver(bool recovery, bool existingBinding)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var allocation = Guid.NewGuid();
        var first = Launch(fixture, allocation, 0, 'a', Guid.NewGuid());
        await PlanAsync(fixture, first, cancellation);
        var binding = Executor(fixture, allocation);
        var provider = new Provider(fixture.Clock)
        {
            Observe = (envelope, _) => Positive(fixture, envelope, binding, ExecutorResourceState.Running),
        };
        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, first, provider, cancellation));
        var executing = await ReadAsync(fixture, first, cancellation);
        Assert.Equal(EffectState.Executing, executing.Entry.State);
        var attempt = Assert.IsType<EffectAttempt>(executing.Entry.Attempt);
        var forged = new EffectEvidence(EffectResolutionKind.Rejected, first.Sha256, first.Target, fixture.Clock.Now, attempt.Id)
        {
            Ec2LaunchRejection = Ec2LaunchRejection.InsufficientInstanceCapacity,
            LatestObservedAt = attempt.BeganAt,
        };
        Assert.True(executing.Entry.CanResolve(forged));
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            await fixture.Journal.ResolveAsync(fixture.Lease, executing, Guid.NewGuid(), forged, cancellation));
        Assert.Equal(1, (await fixture.Store.ReadEffectIndexAsync(fixture.Profile, cancellation))?.UnfinishedCount);
        Assert.Null(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, cancellation));
        var competitor = Launch(fixture, allocation, 1, 'b', Guid.NewGuid());
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            await fixture.Journal.PlanNextEc2LaunchAsync(fixture.Lease, executing, competitor, Guid.NewGuid(), cancellation));

        var claimKey = LifecycleKey.Claim("aws-resource#" + binding.ResourceArn);
        Guid? priorClaimTransition = null;
        if (existingBinding)
        {
            Assert.Equal(LifecycleWriteOutcome.Committed,
                await fixture.Store.BindExecutorAsync(fixture.Lease.Fence, binding, cancellation));
            priorClaimTransition = Assert.IsType<LifecycleRecord>(await fixture.Store.ReadRecordAsync(claimKey, cancellation)).TransitionId;
        }

        var current = fixture.Lease;
        EffectRecoveryAuthority? authority = null;
        if (recovery)
        {
            fixture.Clock.Now = current.ValidUntil.AddSeconds(10);
            var successor = await fixture.Store.TryAcquireLeaseAsync(current.Deployment, "ec2-successor", NextTask,
                current.WriterProtocol, fixture.Clock.Now.AddSeconds(-1), fixture.Clock.Now.AddMinutes(5), cancellation);
            current = Assert.IsType<ControllerLease>(successor.Lease);
            Assert.Equal(ControllerLeaseMode.RecoveryOnly, current.Mode);
            Assert.Equal(LifecycleWriteOutcome.Conflict,
                await fixture.Journal.ResolveRecoveryAsync(current, executing, Guid.NewGuid(), forged, cancellation));
            Assert.Equal(EffectDispatchStatus.OwnershipUnavailable, await DispatchAsync(fixture, first, provider, cancellation));
            var stalePlacement = Launch(fixture, Guid.NewGuid(), 0, 'b', Guid.NewGuid());
            Assert.Equal(LifecycleWriteOutcome.LeaseRejected,
                await fixture.Journal.PlanAsync(fixture.Lease, stalePlacement, Guid.NewGuid(), cancellation));
            var observer = new StoppedOwner(fixture.Lease.OwnerTaskArn);
            authority = new EffectRecoveryAuthority(new ControllerLeaseRecoveryCoordinator(fixture.Store, observer, fixture.Clock, Reserve), []);
        }
        var progress = await fixture.Dispatcher.ReconcilePassAsync(current, ClockProof(fixture.Clock), fixture.Profile,
            null, provider, authority, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, progress.Outcome);
        Assert.Equal(1, progress.Settled);
        Assert.Equal(first.Sha256, Assert.Single(provider.Sends));
        Assert.Equal(first.Sha256, Assert.Single(provider.Observes));
        var bound = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(fixture.Profile, binding.AllocationId, cancellation));
        Assert.Equal(binding, bound.Binding);
        Assert.Equal(ExecutorResourceState.Running, bound.State.Resource?.State);
        var claim = Assert.IsType<LifecycleRecord>(await fixture.Store.ReadRecordAsync(claimKey, cancellation));
        var settled = await ReadAsync(fixture, first, cancellation);
        Assert.Equal(EffectResolutionKind.Applied, settled.Entry.Resolution?.Kind);
        Assert.Equal(bound.TransitionId, settled.TransitionId);
        Assert.Equal(priorClaimTransition ?? bound.TransitionId, claim.TransitionId);
        Assert.Equal(0, (await fixture.Store.ReadEffectIndexAsync(fixture.Profile, cancellation))?.UnfinishedCount);
    }

    [Fact]
    public async Task AdvancePlansAnAlternateOnlyAfterDefinitiveRejectionAndKeepsTheAmbiguousAllocationCounted()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var allocationId = Guid.NewGuid();
        bool healthy = false;
        int placements = 0;
        Task<Ec2LaunchEffectRequest?> Select(int ordinal, IReadOnlySet<string> tried, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            placements++;
            if (!healthy)
            {
                return Task.FromResult<Ec2LaunchEffectRequest?>(null);
            }
            Assert.Equal(ordinal == 0 ? [] : new[] { Region + "a" }, tried);
            return Task.FromResult<Ec2LaunchEffectRequest?>(
                (Ec2LaunchEffectRequest)Launch(fixture, allocationId, ordinal, ordinal == 0 ? 'a' : 'b', Guid.NewGuid()).Request);
        }
        var provider = new Provider(fixture.Clock)
        {
            Send = (envelope, attempt) => envelope.Request is Ec2LaunchEffectRequest { AttemptOrdinal: 0 }
                ? new EffectEvidence(EffectResolutionKind.Rejected, envelope.Sha256, envelope.Target, fixture.Clock.Now, attempt.Id)
                {
                    Ec2LaunchRejection = Ec2LaunchRejection.InsufficientFreeAddressesInSubnet,
                }
                : null,
        };
        Task<Ec2LaunchAdvanceResult> AdvanceAsync() => fixture.Dispatcher.AdvanceEc2LaunchAsync(fixture.Lease, ClockProof(fixture.Clock),
            fixture.Profile, allocationId, Select, provider, CancellationToken.None, cancellation);
        async Task<PendingEc2LaunchPage> PendingAsync() => Assert.IsType<PendingEc2LaunchPage>(
            await fixture.Store.ReadPendingEc2LaunchesAsync(fixture.Profile, fixture.Lease.Fence, null, cancellation));

        Assert.Equal(Ec2LaunchAdvanceStatus.NoPlacement, (await AdvanceAsync()).Status);
        Assert.Null(await fixture.Store.ReadEc2LaunchLineageAsync(fixture.Profile, allocationId, cancellation));
        Assert.Empty((await PendingAsync()).Launches);
        Assert.Empty(provider.Sends);

        healthy = true;
        var first = await AdvanceAsync();
        Assert.Equal(Ec2LaunchAdvanceStatus.Planned, first.Status);
        Assert.Equal(new PendingEc2Launch(allocationId, 0, first.IntentId!, EffectState.Planned), Assert.Single((await PendingAsync()).Launches));
        Assert.Empty(provider.Sends);
        Assert.Equal(EffectDispatchStatus.Settled, (await AdvanceAsync()).DispatchStatus);
        Assert.Empty((await PendingAsync()).Launches);

        var alternate = await AdvanceAsync();
        Assert.Equal(Ec2LaunchAdvanceStatus.Planned, alternate.Status);
        Assert.Equal(new PendingEc2Launch(allocationId, 1, alternate.IntentId!, EffectState.Planned), Assert.Single((await PendingAsync()).Launches));
        Assert.Single(provider.Sends);

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, (await AdvanceAsync()).DispatchStatus);
        Assert.Equal(Ec2LaunchAdvanceStatus.AwaitingEvidence, (await AdvanceAsync()).Status);
        Assert.Equal(3, placements);
        Assert.Equal(2, provider.Sends.Count);
        Assert.Equal(new PendingEc2Launch(allocationId, 1, alternate.IntentId!, EffectState.Executing), Assert.Single((await PendingAsync()).Launches));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitivelyObsoletePlanReleasesItsHoldWithoutSendingOrSelectingAnotherPlacement(bool newGeneration)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var allocationId = Guid.NewGuid();
        var first = Launch(fixture, allocationId, 0, 'a', Guid.NewGuid());
        await PlanAsync(fixture, first, cancellation);
        ControllerLease lease = fixture.Lease;
        bool hasDefinitivePreflight = false;
        var provider = new Provider(fixture.Clock)
        {
            Check = envelope => hasDefinitivePreflight
                ? new EffectDispatchPrecondition(envelope.Sha256, envelope.Target, fixture.Clock.Now, Eligible: false)
                {
                    DefinitivelyObsolete = true,
                }
                : null,
        };
        Task<Ec2LaunchAdvanceResult> AdvanceAsync() => fixture.Dispatcher.AdvanceEc2LaunchAsync(lease, ClockProof(fixture.Clock),
            fixture.Profile, allocationId, (_, _, _) => throw new InvalidOperationException("An obsolete plan must not select placement."),
            provider, CancellationToken.None, cancellation);

        var unavailable = await AdvanceAsync();
        Assert.Equal(EffectDispatchStatus.Ineligible, unavailable.DispatchStatus);
        Assert.Equal(EffectState.Planned, (await ReadAsync(fixture, first, cancellation)).Entry.State);
        Assert.Equal(1, (await fixture.Store.ReadEffectIndexAsync(fixture.Profile, cancellation))?.UnfinishedCount);
        if (newGeneration)
        {
            Assert.Equal(LifecycleWriteOutcome.Committed, (await fixture.Store.ReleaseLeaseAsync(lease, fixture.Clock.Now, cancellation)).Outcome);
            var next = await fixture.Store.TryAcquireLeaseAsync(lease.Deployment, "ec2-new-generation", NextTask, lease.WriterProtocol,
                fixture.Clock.Now, fixture.Clock.Now.AddMinutes(5), cancellation);
            var promoted = await fixture.Store.PromoteLeaseAsync(Assert.IsType<ControllerLease>(next.Lease), fixture.Clock.Now, cancellation);
            lease = Assert.IsType<ControllerLease>(promoted.Lease);
            await ApplyCatalogAsync(fixture.Store, lease.Fence, fixture.Configuration, 2, cancellation);
            var rebuild = await fixture.Store.AdvanceEffectRebuildAsync(fixture.Profile, lease.Fence, cancellation);
            Assert.Equal(EffectIndexState.Ready, rebuild.Head?.State);
        }
        else
        {
            hasDefinitivePreflight = true;
        }

        var canceled = await AdvanceAsync();
        Assert.Equal(new Ec2LaunchAdvanceResult(Ec2LaunchAdvanceStatus.CanceledObsolete, first.IntentId,
            LifecycleWriteOutcome.Committed, EffectDispatchStatus.StalePlan), canceled);
        var settled = await ReadAsync(fixture, first, cancellation);
        Assert.Equal(EffectResolutionKind.CanceledBeforeSend, settled.Entry.Resolution?.Kind);
        Assert.Null(settled.Entry.Attempt);
        var slots = Assert.IsType<PendingEc2LaunchPage>(await fixture.Store.ReadPendingEc2LaunchesAsync(
            fixture.Profile, lease.Fence, null, cancellation));
        Assert.Empty(slots.Launches);
        Assert.Equal(Ec2LaunchAdvanceStatus.Finished, (await AdvanceAsync()).Status);
        Assert.Equal(newGeneration ? 1 : 2, provider.Checks);
        Assert.Empty(provider.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConfirmedLateBeginReleasesTheHoldButALostBeginResponseKeepsIt(bool loseResponse)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var first = Launch(fixture, Guid.NewGuid(), 0, 'a', Guid.NewGuid());
        await PlanAsync(fixture, first, cancellation);
        var provider = new Provider(fixture.Clock);
        var begin = table.Wire.PauseNextTransactionWrite(LifecycleKey.Operation(fixture.Profile, first.IntentId));
        var dispatch = DispatchAsync(fixture, first, provider, cancellation);
        try
        {
            await begin.Captured.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            fixture.Clock.Now += Timing.OperationBudget + Skew;
            if (loseResponse)
            {
                table.Wire.DropNextSuccessfulTransactionResponse();
            }
        }
        finally
        {
            begin.Resume();
        }

        Assert.Equal(loseResponse ? EffectDispatchStatus.AwaitingEvidence : EffectDispatchStatus.Settled, await dispatch);
        Assert.Equal(1, provider.Checks);
        Assert.Empty(provider.Sends);
        var current = await ReadAsync(fixture, first, cancellation);
        var attempt = Assert.IsType<EffectAttempt>(current.Entry.Attempt);
        Assert.True(attempt.Deadline <= fixture.Clock.Now);
        var pending = Assert.IsType<PendingEc2LaunchPage>(await fixture.Store.ReadPendingEc2LaunchesAsync(
            fixture.Profile, fixture.Lease.Fence, null, cancellation));
        if (loseResponse)
        {
            Assert.Equal(EffectState.Executing, current.Entry.State);
            Assert.Null(current.Entry.Resolution);
            Assert.Equal(first.IntentId, Assert.Single(pending.Launches).IntentId);
            Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, first, provider, cancellation));
            Assert.Empty(provider.Sends);
            return;
        }

        var proof = Assert.IsType<EffectEvidence>(current.Entry.Resolution);
        Assert.Equal(EffectResolutionKind.AbandonedBeforeSend, proof.Kind);
        Assert.Equal(attempt.Id, proof.AttemptId);
        Assert.True(proof.ObservedAt >= attempt.Deadline);
        Assert.Empty(pending.Launches);
    }

    [Fact]
    public async Task AChangedAllocationAfterConfirmedBeginReleasesOnlyTheOriginalNeverSentAttempt()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var allocation = Guid.NewGuid();
        var first = Launch(fixture, allocation, 0, 'a', Guid.NewGuid());
        await PlanAsync(fixture, first, cancellation);
        var provider = new Provider(fixture.Clock);
        var opKey = LifecycleKey.Operation(fixture.Profile, first.IntentId);
        var begin = table.Wire.PauseNextTransactionWrite(opKey);
        var dispatch = DispatchAsync(fixture, first, provider, cancellation);
        MiniStackWire.PausedRequest postBegin;
        try
        {
            await begin.Captured.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            postBegin = table.Wire.PauseNextRead(opKey);
        }
        finally
        {
            begin.Resume();
        }

        var existing = Executor(fixture, allocation);
        try
        {
            await postBegin.Captured.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            var executing = await ReadAsync(fixture, first, cancellation);
            Assert.Equal(EffectState.Executing, executing.Entry.State);
            Assert.Equal(LifecycleWriteOutcome.Committed,
                await fixture.Store.BindExecutorAsync(fixture.Lease.Fence, existing, cancellation));
        }
        finally
        {
            postBegin.Resume();
        }

        Assert.Equal(EffectDispatchStatus.Settled, await dispatch);
        Assert.Equal(1, provider.Checks);
        Assert.Empty(provider.Sends);
        var settled = await ReadAsync(fixture, first, cancellation);
        Assert.Equal(EffectResolutionKind.AbandonedBeforeSend, settled.Entry.Resolution?.Kind);
        Assert.Equal(settled.Entry.Attempt?.Id, settled.Entry.Resolution?.AttemptId);
        Assert.Equal(existing, (await fixture.Store.ReadExecutorAsync(fixture.Profile, existing.AllocationId, cancellation))?.Binding);
        var pending = Assert.IsType<PendingEc2LaunchPage>(await fixture.Store.ReadPendingEc2LaunchesAsync(
            fixture.Profile, fixture.Lease.Fence, null, cancellation));
        Assert.Empty(pending.Launches);
    }

    [Fact]
    public async Task ResourceClaimCollisionLeavesTheEffectUnresolvedAndOccupancyConservative()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var existing = Executor(fixture, Guid.NewGuid());
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorAsync(fixture.Lease.Fence, existing, cancellation));
        var target = Executor(fixture, Guid.NewGuid());
        var first = Launch(fixture, Guid.Parse(target.AllocationId), 0, 'a', Guid.NewGuid());
        await PlanAsync(fixture, first, cancellation);
        var provider = new Provider(fixture.Clock)
        {
            Send = (envelope, _) => Positive(fixture, envelope, target, ExecutorResourceState.Running),
        };

        Assert.Equal(EffectDispatchStatus.AwaitingEvidence, await DispatchAsync(fixture, first, provider, cancellation));
        Assert.Null(await fixture.Store.ReadExecutorAsync(fixture.Profile, target.AllocationId, cancellation));
        Assert.Equal(existing, Assert.IsType<ExecutorSnapshot>(
            await fixture.Store.ReadExecutorAsync(fixture.Profile, existing.AllocationId, cancellation)).Binding);
        Assert.Equal(EffectState.Executing, (await ReadAsync(fixture, first, cancellation)).Entry.State);
        Assert.Equal(1, (await fixture.Store.ReadEffectIndexAsync(fixture.Profile, cancellation))?.UnfinishedCount);
    }

    [Fact]
    public async Task ExactEc2CleanupDoesNotReachTheProviderWhileItsRunnerHasAnActiveJob()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var fixture = await CreateFixtureAsync(table, cancellation);
        var allocation = Guid.NewGuid();
        var binding = Executor(fixture, allocation);
        var instance = Assert.IsType<Ec2ExecutorIdentity>(binding.Provider);
        var fence = fixture.Lease.Fence;
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorAsync(fence, binding, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.BindExecutorRunnerAsync(fence, binding, 17, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(fence, binding,
            new ExecutorResourceObservation(ExecutorResourceState.Running, fixture.Clock.Now, instance.InstanceLaunchTime, Guid.NewGuid()), cancellation));

        var catalog = Assert.IsType<CatalogProfile>(await fixture.Store.ReadCatalogProfileAsync(fixture.Profile, cancellation));
        var begun = await fixture.Store.BeginSessionOpenAsync(new SessionBinding(fixture.Profile, catalog.Binding.ScopeIdentity, ScaleSetId), fence, null,
            "ec2-cleanup-fixture", fixture.Clock.Now, fixture.Clock.Now.AddMinutes(1), cancellation);
        var permission = Assert.IsType<SessionSendAuthorization>(begun.Permission);
        Assert.True(permission.TryConsume(out _));
        var sessionId = Guid.NewGuid();
        var opened = await fixture.Store.ConfirmSessionOpenedAsync(permission, sessionId, cancellation);
        var owned = Assert.IsType<OwnedSessionFence>(opened.Session?.Fence);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.WriteSessionDeliveryRecordsAsync(owned, Guid.NewGuid(),
            [new LifecycleRecordWrite(LifecycleKey.Message(fixture.Profile, sessionId, 0), null, "{}")],
            [new ExecutorJobObservation(9, 17, ExecutorJobPhase.Started, sessionId, 0, fixture.Clock.Now)], cancellation));

        var cleanup = new Ec2TerminateEffectRequest(allocation, binding.ResourceArn, instance.InstanceLaunchTime,
            instance.LaunchTemplateId, instance.LaunchTemplateVersion);
        var envelope = EffectEnvelope.Create(fixture.Profile, EffectIntentIdentity.ForAllocation(cleanup)!, 1, cleanup, fixture.Clock.Now, null);
        await PlanAsync(fixture, envelope, cancellation);
        var provider = new Provider(fixture.Clock);
        Assert.Equal(EffectDispatchStatus.Ineligible, await DispatchAsync(fixture, envelope, provider, cancellation));
        Assert.Equal(0, provider.Checks);
        Assert.Empty(provider.Sends);
        Assert.Equal(EffectState.Planned, (await ReadAsync(fixture, envelope, cancellation)).Entry.State);
    }

    private static async Task<Fixture> CreateFixtureAsync(MiniStackTable table, CancellationToken cancellationToken)
    {
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        string path = Path.Combine(AppContext.BaseDirectory, "ec2-only-pinned.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!;
        document["spec"]!["mode"] = "active";
        var configuration = RuntimeConfigurationLoader.Load(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document));
        var desired = Assert.Single(configuration.Profiles);
        var provisioner = Assert.IsType<Ec2ProvisionerConfiguration>(desired.Provisioner);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var fence = table.Lease with { Deployment = deployment };
        await table.SetLeaseAsync(fence, clock.Now.AddMinutes(5), cancellationToken);
        var lease = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(deployment, cancellationToken));
        var profile = new LifecycleProfile(deployment, desired.Id);
        await ApplyCatalogAsync(store, fence, configuration, 1, cancellationToken);
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(fence, catalog, 7, ScaleSetId, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.AdvanceEffectRebuildAsync(profile, fence, cancellationToken)).Outcome);
        return new Fixture(store, new DynamoDbEffectJournal(store, clock), new DynamoDbEffectDispatcher(store, clock, Timing),
            lease, profile, provisioner, configuration, clock);
    }

    private static async Task ApplyCatalogAsync(DynamoDbLifecycleStore store, DeploymentLeaseFence fence,
        RuntimeConfiguration configuration, long generation, CancellationToken cancellationToken)
    {
        for (int pass = 0; ; pass++)
        {
            var rollout = await store.AdvanceCatalogRolloutAsync(fence, configuration, generation, cancellationToken);
            if (rollout.Status != CatalogRolloutStatus.Staging || pass == 3)
            {
                Assert.Equal(CatalogRolloutStatus.Applied, rollout.Status);
                return;
            }
        }
    }

    private static ExecutorBinding Executor(Fixture fixture, Guid allocation)
    {
        var initial = MiniStackTable.Now.AddSeconds(-4);
        return new ExecutorBinding(fixture.Profile, allocation.ToString("D"), InstanceArn,
            Ec2ExecutorIncarnation.Compute(Account, Region, InstanceId, initial, 12),
            new Ec2ExecutorIdentity(initial, fixture.Provisioner.LaunchTemplateId, 12));
    }

    private static EffectEnvelope Launch(Fixture fixture, Guid allocationId, int ordinal, char zone, Guid token)
    {
        var tags = new Ec2LaunchOwnershipTags(RunnerOwnershipTags.ControllerValue, fixture.Profile.Deployment,
            fixture.Profile.ProfileId, allocationId.ToString("D"), "v1");
        var request = new Ec2LaunchEffectRequest(allocationId, Region, "subnet-0123456789abcdef" + zone, Region + zone,
            fixture.Provisioner.LaunchTemplateId, 12, "c7g.xlarge", ["sg-0123456789abcdef1"], fixture.Provisioner.InstanceProfileArn,
            token, ordinal, Ec2Tenancy.Default, tags);
        return EffectEnvelope.Create(fixture.Profile, EffectIntentIdentity.ForAllocation(request)!, 1, request, fixture.Clock.Now, null);
    }

    private static EffectEvidence Positive(Fixture fixture, EffectEnvelope envelope, ExecutorBinding binding, ExecutorResourceState state) =>
        new(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, fixture.Clock.Now)
        {
            ExecutorBinding = binding,
            ResourceObservation = new(state, fixture.Clock.Now, ((Ec2ExecutorIdentity)binding.Provider).InstanceLaunchTime, Guid.NewGuid()),
        };

    private static async Task PlanAsync(Fixture fixture, EffectEnvelope envelope, CancellationToken cancellationToken) =>
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Journal.PlanAsync(fixture.Lease, envelope, Guid.NewGuid(), cancellationToken));

    private static async Task<EffectJournalSnapshot> ReadAsync(Fixture fixture, EffectEnvelope envelope, CancellationToken cancellationToken) =>
        Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken));

    private static Task<EffectDispatchStatus> DispatchAsync(Fixture fixture, EffectEnvelope envelope, Provider provider,
        CancellationToken cancellationToken) =>
        fixture.Dispatcher.DispatchPlannedAsync(fixture.Lease, ClockProof(fixture.Clock), envelope.Profile, envelope.IntentId,
            provider, CancellationToken.None, cancellationToken);

    private static ControllerClockEvidence ClockProof(FixtureClock clock) => new(clock.Now, clock.Now.AddMinutes(2), Skew);

    private sealed record Fixture(DynamoDbLifecycleStore Store, DynamoDbEffectJournal Journal, DynamoDbEffectDispatcher Dispatcher,
        ControllerLease Lease, LifecycleProfile Profile, Ec2ProvisionerConfiguration Provisioner, RuntimeConfiguration Configuration, FixtureClock Clock);

    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = MiniStackTable.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StoppedOwner(string expectedTask) : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedTask, taskArn);
            return Task.FromResult(ControllerTaskFenceStatus.Stopped);
        }
    }

    private sealed class Provider(FixtureClock clock) : IReconciliationEffectProvider
    {
        public int Checks { get; private set; }
        public List<string> Sends { get; } = [];
        public List<string> Observes { get; } = [];
        public Func<EffectEnvelope, EffectDispatchPrecondition?>? Check { get; init; }
        public Func<EffectEnvelope, EffectAttempt, EffectEvidence?>? Send { get; init; }
        public Func<EffectEnvelope, EffectAttempt, EffectEvidence?>? Observe { get; init; }

        public Task<EffectDispatchPrecondition?> CheckAsync(EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checks++;
            return Task.FromResult(Check is null
                ? new EffectDispatchPrecondition(envelope.Sha256, envelope.Target, clock.Now, Eligible: true) : Check(envelope));
        }

        public Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sends.Add(envelope.Sha256);
            return Task.FromResult(Send?.Invoke(envelope, attempt));
        }

        public Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observes.Add(envelope.Sha256);
            return Task.FromResult(Observe?.Invoke(envelope, attempt));
        }
    }
}
