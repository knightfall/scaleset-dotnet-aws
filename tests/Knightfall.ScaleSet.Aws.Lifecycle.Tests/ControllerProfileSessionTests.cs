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

    [Theory]
    [InlineData("Replacement")]
    [InlineData("AcknowledgementSettledBeforeRotation")]
    [InlineData("AcknowledgementUnauthorized")]
    [InlineData("RefreshUnauthorized")]
    [InlineData("BindingMismatch")]
    [InlineData("CloseUnauthorized")]
    [InlineData("CloseNotFound")]
    [InlineData("CloseResponseLost")]
    [InlineData("SessionlessReplacement")]
    [InlineData("SessionlessRetiredReplacement")]
    [InlineData("SessionlessOpenIntent")]
    [InlineData("SessionlessMutationUnauthorized")]
    public async Task RecoveredStorageRotatesOnlyAtAnAuthorizedSessionBoundary(string outcome)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        var token = budget.Token;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var config = await ConfigurationAsync(token);
        var profile = config.Profiles.Single(profile => profile.Id == FirstProfile);
        using var ownership = await SetupAuthorityAsync(store, config, clock, token);
        var current = Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority());
        var key = new LifecycleProfile(current.Lease.Deployment, FirstProfile);
        var checkpointKey = LifecycleKey.Checkpoint(key, GitHubWire.SessionId);
        var inventory = new ExecutorInventoryObservation(clock.Now, IsComplete: true, BackendCapacityCeiling: 2, BackendLaunchHeadroom: 2);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(key, current.Lease.Fence, null, inventory, token));
        bool sessionless = outcome.StartsWith("Sessionless", StringComparison.Ordinal);
        bool resumeAcknowledgement = outcome == "AcknowledgementSettledBeforeRotation";
        bool replacementOpens = outcome is "Replacement" or "AcknowledgementSettledBeforeRotation" or
            "SessionlessReplacement" or "SessionlessRetiredReplacement";
        Guid? priorSession = sessionless && outcome != "SessionlessRetiredReplacement" ? null : GitHubWire.SessionId;
        if (sessionless)
        {
            var catalog = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(key, token));
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindCatalogScaleSetAsync(
                current.Lease.Fence, catalog, GitHubWire.GroupId, GitHubWire.SetId, token));
            if (outcome is "SessionlessOpenIntent" or "SessionlessRetiredReplacement")
            {
                var open = await store.BeginSessionOpenAsync(new SessionBinding(key, GitHubRoutingIdentity.Scope(Scope), GitHubWire.SetId),
                    current.Lease.Fence, null, "prior-fixture", clock.Now, clock.Now.AddSeconds(20), token);
                var opening = Assert.IsType<SessionSendAuthorization>(open.Permission);
                Assert.True(opening.TryConsume(out _));
                if (outcome == "SessionlessRetiredReplacement")
                {
                    var active = await store.ConfirmSessionOpenedAsync(opening, GitHubWire.SessionId, token);
                    var close = await store.BeginSessionCloseAsync(Assert.IsType<SessionRecord>(active.Session), current.Lease.Fence,
                        SessionCloseKind.OwnedClient, clock.Now, clock.Now.AddSeconds(20), token);
                    var closing = Assert.IsType<SessionSendAuthorization>(close.Permission);
                    Assert.True(closing.TryConsume(out _));
                    Assert.Equal(LifecycleWriteOutcome.Committed, (await store.ConfirmSessionRetiredAsync(closing, token)).Outcome);
                }
            }
            table.Wire.DenyGetItem(LifecycleKey.Session(key));
        }
        var storageDenied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldUnauthorized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<GitHubListenerLaneResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newPollAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string sessionPath = SessionPath(profile);
        string closePath = sessionPath + $"/{GitHubWire.SessionId:D}";
        const string oldQueue = "/old-queue";
        const string newQueue = "/replacement-queue";
        int oldPolls = 0;
        int replacementPolls = 0;

        using var previousWire = new GitHubWire(async (request, cancellation) =>
        {
            string path = request.Uri.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == sessionPath)
            {
                if (sessionless)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                using var body = JsonDocument.Parse(request.Body!);
                return SessionResponse(GitHubWire.SessionId, body.RootElement.GetProperty("ownerName").GetString()!, oldQueue);
            }
            if (request.Method == HttpMethod.Get && path == oldQueue)
            {
                int poll = Interlocked.Increment(ref oldPolls);
                if (poll == 1)
                {
                    Assert.DoesNotContain("lastMessageId", request.Uri.Query, StringComparison.Ordinal);
                    return Delivery(9);
                }
                Assert.Contains("lastMessageId=9", request.Uri.Query, StringComparison.Ordinal);
                if (poll == 2 && !resumeAcknowledgement)
                {
                    table.Wire.DenyGetItem(checkpointKey);
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
                return poll == 3 && outcome == "AcknowledgementUnauthorized"
                    ? Delivery(10) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (request.Method == HttpMethod.Patch && path == closePath)
            {
                if (outcome is "AcknowledgementUnauthorized" or "RefreshUnauthorized")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                var active = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(key, cancellation));
                Assert.Equal(GitHubWire.SessionId, active.SessionId);
                return SessionResponse(GitHubWire.SessionId, active.OwnerName, oldQueue);
            }
            if (request.Method == HttpMethod.Delete && path == oldQueue + "/9")
            {
                if (resumeAcknowledgement)
                {
                    table.Wire.FailNextTransactWrite(checkpointKey, "AccessDeniedException");
                    table.Wire.DenyGetItem(checkpointKey);
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Delete && (path == oldQueue + "/10" || path == closePath))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return FixtureLookup(request, profile);
        });
        using var candidateWire = new GitHubWire(async (request, cancellation) =>
        {
            string path = request.Uri.AbsolutePath;
            if (request.Method == HttpMethod.Delete && path == closePath)
            {
                var closing = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(key, cancellation));
                Assert.Equal((SessionPhase.Closing, SessionCloseKind.OwnedClient, GitHubWire.SessionId, current.Lease.Fence),
                    (closing.Phase, closing.CloseKind, closing.SessionId, closing.Owner));
                Assert.Equal((key, GitHubRoutingIdentity.Scope(Scope), GitHubWire.SetId),
                    (closing.Binding.Profile, closing.Binding.ScopeIdentity, closing.Binding.ScaleSetId));
                return outcome switch
                {
                    "CloseUnauthorized" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                    "CloseNotFound" => new HttpResponseMessage(HttpStatusCode.NotFound),
                    "CloseResponseLost" => throw new HttpRequestException("Synthetic lost exact session-close response."),
                    _ => new HttpResponseMessage(HttpStatusCode.NoContent),
                };
            }
            if (request.Method == HttpMethod.Post && path == sessionPath)
            {
                var opening = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(key, cancellation));
                Assert.Equal(SessionPhase.OpenIntent, opening.Phase);
                Assert.Equal(priorSession, opening.PriorSessionId);
                Assert.Equal(current.Lease.Fence, opening.Owner);
                using var body = JsonDocument.Parse(request.Body!);
                return SessionResponse(SecondSession, body.RootElement.GetProperty("ownerName").GetString()!, newQueue);
            }
            if (request.Method == HttpMethod.Get && path == newQueue)
            {
                if (Interlocked.Increment(ref replacementPolls) == 1)
                {
                    Assert.DoesNotContain("lastMessageId", request.Uri.Query, StringComparison.Ordinal);
                    return Delivery(19, requestId: 73);
                }
                Assert.Contains("lastMessageId=19", request.Uri.Query, StringComparison.Ordinal);
                newPollAcknowledged.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            if (request.Method == HttpMethod.Post && path == SetPath(profile) + "/acquirejobs")
            {
                Assert.Equal(73, Assert.Single(Assert.IsType<int[]>(JsonSerializer.Deserialize<int[]>(request.Body!))));
                return GitHubWire.Json(HttpStatusCode.OK, """{"count":1,"value":[73]}""");
            }
            if (request.Method == HttpMethod.Delete && path == newQueue + "/19")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return FixtureLookup(request, outcome == "BindingMismatch" && path == SetPath(profile)
                ? config.Profiles.Single(profile => profile.Id == SecondProfile) : profile, SetPath(profile));
        });
        using var previousHttp = new HttpClient(previousWire, disposeHandler: false);
        using var candidateHttp = new HttpClient(candidateWire, disposeHandler: false);
        using var aws = CreateSecretWire(config);
        using var replacementAws = outcome == "SessionlessMutationUnauthorized" ? CreateSecretWire(config) :
            CreateSecretWire(config, "abcdef0123456789abcdef0123456789", "ghp_FixtureReplacementNeverValid");
        using var sdk = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, aws.Configure(new AmazonSecretsManagerConfig()));
        using var replacementSdk = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, replacementAws.Configure(new AmazonSecretsManagerConfig()));
        var secrets = new GitHubSecretLoader(sdk);
        var clients = new GitHubScaleSetClientFactory(httpClient: previousHttp, timeProvider: clock);
        var rotation = new GitHubCredentialRotator(new GitHubSecretLoader(replacementSdk),
            new GitHubScaleSetClientFactory(httpClient: candidateHttp, timeProvider: clock));
        await using var session = new ControllerProfileSession(config, profile, 1, ownership, store, secrets, clients,
            new GitHubScaleSetRegistration(store, clock, Timing.WriteReserve, Timing.SessionSendWindow),
            new GitHubListenerSupervisor(store, clock, Timing, rotation), rotation,
            new GitHubCredentialCooldown(clock, Timing.UnauthorizedCooldown), clock);
        session.ListenerHealthChanged = () =>
        {
            switch (session.ListenerResult)
            {
                case { Status: GitHubListenerLaneStatus.StorageRejected }:
                    storageDenied.TrySetResult();
                    break;
                case { Status: GitHubListenerLaneStatus.Failed, Failure.StatusCode: 401 }:
                    oldUnauthorized.TrySetResult();
                    break;
                case { Status: GitHubListenerLaneStatus.RecoveryRequired or GitHubListenerLaneStatus.RecoveryCredentialRejected } result:
                    held.TrySetResult(result);
                    break;
            }
        };

        Assert.Equal(GitHubWire.SetId, (await session.EnsureRegisteredAsync(token))?.ScaleSetId);
        await storageDenied.Task.WaitAsync(PollObservationBudget, token);
        if (sessionless)
        {
            // Storage recovers before the periodic rotation deadline.
            table.Wire.RestoreGetItem();
            if (replacementOpens)
            {
                await AssertReplacementPollAsync();
                Assert.Single(candidateWire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == sessionPath);
            }
            else
            {
                var result = await held.Task.WaitAsync(PollObservationBudget, token);
                Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, result.Status);
                Assert.Empty(candidateWire.Requests);
                Assert.False(session.IsListenerAvailable);
                if (outcome == "SessionlessMutationUnauthorized")
                {
                    Assert.Equal(401, result.Failure?.StatusCode);
                    Assert.Single(replacementAws.Requests);
                }
                else
                {
                    Assert.Empty(replacementAws.Requests);
                }
            }
            await session.CancelPollsAsync(token);
            await session.JoinMutatorsAsync(token);
            var observed = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(key, token));
            Assert.Equal(current.Lease.Fence, observed.Owner);
            Assert.Equal(outcome switch
            {
                "SessionlessOpenIntent" => SessionPhase.Quarantined,
                "SessionlessMutationUnauthorized" => SessionPhase.OpenIntent,
                _ => SessionPhase.Active,
            }, observed.Phase);
            Assert.Equal(replacementOpens ? SecondSession : null, observed.SessionId);
            Assert.Equal(outcome == "SessionlessMutationUnauthorized" ? 1 : 0,
                previousWire.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == sessionPath));
            Assert.DoesNotContain(previousWire.Requests.Concat(candidateWire.Requests),
                request => request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.ServiceHost);
            return;
        }

        var original = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(key, token));
        var checkpointStore = new GitHubListenerCheckpointStore(store, Assert.IsType<OwnedSessionFence>(original.Fence), clock);
        clock.Now = clock.Now.AddSeconds(61);
        table.Wire.RestoreGetItem();
        if (outcome is "AcknowledgementUnauthorized" or "RefreshUnauthorized")
        {
            Assert.Equal(401, (await held.Task.WaitAsync(PollObservationBudget, token)).Failure?.StatusCode);
            Assert.Empty(replacementAws.Requests);
            Assert.Empty(candidateWire.Requests);
        }
        else
        {
            await oldUnauthorized.Task.WaitAsync(PollObservationBudget, token);
            clock.Now = clock.Now.AddSeconds(20);
            if (replacementOpens)
            {
                await AssertReplacementPollAsync();
            }
            else
            {
                var failure = await held.Task.WaitAsync(PollObservationBudget, token);
                Assert.Equal(outcome switch { "CloseUnauthorized" => 401, "CloseNotFound" => 404, _ => (int?)null }, failure.Failure?.StatusCode);
                Assert.False(session.IsListenerAvailable);
                Assert.DoesNotContain(candidateWire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == sessionPath);
            }
        }
        await session.CancelPollsAsync(token);
        await session.JoinMutatorsAsync(token);
        var durable = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(key, token));
        Assert.Equal(original.Binding, durable.Binding);
        Assert.Equal(current.Lease.Fence, durable.Owner);
        Assert.Equal(replacementOpens ? SecondSession : GitHubWire.SessionId, durable.SessionId);
        Assert.Equal(outcome.StartsWith("Close", StringComparison.Ordinal) ? SessionPhase.Closing : SessionPhase.Active, durable.Phase);
        Assert.DoesNotContain(previousWire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == closePath);
        if (replacementOpens || outcome.StartsWith("Close", StringComparison.Ordinal))
        {
            Assert.Single(candidateWire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == closePath);
        }
        else
        {
            Assert.DoesNotContain(candidateWire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == closePath);
        }
        if (resumeAcknowledgement)
        {
            Assert.Single(previousWire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == oldQueue + "/9");
            Assert.Contains(previousWire.Requests, request => request.Method == HttpMethod.Get &&
                request.Uri.AbsolutePath == oldQueue && request.Uri.Query.Contains("lastMessageId=9", StringComparison.Ordinal));
        }
        if (outcome is "AcknowledgementUnauthorized" or "RefreshUnauthorized" or "BindingMismatch")
        {
            var checkpoint = Assert.IsType<ScaleSetListenerCheckpoint>(await checkpointStore.ReadAsync(checkpointStore.SessionKey, token));
            Assert.Equal(9L, checkpoint.LastAcknowledgedMessageId);
            if (outcome == "AcknowledgementUnauthorized")
            {
                Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, checkpoint.Pending?.Phase);
                Assert.Equal(10L, checkpoint.Pending?.Delivery.Message?.MessageId);
                Assert.Single(previousWire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == oldQueue + "/10");
            }
            else
            {
                Assert.Null(checkpoint.Pending);
            }
        }

        async Task AssertReplacementPollAsync()
        {
            await newPollAcknowledged.Task.WaitAsync(PollObservationBudget, token);
            Assert.True(session.IsListenerAvailable);
            Assert.Single(candidateWire.Requests, request => request.Method == HttpMethod.Post &&
                request.Uri.AbsolutePath == SetPath(profile) + "/acquirejobs");
            Assert.Single(candidateWire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == newQueue + "/19");
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

    private static HttpResponseMessage SessionResponse(Guid sessionId, string ownerName, string queue) =>
        GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            sessionId, ownerName,
            messageQueueUrl = "https://" + GitHubWire.QueueHost + queue,
            messageQueueAccessToken = GitHubWire.QueueToken,
            statistics = new { totalAssignedJobs = 0 },
        }));

    private static HttpResponseMessage FixtureLookup(GitHubRequest request, RunnerProfile profile, string? setPath = null) =>
        (request.Method, request.Uri.AbsolutePath) switch
        {
            (var method, GroupsPath) when method == HttpMethod.Get => GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                count = 1, value = new[] { new { id = GroupId(profile), name = profile.GitHub.RunnerGroup } },
            })),
            (var method, SetsPath) when method == HttpMethod.Get => GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                count = 1, value = new[] { SetBody(profile) },
            })),
            (var method, var path) when method == HttpMethod.Get && path == (setPath ?? SetPath(profile)) =>
                GitHubWire.Json(HttpStatusCode.OK, JsonSerializer.Serialize(SetBody(profile))),
            _ => throw new InvalidOperationException("Unexpected fixture GitHub operation."),
        };

    private static AwsWireTransport CreateSecretWire(RuntimeConfiguration configuration, string version = "0123456789abcdef0123456789abcdef",
        string token = "ghp_FixtureOnlyNeverValid") => new(_ => AwsWireTransport.Json(JsonSerializer.Serialize(new
    {
        ARN = configuration.GitHub["engineering"].SecretArn,
        VersionId = version,
        VersionStages = CurrentStage,
        SecretString = JsonSerializer.Serialize(new { token }),
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
