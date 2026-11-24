using System.Text.Json;
using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class CatalogRolloutTests
{
    private const string ProfileId = "linux-shell";
    private const string SecondTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";

    [Fact]
    public async Task RolloutStagesMoreThanOneTransactionAndResumesLostResponseBeforePublishing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var configuration = await ConfigurationAsync("large-rollout", cancellation, profile =>
        {
            var labels = profile["github"]!["labels"]!.AsArray();
            foreach (int i in Enumerable.Range(0, 130))
            {
                labels.Add(FormattableString.Invariant($"managed-{i:D3}"));
            }
        });
        var lease = await AcquireAsync(store, configuration, MiniStackTable.FirstTask, cancellation);
        var profile = new LifecycleProfile(lease.Deployment, ProfileId);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.EnsureEffectProfileAsync(lease, profile, cancellation));

        var begun = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, 1, cancellation);
        Assert.Equal(CatalogRolloutStatus.Staging, begun.Status);
        Assert.Equal(134, begun.Head?.Pending?.ActionCount);
        Assert.Equal(0, begun.Head?.AppliedGeneration);
        Assert.Null(await store.ReadCatalogProfileAsync(profile, cancellation));

        table.Wire.Clear();
        table.Wire.DropNextSuccessfulTransactionResponse();
        var lostResponse = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, 1, cancellation);
        Assert.Equal(CatalogRolloutStatus.Staging, lostResponse.Status);
        Assert.Equal(64, lostResponse.Head?.Pending?.NextAction);
        Assert.Null(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(lostResponse.Head, await table.CreateStore().ReadCatalogRolloutAsync(lease.Deployment, cancellation));

        var final = await ApplyAsync(table.CreateStore(), lease.Fence, configuration, 1, cancellation);
        Assert.Equal(1, final.AppliedGeneration);
        Assert.Equal(configuration.Sha256, final.AppliedDigest);
        Assert.Null(final.Pending);
        var visible = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(CatalogProfileState.Active, visible.State);
        Assert.Equal(132, visible.Binding.Labels.Length);
        Assert.Equal([profile], await store.ReadEffectProfilesAsync(profile.Deployment, cancellation));

        var transactions = table.Wire.Requests.Where(call => call.Operation == "TransactWriteItems").ToArray();
        Assert.True(transactions.Length >= 4);
        Assert.All(transactions, transaction =>
            Assert.InRange(transaction.Request.GetProperty("TransactItems").GetArrayLength(), 1, 65));
    }

    [Fact]
    public async Task RolloutFencesGenerationsAndKeepsResolvedRemoteBindingImmutableAcrossRenewal()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var configuration = await ConfigurationAsync("catalog-generations", cancellation);
        var changed = await ConfigurationAsync("catalog-generations", cancellation, profile =>
        {
            profile["capacity"]!["maxRunners"] = 25;
            profile["github"]!["runnerGroup"] = "NO-DOCKER";
            profile["github"]!["scaleSet"] = "LINUX-SHELL";
            profile["github"]!["labels"] = new JsonArray("SHELL-X64", "LINUX-SHELL");
        });
        var incompatible = await ConfigurationAsync("catalog-generations", cancellation, profile =>
            profile["provisioner"]!["taskDefinitionArn"] = "arn:aws:ecs:ap-southeast-2:111122223333:task-definition/linux-shell-runner:8");
        var first = await AcquireAsync(store, configuration, MiniStackTable.FirstTask, cancellation);
        var key = new LifecycleProfile(first.Deployment, ProfileId);
        var applied = await ApplyAsync(store, first.Fence, configuration, 4, cancellation);
        Assert.Equal(applied, (await store.AdvanceCatalogRolloutAsync(first.Fence, configuration, 4, cancellation)).Head);
        Assert.Equal(CatalogRolloutStatus.StaleGeneration, (await store.AdvanceCatalogRolloutAsync(first.Fence, configuration, 3, cancellation)).Status);
        Assert.Equal(CatalogRolloutStatus.StaleGeneration, (await store.AdvanceCatalogRolloutAsync(first.Fence, changed, 4, cancellation)).Status);
        Assert.Equal(CatalogRolloutStatus.LeaseRejected, (await store.AdvanceCatalogRolloutAsync(first.Fence, changed, 5, cancellation)).Status);

        var unbound = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, cancellation));
        Assert.Null(unbound.Binding.ScaleSetId);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(first.Fence, unbound, 5, 7_000_000_009, cancellation));
        var bound = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, cancellation));
        Assert.Equal(5, bound.Binding.RunnerGroupId);
        Assert.Equal(7_000_000_009, bound.Binding.ScaleSetId);
        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.BindCatalogScaleSetAsync(first.Fence, bound, 5, 7_000_000_010, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.BindCatalogScaleSetAsync(first.Fence, bound, 6, 7_000_000_009, cancellation));
        var renewal = await store.RenewLeaseAsync(first, MiniStackTable.Now, first.ValidUntil.AddMinutes(10), cancellation);
        var renewed = Assert.IsType<ControllerLease>(renewal.Lease);
        Assert.Equal(applied, await store.ReadCatalogRolloutAsync(first.Deployment, cancellation));

        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.ReleaseLeaseAsync(renewed, MiniStackTable.Now, cancellation)).Outcome);
        var next = await AcquireAsync(store, configuration, SecondTask, cancellation);
        Assert.Equal(CatalogRolloutStatus.BindingConflict, (await store.AdvanceCatalogRolloutAsync(next.Fence, incompatible, 5, cancellation)).Status);
        var nextHead = await ApplyAsync(store, next.Fence, changed, 5, cancellation);
        Assert.Equal(next.Epoch, nextHead.AppliedEpoch);
        var stable = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, cancellation));
        Assert.Equal(7_000_000_009, stable.Binding.ScaleSetId);
        Assert.Equal(configuration.Profiles[0].Provisioner.BootstrapRoleArn, stable.Binding.BootstrapRoleArn);
        Assert.Equal(5, stable.AppliedGeneration);
        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.WriteRecordsAsync(next.Fence, Guid.NewGuid(),
            [new(LifecycleKey.Profile(key), stable.Revision, "{}")], cancellation));
    }

    [Fact]
    public async Task LegacySessionRetainsItsExactNumericalBindingWhenTheCatalogIsFirstApplied()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var configuration = await ConfigurationAsync("catalog-legacy-session", cancellation);
        var conflictingPin = await ConfigurationAsync("catalog-legacy-session", cancellation, profile =>
        {
            profile["github"]!["registration"] = "existing";
            profile["github"]!["scaleSetId"] = "7000000010";
        });
        var lease = await AcquireAsync(store, configuration, MiniStackTable.FirstTask, cancellation);
        var profile = new LifecycleProfile(lease.Deployment, ProfileId);
        var binding = new SessionBinding(profile, "example.ghe.com/engineering/tools", 7_000_000_009);
        await store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellation);
        var now = MiniStackTable.Now;
        var opening = await store.BeginSessionOpenAsync(binding, lease.Fence, null, "fixture-owner", now,
            now.AddMinutes(1), cancellation);
        var permit = Assert.IsType<SessionSendAuthorization>(opening.Permission);
        Assert.True(permit.TryConsume(out _));
        var session = (await store.ConfirmSessionOpenedAsync(permit, Guid.NewGuid(), cancellation)).Session;
        Assert.NotNull(session);

        Assert.Equal(CatalogRolloutStatus.BindingConflict,
            (await store.AdvanceCatalogRolloutAsync(lease.Fence, conflictingPin, 1, cancellation)).Status);
        Assert.Null(await store.ReadCatalogRolloutAsync(lease.Deployment, cancellation));
        await ApplyAsync(store, lease.Fence, configuration, 1, cancellation);
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(binding.ScopeIdentity, catalog.Binding.ScopeIdentity);
        Assert.Equal(binding.ScaleSetId, catalog.Binding.ScaleSetId);
        Assert.Equal(session, await store.ReadSessionAsync(profile, cancellation));
    }

    [Fact]
    public async Task AbortingCollisionReleasesOnlyUnpublishedClaimsAndLeavesTheWinningProfileIntact()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var large = await ConfigurationAsync("catalog-abort", cancellation, profile =>
        {
            foreach (int i in Enumerable.Range(0, 80))
            {
                profile["github"]!["labels"]!.AsArray().Add(FormattableString.Invariant($"managed-{i:D3}"));
            }
        });
        var winner = await ConfigurationAsync("catalog-winner", cancellation, profile =>
        {
            profile["github"]!["scaleSet"] = "winner";
            profile["github"]!["labels"] = new JsonArray("winner", "MANAGED-079");
        });
        var reuse = await ConfigurationAsync("catalog-reuse", cancellation, profile =>
        {
            profile["github"]!["scaleSet"] = "reuse";
            profile["github"]!["labels"] = new JsonArray("reuse", "managed-000");
        });
        var first = await AcquireAsync(store, large, MiniStackTable.FirstTask, cancellation);
        Assert.Equal(CatalogRolloutStatus.Staging, (await store.AdvanceCatalogRolloutAsync(first.Fence, large, 1, cancellation)).Status);
        Assert.Equal(64, (await store.AdvanceCatalogRolloutAsync(first.Fence, large, 1, cancellation)).Head?.Pending?.NextAction);
        var second = await AcquireAsync(store, winner, SecondTask, cancellation);
        Assert.Equal(1, (await ApplyAsync(store, second.Fence, winner, 1, cancellation)).AppliedGeneration);
        var collision = await store.AdvanceCatalogRolloutAsync(first.Fence, large, 1, cancellation);
        Assert.Equal(CatalogRolloutStatus.BindingConflict, collision.Status);
        Assert.Equal(64, collision.Head?.Pending?.NextAction);
        Assert.Null(await store.ReadCatalogProfileAsync(new LifecycleProfile(first.Deployment, ProfileId), cancellation));

        for (int i = 0; i < 3; i++)
        {
            var canceled = await store.AbortCatalogRolloutAsync(first.Fence, large, 1, cancellation);
            Assert.Equal(i == 2 ? CatalogRolloutStatus.Aborted : CatalogRolloutStatus.Staging, canceled.Status);
        }
        Assert.Null((await store.ReadCatalogRolloutAsync(first.Deployment, cancellation))?.Pending);
        var winningProfile = Assert.IsType<CatalogProfile>(
            await store.ReadCatalogProfileAsync(new LifecycleProfile(second.Deployment, ProfileId), cancellation));
        Assert.Equal("winner", winningProfile.Binding.ScaleSet);
        var reuseLease = await AcquireAsync(store, reuse, MiniStackTable.FirstTask, cancellation);
        Assert.Equal(1, (await ApplyAsync(store, reuseLease.Fence, reuse, 1, cancellation)).AppliedGeneration);
    }

    [Fact]
    public async Task ClosureAndTombstoneUseExactEvidenceRevisionsAndDoNotReleaseClaims()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var desired = await ConfigurationAsync("catalog-retirement", cancellation, profile => profile["state"] = "draining");
        var lease = await AcquireAsync(store, desired, MiniStackTable.FirstTask, cancellation);
        await ApplyAsync(store, lease.Fence, desired, 1, cancellation);
        var profile = new LifecycleProfile(lease.Deployment, ProfileId);
        var original = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        var effects = await store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellation);
        Assert.Equal(0, effects.Head?.UnfinishedCount);

        var demand = LifecycleKey.Demand(profile);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.WriteRecordsAsync(lease.Fence, Guid.NewGuid(), [new(demand, null, "{}")], cancellation));
        Assert.Equal(LifecycleWriteOutcome.Conflict,
            await store.TryHoldCatalogClosureAsync(lease.Fence, original, [new(demand, null)], cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.TryHoldCatalogClosureAsync(lease.Fence, original, [new(demand, 1)], cancellation));
        var held = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(MiniStackTable.Now, held.ClosureHeldAt);
        Assert.Equal(CatalogProfileState.ClosureHeld, held.State);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.TryTombstoneAsync(lease.Fence, held, [new(demand, 1)], cancellation));
        var retired = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(CatalogProfileState.Tombstoned, retired.State);
        Assert.Equal(held.Binding.ProviderDefinition, retired.Binding.ProviderDefinition);

        var foreign = await ConfigurationAsync("catalog-retirement-collision", cancellation);
        var foreignLease = await AcquireAsync(store, foreign, SecondTask, cancellation);
        Assert.Equal(CatalogRolloutStatus.BindingConflict,
            (await store.AdvanceCatalogRolloutAsync(foreignLease.Fence, foreign, 1, cancellation)).Status);
        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.ReleaseLeaseAsync(lease, MiniStackTable.Now, cancellation)).Outcome);
        var successor = await AcquireAsync(store, desired, SecondTask, cancellation);
        Assert.Equal(CatalogRolloutStatus.BindingConflict,
            (await store.AdvanceCatalogRolloutAsync(successor.Fence, desired, 2, cancellation)).Status);
    }

    [Fact]
    public async Task ClosureHoldRejectsRacingRotationAndRequiresCurrentDrainProofToClose()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var store = table.CreateStore();
        var desired = await ConfigurationAsync("catalog-retirement-session", cancellation, profile => profile["state"] = "draining");
        var lease = await AcquireAsync(store, desired, MiniStackTable.FirstTask, cancellation);
        await ApplyAsync(store, lease.Fence, desired, 1, cancellation);
        var profile = new LifecycleProfile(lease.Deployment, ProfileId);
        var unbound = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(lease.Fence, unbound, 42, 84, cancellation));
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        var index = await store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellation);
        var effects = Assert.IsType<EffectIndexHead>(index.Head);
        var now = MiniStackTable.Now;
        var binding = new SessionBinding(profile, catalog.Binding.ScopeIdentity, 84);
        var begin = await store.BeginSessionOpenAsync(binding, lease.Fence, null, "fixture-session", now, now.AddMinutes(1), cancellation);
        var opening = Assert.IsType<SessionSendAuthorization>(begin.Permission);
        Assert.True(opening.TryConsume(out _));
        var id = Guid.NewGuid();
        var opened = await store.ConfirmSessionOpenedAsync(opening, id, cancellation);
        var session = Assert.IsType<SessionRecord>(opened.Session);
        var owned = Assert.IsType<OwnedSessionFence>(session.Fence);
        var checkpoint = LifecycleKey.Checkpoint(profile, id);
        var demand = LifecycleKey.Demand(profile);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteSessionRecordsAsync(owned, Guid.NewGuid(),
            [new(checkpoint, null, "{\"pending\":false}"), new(demand, null, "{\"totalAssignedJobs\":0}")], cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, lease.Fence, null,
            new ExecutorInventoryObservation(now, IsComplete: true), cancellation));
        var guards = new LifecycleRecordCondition[]
        {
            new(LifecycleKey.Session(profile), session.Revision),
            new(LifecycleKey.Effects(profile), effects.Revision),
            new(LifecycleKey.RunnerInventory(profile), 1),
            new(LifecycleKey.Admission(profile), null),
            new(checkpoint, 1),
            new(demand, 1),
        };

        var pause = table.Wire.PauseNextRead(LifecycleKey.Profile(profile));
        var racingRotation = store.BeginSessionCloseAsync(session, lease.Fence, SessionCloseKind.OwnedClient,
            now, now.AddMinutes(1), cancellation);
        try
        {
            await pause.Captured.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.TryHoldCatalogClosureAsync(lease.Fence, catalog, guards, cancellation));
        }
        finally
        {
            pause.Resume();
        }
        var raced = await racingRotation;
        Assert.Equal(LifecycleWriteOutcome.Conflict, raced.Outcome);
        Assert.Null(raced.Permission);
        var ordinary = await store.BeginSessionCloseAsync(session, lease.Fence, SessionCloseKind.OwnedClient, now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, ordinary.Outcome);
        Assert.Null(ordinary.Permission);

        var held = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellation));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteSessionRecordsAsync(owned, Guid.NewGuid(),
            [new(checkpoint, 1, "{\"pending\":true}")], cancellation));
        var staleProof = await store.BeginProfileRetirementSessionCloseAsync(session, lease.Fence, held, guards, now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Conflict, staleProof.Outcome);
        Assert.Null(staleProof.Permission);
        Assert.Equal(session, await store.ReadSessionAsync(profile, cancellation));

        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteSessionRecordsAsync(owned, Guid.NewGuid(),
            [new(checkpoint, 2, "{\"pending\":false}")], cancellation));
        var currentProof = guards.Select(guard => guard.Key == checkpoint ? guard with { ExpectedRevision = 3 } : guard).ToArray();
        var close = await store.BeginProfileRetirementSessionCloseAsync(session, lease.Fence, held, currentProof, now, now.AddMinutes(1), cancellation);
        Assert.Equal(LifecycleWriteOutcome.Committed, close.Outcome);
        Assert.Equal(SessionCloseKind.OwnedClient, close.Session?.CloseKind);
        Assert.Equal(id, close.Session?.SessionId);
        Assert.NotNull(close.Permission);
        Assert.Equal(LifecycleWriteOutcome.LeaseRejected, await store.WriteSessionRecordsAsync(owned, Guid.NewGuid(),
            [new(checkpoint, 3, "{\"pending\":true}")], cancellation));
    }

    [Fact]
    public async Task CatalogRevisionChangeFencesAnInFlightAcquisition()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var active = await ConfigurationAsync("catalog-retirement-admission", cancellation, PinListenerScaleSet);
        var (catalog, owned) = await OpenCatalogListenerAsync(store, active, clock.Now, cancellation);
        var profile = catalog.Profile;
        var session = new ListenerDeliveryTests.TestSession(owned.Identity.SessionId);
        var handler = new GitHubListenerDeliveryHandler(store, owned, session, _ => Task.FromResult(new ListenerCapacity(5, 0)), clock);
        await ListenerDeliveryTests.InitializeAsync(handler, owned, clock.Now, 0, cancellation);
        Assert.Equal(5, await handler.ReadPollCapacityAsync(cancellation));

        const long requestId = 1;
        var acquisition = LifecycleKey.Acquisition(profile, requestId);
        var delivery = JobOffer(owned, clock.Now, requestId);
        var pause = table.Wire.PauseNextTransactionWrite(acquisition);
        var reserving = handler.HandleAsync(delivery, cancellation);
        try
        {
            await pause.Captured.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            Assert.Null(catalog.Binding.RunnerGroupId);
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(owned.Lease, catalog, 42, 84, cancellation));
        }
        finally
        {
            pause.Resume();
        }

        Assert.Equal(ScaleSetListenerDeliveryDisposition.Deferred, await reserving);
        Assert.Empty(session.Acquisitions);
        Assert.Null(await store.ReadRecordAsync(acquisition, cancellation));
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Admission(profile), cancellation));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(delivery, cancellation));
        Assert.Equal(requestId, Assert.Single(Assert.Single(session.Acquisitions)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainingOrHeldCatalogAdvertisesZeroAndProcessesOffersWithoutAcquisition(bool held)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var draining = await ConfigurationAsync("catalog-retirement-drain", cancellation, profile =>
        {
            profile["state"] = "draining";
            PinListenerScaleSet(profile);
        });
        var (catalog, owned) = await OpenCatalogListenerAsync(store, draining, clock.Now, cancellation);
        var profile = catalog.Profile;
        var admission = LifecycleKey.Admission(profile);
        if (held)
        {
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.TryHoldCatalogClosureAsync(owned.Lease, catalog,
                [new(admission, null)], cancellation));
        }
        var session = new ListenerDeliveryTests.TestSession(owned.Identity.SessionId);
        var handler = new GitHubListenerDeliveryHandler(store, owned, session, _ => Task.FromResult(new ListenerCapacity(5, 0)), clock);
        await ListenerDeliveryTests.InitializeAsync(handler, owned, clock.Now, 0, cancellation);
        const long requestId = 1;
        var acquisition = LifecycleKey.Acquisition(profile, requestId);
        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.WriteSessionRecordsAsync(owned, Guid.NewGuid(),
            [new(acquisition, null, "{}")], cancellation));
        Assert.Equal(0, await handler.ReadPollCapacityAsync(cancellation));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(JobOffer(owned, clock.Now, requestId), cancellation));
        Assert.Empty(session.Acquisitions);
        Assert.Null(await store.ReadRecordAsync(acquisition, cancellation));
        Assert.Null(await store.ReadRecordAsync(admission, cancellation));
    }

    private static ScaleSetListenerDelivery JobOffer(OwnedSessionFence owned, DateTimeOffset now, long requestId) =>
        ListenerDeliveryTests.Delivery(owned, now, new RunnerScaleSetMessage
        {
            MessageId = 1,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 0 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = requestId }],
        });

    private static void PinListenerScaleSet(JsonNode profile)
    {
        var routing = profile["github"]!;
        routing["registration"] = "existing";
        routing["scaleSetId"] = "84";
    }

    private static async Task<(CatalogProfile Catalog, OwnedSessionFence Session)> OpenCatalogListenerAsync(
        DynamoDbLifecycleStore store, RuntimeConfiguration configuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lease = await AcquireAsync(store, configuration, MiniStackTable.FirstTask, cancellationToken);
        await ApplyAsync(store, lease.Fence, configuration, 1, cancellationToken);
        var profile = new LifecycleProfile(lease.Deployment, ProfileId);
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellationToken));
        var rebuilt = await store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellationToken);
        Assert.Equal(EffectIndexState.Ready, rebuilt.Head?.State);
        var binding = new SessionBinding(profile, catalog.Binding.ScopeIdentity, 84);
        var begin = await store.BeginSessionOpenAsync(binding, lease.Fence, null, "fixture-session", now, now.AddMinutes(1), cancellationToken);
        var opening = Assert.IsType<SessionSendAuthorization>(begin.Permission);
        Assert.True(opening.TryConsume(out _));
        var opened = await store.ConfirmSessionOpenedAsync(opening, Guid.NewGuid(), cancellationToken);
        return (catalog, Assert.IsType<OwnedSessionFence>(opened.Session?.Fence));
    }

    private static async Task<RuntimeConfiguration> ConfigurationAsync(
        string environment, CancellationToken cancellationToken, Action<JsonNode>? change = null)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fargate-only.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(fixture, cancellationToken))!;
        root["metadata"]!["name"] = environment;
        change?.Invoke(root["spec"]!["profiles"]![0]!);
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static async Task<ControllerLease> AcquireAsync(
        DynamoDbLifecycleStore store, RuntimeConfiguration configuration, string ownerTask, CancellationToken cancellationToken)
    {
        var now = MiniStackTable.Now;
        var acquired = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), Guid.NewGuid().ToString("D"),
            ownerTask, ControllerLease.CurrentWriterProtocol, now, now.AddHours(1), cancellationToken);
        var active = await store.PromoteLeaseAsync(Assert.IsType<ControllerLease>(acquired.Lease), now, cancellationToken);
        return Assert.IsType<ControllerLease>(active.Lease);
    }

    private static async Task<CatalogRolloutHead> ApplyAsync(
        DynamoDbLifecycleStore store, DeploymentLeaseFence lease, RuntimeConfiguration configuration, long generation, CancellationToken cancellationToken)
    {
        for (int pass = 0; pass < 10; pass++)
        {
            var change = await store.AdvanceCatalogRolloutAsync(lease, configuration, generation, cancellationToken);
            if (change.Status == CatalogRolloutStatus.Applied)
            {
                return Assert.IsType<CatalogRolloutHead>(change.Head);
            }
            Assert.Equal(CatalogRolloutStatus.Staging, change.Status);
        }
        throw new InvalidOperationException("The expected catalog rollout did not finish.");
    }
}
