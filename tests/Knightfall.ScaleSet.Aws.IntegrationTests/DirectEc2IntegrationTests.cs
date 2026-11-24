using Amazon.EC2.Model;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class DirectEc2IntegrationTests
{
    private const string Account = "111122223333";
    private const string Region = "ap-southeast-2";
    private const string FirstTask = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-dev-controller/00000000000000000000000000000001";
    private const string SecondTask = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-dev-controller/00000000000000000000000000000002";
    private static readonly TimeSpan WriteReserve = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OwnerDuration = TimeSpan.FromMinutes(2);
    private static readonly EffectDispatcherTiming Timing = new(TimeSpan.FromSeconds(20), WriteReserve,
        TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task NativeAliasDriftFreezesNewLaunchesAndRetainsTheAlternateZoneInstanceAcrossReplacement()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        using var cloud = await MiniStackIamFixture.CreateAsync(token);
        var role = await cloud.EnsureRunnerRoleAsync(DirectEc2Fixture.RoleName(), includeStatus: true, token);
        using var fixture = await DirectEc2Fixture.CreateAsync(cloud.Endpoint, cloud.Controller.Credentials,
            cloud.Controller.Ec2, cloud.Controller.Iam, Account, Region, role.Arn, token);
        var clock = new FixtureClock();
        var configuration = await fixture.ConfigurationAsync(cloud.TableName, "$Latest", token);
        using var inspector = fixture.Inspector(clock);
        foreach (string selector in new[] { "1", "$Default", "$Latest" })
        {
            var selected = await fixture.ConfigurationAsync(cloud.TableName, selector, token);
            var preflight = await inspector.ResolvePreflightAsync(selected, DirectEc2Fixture.ProfileId, token);
            Assert.Equal(Ec2PreflightStatus.Verified, preflight.Status);
            Assert.Equal(1, preflight.ObservedVersionNumber);
            Assert.Equal(role.RoleId, preflight.Template?.BootstrapRoleId);
            Assert.Equal(new[] { fixture.FirstSubnet, fixture.SecondSubnet }, preflight.Template?.Subnets.Select(subnet => subnet.SubnetId));
        }

        var store = new DynamoDbLifecycleStore(cloud.Controller.DynamoDb, cloud.TableName, clock);
        var owner = await ActivateInitialAsync(store, configuration, clock, token);
        var profile = Assert.Single(configuration.Profiles);
        var key = new LifecycleProfile(owner.Lease.Deployment, profile.Id);
        var catalog = await BindCatalogAsync(store, key, owner.Lease, token);
        using var provider = fixture.Provider(configuration, key, clock);
        var backend = Backend(configuration, profile, key, inspector, provider, store, clock);
        await ReconcileAsync(backend, owner, catalog, token);
        var rejection = Assert.Single(Assert.IsType<RejectedEc2LaunchPage>(await store.ReadRejectedEc2LaunchesAsync(
            key, catalog.AppliedGeneration, null, token)).Launches);
        Assert.Equal(Region + "a", rejection.AvailabilityZone);
        Assert.Equal(0, rejection.AttemptOrdinal);

        await ReconcileAsync(backend, owner, catalog, token);
        var lineage = Assert.IsType<Ec2LaunchLineageSnapshot>(await store.ReadEc2LaunchLineageAsync(key, rejection.AllocationId, token));
        Assert.Equal(1, lineage.AttemptOrdinal);
        Assert.Equal(new[] { Region + "a", Region + "b" }, lineage.TriedAvailabilityZones);
        Assert.NotEqual(lineage.ClientTokens[0], lineage.ClientTokens[1]);
        Assert.Collection(fixture.Wire.Calls("RunInstances"),
            first => Assert.Equal((fixture.FirstSubnet, lineage.ClientTokens[0].ToString("D"), "1"),
                (first["SubnetId"], first["ClientToken"], first["LaunchTemplate.Version"])),
            second => Assert.Equal((fixture.SecondSubnet, lineage.ClientTokens[1].ToString("D"), "1"),
                (second["SubnetId"], second["ClientToken"], second["LaunchTemplate.Version"])));

        var executor = Assert.IsType<ExecutorSnapshot>(await store.ReadExecutorAsync(key, rejection.AllocationId.ToString("D"), token));
        var provenance = Assert.IsType<Ec2ExecutorIdentity>(executor.Binding.Provider);
        Assert.Equal((fixture.TemplateId, 1L), (provenance.LaunchTemplateId, provenance.LaunchTemplateVersion));
        Assert.Equal(ExecutorResourceState.Running, executor.State.Resource?.State);
        string instanceId = InstanceId(executor.Binding);
        var native = Assert.Single((await cloud.Controller.Ec2.DescribeInstancesAsync(
            new DescribeInstancesRequest { InstanceIds = [instanceId] }, token)).Reservations.SelectMany(item => item.Instances));
        Assert.Equal((instanceId, fixture.SecondSubnet, Region + "b", "running"),
            (native.InstanceId, native.SubnetId, native.Placement.AvailabilityZone, native.State.Name.Value));

        Assert.Equal(2, await fixture.AddRevisionAsync(cloud.Controller.Ec2, requireImdsV2: false, token));
        var invalid = await inspector.ResolvePreflightAsync(configuration, key.ProfileId, token);
        Assert.Equal((Ec2PreflightStatus.ImdsV2Required, 2L), (invalid.Status, invalid.ObservedVersionNumber));
        foreach (string selector in new[] { "1", "$Default" })
        {
            var unchanged = await fixture.ConfigurationAsync(cloud.TableName, selector, token);
            var preflight = await inspector.ResolvePreflightAsync(unchanged, key.ProfileId, token);
            Assert.Equal((Ec2PreflightStatus.Verified, 1L), (preflight.Status, preflight.ObservedVersionNumber));
        }
        var frozen = await backend.ReadAsync(owner.Lease, token);
        Assert.True(frozen.IsComplete);
        Assert.Null(frozen.LaunchHeadroom);
        Assert.Equal(executor.Binding, Assert.Single(frozen.Resources).Binding);
        await ReconcileAsync(backend, owner, catalog, token);
        Assert.Equal(2, fixture.Wire.Calls("RunInstances").Length);

        var replacementStore = new DynamoDbLifecycleStore(cloud.Controller.DynamoDb, cloud.TableName, clock);
        var successor = await ReplaceAsync(replacementStore, configuration, owner.Lease, clock, token);
        using var replacementInspector = fixture.Inspector(clock);
        using var replacementProvider = fixture.Provider(configuration, key, clock);
        var replacement = Backend(configuration, profile, key, replacementInspector, replacementProvider, replacementStore, clock);
        var surviving = Assert.Single((await replacement.ReadAsync(successor.Lease, token)).Resources);
        Assert.Equal(executor.Binding, surviving.Binding);
        Assert.Equal(ExecutorResourceState.Running, surviving.Observation?.State);
        Assert.Equal(executor, await replacementStore.ReadExecutorAsync(key, executor.Binding.AllocationId, token));
        await ReconcileAsync(replacement, successor, catalog, token);
        Assert.Equal(2, fixture.Wire.Calls("RunInstances").Length);
        Assert.Empty(fixture.Wire.Calls("TerminateInstances"));
    }

    private static Task ReconcileAsync(Ec2RunnerBackend backend, ActiveControllerAuthority authority, CatalogProfile catalog,
        CancellationToken cancellationToken) => backend.ReconcileLaunchesAsync(authority, catalog,
            allowPreparedLaunches: true, allowNewLaunch: true, CancellationToken.None, cancellationToken);

    private static Ec2RunnerBackend Backend(RuntimeConfiguration configuration, RunnerProfile profile, LifecycleProfile key,
        Ec2ProfileInspector inspector, Ec2EffectProvider provider, DynamoDbLifecycleStore store, FixtureClock clock) =>
        new(configuration, profile, key, inspector, provider, store, new DynamoDbEffectJournal(store, clock),
            new DynamoDbEffectDispatcher(store, clock, Timing), clock, Timing);

    private static async Task<CatalogProfile> BindCatalogAsync(
        DynamoDbLifecycleStore store, LifecycleProfile key, ControllerLease owner, CancellationToken token)
    {
        var staged = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, token));
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.BindCatalogScaleSetAsync(owner.Fence, staged, GitHubSimulator.GroupId, GitHubSimulator.SetId, token));
        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.AdvanceEffectRebuildAsync(key, owner.Fence, token)).Outcome);
        return Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, token));
    }

    private static async Task<ActiveControllerAuthority> ActivateInitialAsync(
        DynamoDbLifecycleStore store, RuntimeConfiguration configuration, FixtureClock clock, CancellationToken token)
    {
        var recovery = new ControllerLeaseRecoveryCoordinator(store, new TaskFence(), clock, WriteReserve);
        var acquired = await AcquireAsync(store, configuration, FirstTask, clock, token);
        var proof = ClockProof(clock);
        var activated = await recovery.TryActivateAsync(acquired, configuration, proof, [], token);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        var lease = Assert.IsType<ControllerLease>(activated.Lease);
        for (int pass = 0; pass < 10; pass++)
        {
            var rollout = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, 1, token);
            if (rollout.Status == CatalogRolloutStatus.Applied)
            {
                return new ActiveControllerAuthority(lease, proof);
            }
            Assert.Equal(CatalogRolloutStatus.Staging, rollout.Status);
        }
        throw new InvalidOperationException("The EC2 fixture catalog did not publish.");
    }

    private static async Task<ActiveControllerAuthority> ReplaceAsync(DynamoDbLifecycleStore store,
        RuntimeConfiguration configuration, ControllerLease prior, FixtureClock clock, CancellationToken token)
    {
        clock.Set(prior.ValidUntil.AddSeconds(2));
        var model = new TaskFence();
        var recovery = new ControllerLeaseRecoveryCoordinator(store, model, clock, WriteReserve);
        var acquired = await AcquireAsync(store, configuration, SecondTask, clock, token);
        Assert.Equal(prior.OwnerTaskArn, acquired.RequiredFencingTaskArn);
        var proof = ClockProof(clock);
        var held = await recovery.TryActivateAsync(acquired, configuration, proof, [], token);
        Assert.Equal(LeaseActivationStatus.PriorTaskUnisolated, held.Status);
        model.Status = ControllerTaskFenceStatus.Stopped;
        var activated = await recovery.TryActivateAsync(acquired, configuration, proof, [], token);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        var lease = Assert.IsType<ControllerLease>(activated.Lease);
        Assert.Equal(prior.Epoch + 1, lease.Epoch);
        return new ActiveControllerAuthority(lease, proof);
    }

    private static async Task<ControllerLease> AcquireAsync(DynamoDbLifecycleStore store,
        RuntimeConfiguration configuration, string ownerTask, FixtureClock clock, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var acquired = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration),
            ownerTask, ownerTask, ControllerLease.CurrentWriterProtocol, now.AddSeconds(-1), now + OwnerDuration, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquired.Outcome);
        var lease = Assert.IsType<ControllerLease>(acquired.Lease);
        Assert.Equal(ControllerLeaseMode.RecoveryOnly, lease.Mode);
        return lease;
    }

    private static ControllerClockEvidence ClockProof(FixtureClock clock)
    {
        var now = clock.GetUtcNow();
        return new ControllerClockEvidence(now, now.AddMinutes(1), TimeSpan.FromSeconds(1));
    }

    private static string InstanceId(ExecutorBinding binding) => binding.ResourceArn[(binding.ResourceArn.LastIndexOf('/') + 1)..];

    private sealed class FixtureClock : TimeProvider
    {
        private long _offsetMilliseconds;

        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddMilliseconds(Interlocked.Read(ref _offsetMilliseconds));

        internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref _offsetMilliseconds,
            (long)(value - TimeProvider.System.GetUtcNow()).TotalMilliseconds);
    }

    private sealed class TaskFence : IControllerTaskFenceObserver
    {
        internal ControllerTaskFenceStatus Status { get; set; } = ControllerTaskFenceStatus.Running;

        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            Assert.Equal(FirstTask, taskArn);
            return Task.FromResult(Status);
        }
    }
}
