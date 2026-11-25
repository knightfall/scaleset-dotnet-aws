using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class ControllerProfileSessionTests
{
    private const string FirstProfile = "linux-shell";
    private const string SecondProfile = "linux-batch";
    private const string Scope = "https://github.com/enterprises/engineering";
    private const string GroupsPath = GitHubWire.ServicePath + "/_apis/runtime/runnergroups/";
    private const string SetsPath = GitHubWire.ServicePath + "/_apis/runtime/runnerscalesets";
    private const long SecondSet = 7_000_000_002;
    private const long SecondGroup = 8_000_000_003;
    private const string SuccessorTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private static readonly Guid SecondSession = Guid.Parse("51688c9f-f017-4a9a-b59f-77ed34074c1c");
    private static readonly string[] CurrentStage = ["AWSCURRENT"];
    private static readonly string[] OfferLabels = ["linux"];
    private static readonly GitHubListenerTiming Timing = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(15));
    private static readonly TimeSpan PollObservationBudget = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(null)]
    [InlineData("AccessDeniedException")]
    [InlineData("ExpiredTokenException")]
    [InlineData("ClientFailure")]
    [InlineData("RotationRejected")]
    [InlineData("UnexpectedPollFailure")]
    [InlineData("UnexpectedAcknowledgementFailure")]
    [InlineData("PermanentRejection")]
    [InlineData("ReplacedOwner")]
    public async Task IndependentLanesRecoverOnlyWithExactSessionCursorAndOwner(string? failure)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // Table setup and earlier requests can consume most of the budget on a shared CI runner.
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        var token = budget.Token;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var config = await ConfigurationAsync(token);
        var firstProfile = config.Profiles.Single(profile => profile.Id == FirstProfile);
        var secondProfile = config.Profiles.Single(profile => profile.Id == SecondProfile);
        using var ownership = await SetupAuthorityAsync(store, config, clock, token);
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(config);
        var firstKey = new LifecycleProfile(deployment, FirstProfile);
        var secondKey = new LifecycleProfile(deployment, SecondProfile);
        var checkpointKey = LifecycleKey.Checkpoint(firstKey, GitHubWire.SessionId);
        var current = Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority());
        var inventory = new ExecutorInventoryObservation(clock.Now, IsComplete: true, BackendCapacityCeiling: 2, BackendLaunchHeadroom: 2);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(firstKey, current.Lease.Fence, null, inventory, token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(secondKey, current.Lease.Fence, null, inventory, token));
        var firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRecovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFault = new TaskCompletionSource<GitHubListenerLaneStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHeld = new TaskCompletionSource<GitHubListenerLaneStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstPolls = 0;
        int firstCheckpointReadsAtFault = 0;
        bool deniesCheckpoint = failure is "AccessDeniedException" or "RotationRejected" or "PermanentRejection" or "ReplacedOwner";
        bool ambiguousAck = failure == "UnexpectedAcknowledgementFailure";

        using var wire = new GitHubWire(async (request, cancellation) =>
        {
            string path = request.Uri.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == GroupsPath)
            {
                var group = request.Uri.Query.Contains("Batch", StringComparison.Ordinal) ? secondProfile : firstProfile;
                return GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    count = 1, value = new[] { new { id = GroupId(group), name = group.GitHub.RunnerGroup } },
                }));
            }
            if (request.Method == HttpMethod.Get && path == SetsPath)
            {
                var set = request.Uri.Query.Contains(SecondGroup.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    request.Uri.Query.Contains(SecondProfile, StringComparison.Ordinal) ? secondProfile : firstProfile;
                return GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { count = 1, value = new[] { SetBody(set) } }));
            }
            if (request.Method == HttpMethod.Get && (path == SetPath(firstProfile) || path == SetPath(secondProfile)))
            {
                return GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(SetBody(path == SetPath(firstProfile) ? firstProfile : secondProfile)));
            }
            if (request.Method == HttpMethod.Post && (path == SessionPath(firstProfile) || path == SessionPath(secondProfile)))
            {
                bool isFirst = path == SessionPath(firstProfile);
                var profile = isFirst ? firstProfile : secondProfile;
                var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(isFirst ? firstKey : secondKey, cancellation));
                Assert.Equal(SetId(profile), catalog.Binding.ScaleSetId);
                Assert.Equal(GroupId(profile), catalog.Binding.RunnerGroupId);
                using var sent = JsonDocument.Parse(request.Body!);
                return GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sessionId = isFirst ? GitHubWire.SessionId : SecondSession,
                    ownerName = sent.RootElement.GetProperty("ownerName").GetString(),
                    messageQueueUrl = "https://" + GitHubWire.QueueHost + "/" + profile.Id,
                    messageQueueAccessToken = GitHubWire.QueueToken,
                    statistics = new { totalAssignedJobs = 0 },
                }));
            }
            if (request.Method == HttpMethod.Get && (path == "/" + FirstProfile || path == "/" + SecondProfile))
            {
                if (path == "/" + FirstProfile)
                {
                    switch (Interlocked.Increment(ref firstPolls))
                    {
                        case 1:
                            if (ambiguousAck)
                            {
                                firstPoll.TrySetResult();
                            }
                            return Delivery(1);
                        case 2:
                            Assert.Contains("lastMessageId=1", request.Uri.Query, StringComparison.Ordinal);
                            firstPoll.TrySetResult();
                            if (failure == "UnexpectedPollFailure")
                            {
                                throw new InvalidOperationException("Synthetic unexpected poll fault.");
                            }
                            if (deniesCheckpoint)
                            {
                                Volatile.Write(ref firstCheckpointReadsAtFault, CheckpointReads());
                                table.Wire.DenyGetItem(checkpointKey);
                                if (failure == "RotationRejected")
                                {
                                    clock.Now = clock.Now.AddSeconds(61);
                                }
                            }
                            else if (failure == "ClientFailure")
                            {
                                table.Wire.FailNextGetItem(checkpointKey, new AmazonClientException("Synthetic SDK client fault."));
                            }
                            else if (failure == "ExpiredTokenException")
                            {
                                table.Wire.FailNextGetItem(checkpointKey, HttpStatusCode.Forbidden, failure);
                            }
                            return new HttpResponseMessage(HttpStatusCode.Accepted);
                        case 3:
                            Assert.Contains("lastMessageId=1", request.Uri.Query, StringComparison.Ordinal);
                            return Delivery(2, requestId: 71);
                        case 4:
                            Assert.Contains("lastMessageId=2", request.Uri.Query, StringComparison.Ordinal);
                            firstRecovered.TrySetResult();
                            break;
                        default:
                            throw new InvalidOperationException("A suspended fixture poll was duplicated.");
                    }
                }
                else
                {
                    if (!request.Uri.Query.Contains("lastMessageId=11", StringComparison.Ordinal))
                    {
                        return Delivery(11, requestId: 81);
                    }
                    secondPoll.TrySetResult();
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            if (request.Method == HttpMethod.Post && (path == SetPath(firstProfile) + "/acquirejobs" || path == SetPath(secondProfile) + "/acquirejobs"))
            {
                int requestId = path == SetPath(firstProfile) + "/acquirejobs" ? 71 : 81;
                Assert.Equal(requestId, Assert.Single(Assert.IsType<int[]>(JsonSerializer.Deserialize<int[]>(request.Body!))));
                return GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { count = 1, value = new[] { requestId } }));
            }
            if (request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.QueueHost)
            {
                if (ambiguousAck && path == "/" + FirstProfile + "/1")
                {
                    throw new InvalidOperationException("Synthetic lost acknowledgement response.");
                }
                Assert.Contains(path, new[] { "/" + FirstProfile + "/1", "/" + FirstProfile + "/2", "/" + SecondProfile + "/11" });
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Delete && (path == SessionPath(firstProfile) + $"/{GitHubWire.SessionId:D}" ||
                                                        path == SessionPath(secondProfile) + $"/{SecondSession:D}"))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected fixture GitHub operation.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        using var aws = CreateSecretWire(config);
        using var sdk = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, aws.Configure(new AmazonSecretsManagerConfig()));
        var secrets = new GitHubSecretLoader(sdk);
        var clients = new GitHubScaleSetClientFactory(httpClient: http, timeProvider: clock);
        var rotation = new GitHubCredentialRotator(secrets, clients);
        var supervisor = new GitHubListenerSupervisor(store, clock, Timing, rotation);
        var registration = new GitHubScaleSetRegistration(store, clock, Timing.WriteReserve, Timing.SessionSendWindow);
        var cooldown = new GitHubCredentialCooldown(clock, Timing.UnauthorizedCooldown);
        await using var first = new ControllerProfileSession(config, firstProfile, 1, ownership, store, secrets, clients, registration, supervisor, rotation, cooldown, clock);
        await using var second = new ControllerProfileSession(config, secondProfile, 1, ownership, store, secrets, clients, registration, supervisor, rotation, cooldown, clock);
        first.ListenerHealthChanged = () =>
        {
            var status = first.ListenerResult?.Status;
            if (status is GitHubListenerLaneStatus.StorageRejected or GitHubListenerLaneStatus.StorageUnavailable or GitHubListenerLaneStatus.Failed)
            {
                firstFault.TrySetResult(status.Value);
            }
            if (status is GitHubListenerLaneStatus.RecoveryRequired or GitHubListenerLaneStatus.OwnershipUnavailable)
            {
                firstHeld.TrySetResult(status.Value);
            }
        };

        var firstBinding = Assert.IsType<GitHubScaleSetBinding>(await first.EnsureRegisteredAsync(token));
        await ObservePollAsync(firstPoll.Task, FirstProfile, first);
        if (failure is not null)
        {
            var expected = deniesCheckpoint ? GitHubListenerLaneStatus.StorageRejected : failure switch
            {
                "ExpiredTokenException" or "ClientFailure" => GitHubListenerLaneStatus.StorageUnavailable,
                _ => GitHubListenerLaneStatus.Failed,
            };
            Assert.Equal(expected, await firstFault.Task.WaitAsync(PollObservationBudget, token));
        }
        Assert.Equal(SecondSet, (await second.EnsureRegisteredAsync(token))?.ScaleSetId);
        await ObservePollAsync(secondPoll.Task, SecondProfile, second);
        Assert.True(second.IsListenerAvailable);
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SetPath(secondProfile) + "/acquirejobs");

        if (deniesCheckpoint)
        {
            using var retryBudget = new CancellationTokenSource(PollObservationBudget);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(retryBudget.Token, token);
            while (CheckpointReads() <= Volatile.Read(ref firstCheckpointReadsAtFault) + 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), wait.Token);
            }
            Assert.False(first.IsListenerAvailable);
            Assert.Equal("listener_storage_rejected", first.ListenerFailure);
            Assert.Equal(2, Volatile.Read(ref firstPolls));
        }

        if (failure == "ReplacedOwner")
        {
            var successorClock = new ContractClock { Now = current.Lease.ValidUntil.AddSeconds(2) };
            var successorStore = new DynamoDbLifecycleStore(table.Client, table.Name, successorClock);
            var successor = await successorStore.TryAcquireLeaseAsync(deployment, "successor", SuccessorTask,
                ControllerLease.CurrentWriterProtocol, successorClock.Now, successorClock.Now.AddMinutes(5), token);
            Assert.Equal(LifecycleWriteOutcome.Committed, successor.Outcome);
            table.Wire.RestoreGetItem();
            Assert.Equal(GitHubListenerLaneStatus.OwnershipUnavailable, await firstHeld.Task.WaitAsync(PollObservationBudget, token));
            Assert.False(first.IsListenerAvailable);
            Assert.Equal(2, Volatile.Read(ref firstPolls));
            await ownership.EndAuthorityAsync();
            await first.JoinMutatorsAsync(token);
            await second.JoinMutatorsAsync(token);
            await first.CloseOwnedSessionsAsync(token);
            await second.CloseOwnedSessionsAsync(token);
            Assert.Equal(SessionPhase.Active, (await store.ReadSessionAsync(firstKey, token))?.Phase);
            Assert.DoesNotContain(wire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.ServiceHost);
            return;
        }

        bool permanent = failure == "PermanentRejection";
        if (ambiguousAck)
        {
            Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, await firstHeld.Task.WaitAsync(PollObservationBudget, token));
            Assert.False(first.IsListenerAvailable);
            var session = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(firstKey, token));
            var checkpoint = new GitHubListenerCheckpointStore(store, Assert.IsType<OwnedSessionFence>(session.Fence), clock);
            var saved = Assert.IsType<ScaleSetListenerCheckpoint>(await checkpoint.ReadAsync(checkpoint.SessionKey, token));
            Assert.Null(saved.LastAcknowledgedMessageId);
            Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, saved.Pending?.Phase);
            Assert.Equal(1L, saved.Pending?.Delivery.Message?.MessageId);
            Assert.Equal(1, Volatile.Read(ref firstPolls));
            Assert.Single(wire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == "/" + FirstProfile + "/1");
        }
        else if (!permanent)
        {
            table.Wire.RestoreGetItem();
            await ObservePollAsync(firstRecovered.Task, FirstProfile, first);
            Assert.True(first.IsListenerAvailable);
            Assert.Equal(4, Volatile.Read(ref firstPolls));
            Assert.Single(wire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SetPath(firstProfile) + "/acquirejobs");
        }

        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionPath(firstProfile));
        Assert.Equal(GitHubWire.SessionId, (await store.ReadSessionAsync(firstKey, token))?.SessionId);
        Assert.Equal(firstBinding, await first.EnsureRegisteredAsync(token));
        var reconciliation = Assert.IsType<GitHubScaleSetConnection>(await first.GetConnectionAsync(token));
        Assert.Equal(firstBinding, (await reconciliation.LookupAsync(firstProfile.GitHub, firstBinding, token)).Binding);
        Assert.Equal(4, wire.Requests.Count(request => request.Uri.AbsolutePath == "/actions/runner-registration"));

        await Task.WhenAll(first.StopAdmissionsAsync(token), second.StopAdmissionsAsync(token));
        if (permanent)
        {
            await first.JoinMutatorsAsync(token).WaitAsync(PollObservationBudget, token);
            Assert.Equal(2, Volatile.Read(ref firstPolls));
        }
        int requests = table.Wire.Requests.Count;
        Assert.Equal(new ListenerCapacity(0, 0, IsReady: false), await first.ReadListenerCapacityAsync(token));
        Assert.Equal(requests, table.Wire.Requests.Count);
        await Task.WhenAll(first.CancelPollsAsync(token), second.CancelPollsAsync(token));
        await Task.WhenAll(first.JoinMutatorsAsync(token), second.JoinMutatorsAsync(token));
        Assert.DoesNotContain(wire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.ServiceHost);

        await Task.WhenAll(first.CloseOwnedSessionsAsync(token), second.CloseOwnedSessionsAsync(token));
        Assert.Equal(SessionPhase.Retired, (await store.ReadSessionAsync(firstKey, token))?.Phase);
        Assert.Equal(SessionPhase.Retired, (await store.ReadSessionAsync(secondKey, token))?.Phase);
        var deletes = wire.Requests.Where(request => request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.ServiceHost)
            .Select(request => request.Uri.AbsolutePath).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.Contains(SessionPath(firstProfile) + $"/{GitHubWire.SessionId:D}", deletes);
        Assert.Contains(SessionPath(secondProfile) + $"/{SecondSession:D}", deletes);

        int CheckpointReads() => table.Wire.Requests.Count(call => call.Operation == "GetItem" &&
            call.Request.GetProperty("Key").GetProperty("SK").GetProperty("S").GetString() == checkpointKey.SortKey);

        async Task ObservePollAsync(Task observation, string profile, ControllerProfileSession session)
        {
            try
            {
                await observation.WaitAsync(PollObservationBudget, token);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"No poll from {profile} within {PollObservationBudget}. Lane status is {session.ListenerResult?.Status}. " +
                    string.Join(", ", wire.Requests.Select(request => request.Method + " " + request.Uri.AbsolutePath)));
            }
        }
    }

    private static HttpResponseMessage Delivery(long messageId, long? requestId = null) => GitHubWire.Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new
        {
            messageId,
            messageType = "RunnerScaleSetJobMessages",
            body = requestId is long offered
                ? JsonSerializer.Serialize(new[] { new { messageType = "JobAvailable", runnerRequestId = offered, requestLabels = OfferLabels } })
                : "[]",
            statistics = new { totalAssignedJobs = 0 },
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleGenerationAndReplacedEpochNeverReadSecretsOrOpenAPollLane(bool replaceEpoch)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock();
        var config = await ConfigurationAsync(token);
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        using var ownership = await SetupAuthorityAsync(store, config, clock, token);
        var current = Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority());
        if (replaceEpoch)
        {
            var newerClock = new ContractClock { Now = current.Lease.ValidUntil.AddSeconds(2) };
            var newerStore = new DynamoDbLifecycleStore(table.Client, table.Name, newerClock);
            var successor = await newerStore.TryAcquireLeaseAsync(current.Lease.Deployment, "successor", SuccessorTask,
                ControllerLease.CurrentWriterProtocol, newerClock.Now, newerClock.Now.AddMinutes(5), token);
            Assert.Equal(LifecycleWriteOutcome.Committed, successor.Outcome);
            Assert.NotNull(ownership.ReadAuthority());
        }

        using var aws = new AwsWireTransport(_ => throw new InvalidOperationException("A stale owner read a secret."));
        using var sdk = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, aws.Configure(new AmazonSecretsManagerConfig()));
        using var wire = new GitHubWire((_, _) => throw new InvalidOperationException("A stale owner called GitHub."));
        using var http = new HttpClient(wire, disposeHandler: false);
        var secrets = new GitHubSecretLoader(sdk);
        var clients = new GitHubScaleSetClientFactory(httpClient: http, timeProvider: clock);
        var rotation = new GitHubCredentialRotator(secrets, clients);
        await using var session = new ControllerProfileSession(config, config.Profiles[0], replaceEpoch ? 1 : 2, ownership, store, secrets, clients,
            new GitHubScaleSetRegistration(store, clock, Timing.WriteReserve, Timing.SessionSendWindow),
            new GitHubListenerSupervisor(store, clock, Timing, rotation), rotation,
            new GitHubCredentialCooldown(clock, Timing.UnauthorizedCooldown), clock);

        Assert.Null(await session.EnsureRegisteredAsync(token));
        Assert.Null(await session.GetConnectionAsync(token));
        Assert.Equal(new ListenerCapacity(0, 0, IsReady: false), await session.ReadListenerCapacityAsync(token));
        Assert.Empty(aws.Requests);
        Assert.Empty(wire.Requests);
    }

    private static AwsWireTransport CreateSecretWire(RuntimeConfiguration configuration) => new(_ => AwsWireTransport.Json(JsonSerializer.Serialize(new
    {
        ARN = configuration.GitHub["engineering"].SecretArn,
        VersionId = "0123456789abcdef0123456789abcdef",
        VersionStages = CurrentStage,
        SecretString = JsonSerializer.Serialize(new { token = "ghp_FixtureOnlyNeverValid" }),
    })));

    private static object SetBody(RunnerProfile profile) => new
    {
        id = SetId(profile),
        name = profile.GitHub.ScaleSet,
        runnerGroupId = GroupId(profile),
        labels = profile.GitHub.Labels.Select(name => new { name, type = "System" }).ToArray(),
    };

    private static long SetId(RunnerProfile profile) => profile.Id == FirstProfile ? GitHubWire.SetId : SecondSet;

    private static long GroupId(RunnerProfile profile) => profile.Id == FirstProfile ? GitHubWire.GroupId : SecondGroup;

    private static string SetPath(RunnerProfile profile) => SetsPath + "/" + SetId(profile).ToString(CultureInfo.InvariantCulture);

    private static string SessionPath(RunnerProfile profile) => SetPath(profile) + "/sessions";

    private static async Task<RuntimeConfiguration> ConfigurationAsync(CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken))!;
        var spec = root["spec"]!;
        var connection = spec["github"]!["engineering"]!;
        connection["scopeURL"] = Scope;
        connection["auth"]!["type"] = "enterprise-pat";
        var profiles = spec["profiles"]!.AsArray();
        var second = profiles[0]!.DeepClone();
        second["id"] = SecondProfile;
        second["github"]!["runnerGroup"] = "Batch";
        second["github"]!["scaleSet"] = SecondProfile;
        second["github"]!["labels"] = new JsonArray(SecondProfile);
        profiles.Add(second);
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static async Task<ControllerLeaseOwnership> SetupAuthorityAsync(DynamoDbLifecycleStore store, RuntimeConfiguration configuration,
        ContractClock clock, CancellationToken cancellationToken)
    {
        var acquired = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), "controller-fixture",
            MiniStackTable.FirstTask, ControllerLease.CurrentWriterProtocol, clock.Now, clock.Now.AddMinutes(4), cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquired.Outcome);
        var proof = new ControllerClockEvidence(clock.Now, clock.Now.AddMinutes(3), TimeSpan.FromSeconds(1));
        var recovery = new ControllerLeaseRecoveryCoordinator(store, new NoPredecessor(), clock, Timing.WriteReserve);
        var promoted = await recovery.TryActivateAsync(Assert.IsType<ControllerLease>(acquired.Lease), configuration, proof, [], cancellationToken);
        var active = Assert.IsType<ControllerLease>(promoted.Lease);
        Assert.Equal(ControllerLeaseMode.Active, active.Mode);
        CatalogRolloutChange rollout;
        do
        {
            rollout = await store.AdvanceCatalogRolloutAsync(active.Fence, configuration, 1, cancellationToken);
        } while (rollout.Status == CatalogRolloutStatus.Staging);
        Assert.Equal(CatalogRolloutStatus.Applied, rollout.Status);
        return new ControllerLeaseOwnership(store, active, proof, clock, TimeSpan.FromMinutes(4), Timing.WriteReserve);
    }

    private sealed class NoPredecessor : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The first lease has no predecessor.");
    }
}
