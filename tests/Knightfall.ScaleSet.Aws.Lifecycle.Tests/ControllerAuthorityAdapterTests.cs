using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class ControllerAuthorityAdapterTests
{
    private const string ClusterArn = "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-dev-controller";
    private const string OldTask = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-dev-controller/0123456789abcdef0123456789abcdef";
    private const string NewTask = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-dev-controller/0123456789abcdef0123456789abcdff";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Reserve = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnlyExactStoppedPredecessorAndBoundedClockPermitRecoveryAndPromotion()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var configuration = await LoadConfigurationAsync(cancellation);
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var observer = new TaskObserver();
        var adapter = new ControllerAuthorityAdapter(configuration, store, observer, clock, LeaseDuration, Reserve);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);

        var acquired = await store.TryAcquireLeaseAsync(deployment, "old", OldTask, ControllerLease.CurrentWriterProtocol,
            clock.Now, clock.Now + LeaseDuration, cancellation);
        var initial = Assert.IsType<ControllerLease>(acquired.Lease);
        var promoted = await new ControllerLeaseRecoveryCoordinator(store, observer, clock, Reserve)
            .TryActivateAsync(initial, configuration, Observation(clock).Clock, [], cancellation);
        var predecessor = Assert.IsType<ControllerLease>(promoted.Lease);
        Assert.Equal(ControllerLeaseMode.Active, predecessor.Mode);

        int callbacks = 0;
        Task<bool> Recover(ControllerLease lease, ControllerClockEvidence proof, CancellationToken token)
        {
            callbacks++;
            Assert.Equal(ControllerLeaseMode.RecoveryOnly, lease.Mode);
            Assert.Equal(predecessor.Epoch + 1, lease.Epoch);
            Assert.Equal(NewTask, lease.OwnerTaskArn);
            Assert.Equal(ControllerTaskFenceStatus.Stopped, observer.Status);
            Assert.True(proof.TryGetUpperBound(clock.Now, lease.ValidUntil, Reserve, out _));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        clock.Now = predecessor.ValidUntil.AddMilliseconds(500);
        Assert.Null(await adapter.TryActivateAsync(Observation(clock), Recover, cancellation));
        Assert.Equal(predecessor, await store.ReadLeaseAsync(deployment, cancellation));
        Assert.Empty(observer.Reads);

        clock.Now = predecessor.ValidUntil.AddSeconds(2);
        foreach (var status in new[] { ControllerTaskFenceStatus.Running, ControllerTaskFenceStatus.Missing, ControllerTaskFenceStatus.Unknown })
        {
            observer.Status = status;
            Assert.Null(await adapter.TryActivateAsync(Observation(clock), Recover, cancellation));
            Assert.Equal(ControllerLeaseMode.RecoveryOnly, (await store.ReadLeaseAsync(deployment, cancellation))?.Mode);
        }
        Assert.Equal(0, callbacks);
        Assert.Equal([OldTask, OldTask, OldTask], observer.Reads);

        observer.Status = ControllerTaskFenceStatus.Stopped;
        using var ownership = Assert.IsType<ControllerLeaseOwnership>(await adapter.TryActivateAsync(Observation(clock), Recover, cancellation));
        var authority = Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority());
        Assert.Equal(ControllerLeaseMode.Active, authority.Lease.Mode);
        Assert.Equal(predecessor.Epoch + 1, authority.Lease.Epoch);
        Assert.Equal(1, callbacks);
        Assert.All(observer.Reads, taskArn => Assert.Equal(OldTask, taskArn));
        Assert.Equal(authority.Lease, await store.ReadLeaseAsync(deployment, cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldClusterWithoutCleanReleaseRemainsFencedAfterRestart(bool cleanRelease)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var configuration = await LoadConfigurationAsync(cancellation);
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var observer = new TaskObserver { Status = ControllerTaskFenceStatus.Stopped };
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        string predecessorTask = OldTask.Replace("task/engineering-dev-controller/", "task/prior-controller/", StringComparison.Ordinal);
        var acquired = await store.TryAcquireLeaseAsync(deployment, "old", predecessorTask, ControllerLease.CurrentWriterProtocol,
            clock.Now, clock.Now + LeaseDuration, cancellation);
        var promoted = await new ControllerLeaseRecoveryCoordinator(store, observer, clock, Reserve)
            .TryActivateAsync(Assert.IsType<ControllerLease>(acquired.Lease), configuration, Observation(clock).Clock, [], cancellation);
        var predecessor = Assert.IsType<ControllerLease>(promoted.Lease);
        Assert.Equal(ControllerLeaseMode.Active, predecessor.Mode);
        if (cleanRelease)
        {
            Assert.Equal(LifecycleWriteOutcome.Committed, (await store.ReleaseLeaseAsync(predecessor, clock.Now, cancellation)).Outcome);
        }

        clock.Now = predecessor.ValidUntil.AddSeconds(2);
        int recoveries = 0;
        Task<bool> Recover(ControllerLease lease, ControllerClockEvidence proof, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(predecessor.OwnerTaskArn, lease.PreviousOwner?.OwnerTaskArn);
            Assert.True(lease.PreviousOwner?.CleanRelease);
            Assert.True(proof.TryGetUpperBound(clock.Now, lease.ValidUntil, Reserve, out _));
            recoveries++;
            return Task.FromResult(true);
        }

        var adapter = new ControllerAuthorityAdapter(configuration, store, observer, clock, LeaseDuration, Reserve);
        using var ownership = await adapter.TryActivateAsync(Observation(clock), Recover, cancellation);
        if (cleanRelease)
        {
            Assert.Equal(ControllerLeaseMode.Active, ownership?.ReadAuthority()?.Lease.Mode);
            Assert.Equal(1, recoveries);
        }
        else
        {
            Assert.Null(ownership);
            var held = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(deployment, cancellation));
            Assert.Equal(ControllerLeaseMode.RecoveryOnly, held.Mode);
            Assert.Equal(predecessorTask, held.RequiredFencingTaskArn);
            clock.Now = held.ValidUntil.AddSeconds(2);
            var restarted = new ControllerAuthorityAdapter(configuration, store, observer, clock, LeaseDuration, Reserve);
            var replacement = Observation(clock) with { TaskArn = NewTask[..^1] + "e" };
            Assert.Null(await restarted.TryActivateAsync(replacement, UnexpectedRecovery, cancellation));
            var next = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(deployment, cancellation));
            Assert.Equal((held.Epoch + 1, replacement.TaskArn, predecessorTask, ControllerLeaseMode.RecoveryOnly),
                (next.Epoch, next.OwnerTaskArn, next.RequiredFencingTaskArn, next.Mode));
            Assert.Equal(0, recoveries);
        }
        Assert.DoesNotContain(predecessorTask, observer.Reads);
    }

    [Fact]
    public async Task LostAcquisitionReplyUsesExactOwnedTransitionAndExpiredClockCannotPromote()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var configuration = await LoadConfigurationAsync(cancellation);
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var observer = new TaskObserver();
        var adapter = new ControllerAuthorityAdapter(configuration, store, observer, clock, LeaseDuration, Reserve);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);

        table.Wire.DropNextSuccessfulTransactionResponse();
        ControllerLease? recovery = null;
        var proof = Observation(clock);
        Assert.Null(await adapter.TryActivateAsync(proof, (lease, evidence, _) =>
        {
            recovery = lease;
            clock.Now = evidence.ValidUntil;
            return Task.FromResult(true);
        }, cancellation));
        Assert.NotNull(recovery);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, recovery.Mode);
        Assert.Equal(recovery, await store.ReadLeaseAsync(deployment, cancellation));
        Assert.Empty(observer.Reads);

        using var active = Assert.IsType<ControllerLeaseOwnership>(await adapter.TryActivateAsync(Observation(clock),
            (lease, _, _) =>
            {
                Assert.Equal(recovery.Fence, lease.Fence);
                return Task.FromResult(true);
            }, cancellation));
        Assert.Equal(recovery.Fence, active.ReadAuthority()?.Lease.Fence);
    }

    [Fact]
    public async Task UnboundOrExpiredEvidenceDoesNotReadOrWriteTheLease()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new FixtureClock();
        var configuration = await LoadConfigurationAsync(cancellation);
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var adapter = new ControllerAuthorityAdapter(configuration, store, new TaskObserver(), clock, LeaseDuration, Reserve);
        int requests = table.Wire.Requests.Count;
        var observation = Observation(clock);

        Assert.Null(await adapter.TryActivateAsync(observation with { TaskArn = MiniStackTable.FirstTask }, UnexpectedRecovery, cancellation));
        Assert.Null(await adapter.TryActivateAsync(observation with { ClusterArn = ClusterArn + "-other" }, UnexpectedRecovery, cancellation));
        clock.Now = observation.Clock.ValidUntil;
        Assert.Null(await adapter.TryActivateAsync(observation, UnexpectedRecovery, cancellation));
        Assert.Equal(requests, table.Wire.Requests.Count);
    }

    private static ControllerTaskAuthority Observation(FixtureClock clock) =>
        new(NewTask, ClusterArn, new ControllerClockEvidence(clock.Now, clock.Now.AddSeconds(15), TimeSpan.FromSeconds(1)));

    private static async Task<RuntimeConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken) =>
        RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken));

    private static Task<bool> UnexpectedRecovery(ControllerLease lease, ControllerClockEvidence clock, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unverified authority must not run recovery.");

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = MiniStackTable.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TaskObserver : IControllerTaskFenceObserver
    {
        internal List<string> Reads { get; } = [];

        internal ControllerTaskFenceStatus Status { get; set; }

        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(taskArn);
            return Task.FromResult(Status);
        }
    }
}
