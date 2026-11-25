using System.Text;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class ControllerLeaseRecoveryTests
{
    private const string TaskTwo = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const string TaskThree = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000003";
    private static readonly TimeSpan Reserve = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SuspendedOwnerBlocksRecoveryAndEffectsNeedFreshExactEvidenceAfterFencing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new MutableClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var observer = new TaskObserver();
        var recovery = new ControllerLeaseRecoveryCoordinator(store, observer, clock, Reserve);
        var journal = new DynamoDbEffectJournal(store, clock);
        var configuration = await LoadConfigurationAsync(cancellation);
        var first = await AcquireAsync(store, configuration, clock, "first", MiniStackTable.FirstTask, cancellation);
        first = Assert.IsType<ControllerLease>((await recovery.TryActivateAsync(first, configuration,
            ClockProof(clock), [], cancellation)).Lease);
        Assert.Equal(ControllerLeaseMode.Active, first.Mode);
        Assert.Empty(observer.Reads);

        var profile = new LifecycleProfile(first.Deployment, Assert.Single(configuration.Profiles).Id);
        var envelope = EffectEnvelope.Create(profile, "session-retire", 1,
            new GitHubSessionRetireEffectRequest("https://example.ghe.com/engineering/tools", 42, Guid.NewGuid()), clock.Now, null);
        Assert.Equal(LifecycleWriteOutcome.Committed, await journal.PlanAsync(first, envelope, Guid.NewGuid(), cancellation));
        var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        var send = await journal.BeginAsync(first, planned, Guid.NewGuid(), Guid.NewGuid(), clock.Now,
            clock.Now.AddSeconds(40), Reserve, cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, send.Outcome);
        var executing = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(Assert.IsType<EffectSendAuthorization>(send.Permission).Attempt, executing.Entry.Attempt);

        clock.Now = first.ValidUntil.AddSeconds(2);
        var successor = await AcquireAsync(store, configuration, clock, "second", TaskTwo, cancellation);
        Assert.Equal(first.Epoch + 1, successor.Epoch);
        Assert.Equal(LeaseActivationStatus.ClockUnverified,
            (await recovery.TryActivateAsync(successor, configuration, null, [], cancellation)).Status);
        Assert.Empty(observer.Reads);

        observer.Statuses[first.OwnerTaskArn] = ControllerTaskFenceStatus.Running;
        Assert.Equal(LeaseActivationStatus.PriorTaskUnisolated,
            (await recovery.TryActivateAsync(successor, configuration, ClockProof(clock), [], cancellation)).Status);
        int evidenceCalls = 0;
        var absence = new EffectEvidence(EffectResolutionKind.Absent, envelope.Sha256, envelope.Target, clock.Now);
        Task<EffectEvidence?> Observe(EffectJournalSnapshot requested, CancellationToken token)
        {
            evidenceCalls++;
            Assert.Equal(executing.TransitionId, requested.TransitionId);
            Assert.Equal(cancellation, token);
            return Task.FromResult<EffectEvidence?>(absence);
        }
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await recovery.ResolveFencedEffectAsync(successor, executing,
            Guid.NewGuid(), ClockProof(clock), [], Observe, cancellation));
        Assert.Equal(0, evidenceCalls);

        observer.Statuses[first.OwnerTaskArn] = ControllerTaskFenceStatus.Stopped;
        var replacementConfiguration = await LoadConfigurationAsync(cancellation, replaceProfileId: true);
        var pending = await recovery.TryActivateAsync(successor, replacementConfiguration, ClockProof(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.ProfileRecoveryPending, pending.Status);
        Assert.Equal(
        [
            new ProfileRecoveryObservation("replacement", ProfileRecoveryStatus.Ready),
            new ProfileRecoveryObservation(profile.ProfileId, ProfileRecoveryStatus.AmbiguousEffects),
        ], pending.Profiles);
        Assert.Equal(successor, await store.ReadLeaseAsync(successor.Deployment, cancellation));
        Assert.Equal(EffectState.Executing, (await journal.ReadAsync(profile, envelope.IntentId, cancellation))?.Entry.State);

        clock.Now = clock.Now.AddSeconds(1);
        var qualificationEnds = clock.Now.AddSeconds(2);
        observer.QualificationEnds = qualificationEnds;
        Task<EffectEvidence?> ObserveAfterExpiry(EffectJournalSnapshot requested, CancellationToken token)
        {
            clock.Now = qualificationEnds;
            return Observe(requested, token);
        }
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await recovery.ResolveFencedEffectAsync(successor, executing,
            Guid.NewGuid(), ClockProof(clock), [], ObserveAfterExpiry, cancellation));
        observer.QualificationEnds = null;
        Assert.Equal(LifecycleWriteOutcome.Indeterminate, await recovery.ResolveFencedEffectAsync(successor, executing,
            Guid.NewGuid(), ClockProof(clock), [], Observe, cancellation));
        Assert.Equal(2, evidenceCalls);
        absence = absence with { ObservedAt = clock.Now };
        Assert.Equal(LifecycleWriteOutcome.Committed, await recovery.ResolveFencedEffectAsync(successor, executing,
            Guid.NewGuid(), ClockProof(clock), [], Observe, cancellation));
        Assert.Equal(3, evidenceCalls);
        Assert.Equal(absence with { EarliestObservedAt = clock.Now.AddSeconds(-1) },
            (await journal.ReadAsync(profile, envelope.IntentId, cancellation))?.Entry.Resolution);

        var activated = await recovery.TryActivateAsync(successor, replacementConfiguration, ClockProof(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        Assert.Equal(ControllerLeaseMode.Active, activated.Lease?.Mode);
        Assert.Null(activated.Lease?.RequiredFencingTaskArn);
        Assert.All(observer.Reads, task => Assert.Equal(first.OwnerTaskArn, task));
    }

    [Fact]
    public async Task UnavailableTasksRequireDualSurfaceProofForBothTheImmediateAndLastActivePredecessor()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new MutableClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var observer = new TaskObserver();
        var recovery = new ControllerLeaseRecoveryCoordinator(store, observer, clock, Reserve);
        var configuration = await LoadConfigurationAsync(cancellation);
        var first = await AcquireAsync(store, configuration, clock, "first", MiniStackTable.FirstTask, cancellation);
        var initial = await recovery.TryActivateAsync(first, configuration, ClockProof(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, initial.Status);
        var current = Assert.IsType<ControllerLease>(initial.Lease);
        var profile = new LifecycleProfile(current.Deployment, Assert.Single(configuration.Profiles).Id);
        var journal = new DynamoDbEffectJournal(store, clock);
        var envelope = EffectEnvelope.Create(profile, "session-retire", 1,
            new GitHubSessionRetireEffectRequest("https://example.ghe.com/engineering/tools", 42, Guid.NewGuid()), clock.Now, null);
        Assert.Equal(LifecycleWriteOutcome.Committed, await journal.PlanAsync(current, envelope, Guid.NewGuid(), cancellation));
        var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, (await journal.BeginAsync(current, planned, Guid.NewGuid(), Guid.NewGuid(),
            clock.Now, clock.Now.AddSeconds(40), Reserve, cancellation)).Outcome);
        var executing = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, envelope.IntentId, cancellation));
        Assert.Equal(EffectState.Executing, executing.Entry.State);

        clock.Now = first.ValidUntil.AddSeconds(2);
        var second = await AcquireAsync(store, configuration, clock, "second", TaskTwo, cancellation);
        clock.Now = second.ValidUntil.AddSeconds(2);
        var third = await AcquireAsync(store, configuration, clock, "third", TaskThree, cancellation);
        Assert.Equal(TaskTwo, third.PreviousOwner?.OwnerTaskArn);
        Assert.Equal(first.OwnerTaskArn, third.RequiredFencingTaskArn);
        Assert.Equal(second.AcquiredAt, third.PreviousOwner?.AcquiredAt);
        Assert.Equal(first.AcquiredAt, third.RequiredFencingTaskAcquiredAt);

        observer.Statuses[TaskTwo] = ControllerTaskFenceStatus.Missing;
        observer.Statuses[first.OwnerTaskArn] = ControllerTaskFenceStatus.Unknown;
        var immediate = new VerifiedControllerIsolation(TaskTwo, "aws-proof-2", "github-proof-2", clock.Now, clock.Now.AddMinutes(1));
        var oldest = immediate with { TaskArn = first.OwnerTaskArn, AwsEvidenceId = "aws-proof-1", GitHubEvidenceId = "github-proof-1" };
        Assert.Equal(LeaseActivationStatus.PriorTaskUnisolated,
            (await recovery.TryActivateAsync(third, configuration, ClockProof(clock), [oldest], cancellation)).Status);
        Assert.Equal([TaskTwo], observer.Reads);

        observer.Reads.Clear();
        Assert.Equal(LeaseActivationStatus.PriorTaskUnisolated, (await recovery.TryActivateAsync(third, configuration,
            ClockProof(clock), [immediate, oldest with { GitHubEvidenceId = "" }], cancellation)).Status);
        Assert.Equal([TaskTwo, first.OwnerTaskArn], observer.Reads);

        observer.Statuses[first.OwnerTaskArn] = ControllerTaskFenceStatus.Running;
        Assert.Equal(LeaseActivationStatus.PriorTaskUnisolated,
            (await recovery.TryActivateAsync(third, configuration, ClockProof(clock), [immediate, oldest], cancellation)).Status);
        Assert.Equal(third, await store.ReadLeaseAsync(third.Deployment, cancellation));

        observer.Statuses[first.OwnerTaskArn] = ControllerTaskFenceStatus.Unknown;
        var wrongCluster = oldest with { TaskArn = oldest.TaskArn.Replace("task/controller/", "task/wrong-cluster/", StringComparison.Ordinal) };
        var restarted = new ControllerLeaseRecoveryCoordinator(new DynamoDbLifecycleStore(table.Client, table.Name, clock), observer, clock, Reserve);
        Assert.Equal(LeaseActivationStatus.PriorTaskUnisolated,
            (await restarted.TryActivateAsync(third, configuration, ClockProof(clock), [immediate, wrongCluster], cancellation)).Status);
        var stale = oldest with { ObservedAt = clock.Now.AddMinutes(-1), ValidUntil = clock.Now };
        int observations = 0;
        Task<EffectEvidence?> Observe(EffectJournalSnapshot requested, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(executing.TransitionId, requested.TransitionId);
            observations++;
            return Task.FromResult<EffectEvidence?>(new EffectEvidence(EffectResolutionKind.Absent, envelope.Sha256, envelope.Target, clock.Now));
        }
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await restarted.ResolveFencedEffectAsync(third, executing,
            Guid.NewGuid(), ClockProof(clock), [immediate, stale], Observe, cancellation));
        Assert.Equal(0, observations);
        Assert.Equal(LeaseActivationStatus.ProfileRecoveryPending,
            (await restarted.TryActivateAsync(third, configuration, ClockProof(clock), [immediate, oldest], cancellation)).Status);
        Assert.Equal(LifecycleWriteOutcome.Committed, await restarted.ResolveFencedEffectAsync(third, executing,
            Guid.NewGuid(), ClockProof(clock), [immediate, oldest], Observe, cancellation));
        Assert.Equal(1, observations);
        var recovered = await restarted.TryActivateAsync(third, configuration, ClockProof(clock), [immediate, oldest], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, recovered.Status);
        Assert.Equal((third.Fence, ControllerLeaseMode.Active), (recovered.Lease?.Fence, recovered.Lease?.Mode));
        Assert.Null(recovered.Lease?.RequiredFencingTaskArn);
    }

    [Fact]
    public async Task ClockEvidenceMustRemainFreshWithAReservedWriteMarginAfterTaskObservation()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new MutableClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var observer = new TaskObserver();
        var recovery = new ControllerLeaseRecoveryCoordinator(store, observer, clock, Reserve);
        var configuration = await LoadConfigurationAsync(cancellation);
        var first = await AcquireAsync(store, configuration, clock, "first", MiniStackTable.FirstTask, cancellation);
        Assert.Equal(LeaseActivationStatus.Activated,
            (await recovery.TryActivateAsync(first, configuration, ClockProof(clock), [], cancellation)).Status);

        clock.Now = first.ValidUntil.AddSeconds(2);
        var successor = await AcquireAsync(store, configuration, clock, "second", TaskTwo, cancellation);
        observer.Statuses[first.OwnerTaskArn] = ControllerTaskFenceStatus.Stopped;
        observer.OnRead = () => clock.Now = successor.ValidUntil - Reserve - TimeSpan.FromSeconds(1);
        var evidence = ClockProof(clock) with { ValidUntil = successor.ValidUntil };

        Assert.Equal(LeaseActivationStatus.ClockUnverified,
            (await recovery.TryActivateAsync(successor, configuration, evidence, [], cancellation)).Status);
        Assert.Equal([first.OwnerTaskArn], observer.Reads);
        Assert.Equal(successor, await store.ReadLeaseAsync(successor.Deployment, cancellation));
    }

    [Theory]
    [InlineData(ControllerLease.CurrentWriterProtocol, false)]
    [InlineData(7, true)]
    public async Task CleanHandoffReusesTheIndexOnlyWithKnownWriterContinuity(int predecessorProtocol, bool rebuilds)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new MutableClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var observer = new TaskObserver();
        var recovery = new ControllerLeaseRecoveryCoordinator(store, observer, clock, Reserve);
        var configuration = await LoadConfigurationAsync(cancellation);
        var acquired = await AcquireAsync(store, configuration, clock, "prior", MiniStackTable.FirstTask, cancellation, predecessorProtocol);
        var initial = await recovery.TryActivateAsync(acquired, configuration, ClockProof(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, initial.Status);
        var prior = Assert.IsType<ControllerLease>(initial.Lease);
        var profile = new LifecycleProfile(prior.Deployment, Assert.Single(configuration.Profiles).Id);
        var previousHead = Assert.IsType<EffectIndexHead>(await store.ReadEffectIndexAsync(profile, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.ReleaseLeaseAsync(prior, clock.Now, cancellation)).Outcome);

        var successor = await AcquireAsync(store, configuration, clock, "next", TaskTwo, cancellation);
        table.Wire.Clear();
        var activation = await recovery.TryActivateAsync(successor, configuration, ClockProof(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, activation.Status);
        Assert.Empty(observer.Reads);
        Assert.Equal(rebuilds, table.Wire.Requests.Any(call => call.QueryPrefix == "OP#"));
        var currentHead = Assert.IsType<EffectIndexHead>(await store.ReadEffectIndexAsync(profile, cancellation));
        Assert.Equal(rebuilds, currentHead.Generation != previousHead.Generation);
        Assert.Equal(successor.Epoch, currentHead.OwnerEpoch);
        Assert.Equal(ControllerLease.CurrentWriterProtocol, currentHead.WriterProtocol);
    }

    private static async Task<RuntimeConfiguration> LoadConfigurationAsync(CancellationToken cancellation, bool replaceProfileId = false)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellation);
        if (replaceProfileId)
        {
            var json = Encoding.UTF8.GetString(bytes).Replace("\"id\": \"linux-shell\"", "\"id\": \"replacement\"", StringComparison.Ordinal);
            bytes = Encoding.UTF8.GetBytes(json);
        }
        return RuntimeConfigurationLoader.Load(bytes);
    }

    private static async Task<ControllerLease> AcquireAsync(
        DynamoDbLifecycleStore store,
        RuntimeConfiguration configuration,
        MutableClock clock,
        string ownerId,
        string taskArn,
        CancellationToken cancellation,
        int writerProtocol = ControllerLease.CurrentWriterProtocol)
    {
        var acquisition = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration),
            ownerId, taskArn, writerProtocol, clock.Now.AddSeconds(-1), clock.Now.AddMinutes(2), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquisition.Outcome);
        var lease = Assert.IsType<ControllerLease>(acquisition.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, lease.Mode);
        return lease;
    }

    private static ControllerClockEvidence ClockProof(MutableClock clock) => new(clock.Now, clock.Now.AddMinutes(1), TimeSpan.FromSeconds(1));

    private sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = MiniStackTable.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TaskObserver : IControllerTaskFenceObserver
    {
        internal readonly Dictionary<string, ControllerTaskFenceStatus> Statuses = new(StringComparer.Ordinal);
        internal readonly List<string> Reads = [];
        internal Action? OnRead { get; set; }
        internal DateTimeOffset? QualificationEnds { get; set; }

        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(taskArn);
            OnRead?.Invoke();
            return Task.FromResult(Statuses.GetValueOrDefault(taskArn));
        }

        public async Task<ControllerTaskFenceObservation> ObserveAsync(string taskArn, DateTimeOffset? acquiredAt, CancellationToken cancellationToken) =>
            new(await ReadAsync(taskArn, cancellationToken), QualificationEnds);
    }
}
