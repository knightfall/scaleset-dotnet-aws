using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SecretsManager;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class GitHubListenerSupervisorTests
{
    private const string Scope = "https://github.com/enterprises/engineering";
    private const string FirstProfile = "linux-shell";
    private const string SecondProfile = "linux-batch";
    private const string FirstQueue = "/messages/first";
    private const string SecondQueue = "/messages/second";
    private const string SuccessorTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const string SetsPath = GitHubWire.ServicePath + "/_apis/runtime/runnerscalesets/";
    private static readonly Guid FirstSession = Guid.Parse("1905ff9f-f017-4a9a-b59f-77ed34074c11");
    private static readonly Guid SecondSession = Guid.Parse("1905ff9f-f017-4a9a-b59f-77ed34074c12");
    private static readonly string[] CurrentStage = ["AWSCURRENT"];
    private static readonly GitHubListenerTiming Timing = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(15));
    private static readonly ListenerCapacity Capacity = new(8, 0);

    [Theory]
    [InlineData("confirmed")]
    [InlineData("lost")]
    [InlineData("bad-request")]
    [InlineData("not-found")]
    [InlineData("stale-lease")]
    public async Task TakeoverReplacesOnlyAfterConfirmedExactFreshClientRetirement(string outcome)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(cancellation);
        var prior = await AcquireActiveAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var profile = new LifecycleProfile(prior.Deployment, FirstProfile);
        int sessionCreates = 0;
        int pollCalls = 0;
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId))
            {
                bool first = sessionCreates++ == 0;
                return GitHubWire.Reply(HttpStatusCode.OK, SessionJson(request, first ? FirstSession : SecondSession, first ? FirstQueue : SecondQueue));
            }
            if (request.Method == HttpMethod.Get && request.Uri.AbsolutePath == FirstQueue)
            {
                pollCalls++;
                return GitHubWire.Reply(HttpStatusCode.OK, DeliveryJson(73));
            }
            if (request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == FirstQueue + "/73")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}")
            {
                return outcome switch
                {
                    "confirmed" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
                    "lost" => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture lost retirement response")),
                    "bad-request" => GitHubWire.Reply(HttpStatusCode.BadRequest, "{}"),
                    "not-found" => GitHubWire.Reply(HttpStatusCode.NotFound, "{}"),
                    _ => throw new InvalidOperationException("A stale owner reached retirement."),
                };
            }
            throw new InvalidOperationException("Unexpected fixture operation.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        var first = await ConnectAsync(configuration, http, clock, cancellation);
        using (var previous = new GitHubListenerSupervisor(store, clock, Timing)
            .Register(Definition(configuration, FirstProfile, first, prior, clock)))
        {
            Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await previous.StepAsync(cancellation)).Step?.Status);
            Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await previous.StepAsync(cancellation)).Step?.Status);
        }
        var oldCheckpoint = Assert.IsType<LifecycleRecord>(await store.ReadRecordAsync(LifecycleKey.Checkpoint(profile, FirstSession), cancellation));
        Assert.Equal(1, pollCalls);
        clock.Now = prior.ValidUntil.AddSeconds(2);
        var successor = await AcquireActiveAsync(store, configuration, clock, SuccessorTask, cancellation);
        var selected = outcome == "stale-lease" ? prior : successor;
        var client = await ConnectAsync(configuration, http, clock, cancellation);
        using var replacement = new GitHubListenerSupervisor(store, clock, Timing)
            .Register(Definition(configuration, FirstProfile, client, selected, clock));

        var next = await replacement.StepAsync(cancellation);
        if (outcome == "confirmed")
        {
            Assert.Equal(ScaleSetListenerStepStatus.Initialized, next.Step?.Status);
            var newSession = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(profile, cancellation));
            Assert.Equal(SecondSession, newSession.SessionId);
            var check = new GitHubListenerCheckpointStore(store, Assert.IsType<OwnedSessionFence>(newSession.Fence));
            var checkpoint = Assert.IsType<ScaleSetListenerCheckpoint>(await check.ReadAsync(check.SessionKey, cancellation));
            Assert.Null(checkpoint.LastAcknowledgedMessageId);
            Assert.True(checkpoint.InitialStatisticsCompleted);
            Assert.Equal(2, sessionCreates);
        }
        else
        {
            if (outcome == "stale-lease")
            {
                Assert.Equal(GitHubListenerLaneStatus.OwnershipUnavailable, next.Status);
            }
            else
            {
                Assert.Equal(GitHubListenerLaneStatus.Failed, next.Status);
                Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, (await replacement.StepAsync(cancellation)).Status);
                Assert.Equal(SessionPhase.Quarantined, (await store.ReadSessionAsync(profile, cancellation))?.Phase);
            }
            Assert.Equal(1, sessionCreates);
        }

        var retirementRequests = wire.Requests.Where(request => request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.ServiceHost).ToArray();
        if (outcome == "stale-lease")
        {
            Assert.Empty(retirementRequests);
        }
        else
        {
            Assert.Equal(SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}", Assert.Single(retirementRequests).Uri.AbsolutePath);
        }
        Assert.Equal(oldCheckpoint, await store.ReadRecordAsync(LifecycleKey.Checkpoint(profile, FirstSession), cancellation));
        Assert.Equal(1, pollCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "ProvisionedThroughputExceededException")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalServerError")]
    [InlineData(null, null)]
    public async Task StorageOutageAfterSuccessfulAckResumesTheSamePumpWithoutRepeatingDelete(HttpStatusCode? status, string? code)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(cancellation);
        var lease = await AcquireActiveAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var profile = new LifecycleProfile(lease.Deployment, FirstProfile);
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId))
            {
                return GitHubWire.Reply(HttpStatusCode.OK, SessionJson(request, FirstSession, FirstQueue));
            }
            if (request.Method == HttpMethod.Get && request.Uri.AbsolutePath == FirstQueue)
            {
                return GitHubWire.Reply(HttpStatusCode.OK, DeliveryJson(0));
            }
            if (request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == FirstQueue + "/0")
            {
                table.Wire.FailNextGetItem(LifecycleKey.Lease(lease.Deployment), status, code);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            throw new InvalidOperationException("Unexpected fixture operation.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        var supervisor = new GitHubListenerSupervisor(store, clock, Timing);
        using var lane = supervisor.Register(Definition(configuration, FirstProfile,
            await ConnectAsync(configuration, http, clock, cancellation), lease, clock));
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(cancellation)).Step?.Status);

        var unavailable = await lane.StepAsync(cancellation);
        Assert.Equal(GitHubListenerLaneStatus.StorageUnavailable, unavailable.Status);
        Assert.Null(unavailable.Failure);
        var active = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(profile, cancellation));
        var checkpointStore = new GitHubListenerCheckpointStore(store, Assert.IsType<OwnedSessionFence>(active.Fence));
        var pending = Assert.IsType<ScaleSetListenerCheckpoint>(
            await checkpointStore.ReadAsync(checkpointStore.SessionKey, cancellation));
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, pending.Pending?.Phase);
        Assert.Null(pending.LastAcknowledgedMessageId);

        var recovered = await lane.StepAsync(cancellation);
        Assert.Equal(GitHubListenerLaneStatus.Stepped, recovered.Status);
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, recovered.Step?.Status);
        var settled = Assert.IsType<ScaleSetListenerCheckpoint>(
            await checkpointStore.ReadAsync(checkpointStore.SessionKey, cancellation));
        Assert.Equal(pending.Revision + 1, settled.Revision);
        Assert.Null(settled.Pending);
        Assert.Equal(0, settled.LastAcknowledgedMessageId);
        Assert.Equal(active, await store.ReadSessionAsync(profile, cancellation));
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId));
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Get && request.Uri.AbsolutePath == FirstQueue);
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == FirstQueue + "/0");
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentUnauthorizedAndRateLimitShareOneCooldownAcrossProfiles()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(cancellation, secondProfile: true);
        var lease = await AcquireActiveAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var bothPolls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int polls = 0;
        using var wire = new GitHubWire(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                bool first = request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId);
                return GitHubWire.Json(HttpStatusCode.OK, SessionJson(request, first ? FirstSession : SecondSession, first ? FirstQueue : SecondQueue));
            }
            if (request.Method == HttpMethod.Get && request.Uri.Host == GitHubWire.QueueHost)
            {
                if (Interlocked.Increment(ref polls) == 2)
                {
                    bothPolls.TrySetResult();
                }
                await bothPolls.Task.WaitAsync(token);
                if (request.Uri.AbsolutePath == FirstQueue)
                {
                    return GitHubWire.Json(HttpStatusCode.Unauthorized, "{}");
                }
                var rateLimit = GitHubWire.Json(HttpStatusCode.TooManyRequests, "{}");
                rateLimit.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return rateLimit;
            }
            if (request.Method == HttpMethod.Patch && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}")
            {
                return GitHubWire.Json(HttpStatusCode.Unauthorized, "{}");
            }
            throw new InvalidOperationException("Unexpected fixture operation.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        var supervisor = new GitHubListenerSupervisor(store, clock, Timing);
        var firstClient = await ConnectAsync(configuration, http, clock, cancellation);
        var secondClient = await ConnectAsync(configuration, http, clock, cancellation);
        using var first = supervisor.Register(Definition(configuration, FirstProfile, firstClient, lease, clock));
        using var second = supervisor.Register(Definition(configuration, SecondProfile, secondClient, lease, clock));
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await first.StepAsync(cancellation)).Step?.Status);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await second.StepAsync(cancellation)).Step?.Status);

        var failures = await Task.WhenAll(first.StepAsync(cancellation), second.StepAsync(cancellation));
        Assert.Equal(401, failures[0].Failure?.StatusCode);
        Assert.Equal(429, failures[1].Failure?.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), failures[1].Failure?.RateLimit?.RetryAfter);
        var deferred = await Task.WhenAll(first.StepAsync(cancellation), second.StepAsync(cancellation));
        Assert.All(deferred, result =>
        {
            Assert.Equal(ScaleSetListenerStepStatus.Deferred, result.Step?.Status);
            Assert.Equal(clock.Now.AddMinutes(1), result.RetryAt);
        });
        Assert.Equal(2, polls);
        Assert.Equal(2, wire.Requests.Count(request => request.Uri.Host == GitHubWire.QueueHost));
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Patch &&
            request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}");
    }

    [Fact(Timeout = 30_000)]
    public async Task GracefulStopJoinsQueueAcknowledgementAndClosesOnlyTheOwnedSession()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var host = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(cancellation, secondProfile: true);
        var lease = await AcquireActiveAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var ackEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wire = new GitHubWire(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId))
            {
                return GitHubWire.Json(HttpStatusCode.OK, SessionJson(request, FirstSession, FirstQueue));
            }
            if (request.Method == HttpMethod.Get && request.Uri.AbsolutePath == FirstQueue)
            {
                return GitHubWire.Json(HttpStatusCode.OK, DeliveryJson(0));
            }
            if (request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == FirstQueue + "/0")
            {
                ackEntered.TrySetResult(token);
                await finishAck.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}")
            {
                Assert.True(finishAck.Task.IsCompletedSuccessfully);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected fixture operation.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        var supervisor = new GitHubListenerSupervisor(store, clock, Timing);
        using var lane = supervisor.Register(Definition(configuration, FirstProfile, await ConnectAsync(configuration, http, clock, cancellation), lease, clock));
        using var other = supervisor.Register(Definition(configuration, SecondProfile, await ConnectAsync(configuration, http, clock, cancellation), lease, clock));
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(cancellation)).Step?.Status);

        var unownedProfile = new LifecycleProfile(lease.Deployment, SecondProfile);
        var unowned = new SessionBinding(unownedProfile, GitHubRoutingIdentity.Scope(Scope), GitHubWire.SetId + 1);
        var started = await store.BeginSessionOpenAsync(unowned, lease.Fence, null, "fixture-other", clock.Now.AddSeconds(1),
            clock.Now.AddSeconds(20), cancellation);
        var permission = Assert.IsType<SessionSendAuthorization>(started.Permission);
        Assert.True(permission.TryConsume(out _));
        Assert.Equal(LifecycleWriteOutcome.Committed, (await store.ConfirmSessionOpenedAsync(permission, SecondSession, cancellation)).Outcome);

        Task<GitHubListenerLaneResult> step = lane.StepAsync(host.Token);
        CancellationToken acknowledgement = await ackEntered.Task.WaitAsync(cancellation);
        await host.CancelAsync();
        Task<GitHubListenerLaneResult> stopping = lane.StopAsync(cancellation);
        Assert.Equal(GitHubListenerLaneStatus.Stopped, (await other.StopAsync(cancellation)).Status);
        Assert.False(acknowledgement.IsCancellationRequested);
        Assert.DoesNotContain(wire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.ServiceHost);
        finishAck.SetResult();

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await step).Step?.Status);
        Assert.Equal(GitHubListenerLaneStatus.Stopped, (await stopping).Status);
        var close = Assert.Single(wire.Requests, request => request.Method == HttpMethod.Delete && request.Uri.Host == GitHubWire.ServiceHost);
        Assert.Equal(SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}", close.Uri.AbsolutePath);
        var retired = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(new LifecycleProfile(lease.Deployment, FirstProfile), cancellation));
        Assert.Equal(SessionPhase.Retired, retired.Phase);
        Assert.Equal(SessionPhase.Active, (await store.ReadSessionAsync(unownedProfile, cancellation))?.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatedCurrentVersionRetiresOldSessionBeforeOpeningReplacement(bool loseRetirementWrite)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(cancellation);
        var lease = await AcquireActiveAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        int opens = 0;
        Task<HttpResponseMessage> Respond(GitHubRequest request, CancellationToken _)
        {
            string path = request.Uri.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == SessionsPath(GitHubWire.SetId))
            {
                bool first = opens++ == 0;
                return GitHubWire.Reply(HttpStatusCode.OK, SessionJson(request, first ? FirstSession : SecondSession, first ? FirstQueue : SecondQueue));
            }
            if (request.Method == HttpMethod.Delete && path == SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}")
            {
                if (loseRetirementWrite)
                {
                    table.Wire.DropNextTransactionBeforeSend();
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (path == GitHubWire.ServicePath + "/_apis/runtime/runnergroups/")
            {
                return GitHubWire.Reply(HttpStatusCode.OK, $$"""{"count":1,"value":[{"id":{{GitHubWire.GroupId}},"name":"No-Docker"}]}""");
            }
            if (path == SetsPath + GitHubWire.SetId.ToString(CultureInfo.InvariantCulture))
            {
                return GitHubWire.Reply(HttpStatusCode.OK, $$"""
                    {"id":{{GitHubWire.SetId}},"runnerGroupId":{{GitHubWire.GroupId}},"name":"linux-shell",
                    "labels":[{"name":"linux-shell"},{"name":"shell-x64"}]}
                    """);
            }
            throw new InvalidOperationException("Unexpected fixture operation.");
        }
        using var previousWire = new GitHubWire(Respond);
        using var candidateWire = new GitHubWire(Respond);
        using var previousHttp = new HttpClient(previousWire, disposeHandler: false);
        using var candidateHttp = new HttpClient(candidateWire, disposeHandler: false);
        using var rotatedSecretWire = new AwsWireTransport(_ => AwsWireTransport.Json(SecretResponse(configuration,
            "abcdef0123456789abcdef0123456789", "ghp_FixtureRotatedNeverValid")));
        using var secrets = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, rotatedSecretWire.Configure(new AmazonSecretsManagerConfig()));
        var rotator = new GitHubCredentialRotator(new GitHubSecretLoader(secrets), new GitHubScaleSetClientFactory(httpClient: candidateHttp, timeProvider: clock));
        var supervisor = new GitHubListenerSupervisor(store, clock, Timing, rotator);
        using var lane = supervisor.Register(Definition(configuration, FirstProfile, await ConnectAsync(configuration, previousHttp, clock, cancellation), lease, clock));

        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(cancellation)).Step?.Status);
        Assert.Equal(loseRetirementWrite ? GitHubListenerLaneStatus.RecoveryRequired : GitHubListenerLaneStatus.CredentialAdopted,
            (await lane.RotateAsync(cancellation)).Status);
        Assert.Equal(1, opens);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(cancellation)).Step?.Status);

        Assert.Collection(previousWire.Requests.Where(request => request.Uri.Host == GitHubWire.ServiceHost &&
            request.Uri.AbsolutePath.Contains("/sessions", StringComparison.Ordinal)),
            first => Assert.Equal(HttpMethod.Post, first.Method),
            close => Assert.Equal((HttpMethod.Delete, SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}"), (close.Method, close.Uri.AbsolutePath)));
        Assert.Single(candidateWire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId));
        Assert.Single(rotatedSecretWire.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalOperationDeadlineDoesNotDiscardOwnedListener(bool cancellationFailure)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(cancellation);
        var lease = await AcquireActiveAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        int reads = 0;
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId))
            {
                return GitHubWire.Reply(HttpStatusCode.OK, SessionJson(request, FirstSession, FirstQueue));
            }
            if (request.Method == HttpMethod.Get && request.Uri.AbsolutePath == FirstQueue)
            {
                if (reads++ == 0)
                {
                    Exception failure = cancellationFailure ? new OperationCanceledException("fixture operation canceled") : new TimeoutException("fixture operation deadline");
                    return Task.FromException<HttpResponseMessage>(failure);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }
            throw new InvalidOperationException("Unexpected fixture operation.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        using var lane = new GitHubListenerSupervisor(store, clock, Timing)
            .Register(Definition(configuration, FirstProfile, await ConnectAsync(configuration, http, clock, cancellation), lease, clock));

        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(cancellation)).Step?.Status);
        Assert.Equal(GitHubListenerLaneStatus.Failed, (await lane.StepAsync(cancellation)).Status);
        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await lane.StepAsync(cancellation)).Step?.Status);
        Assert.Equal(2, reads);
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId));
        Assert.DoesNotContain(wire.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task MissingInitialStatisticsHoldsDemandAndAllowsClosingTheKnownExactSession()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var configuration = await ConfigurationAsync(cancellation);
        var lease = await AcquireActiveAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId))
            {
                var session = JsonNode.Parse(SessionJson(request, FirstSession, FirstQueue))!.AsObject();
                session.Remove("statistics");
                return GitHubWire.Reply(HttpStatusCode.OK, session.ToJsonString());
            }
            if (request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == SessionsPath(GitHubWire.SetId) + $"/{FirstSession:D}")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            throw new InvalidOperationException("Unexpected fixture operation.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        using var lane = new GitHubListenerSupervisor(store, clock, Timing)
            .Register(Definition(configuration, FirstProfile, await ConnectAsync(configuration, http, clock, cancellation), lease, clock));
        var profile = new LifecycleProfile(lease.Deployment, FirstProfile);

        Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, (await lane.StepAsync(cancellation)).Status);
        Assert.Equal(FirstSession, (await store.ReadSessionAsync(profile, cancellation))?.SessionId);
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Demand(profile), cancellation));
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Checkpoint(profile, FirstSession), cancellation));
        Assert.Equal(GitHubListenerLaneStatus.RecoveryRequired, (await lane.StepAsync(cancellation)).Status);
        Assert.Equal(GitHubListenerLaneStatus.Stopped, (await lane.StopAsync(cancellation)).Status);
        Assert.DoesNotContain(wire.Requests, request => request.Uri.Host == GitHubWire.QueueHost);
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Delete);
    }

    private static GitHubListenerProfile Definition(
        RuntimeConfiguration configuration, string profileId, GitHubScaleSetConnection connection, ControllerLease lease, ContractClock clock)
    {
        var profile = configuration.Profiles.Single(profile => profile.Id == profileId);
        long set = profileId == FirstProfile ? GitHubWire.SetId : GitHubWire.SetId + 1;
        var binding = new GitHubScaleSetBinding(Scope, profile.GitHub.RunnerGroup, GitHubWire.GroupId, profile.GitHub.ScaleSet, set,
            profile.GitHub.Labels);
        return new GitHubListenerProfile(configuration, profileId, binding, connection, lease.Fence,
            _ => Task.FromResult<GitHubRegistrationLease?>(new GitHubRegistrationLease(lease, 1, Evidence(clock))),
            _ => Task.FromResult(Capacity), (_, _) => Task.FromResult(true), CancellationToken.None);
    }

    private static async Task<RuntimeConfiguration> ConfigurationAsync(CancellationToken cancellationToken, bool secondProfile = false)
    {
        string json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken);
        var root = JsonNode.Parse(json)!;
        var spec = root["spec"]!;
        var connection = spec["github"]!["engineering"]!;
        connection["scopeURL"] = Scope;
        connection["auth"]!["type"] = "enterprise-pat";
        if (secondProfile)
        {
            var profiles = spec["profiles"]!.AsArray();
            var second = profiles[0]!.DeepClone();
            second["id"] = SecondProfile;
            second["github"]!["runnerGroup"] = "Batch";
            second["github"]!["scaleSet"] = SecondProfile;
            second["github"]!["labels"] = new JsonArray(SecondProfile);
            profiles.Add(second);
        }
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static async Task<ControllerLease> AcquireActiveAsync(
        DynamoDbLifecycleStore store, RuntimeConfiguration configuration, ContractClock clock, string task, CancellationToken cancellationToken)
    {
        var acquired = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), task, task,
            ControllerLease.CurrentWriterProtocol, clock.Now.AddSeconds(-1), clock.Now.AddMinutes(4), cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, acquired.Outcome);
        var recovery = new ControllerLeaseRecoveryCoordinator(store, new StoppedObserver(), clock, Timing.WriteReserve);
        var activated = await recovery.TryActivateAsync(Assert.IsType<ControllerLease>(acquired.Lease), configuration, Evidence(clock), [], cancellationToken);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        return Assert.IsType<ControllerLease>(activated.Lease);
    }

    private static async Task<GitHubScaleSetConnection> ConnectAsync(
        RuntimeConfiguration configuration, HttpClient http, TimeProvider clock, CancellationToken cancellationToken)
    {
        string secretResponse = SecretResponse(configuration, "0123456789abcdef0123456789abcdef", "ghp_FixtureOnlyNeverValid");
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(secretResponse));
        using var sdk = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, wire.Configure(new AmazonSecretsManagerConfig()));
        var secret = Assert.IsType<GitHubSecretVersion>(await new GitHubSecretLoader(sdk).LoadAsync(configuration, "engineering", cancellationToken));
        return new GitHubScaleSetClientFactory(httpClient: http, timeProvider: clock).Create(secret);
    }

    private static string SecretResponse(RuntimeConfiguration configuration, string versionId, string token) => JsonSerializer.Serialize(new
    {
        ARN = configuration.GitHub["engineering"].SecretArn,
        VersionId = versionId,
        VersionStages = CurrentStage,
        SecretString = JsonSerializer.Serialize(new { token }),
    });

    private static ControllerClockEvidence Evidence(ContractClock clock) => new(clock.Now, clock.Now.AddMinutes(3), TimeSpan.FromSeconds(1));

    private static string SessionsPath(long scaleSetId) => SetsPath + scaleSetId.ToString(CultureInfo.InvariantCulture) + "/sessions";

    private static string SessionJson(GitHubRequest request, Guid sessionId, string queuePath)
    {
        using var payload = JsonDocument.Parse(request.Body!);
        return JsonSerializer.Serialize(new
        {
            sessionId,
            ownerName = payload.RootElement.GetProperty("ownerName").GetString(),
            messageQueueUrl = "https://" + GitHubWire.QueueHost + queuePath,
            messageQueueAccessToken = GitHubWire.QueueToken,
            statistics = new { totalAssignedJobs = 3 },
        });
    }

    private static string DeliveryJson(long messageId) => JsonSerializer.Serialize(new
    {
        messageId,
        messageType = "RunnerScaleSetJobMessages",
        body = "[]",
    });

    private sealed class StoppedObserver : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(MiniStackTable.FirstTask, taskArn);
            return Task.FromResult(ControllerTaskFenceStatus.Stopped);
        }
    }
}
