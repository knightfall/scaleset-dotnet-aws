using System.Globalization;
using System.Net;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Tests;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.Ec2InspectionFixture;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class Ec2RunnerBackendTests
{
    private static readonly EffectDispatcherTiming Timing = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task InventoryKeepsOlderAliasIdentityAndDoesNotAdoptUnjournaledResources()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var rig = await RuntimeRig.CreateAsync(table, token);
        using var wire = new Ec2InspectionFixture(clock: rig.Clock)
        {
            TemplateResponse = Template(version: 13),
            InventoryResponse = static _ => AwsWireTransport.Xml(InventoryPage(Instance(instanceProfileArn: ProfileArn))),
        };
        using var provider = wire.CreateProvider(rig.Configuration);
        var backend = new Ec2RunnerBackend(rig.Configuration, rig.Profile, rig.Catalog.Profile, wire.Inspector, provider,
            rig.Store, rig.Journal, rig.Dispatcher, rig.Clock, Timing);

        var snapshot = await backend.ReadAsync(rig.Authority.Lease, token);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(7, snapshot.LaunchHeadroom);
        Assert.Equal(1, snapshot.DriftCount);
        var resource = Assert.Single(snapshot.Resources);
        var binding = Assert.IsType<ExecutorBinding>(resource.Binding);
        var launch = DateTimeOffset.Parse(OriginalLaunch, CultureInfo.InvariantCulture);
        Assert.Equal(Ec2ExecutorIncarnation.Compute(Account, Region, Instance1, launch, 12), binding.Incarnation);
        Assert.Equal(new Ec2ExecutorIdentity(launch, TemplateId, 12), binding.Provider);
        Assert.Equal(ExecutorResourceState.Running, resource.Observation?.State);
        Assert.Equal(ProfileArn, resource.BootstrapCredentialArn);
        Assert.Null(await rig.Store.ReadExecutorAsync(rig.Catalog.Profile, binding.AllocationId, token));

        wire.InventoryResponse = static _ => AwsWireTransport.Xml(InventoryPage(Instance(originalLaunch: null, instanceProfileArn: ProfileArn)));
        var unknown = await backend.ReadAsync(rig.Authority.Lease, token);
        Assert.True(unknown.IsComplete);
        Assert.Null(unknown.DriftCount);
        Assert.Equal(new RunnerBackendResource(resource.ResourceArn, null, null, null), Assert.Single(unknown.Resources));

        wire.InventoryResponse = static _ => AwsWireTransport.Xml(InventoryPage(Instance(instanceProfileArn: RoleArn)));
        var mismatchedProfile = Assert.Single((await backend.ReadAsync(rig.Authority.Lease, token)).Resources);
        Assert.Equal(binding, mismatchedProfile.Binding);
        Assert.Null(mismatchedProfile.BootstrapCredentialArn);
    }

    [Fact]
    public async Task ReconstructedBackendFindsRejectedLineageAndNeverRepeatsAnAmbiguousSuccessor()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var rig = await RuntimeRig.CreateAsync(table, token);
        int sends = 0;
        using var wire = new Ec2InspectionFixture(clock: rig.Clock)
        {
            SubnetResponse = Subnets(secondAvailable: "5"),
            LaunchResponse = _ => ++sends == 1
                ? Error("InsufficientInstanceCapacity", HttpStatusCode.BadRequest)
                : Error("ServiceUnavailable", HttpStatusCode.ServiceUnavailable),
        };
        using var provider = wire.CreateProvider(rig.Configuration);

        Ec2RunnerBackend Reconstruct() => new(rig.Configuration, rig.Profile, rig.Catalog.Profile, wire.Inspector, provider,
            rig.Store, rig.Journal, rig.Dispatcher, rig.Clock, Timing);
        Task ReconcileAsync(Ec2RunnerBackend backend, bool allowNewLaunch, CatalogProfile catalog) =>
            backend.ReconcileLaunchesAsync(rig.Authority, catalog, allowPreparedLaunches: true, allowNewLaunch, CancellationToken.None, token);

        await ReconcileAsync(Reconstruct(), allowNewLaunch: true, rig.Catalog);
        Assert.Equal(1, sends);
        var rejected = Assert.IsType<RejectedEc2LaunchPage>(await rig.Store.ReadRejectedEc2LaunchesAsync(
            rig.Catalog.Profile, rig.Catalog.AppliedGeneration, null, token));
        var first = Assert.Single(rejected.Launches);
        Assert.Equal(0, first.AttemptOrdinal);
        Assert.Equal(Region + "a", first.AvailabilityZone);
        var otherGeneration = Assert.IsType<RejectedEc2LaunchPage>(await rig.Store.ReadRejectedEc2LaunchesAsync(
            rig.Catalog.Profile, rig.Catalog.AppliedGeneration + 1, null, token));
        Assert.Empty(otherGeneration.Launches);

        await ReconcileAsync(Reconstruct(), allowNewLaunch: false, rig.Catalog);
        await ReconcileAsync(Reconstruct(), allowNewLaunch: true, rig.Catalog with { State = CatalogProfileState.Draining });
        Assert.Equal(1, sends);

        await ReconcileAsync(Reconstruct(), allowNewLaunch: true, rig.Catalog);
        await ReconcileAsync(Reconstruct(), allowNewLaunch: true, rig.Catalog);

        Assert.Equal(2, sends);
        var lineage = Assert.IsType<Ec2LaunchLineageSnapshot>(await rig.Store.ReadEc2LaunchLineageAsync(rig.Catalog.Profile, first.AllocationId, token));
        Assert.Equal(1, lineage.AttemptOrdinal);
        Assert.Equal([Region + "a", Region + "b"], lineage.TriedAvailabilityZones);
        Assert.NotEqual(lineage.ClientTokens[0], lineage.ClientTokens[1]);
        Assert.Collection(wire.Requests.Where(request => request["Action"] == "RunInstances"),
            request => Assert.Equal((Subnet1, lineage.ClientTokens[0].ToString("D")), (request["SubnetId"], request["ClientToken"])),
            request => Assert.Equal((Subnet2, lineage.ClientTokens[1].ToString("D")), (request["SubnetId"], request["ClientToken"])));
        Assert.Null(await rig.Store.ReadExecutorAsync(rig.Catalog.Profile, first.AllocationId.ToString("D"), token));
        var journal = Assert.IsType<EffectJournalSnapshot>(await rig.Journal.ReadAsync(rig.Catalog.Profile, lineage.IntentId, token));
        Assert.Equal(EffectState.Executing, journal.Entry.State);
    }

    [Theory]
    [InlineData(ExecutorResourceState.Running)]
    [InlineData(ExecutorResourceState.Unknown)]
    public async Task CleanupLeavesAssignedAndUnknownExecutorsAlone(ExecutorResourceState resourceState)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var rig = await RuntimeRig.CreateAsync(table, token);
        using var wire = new Ec2InspectionFixture(clock: rig.Clock);
        using var provider = wire.CreateProvider(rig.Configuration);
        var backend = new Ec2RunnerBackend(rig.Configuration, rig.Profile, rig.Catalog.Profile, wire.Inspector, provider,
            rig.Store, rig.Journal, rig.Dispatcher, rig.Clock, Timing);
        var launch = rig.Clock.GetUtcNow().AddMinutes(-1);
        var allocation = Guid.NewGuid();
        var arn = $"arn:aws:ec2:{Region}:{Account}:instance/{Instance1}";
        var binding = new ExecutorBinding(rig.Catalog.Profile, allocation.ToString("D"), arn,
            Ec2ExecutorIncarnation.Compute(Account, Region, Instance1, launch, 12), new Ec2ExecutorIdentity(launch, TemplateId, 12));
        var state = new ExecutorState(binding, RunnerId: 18,
            Resource: new ExecutorResourceObservation(resourceState, rig.Clock.GetUtcNow(), launch, Guid.NewGuid()),
            Job: new ExecutorJobObservation(26, 18, ExecutorJobPhase.Assigned, Guid.NewGuid(), 1, rig.Clock.GetUtcNow()), StartedAt: launch);

        await backend.CleanupAsync(rig.Authority, rig.Catalog, new ExecutorSnapshot(state, 1, Guid.NewGuid()), CancellationToken.None, token);

        Assert.Empty(wire.Requests);
        var request = new Ec2TerminateEffectRequest(allocation, arn, launch, TemplateId, 12);
        Assert.Null(await rig.Journal.ReadAsync(rig.Catalog.Profile, EffectIntentIdentity.ForAllocation(request)!, token));
    }

    private static HttpResponseMessage Error(string code, HttpStatusCode status) => AwsWireTransport.Xml($"""
        <Response><Errors><Error><Code>{code}</Code><Message>synthetic failure</Message></Error></Errors>
        <RequestID>fixture-runtime-rejected</RequestID></Response>
        """, status);

    private sealed record RuntimeRig(RuntimeConfiguration Configuration, RunnerProfile Profile, CatalogProfile Catalog,
        ActiveControllerAuthority Authority, DynamoDbLifecycleStore Store, DynamoDbEffectJournal Journal, DynamoDbEffectDispatcher Dispatcher,
        TimeProvider Clock)
    {
        internal static async Task<RuntimeRig> CreateAsync(MiniStackTable table, CancellationToken cancellationToken)
        {
            var clock = new RuntimeClock();
            var now = clock.GetUtcNow();
            var configuration = await ConfigurationAsync(cancellationToken, active: true);
            var profile = Assert.Single(configuration.Profiles);
            var key = new LifecycleProfile(RunnerOwnershipTags.DeploymentIdentity(configuration), profile.Id);
            var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
            var fence = table.Lease with { Deployment = key.Deployment };
            await table.SetLeaseAsync(fence, now.AddMinutes(5), cancellationToken);
            var lease = Assert.IsType<ControllerLease>(await store.ReadLeaseAsync(key.Deployment, cancellationToken));
            CatalogRolloutChange rollout;
            do
            {
                rollout = await store.AdvanceCatalogRolloutAsync(fence, configuration, 1, cancellationToken);
            }
            while (rollout.Status == CatalogRolloutStatus.Staging);
            Assert.Equal(CatalogRolloutStatus.Applied, rollout.Status);
            var staged = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, cancellationToken));
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(fence, staged, 7, 35, cancellationToken));
            Assert.Equal(LifecycleWriteOutcome.Committed, (await store.AdvanceEffectRebuildAsync(key, fence, cancellationToken)).Outcome);
            var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, cancellationToken));
            return new RuntimeRig(configuration, profile, catalog,
                new ActiveControllerAuthority(lease, new ControllerClockEvidence(now, now.AddMinutes(2), TimeSpan.FromSeconds(3))), store,
                new DynamoDbEffectJournal(store, clock), new DynamoDbEffectDispatcher(store, clock, Timing), clock);
        }
    }

    private sealed class RuntimeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => MiniStackTable.Now;
    }
}
