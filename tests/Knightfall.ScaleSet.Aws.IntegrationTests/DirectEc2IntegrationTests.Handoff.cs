using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EC2.Model;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class DirectEc2IntegrationTests
{
    [Fact]
    public async Task TwoImdsRoleSessionsCompleteExactHandoffsAndOnlyTheFinishedInstanceTerminates()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        var token = deadline.Token;
        using var cloud = await MiniStackIamFixture.CreateAsync(token);
        var role = await cloud.EnsureRunnerRoleAsync(DirectEc2Fixture.RoleName(), includeStatus: true, token);
        using var fixture = await DirectEc2Fixture.CreateAsync(cloud.Endpoint, cloud.Controller.Credentials,
            cloud.Controller.Ec2, cloud.Controller.Iam, Account, Region, role.Arn, token);
        var secret = await cloud.Controller.Secrets.CreateSecretAsync(new CreateSecretRequest
        {
            Name = "t23-gh-" + Guid.NewGuid().ToString("N"),
            ClientRequestToken = Guid.NewGuid().ToString("N"),
            SecretString = JsonSerializer.Serialize(new { token = GitHubSimulator.FirstPat }),
        }, token);
        var configuration = await fixture.ConfigurationAsync(cloud.TableName, "1", token, secret.ARN);
        var clock = new FixtureClock();
        var store = new DynamoDbLifecycleStore(cloud.Controller.DynamoDb, cloud.TableName, clock);
        var active = await ActivateInitialAsync(store, configuration, clock, token);
        using var ownership = new ControllerLeaseOwnership(store, active.Lease, active.Clock, clock, OwnerDuration, WriteReserve);
        var profile = Assert.Single(configuration.Profiles);
        var key = new LifecycleProfile(active.Lease.Deployment, profile.Id);
        var simulator = new GitHubSimulator(profile.GitHub.RunnerGroup);
        simulator.SeedScaleSet(profile.GitHub.ScaleSet, profile.GitHub.Labels);
        await using var github = await GitHubProtocolHost.StartAsync(simulator, token);
        using var http = github.CreateClient();
        var loader = new GitHubSecretLoader(cloud.Controller.Secrets);
        var githubFactory = new GitHubScaleSetClientFactory(http, timeProvider: clock);
        var credential = Assert.IsType<GitHubSecretVersion>(await loader.LoadAsync(configuration, profile.GitHub.Connection, token));
        using var remote = githubFactory.Create(credential);
        var registration = await new GitHubScaleSetRegistration(store, clock, WriteReserve, TimeSpan.FromSeconds(20))
            .ResolveAsync(configuration, key.ProfileId, remote, new GitHubRegistrationLease(active.Lease, 1, active.Clock), null, token);
        var githubBinding = Assert.IsType<GitHubScaleSetBinding>(registration.Binding);
        Assert.Equal((GitHubSimulator.GroupId, GitHubSimulator.SetId), (githubBinding.RunnerGroupId, githubBinding.ScaleSetId));
        var catalog = await BindCatalogAsync(store, key, active.Lease, token);

        using var inspector = fixture.Inspector(clock);
        using var provider = fixture.Provider(configuration, key, clock);
        var backend = Backend(configuration, profile, key, inspector, provider, store, clock);
        // The first native launch attempt is definitively rejected. Two distinct allocations must then settle.
        for (int tick = 0; tick < 3; tick++)
        {
            await ReconcileAsync(backend, active, catalog, token);
        }
        var inventory = await backend.ReadAsync(active.Lease, token);
        Assert.True(inventory.IsComplete);
        var resources = inventory.Resources.OrderBy(resource => resource.ResourceArn, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, resources.Length);
        var first = Assert.IsType<ExecutorBinding>(resources[0].Binding);
        var second = Assert.IsType<ExecutorBinding>(resources[1].Binding);

        using var firstRole = await cloud.AssumeRunnerAsync(role, InstanceId(first), token);
        using var secondRole = await cloud.AssumeRunnerAsync(role, InstanceId(second), token);
        Assert.Equal(role.RoleId + ":" + InstanceId(first), firstRole.AwsUserId);
        Assert.Equal(role.RoleId + ":" + InstanceId(second), secondRole.AwsUserId);
        Assert.NotEqual(firstRole.AwsUserId, secondRole.AwsUserId);
        await using (var crossWired = await DirectEc2ImdsHost.StartAsync(InstanceId(first), Account, Region,
            role.Name, secondRole.Credentials, token))
        {
            using var metadataHttp = Ec2ImdsV2.CreateHttpClient();
            var verifier = new Ec2BootstrapIdentity(new Ec2ImdsV2(metadataHttp, crossWired.Endpoint, clock),
                credentials => new AmazonSecurityTokenServiceClient(credentials, fixture.StsConfig()));
            await Assert.ThrowsAsync<IOException>(() => verifier.VerifyAsync(new Ec2BootstrapAuthority(Account, Region, role.Arn, role.RoleId), token));
            AssertImdsReads(crossWired, role.Name, 1);
        }

        var target = new RunnerBootstrapTarget(key.Deployment, key.ProfileId, catalog.Binding.ScopeIdentity, githubBinding.ScaleSetId);
        await using var firstBootstrap = await DirectEc2BootstrapExecution.StartAsync(cloud, fixture, role, firstRole, first, target, clock, token);
        await using var secondBootstrap = await DirectEc2BootstrapExecution.StartAsync(cloud, fixture, role, secondRole, second, target, clock, token);
        await firstBootstrap.WaitForReceiptAttemptAsync(token);
        await secondBootstrap.WaitForReceiptAttemptAsync(token);
        Assert.Equal(BootstrapPrincipal(firstRole.AwsUserId, first), firstBootstrap.Identity);
        Assert.Equal(BootstrapPrincipal(secondRole.AwsUserId, second), secondBootstrap.Identity);

        var handoff = new GitHubJitHandoff(store, clock,
            new GitHubJitTiming(WriteReserve, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
        var coordinator = new ControllerJitCoordinator(store, new AwsBootstrapPrincipalResolver(cloud.Controller.Iam), handoff);
        long firstRunner = await IssueAsync(resources[0], firstRole.AwsUserId, token);
        long secondRunner = await IssueAsync(resources[1], secondRole.AwsUserId, token);
        Assert.NotEqual(firstRunner, secondRunner);
        Assert.Equal(2, simulator.Requests.Count(request => request.Method == "POST" && request.Path.EndsWith("/generatejitconfig", StringComparison.Ordinal)));

        await RejectPeerOfferAsync(firstRole.Clients.DynamoDb, secondRole.AwsUserId, token);
        await RejectPeerOfferAsync(secondRole.Clients.DynamoDb, firstRole.AwsUserId, token);
        firstBootstrap.StartReceipt(firstRunner);
        secondBootstrap.StartReceipt(secondRunner);
        await ObserveStatusAsync(first, firstRunner, exited: false, token);
        await ObserveStatusAsync(second, secondRunner, exited: false, token);
        Assert.Empty(await ReadOfferAsync(firstRole.AwsUserId, token));
        Assert.Empty(await ReadOfferAsync(secondRole.AwsUserId, token));

        var definition = new GitHubListenerProfile(configuration, key.ProfileId, githubBinding,
            githubFactory.Create(credential), active.Lease.Fence,
            _ => Task.FromResult<GitHubRegistrationLease?>(new GitHubRegistrationLease(active.Lease, 1, active.Clock)),
            _ => Task.FromResult(new ListenerCapacity(4, 0)), (_, _) => Task.FromResult(true), ownership.OwnershipLost);
        using var lane = new GitHubListenerSupervisor(store, clock,
            new GitHubListenerTiming(WriteReserve, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2))).Register(definition);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        const long requestId = 9401;
        simulator.StartJob(requestId, firstRunner);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        var started = await ReadExecutorAsync(first, token);
        Assert.Equal((requestId, firstRunner, ExecutorJobPhase.Started),
            (started.State.Job?.RequestId, started.State.Job?.RunnerId, started.State.Job?.Phase));

        await backend.CleanupAsync(active, catalog, started, ownership.OwnershipLost, token);
        Assert.Empty(fixture.Wire.Calls("TerminateInstances"));

        simulator.Complete(requestId, firstRunner);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await lane.StepAsync(token)).Step?.Status);
        firstBootstrap.FinishJob();
        Assert.Equal(RunnerBootstrapOutcome.Completed, await firstBootstrap.Outcome.WaitAsync(token));
        await ObserveStatusAsync(first, firstRunner, exited: true, token);
        var complete = await ReadExecutorAsync(first, token);
        Assert.Equal((requestId, firstRunner, ExecutorJobPhase.Completed),
            (complete.State.Job?.RequestId, complete.State.Job?.RunnerId, complete.State.Job?.Phase));
        Assert.Equal(ExecutorProcessState.Exited, complete.State.Process?.State);

        clock.Set(clock.GetUtcNow().Add(Timing.LogFlushGrace).AddSeconds(1));
        await backend.CleanupAsync(active, catalog, complete, ownership.OwnershipLost, token);
        Assert.Equal(InstanceId(first), Assert.Single(fixture.Wire.Calls("TerminateInstances"))["InstanceId.1"]);
        var native = (await cloud.Controller.Ec2.DescribeInstancesAsync(new DescribeInstancesRequest
        {
            InstanceIds = [InstanceId(first), InstanceId(second)],
        }, token)).Reservations.SelectMany(reservation => reservation.Instances).ToDictionary(instance => instance.InstanceId, StringComparer.Ordinal);
        Assert.Equal("terminated", native[InstanceId(first)].State.Name.Value);
        Assert.Equal("running", native[InstanceId(second)].State.Name.Value);

        await using var route = await MiniStackRunnerEventRoute.CreateAsync(token);
        string notice = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["source"] = "aws.ec2",
            ["detail-type"] = "EC2 Instance State-change Notification",
            ["account"] = Account,
            ["region"] = Region,
            ["resources"] = new[] { first.ResourceArn },
            ["detail"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instance-id"] = InstanceId(first), ["state"] = "terminated",
            },
        });
        await route.PublishAsync(notice, token);
        var consumer = new Ec2StateChangeConsumer(configuration, route.Sqs, route.QueueUrl, clock);
        Assert.Equal(1, await consumer.ReceiveOnceAsync(async (hint, cancellationToken) =>
        {
            Assert.Equal(new Ec2StateChangeHint(InstanceId(first), first.ResourceArn), hint);
            var readback = (await backend.ReadAsync(active.Lease, cancellationToken)).Resources.Single(resource => resource.ResourceArn == hint.ResourceArn);
            Assert.Equal(first, readback.Binding);
            var observation = Assert.IsType<ExecutorResourceObservation>(readback.Observation);
            Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, observation.State);
            return await store.ObserveResourceAsync(active.Lease.Fence, first, observation, cancellationToken) == LifecycleWriteOutcome.Committed;
        }, token));
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, (await ReadExecutorAsync(first, token)).State.Resource?.State);
        Assert.Equal(ExecutorProcessState.Ready, (await ReadExecutorAsync(second, token)).State.Process?.State);

        secondBootstrap.FinishJob();
        Assert.Equal(RunnerBootstrapOutcome.Completed, await secondBootstrap.Outcome.WaitAsync(token));
        await ObserveStatusAsync(second, secondRunner, exited: true, token);
        Assert.Single(fixture.Wire.Calls("TerminateInstances"));
        AssertImdsReads(firstBootstrap.Imds, role.Name, 3);
        AssertImdsReads(secondBootstrap.Imds, role.Name, 3);

        async Task<long> IssueAsync(RunnerBackendResource resource, string userId, CancellationToken cancellationToken)
        {
            var executor = Assert.IsType<ExecutorBinding>(resource.Binding);
            var issued = await coordinator.AdvanceAsync(configuration, ownership,
                new BootstrapProviderObservation(executor, Assert.IsType<string>(resource.BootstrapCredentialArn),
                    Assert.IsType<ExecutorResourceObservation>(resource.Observation).ObservedAt), remote, cancellationToken);
            Assert.Equal(GitHubJitStatus.Offered, issued.Status);
            var row = Assert.IsType<JitHandoffSnapshot>(await store.ReadJitHandoffAsync(key, Guid.ParseExact(executor.AllocationId, "D"), cancellationToken));
            Assert.Equal(userId, row.Identity.AwsUserId);
            Assert.Equal(executor.ResourceArn, row.Identity.ResourceArn);
            Assert.Equal(executor.Incarnation, row.Identity.Incarnation);
            var offer = await ReadOfferAsync(userId, cancellationToken);
            Assert.NotEmpty(offer);
            Assert.DoesNotContain(offer.Values, value => value.S?.Contains(GitHubSimulator.Jit, StringComparison.Ordinal) == true);
            return Assert.IsType<long>(issued.RunnerId);
        }

        async Task ObserveStatusAsync(ExecutorBinding binding, long runnerId, bool exited, CancellationToken cancellationToken)
        {
            var row = Assert.IsType<JitHandoffSnapshot>(await store.ReadJitHandoffAsync(key, Guid.ParseExact(binding.AllocationId, "D"), cancellationToken));
            JitEc2BootstrapStatus? status;
            do
            {
                status = await store.ReadEc2BootstrapStatusAsync(row, cancellationToken);
                if (status is null || status.Exited != exited)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                }
            }
            while (status is null || status.Exited != exited);
            Assert.Equal((runnerId, exited ? 2L : 1L, row.OfferId), (status.RunnerId, status.Sequence, status.OfferId));
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveProcessAsync(active.Lease.Fence, binding,
                new ExecutorProcessObservation(status.RunnerId, exited ? ExecutorProcessState.Exited : ExecutorProcessState.Ready,
                    status.ObservedAt, Guid.NewGuid()), cancellationToken));
        }

        async Task<ExecutorSnapshot> ReadExecutorAsync(ExecutorBinding binding, CancellationToken cancellationToken) =>
            Assert.IsType<ExecutorSnapshot>(await store.ReadExecutorAsync(key, binding.AllocationId, cancellationToken));

        async Task<Dictionary<string, AttributeValue>> ReadOfferAsync(string userId, CancellationToken cancellationToken) =>
            (await cloud.Controller.DynamoDb.GetItemAsync(cloud.TableName, RowKey(LifecycleKey.Offer(userId)), true, cancellationToken)).Item ?? [];

        async Task RejectPeerOfferAsync(IAmazonDynamoDB peer, string userId, CancellationToken cancellationToken)
        {
            var key = RowKey(LifecycleKey.Offer(userId));
            var read = await Assert.ThrowsAsync<AmazonDynamoDBException>(() =>
                peer.GetItemAsync(cloud.TableName, key, true, cancellationToken));
            Assert.Equal("AccessDeniedException", read.ErrorCode);
            var failure = await Assert.ThrowsAsync<AmazonDynamoDBException>(() => peer.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = cloud.TableName,
                Key = key,
                ReturnValues = ReturnValue.ALL_OLD,
            }, cancellationToken));
            Assert.Equal("AccessDeniedException", failure.ErrorCode);
            Assert.NotEmpty(await ReadOfferAsync(userId, cancellationToken));
        }
    }

    private static (string UserId, string ResourceArn) BootstrapPrincipal(string principal, ExecutorBinding executor) =>
        (principal, executor.ResourceArn);

    private static void AssertImdsReads(DirectEc2ImdsHost host, string roleName, int verifications)
    {
        ImdsRequest[] verification =
        [
            new("PUT", DirectEc2ImdsHost.TokenPath, true),
            new("GET", DirectEc2ImdsHost.DocumentPath, true),
            new("GET", DirectEc2ImdsHost.CredentialsPrefix + roleName, true),
        ];
        Assert.Equal(Enumerable.Range(0, verifications).SelectMany(_ => verification), host.Requests);
    }

    internal static Dictionary<string, AttributeValue> RowKey(LifecycleKey key) => new(StringComparer.Ordinal)
    {
        ["PK"] = new AttributeValue(key.PartitionKey),
        ["SK"] = new AttributeValue(key.SortKey),
    };
}
