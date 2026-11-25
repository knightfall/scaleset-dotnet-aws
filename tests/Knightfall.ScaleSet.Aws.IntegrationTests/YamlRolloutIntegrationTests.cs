using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

[Collection(nameof(GitHubLifecycleIntegrationTests))]
public sealed class YamlRolloutIntegrationTests
{
    private const string RetiringProfile = "linux-shell";
    private const string RetainedProfile = "linux-ec2-build";
    private const string Region = YamlRolloutArtifactSource.Region;
    private const string Account = YamlRolloutArtifactSource.Account;
    private static readonly TimeSpan Reserve = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendWindow = TimeSpan.FromSeconds(20);
    private static readonly ProfileRetirementTiming RetirementTiming = new(
        TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), Reserve);

    [Fact(Timeout = 75_000)]
    public async Task RetainedAlphaOneCatalogAllowsAliasRenameButBlocksHostingAndQualifiedStatusChanges()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var endpoint = new Uri(table.Client.Config.ServiceURL);
        using var sts = new AmazonSecurityTokenServiceClient(new BasicAWSCredentials(Account, "test-only-secret"),
            ControllerAwsClientConfiguration.Create<AmazonSecurityTokenServiceConfig>(Region, endpoint));
        using var source = await YamlRolloutArtifactSource.CreateAsync(endpoint, token);
        var deployed = (await source.PublishAsync(table.Name,
            $"arn:aws:secretsmanager:{Region}:{Account}:secret:github-fixture-a1b2c3", token))[0];
        var clock = new RolloutClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var lease = await AcquireAndApplyAsync(store, deployed.Configuration, 1, clock, token);
        var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(new(lease.Deployment, RetainedProfile), token));
        Assert.Equal(CatalogProfileState.Active, catalog.State);
        var provisioner = Assert.IsType<Ec2ProvisionerConfiguration>(Assert.Single(deployed.Configuration.Profiles).Provisioner);
        Assert.IsType<Ec2StatusQualification>(provisioner.StatusQualification);

        var original = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllBytesAsync(deployed.ReadbackPath, token)));
        (string Name, Action<JsonObject> Change, string? Refusal)[] variants =
        [
            ("same-cluster-alias", spec =>
            {
                var clusters = spec["ecsClusters"]!;
                clusters["renamed"] = clusters["control-plane"]!.DeepClone();
                spec["controller"]!["hosting"]!["cluster"] = "renamed";
            }, null),
            ("moved-hosting-cluster", spec => spec["controller"]!["hosting"]!["cluster"] = "runner-shell", "HostingFenceBindingChanged"),
            ("moved-hosting-cluster-via-dry-run", spec =>
            {
                spec["mode"] = "dry-run";
                spec["controller"]!["hosting"]!["cluster"] = "runner-shell";
            }, "HostingFenceBindingChanged"),
            ("changed-hosting-ownership", spec => spec["ecsClusters"]!["control-plane"] = new JsonObject
            {
                ["type"] = "import",
                ["arn"] = $"arn:aws:ecs:{Region}:{Account}:cluster/engineering-dev-controller",
            }, "HostingFenceBindingChanged"),
            ("removed-status", spec => Ec2(spec).Remove("statusQualification"), "RetirementRequired"),
            ("changed-version", spec => Ec2(spec)["launchTemplateVersion"]!["version"] = "4", "RetirementRequired"),
            ("changed-ami", spec => Ec2(spec)["statusQualification"]!["imageId"] = "ami-1234567890abcdef1", "RetirementRequired"),
            ("changed-role", spec => Ec2(spec)["statusQualification"]!["roleId"] = "AROAFIXTUREBOOTSTRAP02", "RetirementRequired"),
            ("changed-evidence", spec => Ec2(spec)["statusQualification"]!["evidenceSha256"] = new string('c', 64), "RetirementRequired"),
            ("capture-enabled-while-active", spec => spec["controller"]!["predecessorCapture"] = new JsonObject
            {
                ["logGroupArn"] = $"arn:aws:logs:{Region}:{Account}:log-group:/aws/events/controller-fence",
                ["eventRuleArn"] = $"arn:aws:events:{Region}:{Account}:rule/controller-fence",
                ["qualifiedFrom"] = "2026-11-25T00:00:00Z",
                ["qualifiedUntil"] = "2026-11-26T00:00:00Z",
            }, "PredecessorCaptureBindingChanged"),
        ];
        foreach (var (name, change, refusal) in variants)
        {
            var candidate = original.DeepClone();
            change(Assert.IsType<JsonObject>(candidate["spec"]));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(candidate);
            var candidateConfig = RuntimeConfigurationLoader.Load(bytes);
            var path = Path.Combine(Path.GetDirectoryName(deployed.ReadbackPath)!, name + ".json");
            await File.WriteAllBytesAsync(path, bytes, token);
            await using var output = new StringWriter(CultureInfo.InvariantCulture);
            await using var errors = new StringWriter(CultureInfo.InvariantCulture);
            table.Wire.Clear();
            int exit = await RetirementPreflightCommand.RunAsync(
            [
                "preflight", "retire",
                "--current-config", deployed.ReadbackPath, "--current-sha256", deployed.Configuration.Sha256, "--current-generation", "1",
                "--config", path, "--sha256", candidateConfig.Sha256, "--generation", "2",
            ], output, errors, table.Client, sts, clock, token);
            if (refusal is null)
            {
                Assert.Equal(0, exit);
                Assert.Contains("Retirement preflight verified.", output.ToString(), StringComparison.Ordinal);
                Assert.Empty(errors.ToString());
            }
            else
            {
                Assert.Equal(1, exit);
                Assert.Empty(output.ToString());
                Assert.Contains(refusal, errors.ToString(), StringComparison.Ordinal);
                if (refusal == "RetirementRequired")
                {
                    Assert.Contains(RetainedProfile, errors.ToString(), StringComparison.Ordinal);
                }
            }
            Assert.NotEmpty(table.Wire.Requests);
            Assert.All(table.Wire.Requests, request =>
            {
                Assert.True(request.Operation is "GetItem" or "Query");
                Assert.True(request.Request.GetProperty("ConsistentRead").GetBoolean());
            });
        }
        Assert.Equal(lease, await store.ReadLeaseAsync(lease.Deployment, token));
        Assert.Equivalent(catalog, await store.ReadCatalogProfileAsync(catalog.Profile, token), strict: true);

        static JsonObject Ec2(JsonObject spec) => Assert.IsType<JsonObject>(spec["profiles"]![0]!["provisioner"]);
    }

    [Fact(Timeout = 75_000)]
    public async Task VersionedYamlAdditionAndDrainGateRemovalAndAllowOnlyForwardGenerationRollback()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var endpoint = new Uri(table.Client.Config.ServiceURL);
        var credentials = new BasicAWSCredentials(Account, "test-only-secret");
        using var sts = new AmazonSecurityTokenServiceClient(credentials,
            ControllerAwsClientConfiguration.Create<AmazonSecurityTokenServiceConfig>(Region, endpoint));
        using var secrets = new AmazonSecretsManagerClient(credentials,
            ControllerAwsClientConfiguration.Create<AmazonSecretsManagerConfig>(Region, endpoint));
        var secret = await secrets.CreateSecretAsync(new CreateSecretRequest
        {
            Name = "rollout-" + Guid.NewGuid().ToString("N"),
            ClientRequestToken = Guid.NewGuid().ToString("N"),
            SecretString = JsonSerializer.Serialize(new { token = GitHubSimulator.FirstPat }),
        }, token);
        using var source = await YamlRolloutArtifactSource.CreateAsync(endpoint, token);
        PublishedYaml[] versions = await source.PublishAsync(table.Name, secret.ARN, token);
        var original = versions[0];
        var added = versions[1];
        var draining = versions[2];
        var removed = versions[3];
        Assert.Equal(4, versions.Select(item => item.Configuration.Sha256).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(RetainedProfile, Assert.Single(original.Configuration.Profiles).Id);
        Assert.Equal(RetainedProfile, Assert.Single(removed.Configuration.Profiles).Id);
        Assert.Equal(16, Assert.Single(original.Configuration.Profiles).Capacity.MaxRunners);
        Assert.Equal(18, Assert.Single(removed.Configuration.Profiles).Capacity.MaxRunners);
        Assert.Equal(RunnerProfileState.Active, added.Configuration.Profiles.Single(profile => profile.Id == RetiringProfile).State);
        Assert.Equal(RunnerProfileState.Draining, draining.Configuration.Profiles.Single(profile => profile.Id == RetiringProfile).State);

        var clock = new RolloutClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(original.Configuration);
        var retiring = new LifecycleProfile(deployment, RetiringProfile);
        var retained = new LifecycleProfile(deployment, RetainedProfile);
        var first = await AcquireAndApplyAsync(store, original.Configuration, 1, clock, token);
        Assert.Null(await store.ReadCatalogProfileAsync(retiring, token));
        var survivor = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retained, token));

        await RequirePreflightAsync(original, 1, added, 2, expectedRefusal: false, token);
        await ReleaseAsync(store, first, clock, token);
        var second = await AcquireAndApplyAsync(store, added.Configuration, 2, clock, token);
        var routed = added.Configuration.Profiles.Single(profile => profile.Id == RetiringProfile);
        var simulator = new GitHubSimulator(routed.GitHub.RunnerGroup);
        simulator.SeedScaleSet(routed.GitHub.ScaleSet, routed.GitHub.Labels);
        await using var host = await GitHubProtocolHost.StartAsync(simulator, token);
        using var http = host.CreateClient();
        var factory = new GitHubScaleSetClientFactory(http, timeProvider: clock);
        var credential = Assert.IsType<GitHubSecretVersion>(
            await new GitHubSecretLoader(secrets).LoadAsync(added.Configuration, routed.GitHub.Connection, token));
        using var connection = factory.Create(credential);
        var register = await new GitHubScaleSetRegistration(store, clock, Reserve, SendWindow).ResolveAsync(
            added.Configuration, RetiringProfile, connection, new GitHubRegistrationLease(second, 2, clock.Proof()), null, token);
        Assert.Equal(GitHubRegistrationStatus.Bound, register.Status);
        var binding = Assert.IsType<GitHubScaleSetBinding>(register.Binding);
        var active = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retiring, token));
        Assert.Equal(CatalogProfileState.Active, active.State);
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.BindCatalogScaleSetAsync(second.Fence, active, binding.RunnerGroupId, binding.ScaleSetId, token));

        await RequirePreflightAsync(added, 2, draining, 3, expectedRefusal: false, token);
        await ReleaseAsync(store, second, clock, token);
        var owner = await AcquireAndApplyAsync(store, draining.Configuration, 3, clock, token);
        var drainingRow = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retiring, token));
        Assert.Equal((CatalogProfileState.Draining, 3L, binding.ScaleSetId),
            (drainingRow.State, drainingRow.AppliedGeneration, drainingRow.Binding.ScaleSetId));
        await RequirePreflightAsync(draining, 3, removed, 4, expectedRefusal: true, token);

        using var lane = new GitHubListenerSupervisor(store, clock, new GitHubListenerTiming(Reserve, SendWindow, TimeSpan.FromSeconds(5)))
            .Register(new GitHubListenerProfile(draining.Configuration, RetiringProfile, binding, factory.Create(credential), owner.Fence,
                _ => Task.FromResult<GitHubRegistrationLease?>(new GitHubRegistrationLease(owner, 3, clock.Proof())),
                _ => Task.FromResult(new ListenerCapacity(0, 0)), (_, _) => Task.FromResult(true), CancellationToken.None));
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var sessionId = Assert.IsType<Guid>(simulator.SessionId);
        simulator.Offer(7311);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        Assert.DoesNotContain(simulator.Requests, request => request.Path.EndsWith("/acquirejobs", StringComparison.Ordinal));

        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.AdvanceEffectRebuildAsync(retiring, owner.Fence, token)).Outcome);
        await PublishEmptyInventoryAsync(store, retiring, owner.Fence, clock, token);
        var retirement = new ProfileRetirementCoordinator(store, clock, RetirementTiming,
            async (key, cancellation) => await store.ReadProfileHandoffObservationAsync(key, cancellation));
        Assert.Equal(ProfileRetirementStatus.FirstClearRecorded, await AdvanceRetirementAsync(token));
        var firstPass = await table.ReadAsync(LifecycleKey.Drain(retiring), token);
        Assert.Equal("3", firstPass["appliedGeneration"].N);
        Assert.True(long.TryParse(firstPass["firstClearAt"].N, out long firstClear) && firstClear > 0);
        await RequirePreflightAsync(draining, 3, removed, 4, expectedRefusal: true, token);
        Assert.Equal(ProfileRetirementStatus.WaitingForSecondPass, await AdvanceRetirementAsync(token));
        clock.Now += RetirementTiming.ClearSeparation + TimeSpan.FromSeconds(2);
        Assert.Equal(ProfileRetirementStatus.WaitingForSecondPass, await AdvanceRetirementAsync(token));

        await PublishEmptyInventoryAsync(store, retiring, owner.Fence, clock, token);
        Assert.Equal(ProfileRetirementStatus.ClosureHeld, await AdvanceRetirementAsync(token));
        var held = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retiring, token));
        Assert.Equal(CatalogProfileState.ClosureHeld, held.State);
        Assert.NotNull(held.ClosureHeldAt);
        Assert.Null(held.RetiredAt);
        await RequirePreflightAsync(draining, 3, removed, 4, expectedRefusal: true, token);
        await PublishEmptyInventoryAsync(store, retained, owner.Fence, clock, token);
        await using var telemetry = await YamlRolloutTelemetryEvidence.StartAsync(draining.Configuration, store, owner,
            clock.Proof(), Reserve, clock, token);
        await telemetry.AssertBeforeRetirementAsync(retiring, retained, second.Fence, token);
        var needSession = await retirement.AdvanceAsync(retiring, new ActiveControllerAuthority(owner, clock.Proof()), token);
        Assert.Equal(ProfileRetirementStatus.SessionRetirementRequired, needSession.Status);
        Assert.Equal(sessionId, needSession.Session?.SessionId);

        Assert.Equal(GitHubListenerLaneStatus.Stopped, (await lane.RetireDrainingSessionAsync(retirement, token)).Status);
        Assert.Null(simulator.SessionId);
        Assert.Single(simulator.Requests, request => request.Method == "DELETE" && request.Path == $"{GitHubSimulator.Sessions}/{sessionId:D}");
        Assert.Equal(SessionPhase.Retired, (await store.ReadSessionAsync(retiring, token))?.Phase);
        await RequirePreflightAsync(draining, 3, removed, 4, expectedRefusal: true, token);
        Assert.Equal(ProfileRetirementStatus.Retired, await AdvanceRetirementAsync(token));
        var tombstone = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retiring, token));
        Assert.Equal(CatalogProfileState.Tombstoned, tombstone.State);
        Assert.NotNull(tombstone.RetiredAt);
        Assert.Equivalent(drainingRow.Binding, tombstone.Binding, strict: true);
        await telemetry.AssertAfterRetirementAsync(retiring, retained, token);
        await RequirePreflightAsync(draining, 3, removed, 4, expectedRefusal: false, token);
        await ReleaseAsync(store, owner, clock, token);
        var removal = await AcquireAndApplyAsync(store, removed.Configuration, 4, clock, token);

        await RequirePreflightAsync(removed, 4, original, 5, expectedRefusal: false, token);
        table.Wire.Clear();
        Assert.Equal(CatalogRolloutStatus.StaleGeneration,
            (await store.AdvanceCatalogRolloutAsync(removal.Fence, original.Configuration, 1, token)).Status);
        Assert.DoesNotContain(table.Wire.Requests, request => request.Operation == "TransactWriteItems");
        var stillRemoved = Assert.IsType<CatalogRolloutHead>(await store.ReadCatalogRolloutAsync(deployment, token));
        Assert.Equal((4L, removed.Configuration.Sha256), (stillRemoved.AppliedGeneration, stillRemoved.AppliedDigest));

        await ReleaseAsync(store, removal, clock, token);
        await AcquireAndApplyAsync(store, original.Configuration, 5, clock, token);
        var final = Assert.IsType<CatalogRolloutHead>(await store.ReadCatalogRolloutAsync(deployment, token));
        Assert.Equal((5L, original.Configuration.Sha256), (final.AppliedGeneration, final.AppliedDigest));
        var retainedRow = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retained, token));
        Assert.Equal(CatalogProfileState.Active, retainedRow.State);
        Assert.Equivalent(survivor.Binding, retainedRow.Binding, strict: true);
        var retiredRow = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(retiring, token));
        Assert.Equal((CatalogProfileState.Tombstoned, tombstone.RetiredAt), (retiredRow.State, retiredRow.RetiredAt));

        async Task<ProfileRetirementStatus> AdvanceRetirementAsync(CancellationToken cancellation) =>
            (await retirement.AdvanceAsync(retiring, new ActiveControllerAuthority(owner, clock.Proof()), cancellation)).Status;

        async Task RequirePreflightAsync(PublishedYaml deployed, long deployedGeneration, PublishedYaml candidate, long candidateGeneration,
            bool expectedRefusal, CancellationToken cancellation)
        {
            await using var output = new StringWriter(CultureInfo.InvariantCulture);
            await using var errors = new StringWriter(CultureInfo.InvariantCulture);
            table.Wire.Clear();
            int exit = await RetirementPreflightCommand.RunAsync(
            [
                "preflight", "retire",
                "--current-config", deployed.ReadbackPath, "--current-sha256", deployed.Configuration.Sha256,
                "--current-generation", deployedGeneration.ToString(CultureInfo.InvariantCulture),
                "--config", candidate.ReadbackPath, "--sha256", candidate.Configuration.Sha256,
                "--generation", candidateGeneration.ToString(CultureInfo.InvariantCulture),
            ], output, errors, table.Client, sts, clock, cancellation);
            Assert.Equal(expectedRefusal ? 1 : 0, exit);
            if (expectedRefusal)
            {
                Assert.Empty(output.ToString());
                Assert.Contains("RetirementRequired", errors.ToString(), StringComparison.Ordinal);
                Assert.Contains(RetiringProfile, errors.ToString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Retirement preflight verified.", output.ToString(), StringComparison.Ordinal);
                Assert.Empty(errors.ToString());
            }
            Assert.NotEmpty(table.Wire.Requests);
            Assert.All(table.Wire.Requests, request =>
            {
                Assert.True(request.Operation is "GetItem" or "Query");
                Assert.True(request.Request.GetProperty("ConsistentRead").GetBoolean());
            });
        }
    }

    private static async Task PublishEmptyInventoryAsync(DynamoDbLifecycleStore store, LifecycleProfile profile,
        DeploymentLeaseFence lease, RolloutClock clock, CancellationToken token)
    {
        var inventory = await store.ReadExecutorInventoryAsync(profile, token);
        Assert.Empty(inventory.Executors);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, lease, inventory.Revision,
            new ExecutorInventoryObservation(clock.Now, IsComplete: true), token));
    }

    private static async Task<ControllerLease> AcquireAndApplyAsync(DynamoDbLifecycleStore store, RuntimeConfiguration configuration,
        long generation, RolloutClock clock, CancellationToken token)
    {
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var previous = await store.ReadLeaseAsync(deployment, token);
        string taskArn = $"arn:aws:ecs:{Region}:{Account}:task/engineering-dev-controller/{Guid.NewGuid():N}";
        var acquired = await store.TryAcquireLeaseAsync(deployment, taskArn, taskArn, ControllerLease.CurrentWriterProtocol,
            clock.Now.AddSeconds(-1), clock.Now.AddMinutes(3), token);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquired.Outcome);
        var recovery = new ControllerLeaseRecoveryCoordinator(store, new NoPredecessorRead(), clock, Reserve);
        var activated = await recovery.TryActivateAsync(Assert.IsType<ControllerLease>(acquired.Lease), configuration, clock.Proof(), [], token);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        var lease = Assert.IsType<ControllerLease>(activated.Lease);
        Assert.Equal((previous?.Epoch ?? 0) + 1, lease.Epoch);
        for (int pass = 0; pass < 6; pass++)
        {
            var rollout = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, generation, token);
            if (rollout.Status == CatalogRolloutStatus.Applied)
            {
                Assert.Equal((generation, configuration.Sha256, lease.Epoch),
                    (rollout.Head?.AppliedGeneration, rollout.Head?.AppliedDigest, rollout.Head?.AppliedEpoch));
                return lease;
            }
            Assert.Equal(CatalogRolloutStatus.Staging, rollout.Status);
        }
        throw new InvalidOperationException("The authored rollout did not apply within six bounded passes.");
    }

    private static async Task ReleaseAsync(DynamoDbLifecycleStore store, ControllerLease lease, RolloutClock clock, CancellationToken token) =>
        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.ReleaseLeaseAsync(lease, clock.Now, token)).Outcome);

    private sealed class RolloutClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = TimeProvider.System.GetUtcNow().AddMinutes(1);

        public override DateTimeOffset GetUtcNow() => Now;

        internal ControllerClockEvidence Proof() => new(Now, Now.AddMinutes(1), TimeSpan.Zero);
    }

    private sealed class NoPredecessorRead : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The YAML rollout uses only cleanly released successors.");
    }
}
