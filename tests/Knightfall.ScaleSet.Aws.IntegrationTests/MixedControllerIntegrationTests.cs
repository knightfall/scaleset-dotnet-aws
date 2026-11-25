using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Knightfall.ScaleSet.Aws.Telemetry.Tests;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

[Collection(nameof(GitHubLifecycleIntegrationTests))]
public sealed class MixedControllerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task UnexpectedHeartbeatLossExitsUnsuccessfullyAndRetainsTheUncertainPredecessorFence()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        await using var fixture = await MixedRuntimeFixture.CreateAsync("mixed", token);
        string taskArn = await fixture.StartControllerTaskAsync(token);
        await using var worker = MixedControllerProcess.Start(fixture, taskArn, telemetryEndpoint: null);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(fixture.Configuration);
        await WaitAsync(async () => await worker.ReadyAsync(token) == HttpStatusCode.OK, "mixed worker ready before heartbeat loss", token);
        var original = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
        Assert.Equal(taskArn, original.OwnerTaskArn);
        Assert.Equal(ControllerLeaseMode.Active, original.Mode);
        fixture.Cloud.SetClock(taskArn, healthy: false);

        Assert.Equal(1, await worker.ExitCodeAsync(token));

        Assert.Contains(fixture.Cloud.Calls, call => call is { Service: "metadata", Operation: "NOT_SYNCHRONIZED" });
        var retained = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
        Assert.Equal(original.Fence, retained.Fence);
        Assert.Equal(ControllerLeaseMode.Active, retained.Mode);
        foreach (var profile in fixture.Profiles.Values)
        {
            Assert.DoesNotContain(profile.GitHub.Requests, request => request.Method == "DELETE" &&
                request.Path.Contains("/sessions/", StringComparison.Ordinal));
            var session = await fixture.Store.ReadSessionAsync(new LifecycleProfile(deployment, profile.Id), token);
            Assert.Equal(SessionPhase.Active, session?.Phase);
        }
    }

    [Fact]
    public async Task FargateReserveStaysAtOneWithoutDemandAndReplacesAnExactStoppedTaskOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        var token = deadline.Token;
        await using var fixture = await MixedRuntimeFixture.CreateAsync("fargate", token, warmFargateMaximum: 24);
        string taskArn = await fixture.StartControllerTaskAsync(token);
        foreach (var profile in fixture.Profiles.Values)
        {
            profile.GitHub.TotalAssignedJobs = 0;
        }
        await using var worker = MixedControllerProcess.Start(fixture, taskArn, telemetryEndpoint: null);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(fixture.Configuration);
        var first = await WaitForRunnerAsync(fixture, deployment, fixture.Configuration.Profiles[0], token);
        var unaffected = await WaitForRunnerAsync(fixture, deployment, fixture.Configuration.Profiles[1], token);
        await WaitForFargateInventoryPassesAsync(fixture, token);
        Assert.Equal(2, fixture.Cloud.Calls.Count(call => call is { Service: "ecs", Operation: "RunTask" }));
        var provider = Assert.IsType<FargateExecutorIdentity>(first.Binding.Provider);
        await fixture.Cloud.StopControllerTaskAsync(provider.ClusterArn, first.Binding.ResourceArn, token);

        await WaitAsync(async () =>
        {
            var prior = await fixture.Store.ReadExecutorAsync(first.Binding.Profile, first.Binding.AllocationId, token);
            var current = await fixture.Store.ReadExecutorInventoryAsync(first.Binding.Profile, token);
            return prior?.State.Resource?.State == ExecutorResourceState.IrreversiblyTerminated &&
                current.Executors is [{ State.Resource.State: ExecutorResourceState.Running } replacement] &&
                replacement.Binding.AllocationId != first.Binding.AllocationId;
        }, "one replacement for the exact stopped allocation", token);
        await WaitForFargateInventoryPassesAsync(fixture, token);
        Assert.Equal(3, fixture.Cloud.Calls.Count(call => call is { Service: "ecs", Operation: "RunTask" }));
        var peer = await fixture.Store.ReadExecutorInventoryAsync(unaffected.Binding.Profile, token);
        Assert.Equal(unaffected.Binding, Assert.Single(peer.Executors).Binding);
    }

    [Fact]
    public async Task FullFargateWarmReserveAcquiresOneOfferAfterRestartAndHoldsUncorroboratedReadiness()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));
        var token = deadline.Token;
        await using var fixture = await MixedRuntimeFixture.CreateAsync("fargate", token, warmFargateMaximum: 1);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(fixture.Configuration);
        string formerTask = await fixture.StartControllerTaskAsync(token);
        string successorTask = await fixture.StartControllerTaskAsync(token);
        var availableProfile = fixture.Configuration.Profiles[0];
        var uncertainProfile = fixture.Configuration.Profiles[1];
        var available = fixture.Profiles[availableProfile.Id].GitHub;
        var uncertain = fixture.Profiles[uncertainProfile.Id].GitHub;

        await using var former = MixedControllerProcess.Start(fixture, formerTask, telemetryEndpoint: null);
        var warm = await WaitForRunnerAsync(fixture, deployment, availableProfile, token);
        var other = await WaitForRunnerAsync(fixture, deployment, uncertainProfile, token);
        long warmId = await RegisterSimulatedRunnerAsync(fixture, warm, token);
        long uncertainId = await RegisterSimulatedRunnerAsync(fixture, other, token);
        available.MarkRunnerListening(warmId);
        var formerSession = Assert.IsType<Guid>(available.SessionId);
        var formerLease = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
        await former.KillAsync(token);
        await fixture.Cloud.StopControllerTaskAsync(fixture.ControllerCluster, formerTask, token);

        await using var successor = MixedControllerProcess.Start(fixture, successorTask, telemetryEndpoint: null);
        await WaitAsync(async () => await fixture.Store.ReadLeaseAsync(deployment, token) is
            { Mode: ControllerLeaseMode.Active } owner && owner.Epoch > formerLease.Epoch && owner.OwnerTaskArn == successorTask &&
            available.SessionId is Guid session && session != formerSession &&
            await successor.ReadyAsync(token) == HttpStatusCode.OK, "restarted controller and successor listener session", token);

        // A registered local runner without a listening process supplies no idle evidence.
        const long noIdle = 8_334_502_001;
        await OfferAndWaitForAckAsync(uncertain, noIdle);
        Assert.Null(await fixture.Store.ReadRecordAsync(LifecycleKey.Acquisition(other.Binding.Profile, noIdle), token));

        // Even with the same positive idle aggregate, the remote runner must be the locally bound one.
        uncertain.ReplaceWithUnownedIdleRunner(uncertainId);
        const long foreignIdle = 8_334_502_002;
        await OfferAndWaitForAckAsync(uncertain, foreignIdle);
        Assert.Contains(uncertain.Requests, request => request.Method == "GET" &&
            request.Path == FormattableString.Invariant($"{GitHubSimulator.Runners}/{uncertainId}"));
        Assert.Null(await fixture.Store.ReadRecordAsync(LifecycleKey.Acquisition(other.Binding.Profile, foreignIdle), token));
        Assert.Empty(uncertain.AcquiredRequests);

        const long accepted = 8_334_502_003;
        const long held = 8_334_502_004;
        await OfferAndWaitForAckAsync(available, accepted);
        Assert.NotNull(await fixture.Store.ReadRecordAsync(LifecycleKey.Acquisition(warm.Binding.Profile, accepted), token));
        await OfferAndWaitForAckAsync(available, accepted, held);
        Assert.Null(await fixture.Store.ReadRecordAsync(LifecycleKey.Acquisition(warm.Binding.Profile, held), token));
        Assert.Equal([accepted], available.AcquiredRequests);
        Assert.Single(available.Requests, request => request.Method == "POST" &&
            request.Path.EndsWith("/acquirejobs", StringComparison.Ordinal));
        Assert.DoesNotContain(uncertain.Requests, request => request.Method == "POST" &&
            request.Path.EndsWith("/acquirejobs", StringComparison.Ordinal));

        await WaitForFargateInventoryPassesAsync(fixture, token);
        Assert.Equal(2, fixture.Cloud.Calls.Count(call => call is { Service: "ecs", Operation: "RunTask" }));
        var retained = await fixture.Store.ReadExecutorInventoryAsync(warm.Binding.Profile, token);
        Assert.Equal(warm.Binding, Assert.Single(retained.Executors).Binding);

        async Task OfferAndWaitForAckAsync(GitHubSimulator github, params long[] requests)
        {
            var session = Assert.IsType<Guid>(github.SessionId);
            long message = github.Offer(requests);
            string path = FormattableString.Invariant($"/messages/{session:D}/{message}");
            await WaitAsync(() => Task.FromResult(github.Requests.Any(request => request.Method == "DELETE" &&
                request.Host == github.Address.QueueHost && request.Path == path)), $"listener ACK for {message}", token);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1024, 1)]
    [InlineData(32768, 2)]
    public async Task SharedFargateClusterKeepsProfileCapsIndependentAndAccountsForRegionalHeadroom(int availableCpu, int expectedStarts)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bool refresh = availableCpu == 1024;
        deadline.CancelAfter(TimeSpan.FromSeconds(refresh ? 75 : 45));
        var token = deadline.Token;
        await using var fixture = await MixedRuntimeFixture.CreateAsync("fargate", token,
            sharedFargateCluster: true, remainingFargateCpuUnits: availableCpu);
        string taskArn = await fixture.StartControllerTaskAsync(token);
        string unrelatedTaskArn = await fixture.StartUnrelatedServiceTaskAsync(token);
        var profiles = fixture.Configuration.Profiles;
        Assert.Equal(2, profiles.Length);
        Assert.All(profiles, profile => Assert.Equal(1, profile.Capacity.MaxRunners));
        Assert.Single(profiles.Select(profile => Assert.IsType<FargateProvisionerConfiguration>(profile.Provisioner).Cluster).Distinct());
        Assert.Empty(profiles[0].GitHub.Labels.Intersect(profiles[1].GitHub.Labels, StringComparer.Ordinal));
        foreach (var profile in fixture.Profiles.Values)
        {
            profile.GitHub.TotalAssignedJobs = 1;
        }

        if (refresh)
        {
            fixture.EnableRefreshableQuota();
            var stale = TimeProvider.System.GetUtcNow().AddMinutes(-11);
            fixture.Cloud.PublishQuota(new FargateQuotaEvidence(MixedControllerCloud.Account, MixedControllerCloud.Region,
                stale, stale.AddMinutes(15), availableCpu, 0));
        }
        await using var worker = MixedControllerProcess.Start(fixture, taskArn, telemetryEndpoint: null);
        if (refresh)
        {
            await WaitAsync(() => Task.FromResult(
                fixture.Cloud.Calls.Any(call => call is { Service: "s3", Operation: "GetQuota" }) &&
                fixture.Cloud.Calls.Count(call => call is { Service: "ecs", Operation: "ListTasks" }) >= 8),
                "shared-cluster passes with expired quota", token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await worker.ReadyAsync(token));
            Assert.DoesNotContain(fixture.Cloud.Calls, call => call is { Service: "ecs", Operation: "RunTask" });
            var now = TimeProvider.System.GetUtcNow();
            fixture.Cloud.PublishQuota(new FargateQuotaEvidence(MixedControllerCloud.Account, MixedControllerCloud.Region,
                now, now.AddMinutes(10), availableCpu, 0));
        }
        await WaitAsync(async () => await worker.ReadyAsync(token) == HttpStatusCode.OK, "shared-cluster profiles ready", token);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(fixture.Configuration);
        var profileKeys = profiles.Select(profile => new LifecycleProfile(deployment, profile.Id)).ToArray();
        await WaitAsync(async () =>
        {
            var samples = await Task.WhenAll(profileKeys.Select(key => fixture.Store.ReadExecutorInventoryAsync(key, token)));
            return samples.Sum(sample => sample.Executors.Count(row => row.State.Resource?.State == ExecutorResourceState.Running)) == expectedStarts;
        }, "expected number of shared-cluster runners", token);
        await WaitForFargateInventoryPassesAsync(fixture, token);

        Assert.Equal(expectedStarts, fixture.Cloud.Calls.Count(call => call is { Service: "ecs", Operation: "RunTask" }));
        foreach (var key in profileKeys)
        {
            var inventory = await fixture.Store.ReadExecutorInventoryAsync(key, token);
            Assert.InRange(inventory.Executors.Length, 0, 1);
            Assert.All(inventory.Executors, row =>
            {
                Assert.NotEqual(taskArn, row.Binding.ResourceArn);
                Assert.NotEqual(unrelatedTaskArn, row.Binding.ResourceArn);
            });
        }
    }

    private static Task WaitForFargateInventoryPassesAsync(MixedRuntimeFixture fixture, CancellationToken token)
    {
        int inventoryRequests = fixture.Cloud.Calls.Count(call => call is { Service: "ecs", Operation: "ListTasks" });
        return WaitAsync(() => Task.FromResult(fixture.Cloud.Calls.Count(call =>
            call is { Service: "ecs", Operation: "ListTasks" }) >= inventoryRequests + 8), "repeated Fargate reconciliation", token);
    }

    [Theory]
    [InlineData("ec2")]
    [InlineData("fargate")]
    [InlineData("mixed")]
    public async Task VerifiedControllerArtifactKeepsProvidersIndependentThroughOutageAndTakeover(string mode)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(mode == "mixed" ? 160 : 35));
        var token = deadline.Token;
        await using var fixture = await MixedRuntimeFixture.CreateAsync(mode, token, qualifyFirstEc2: mode == "mixed");
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(fixture.Configuration);
        string firstTask = await fixture.StartControllerTaskAsync(token);
        string? secondTask = mode == "mixed" ? await fixture.StartControllerTaskAsync(token) : null;
        var ec2 = fixture.Configuration.Profiles.Where(profile => profile.Provisioner is Ec2ProvisionerConfiguration).ToArray();
        var fargate = fixture.Configuration.Profiles.Where(profile => profile.Provisioner is FargateProvisionerConfiguration).ToArray();
        bool mixed = secondTask is not null;
        if (mixed)
        {
            Assert.Equal(2, ec2.Select(profile => ((Ec2ProvisionerConfiguration)profile.Provisioner).LaunchTemplateId).Distinct().Count());
            Assert.Equal(2, fargate.Select(profile => (FargateProvisionerConfiguration)profile.Provisioner)
                .Select(provisioner => (provisioner.TaskDefinitionArn, provisioner.Cluster)).Distinct().Count());
            fixture.Cloud.DegradeEc2(true);
        }
        string? effectProfile = mixed ? ec2[1].Id : null;
        foreach (var profile in fixture.Profiles.Values)
        {
            profile.GitHub.TotalAssignedJobs = profile.Id == effectProfile ? 0 : 1;
        }

        int receiverOutage = 0;
        int rejected = 0;
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, _ =>
        {
            if (Volatile.Read(ref receiverOutage) == 0)
            {
                return ReceiverReply.Accepted;
            }
            Interlocked.Increment(ref rejected);
            return ReceiverReply.Rejected;
        }, token);
        await using var first = MixedControllerProcess.Start(fixture, firstTask, mixed ? receiver.Url : null);
        try
        {
            await WaitAsync(async () => await first.ReadyAsync(token) is not null, "worker probe", token);
            foreach (var profile in fargate)
            {
                await WaitForRunnerAsync(fixture, deployment, profile, token);
            }
            if (mixed)
            {
                Assert.Contains(fixture.Cloud.Calls, call => call is { Service: "ec2", Operation: "DescribeImages" });
                Assert.DoesNotContain(fixture.Cloud.Calls, call => call is { Service: "ec2", Operation: "RunInstances" });
                Assert.Equal(HttpStatusCode.ServiceUnavailable, await first.ReadyAsync(token));
                fixture.Cloud.DegradeEc2(false);
            }

            foreach (var profile in ec2.Where(profile => profile.Id != effectProfile))
            {
                await WaitForRunnerAsync(fixture, deployment, profile, token);
            }
            await WaitAsync(async () => await first.ReadyAsync(token) == HttpStatusCode.OK, "first worker ready", token);
            Assert.Equal(mixed ? 1 : ec2.Length, fixture.Cloud.Calls.Count(call => call is { Service: "ec2", Operation: "RunInstances" }));
            Assert.Equal(fargate.Length, fixture.Cloud.Calls.Count(call => call is { Service: "ecs", Operation: "RunTask" }));
            if (fargate.Length == 0)
            {
                Assert.DoesNotContain(fixture.Cloud.Calls, call => call.Service == "ecs");
            }
            if (ec2.Length == 0)
            {
                Assert.DoesNotContain(fixture.Cloud.Calls, call =>
                    call.Operation is "DescribeLaunchTemplateVersions" or "DescribeImages" or "DescribeInstances");
            }

            if (secondTask is not null)
            {
                await WaitAsync(() => Task.FromResult(ProfileGaugeBatch(receiver, MixedOtlpAssertions.Ready, 1,
                    fixture.Profiles.Keys)), "four ready OTLP profiles", token);
                Volatile.Write(ref receiverOutage, 1);
                await WaitAsync(() => Task.FromResult(Volatile.Read(ref rejected) > 0), "a receiver rejection", token);
                var owner = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
                Assert.Equal(ControllerLeaseMode.Active, owner.Mode);

                var firstJob = await StartSimulatedJobAsync(fixture, deployment, ec2[0], 8_334_500_001, token);
                var malformedStatus = await SeedMalformedSupervisorStatusAsync(fixture, firstJob, token);
                int afterMalformed = fixture.Cloud.Calls.Length;
                await WaitAsync(() => Task.FromResult(ReadSupervisorStatus(fixture.Cloud.Calls.Skip(afterMalformed), malformedStatus)),
                    "the qualified EC2 supervisor status read", token);
                var secondJob = await StartSimulatedJobAsync(fixture, deployment, fargate[0], 8_334_500_002, token);
                // Wire ACK, persisted job starts and a new lease heartbeat occur with failed exports.
                await WaitAsync(async () => (await fixture.Store.ReadLeaseAsync(deployment, token))?.ValidUntil > owner.ValidUntil,
                    "lease heartbeat through rejected OTLP", token);
                Volatile.Write(ref receiverOutage, 0);
                int restoredAt = receiver.Requests.Count;
                await WaitAsync(() => Task.FromResult(ActiveProfiles(receiver, restoredAt, firstJob, secondJob)), "OTLP metrics recovery", token);

                await AssertUncleanTakeoverAsync(fixture, first, secondTask, deployment, ec2[1], firstJob, secondJob,
                    malformedStatus, receiver, token);
            }
            else
            {
                Assert.Empty(receiver.Requests);
            }
        }
        catch
        {
            foreach (var group in fixture.Cloud.Calls.GroupBy(call => (call.Service, call.Operation)))
            {
                output.WriteLine($"{group.Key.Service} {group.Key.Operation} {group.Count()}");
            }
            throw;
        }
        finally
        {
            await first.KillAsync(CancellationToken.None);
            string diagnostics = await first.DiagnosticsAsync();
            AssertSafeText(diagnostics);
            output.WriteLine(diagnostics);
        }
    }

    private async Task AssertUncleanTakeoverAsync(MixedRuntimeFixture fixture, MixedControllerProcess first, string secondTask,
        string deployment, RunnerProfile launchProfile, ExecutorSnapshot ec2Job, ExecutorSnapshot fargateJob,
        LifecycleKey malformedStatus, OtlpLoopback receiver, CancellationToken token)
    {
        var setup = Assert.IsType<Ec2ProvisionerConfiguration>(launchProfile.Provisioner);
        fixture.Cloud.HoldNextEc2Launch(setup.LaunchTemplateId);
        var github = fixture.Profiles[launchProfile.Id].GitHub;
        github.TotalAssignedJobs = 1;
        github.Offer(8_334_500_003);
        var held = await fixture.Cloud.HeldLaunch.WaitAsync(TimeSpan.FromSeconds(35), token);
        var key = new LifecycleProfile(deployment, launchProfile.Id);
        var journal = new DynamoDbEffectJournal(fixture.Store, TimeProvider.System);
        string intent = EffectIntentIdentity.ForEc2Launch(held.AllocationId, 0);
        var executing = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(key, intent, token));
        Assert.Equal(EffectState.Executing, executing.Entry.State);
        var launch = Assert.IsType<Ec2LaunchEffectRequest>(executing.Entry.Envelope.Request);
        Assert.Equal(Guid.Parse(held.ClientToken), launch.ClientToken);
        await first.KillAsync(token);
        var former = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
        Assert.Equal(ControllerLeaseMode.Active, former.Mode);
        int priorCalls = fixture.Cloud.Calls.Length;
        int launchCalls = fixture.Cloud.Calls.Count(IsRunnerMutation);
        int githubCalls = fixture.Profiles.Values.Sum(value => value.GitHub.Requests.Length);

        int beforeSuccessor = receiver.Requests.Count;
        string formerInstance = Assert.Single(MixedOtlpAssertions.MetricPoints(receiver.Requests).Select(point => point.InstanceId).Distinct());
        await using var successor = MixedControllerProcess.Start(fixture, secondTask, receiver.Url);
        try
        {
            await WaitAsync(async () =>
            {
                var lease = await fixture.Store.ReadLeaseAsync(deployment, token);
                return lease is { Mode: ControllerLeaseMode.RecoveryOnly } && lease.OwnerTaskArn == secondTask &&
                    DescribedTask(fixture.Cloud.Calls.Skip(priorCalls), fixture.ControllerCluster, former.OwnerTaskArn);
            }, "recovery-only lease and exact native predecessor observation", token);
            var recovering = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
            Assert.True(recovering.Epoch > former.Epoch);
            Assert.Equal(former.OwnerTaskArn, recovering.RequiredFencingTaskArn);
            await AssertHeldAsync();

            fixture.Cloud.SetClock(secondTask, healthy: false);
            await WaitAsync(() => Task.FromResult(fixture.Cloud.Calls.Skip(priorCalls)
                .Any(call => call is { Service: "metadata", Operation: "NOT_SYNCHRONIZED" } && call.Request == secondTask)),
                "invalid successor clock observed", token);
            await fixture.Cloud.StopControllerTaskAsync(fixture.ControllerCluster, former.OwnerTaskArn, token);
            int afterStop = fixture.Cloud.Calls.Length;
            await WaitAsync(() => Task.FromResult(fixture.Cloud.Calls.Skip(afterStop)
                .Any(call => call is { Service: "metadata", Operation: "NOT_SYNCHRONIZED" } && call.Request == secondTask)),
                "an invalid clock sample after native STOPPED", token);
            Assert.False(fixture.Cloud.RecoveryRead.IsCompleted);
            await AssertHeldAsync();

            fixture.Cloud.SetClock(secondTask, healthy: true);
            await fixture.Cloud.RecoveryRead.WaitAsync(TimeSpan.FromSeconds(30), token);
            await AssertHeldAsync();
            await WaitAsync(() => Task.FromResult(ProfileGaugeBatch(receiver, MixedOtlpAssertions.Ready, 0,
                fixture.Profiles.Keys, beforeSuccessor)), "one recovery-only readiness batch", token);
            var recoveryPoints = MixedOtlpAssertions.MetricPoints(receiver.Requests)
                .Where(point => point.Batch >= beforeSuccessor && point.InstanceId != formerInstance).ToArray();
            Assert.NotEmpty(recoveryPoints);
            Assert.All(recoveryPoints, point =>
            {
                Assert.Equal(MixedOtlpAssertions.Ready, point.Name);
                Assert.Equal(0, point.Number);
            });
            int beforePromotion = receiver.Requests.Count;
            fixture.Cloud.ReleaseRecoveryRead();
            await WaitAsync(async () =>
                await fixture.Store.ReadLeaseAsync(deployment, token) is { Mode: ControllerLeaseMode.Active } lease && lease.OwnerTaskArn == secondTask,
                "verified recovery takeover", token);

            var settled = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(key, intent, token));
            Assert.Equal(EffectState.Settled, settled.Entry.State);
            var recovered = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(key, held.AllocationId.ToString("D"), token));
            Assert.Equal(held.ResourceArn, recovered.Binding.ResourceArn);
            var provider = Assert.IsType<Ec2ExecutorIdentity>(recovered.Binding.Provider);
            Assert.Equal((held.TemplateId, held.Version), (provider.LaunchTemplateId, provider.LaunchTemplateVersion));
            Assert.Single(fixture.Cloud.Calls, call => call is { Service: "ec2", Operation: "RunInstances" } &&
                HttpUtility.ParseQueryString(call.Request)["ClientToken"] == held.ClientToken);
            Assert.Equal(launchCalls, fixture.Cloud.Calls.Count(IsRunnerMutation));
            await AssertJobSurvivedAsync(fixture, ec2Job, token);
            await AssertJobSurvivedAsync(fixture, fargateJob, token);

            await WaitAsync(() => Task.FromResult(ActiveProfiles(receiver, beforePromotion, ec2Job, fargateJob)), "successor active capacity metrics", token);
            AssertTelemetry(fixture, receiver.Requests, held, ec2Job, fargateJob, formerInstance);
            Assert.True(ReadSupervisorStatus(fixture.Cloud.Calls.Skip(priorCalls), malformedStatus));
            var otherFargate = fixture.Configuration.Profiles.Single(profile =>
                profile.Provisioner is FargateProvisionerConfiguration && profile.Id != fargateJob.Binding.Profile.ProfileId);
            await StartSimulatedJobAsync(fixture, deployment, launchProfile, 8_334_500_004, token);
            await StartSimulatedJobAsync(fixture, deployment, otherFargate, 8_334_500_005, token);
            var heldExecutor = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(ec2Job.Binding.Profile,
                ec2Job.Binding.AllocationId, token));
            Assert.Null(heldExecutor.State.Process);
            Assert.Equal(launchCalls, fixture.Cloud.Calls.Count(IsRunnerMutation));
        }
        finally
        {
            await successor.KillAsync(CancellationToken.None);
            string diagnostics = await successor.DiagnosticsAsync();
            AssertSafeText(diagnostics);
            output.WriteLine(diagnostics);
        }

        async Task AssertHeldAsync()
        {
            Assert.Equal(ControllerLeaseMode.RecoveryOnly, (await fixture.Store.ReadLeaseAsync(deployment, token))?.Mode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await successor.ReadyAsync(token));
            Assert.Equal(githubCalls, fixture.Profiles.Values.Sum(value => value.GitHub.Requests.Length));
            Assert.DoesNotContain(fixture.Cloud.Calls.Skip(priorCalls), IsRunnerMutation);
        }
    }

    private static async Task<LifecycleKey> SeedMalformedSupervisorStatusAsync(MixedRuntimeFixture fixture,
        ExecutorSnapshot executor, CancellationToken token)
    {
        var handoff = Assert.IsType<JitHandoffSnapshot>(await fixture.Store.ReadJitHandoffAsync(executor.Binding.Profile,
            Guid.ParseExact(executor.Binding.AllocationId, "D"), token));
        var key = LifecycleKey.Status(handoff.Identity.AwsUserId);
        var observed = TimeProvider.System.GetUtcNow();
        await fixture.Table.Client.PutItemAsync(fixture.Table.Name, new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            ["PK"] = new() { S = key.PartitionKey },
            ["SK"] = new() { S = key.SortKey },
            ["awsUserId"] = new() { S = handoff.Identity.AwsUserId },
            ["resourceArn"] = new() { S = handoff.Identity.ResourceArn },
            ["helloSha256"] = new() { S = handoff.HelloSha256 },
            ["offerId"] = new() { S = handoff.OfferId },
            ["runnerId"] = new() { N = FormattableString.Invariant($"{handoff.RunnerId}") },
            ["sequence"] = new() { N = "2" },
            ["observedAt"] = new() { N = FormattableString.Invariant($"{observed.ToUnixTimeMilliseconds()}") },
            ["exited"] = new() { BOOL = true },
            ["payload"] = new() { S = "{" },
            ["qualificationSha256"] = new() { S = DirectEc2Fixture.StatusQualificationDigest },
        }, token);
        return key;
    }

    private static bool ReadSupervisorStatus(IEnumerable<MixedCloudCall> calls, LifecycleKey key) =>
        calls.Any(call => call is { Service: "dynamodb", Operation: "GetItem" } &&
            call.Request.Contains(key.PartitionKey, StringComparison.Ordinal));

    private static async Task<ExecutorSnapshot> StartSimulatedJobAsync(MixedRuntimeFixture fixture, string deployment, RunnerProfile profile,
        long requestId, CancellationToken token)
    {
        var running = await WaitForRunnerAsync(fixture, deployment, profile, token);
        var simulated = fixture.Profiles[profile.Id];
        long runnerId = await RegisterSimulatedRunnerAsync(fixture, running, token);
        long cursor = simulated.GitHub.StartJob(requestId, runnerId);
        ExecutorSnapshot? started = null;
        await WaitAsync(async () =>
        {
            started = await fixture.Store.ReadExecutorAsync(running.Binding.Profile, running.Binding.AllocationId, token);
            return started?.State.Job is { Phase: ExecutorJobPhase.Started } job && job.RunnerId == runnerId && job.RequestId == requestId &&
                simulated.GitHub.Requests.Any(request => request.Method == "DELETE" && request.Host == simulated.GitHub.Address.QueueHost &&
                    request.Path.EndsWith(FormattableString.Invariant($"/{cursor}"), StringComparison.Ordinal));
        }, "durable JobStarted and released listener ACK", token);
        return started!;
    }

    private static async Task<long> RegisterSimulatedRunnerAsync(MixedRuntimeFixture fixture, ExecutorSnapshot running, CancellationToken token)
    {
        var simulated = fixture.Profiles[running.Binding.Profile.ProfileId];
        string resource = running.Binding.ResourceArn;
        string principal = simulated.BootstrapRoleId + ":" + resource[(resource.LastIndexOf('/') + 1)..];
        var bootstrap = new DynamoDbBootstrapOfferClient(fixture.Table.Client, fixture.Table.Name, principal, resource, TimeProvider.System);
        using var recipient = JitRecipientKey.Create();
        Assert.Equal(LifecycleWriteOutcome.Committed, await BrokerHelloFixture.RegisterAsync(fixture.Table.Client,
            fixture.Table.Name, principal, recipient.Hello, TimeProvider.System, token));
        BootstrapOfferReceipt? receipt = null;
        await WaitAsync(async () =>
        {
            receipt = await bootstrap.TryConsumeOfferAsync(recipient.Hello, token);
            Assert.Contains(receipt.Status, new[] { BootstrapOfferStatus.NotAvailable, BootstrapOfferStatus.Consumed });
            return receipt.Status == BootstrapOfferStatus.Consumed;
        }, "one encrypted JIT offer", token);
        var envelope = Assert.IsType<JitOfferEnvelope>(receipt!.Offer);
        using (var jit = recipient.Open(envelope, resource, principal, TimeProvider.System))
        {
            Assert.True(jit.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(GitHubSimulator.Jit)));
        }
        return Assert.IsType<long>(simulated.GitHub.RegisteredRunnerId);
    }

    private static async Task<ExecutorSnapshot> WaitForRunnerAsync(MixedRuntimeFixture fixture, string deployment, RunnerProfile profile,
        CancellationToken token)
    {
        ExecutorSnapshot? running = null;
        await WaitAsync(async () =>
        {
            var sample = await fixture.Store.ReadExecutorInventoryAsync(new LifecycleProfile(deployment, profile.Id), token);
            running = sample.Executors.FirstOrDefault(executor => executor.State.Resource?.State == ExecutorResourceState.Running);
            return running is not null;
        }, $"running allocation for {profile.Id}", token);
        return running!;
    }

    private static async Task AssertJobSurvivedAsync(MixedRuntimeFixture fixture, ExecutorSnapshot old, CancellationToken token)
    {
        var readback = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(old.Binding.Profile, old.Binding.AllocationId, token));
        Assert.Equal(old.Binding, readback.Binding);
        Assert.Equal(old.State.Job, readback.State.Job);
        Assert.Equal(ExecutorResourceState.Running, readback.State.Resource?.State);
    }

    private static bool DescribedTask(IEnumerable<MixedCloudCall> calls, string clusterArn, string taskArn) =>
        calls.Any(call =>
        {
            if (call is not { Service: "ecs", Operation: "DescribeTasks" })
            {
                return false;
            }
            using var body = JsonDocument.Parse(call.Request);
            var tasks = body.RootElement.GetProperty("tasks");
            return body.RootElement.GetProperty("cluster").GetString() == clusterArn && tasks.GetArrayLength() == 1 && tasks[0].GetString() == taskArn;
        });

    private static bool IsRunnerMutation(MixedCloudCall call) => call.Operation is "RunInstances" or "RunTask" or "TerminateInstances" or "StopTask" or "CreateTags" or "TagResource";

    private static async Task WaitAsync(Func<Task<bool>> condition, string evidence, CancellationToken token)
    {
        try
        {
            while (!await condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(125), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Timed out waiting for {evidence}.");
        }
    }

    private static void AssertSafeText(string text)
    {
        foreach (string forbidden in new[] { GitHubSimulator.FirstPat, GitHubSimulator.Jit, "mixed-synthetic-collector", "private-collector-rejection" })
        {
            Assert.False(text.Contains(forbidden, StringComparison.Ordinal), "Synthetic secret appeared in controller diagnostics.");
        }
    }

    private static bool ActiveProfiles(OtlpLoopback receiver, int startingBatch, ExecutorSnapshot first, ExecutorSnapshot second) =>
        ProfileGaugeBatch(receiver, MixedOtlpAssertions.Active, 1, [first.Binding.Profile.ProfileId, second.Binding.Profile.ProfileId], startingBatch);

    private static bool ProfileGaugeBatch(OtlpLoopback receiver, string name, double number, IEnumerable<string> profiles, int startingBatch = 0) =>
        MixedOtlpAssertions.MetricPoints(receiver.Requests)
            .Where(point => point.Batch >= startingBatch && point.Name == name && point.Number == number)
            .GroupBy(point => (point.InstanceId, point.Batch))
            .Any(batch => profiles.All(profile => batch.Any(point => point.Tags["profile"] == profile)));

    private static void AssertTelemetry(MixedRuntimeFixture fixture, IReadOnlyList<OtlpCapture> requests,
        HeldEc2Launch held, ExecutorSnapshot first, ExecutorSnapshot second, string formerInstance)
    {
        var expected = fixture.Configuration.Profiles.ToDictionary(profile => profile.Id,
            profile => profile.Provisioner is Ec2ProvisionerConfiguration ? "ec2" : "fargate", StringComparer.Ordinal);
        var instances = MixedOtlpAssertions.AssertPayloads(requests, expected,
            [GitHubSimulator.FirstPat, GitHubSimulator.Jit, "mixed-synthetic-collector", "private-collector-rejection", held.ClientToken,
                held.AllocationId.ToString("D"), held.ResourceArn, first.Binding.AllocationId, first.Binding.ResourceArn, second.Binding.AllocationId, second.Binding.ResourceArn,
                "8334500001", "8334500002", "8334500003"], new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service.name"] = "scaleset-aws-controller",
                ["scaleset.deployment.name"] = fixture.Configuration.EnvironmentName,
                ["cloud.account.id"] = fixture.Configuration.Aws.Account,
                ["cloud.region"] = fixture.Configuration.Aws.Region,
            }, requireLogs: true);
        Assert.Equal(2, instances.Count);
        Assert.Contains(formerInstance, instances);
        Assert.All(requests, request => Assert.Equal("Bearer mixed-synthetic-collector", request.Authorization));
    }
}
