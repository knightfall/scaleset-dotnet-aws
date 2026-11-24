using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class ListenerDeliveryTests
{
    private const string Scope = "github.com/owner/repository";
    private const long FirstRequest = 6_000_000_001;
    private const long SecondRequest = 6_000_000_002;
    private const long NextRequest = 6_000_000_003;

    [Fact]
    public async Task ReservationsSurviveLostAcquireResponseReplayAndReplacementSession()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("lost-acquire");
        var owned = await OpenAsync(store, table.Lease, profile, GitHubWire.SessionId, token);
        using var wire = new GitHubWire(async (request, cancellationToken) =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                return GitHubWire.Json(HttpStatusCode.OK, GitHubWire.SessionJson());
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/acquirejobs", request.Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(new[] { FirstRequest, SecondRequest }, JsonSerializer.Deserialize<long[]>(request.Body!));
            Assert.Equal(2, await HeldCountAsync(store, profile, cancellationToken));
            Assert.Equal(0, (await PayloadAsync(store, LifecycleKey.Acquisition(profile, FirstRequest), cancellationToken)).GetProperty("state").GetInt32());
            var persisted = await PayloadAsync(store, LifecycleKey.Message(profile, owned.Identity.SessionId, 0), cancellationToken);
            var attempt = persisted.GetProperty("admission");
            var envelope = attempt.GetProperty("request");
            Assert.Equal(Scope, envelope.GetProperty("scopeIdentity").GetString());
            Assert.Equal(GitHubWire.SetId, envelope.GetProperty("scaleSetId").GetInt64());
            Assert.Equal(GitHubWire.SessionId, envelope.GetProperty("sessionId").GetGuid());
            Assert.Equal(0, envelope.GetProperty("messageId").GetInt64());
            Assert.Equal(request.Body, envelope.GetProperty("requestIds").GetRawText());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.GetRawText()))),
                attempt.GetProperty("sha256").GetString());
            throw new HttpRequestException("Fixture response lost after the acquisition request left the process.");
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        using var client = GitHubWire.Connect(http, clock);
        var session = await client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", token);
        var handler = Handler(store, owned, session, clock, 3);
        await InitializeAsync(handler, owned, clock.Now, 1, token);
        var delivery = Offer(owned, clock.Now, 0, FirstRequest, FirstRequest, 0, SecondRequest, NextRequest);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(delivery, token));
        Assert.Equal(2, await HeldCountAsync(store, profile, token));
        Assert.Equal(2, (await PayloadAsync(store, LifecycleKey.Acquisition(profile, FirstRequest), token)).GetProperty("state").GetInt32());

        var restarted = Handler(store, owned, session, clock, 3);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await restarted.HandleAsync(delivery, token));
        Assert.Single(wire.Requests, request => request.Uri.AbsolutePath.EndsWith("/acquirejobs", StringComparison.Ordinal));
        var receipt = await PayloadAsync(store, LifecycleKey.Message(profile, owned.Identity.SessionId, 0), token);
        Assert.Equal(clock.Now, receipt.GetProperty("observedAt").GetDateTimeOffset());
        Assert.DoesNotContain("synthetic-queue", receipt.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-private-url", receipt.GetRawText(), StringComparison.Ordinal);

        var replacement = await ReplaceAsync(store, owned, token);
        var replacementSession = new TestSession(replacement.Identity.SessionId);
        var next = Handler(store, replacement, replacementSession, clock, 3);
        Assert.Equal(0, await next.ReadPollCapacityAsync(token));
        await InitializeAsync(next, replacement, clock.Now, 1, token);
        Assert.Equal(3, await next.ReadPollCapacityAsync(token));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed,
            await next.HandleAsync(Offer(replacement, clock.Now, 0, NextRequest), token));
        Assert.Empty(replacementSession.Acquisitions);
        Assert.Equal(2, await HeldCountAsync(store, profile, token));
        Assert.NotNull(await store.ReadRecordAsync(LifecycleKey.Message(profile, owned.Identity.SessionId, 0), token));
    }

    [Fact]
    public async Task ACommittedReservationWithLostWriteResponseIsHeldWithoutAuthorizingReplaySend()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("reservation-readback");
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        var session = new TestSession(owned.Identity.SessionId);
        var handler = new GitHubListenerDeliveryHandler(store, owned, session, _ =>
        {
            table.Wire.DropNextSuccessfulTransactionResponse();
            return Task.FromResult(new ListenerCapacity(3, 0));
        }, clock);
        await InitializeAsync(handler, owned, clock.Now, 1, token);
        var delivery = Offer(owned, clock.Now, 0, FirstRequest, SecondRequest);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Deferred, await handler.HandleAsync(delivery, token));
        Assert.Equal(2, await HeldCountAsync(store, profile, token));
        Assert.Empty(session.Acquisitions);

        var restarted = Handler(store, owned, session, clock, 3);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await restarted.HandleAsync(delivery, token));
        Assert.Equal(2, await HeldCountAsync(store, profile, token));
        Assert.Equal(2, (await PayloadAsync(store, LifecycleKey.Acquisition(profile, FirstRequest), token)).GetProperty("state").GetInt32());
        Assert.Empty(session.Acquisitions);
    }

    [Fact]
    public async Task DemandAndJobHistoryAreMonotonicAndOnlyExactOutcomeReleasesHeldRequests()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("delivery-evidence");
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        var session = new TestSession(owned.Identity.SessionId, [FirstRequest]);
        var handler = Handler(store, owned, session, clock, 5);
        await InitializeAsync(handler, owned, clock.Now, 1, token);
        clock.Now = clock.Now.AddMinutes(1);
        var offer = Offer(owned, clock.Now, 0, FirstRequest, SecondRequest);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(offer, token));
        Assert.Equal(new[] { FirstRequest, SecondRequest }, Assert.Single(session.Acquisitions));
        var demandKey = LifecycleKey.Demand(profile);
        var demand = await PayloadAsync(store, demandKey, token);
        Assert.Equal(0, demand.GetProperty("messageId").GetInt64());
        Assert.Equal(1, demand.GetProperty("totalAssignedJobs").GetInt32());
        Assert.Equal(clock.Now, demand.GetProperty("observedAt").GetDateTimeOffset());

        clock.Now = clock.Now.AddMinutes(1);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(
            new ScaleSetListenerDelivery(Key(owned), ScaleSetListenerDeliveryKind.EmptyPoll, clock.Now), token));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(
            new ScaleSetListenerDelivery(Key(owned), ScaleSetListenerDeliveryKind.Message, clock.Now, offer.Message), token));
        Assert.Equal(demand.GetRawText(), (await PayloadAsync(store, demandKey, token)).GetRawText());

        var assigned = new RunnerScaleSetMessage
        {
            MessageId = 1,
            JobAssignedMessages = [new JobAssignedMessage { RunnerRequestId = FirstRequest }],
        };
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(Delivery(owned, clock.Now, assigned), token));
        Assert.Equal(2, await HeldCountAsync(store, profile, token));
        var accepted = await PayloadAsync(store, LifecycleKey.Acquisition(profile, FirstRequest), token);
        Assert.Equal(1, accepted.GetProperty("state").GetInt32());
        Assert.False(await handler.ConfirmDefinitiveRejectionAsync(FirstRequest, accepted.GetProperty("attemptId").GetGuid(), Guid.NewGuid(), token));

        var completed = new RunnerScaleSetMessage
        {
            MessageId = 2,
            JobCompletedMessages = [new JobCompletedMessage { RunnerRequestId = FirstRequest, RunnerId = GitHubWire.RunnerId }],
        };
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(Delivery(owned, clock.Now, completed), token));
        Assert.Equal(1, await HeldCountAsync(store, profile, token));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(
            Delivery(owned, clock.Now, assigned with { MessageId = 3 }), token));
        var job = await PayloadAsync(store, LifecycleKey.Job(profile, "6000000001"), token);
        Assert.Equal(2, job.GetProperty("observation").GetProperty("phase").GetInt32());
        Assert.Equal(2, job.GetProperty("messageId").GetInt64());

        var uncertain = await PayloadAsync(store, LifecycleKey.Acquisition(profile, SecondRequest), token);
        var attemptId = uncertain.GetProperty("attemptId").GetGuid();
        var evidenceId = Guid.NewGuid();
        Assert.False(await handler.ConfirmDefinitiveRejectionAsync(SecondRequest, Guid.NewGuid(), evidenceId, token));
        Assert.True(await handler.ConfirmDefinitiveRejectionAsync(SecondRequest, attemptId, evidenceId, token));
        Assert.True(await handler.ConfirmDefinitiveRejectionAsync(SecondRequest, attemptId, evidenceId, token));
        Assert.Equal(0, await HeldCountAsync(store, profile, token));
        Assert.Equal(demand.GetRawText(), (await PayloadAsync(store, demandKey, token)).GetRawText());

        clock.Now = clock.Now.AddMinutes(11);
        Assert.Equal(0, await handler.ReadPollCapacityAsync(token));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(
            Delivery(owned, clock.Now, new RunnerScaleSetMessage
            {
                MessageId = 4,
                JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = NextRequest }],
            }), token));
        Assert.Single(session.Acquisitions);
    }

    [Fact]
    public async Task OutsideAssignedAndCapacityBasisRevisionCanEachPreventAcquisition()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("capacity-basis");
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        var session = new TestSession(owned.Identity.SessionId);
        var capacity = new ListenerCapacity(10, 2);
        var runnerKey = LifecycleKey.Runner(profile, "allocation-1");
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.WriteRecordsAsync(table.Lease, Guid.NewGuid(), [new LifecycleRecordWrite(runnerKey, null, "{}")], token));
        bool replaceCapacityBasis = false;
        var handler = new GitHubListenerDeliveryHandler(store, owned, session, async cancellationToken =>
        {
            var basis = await store.ReadRecordAsync(runnerKey, cancellationToken);
            if (replaceCapacityBasis)
            {
                replaceCapacityBasis = false;
                Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteRecordsAsync(table.Lease, Guid.NewGuid(),
                    [new LifecycleRecordWrite(runnerKey, basis?.Revision, "{\"superseded\":true}")], cancellationToken));
            }
            return capacity with { ReadGuards = [new LifecycleRecordCondition(runnerKey, basis?.Revision)] };
        }, clock);
        await InitializeAsync(handler, owned, clock.Now, 8, token);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed,
            await handler.HandleAsync(Offer(owned, clock.Now, 0, NextRequest, assigned: 8), token));
        Assert.Empty(session.Acquisitions);
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Admission(profile), token));

        capacity = capacity with { OutsideAssigned = 1 };
        replaceCapacityBasis = true;
        var next = Offer(owned, clock.Now, 1, NextRequest, assigned: 8);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Deferred, await handler.HandleAsync(next, token));
        Assert.Empty(session.Acquisitions);
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Acquisition(profile, NextRequest), token));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(next, token));
        Assert.Equal(NextRequest, Assert.Single(Assert.Single(session.Acquisitions)));
        Assert.Equal(1, await HeldCountAsync(store, profile, token));
    }

    [Fact]
    public async Task OversizedBasisDefersAndExactOverlappingGuardsFitTheTransactionLimit()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("capacity-transaction-limit");
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        var session = new TestSession(owned.Identity.SessionId);
        var runners = Enumerable.Range(0, 96)
            .Select(index => new LifecycleRecordCondition(LifecycleKey.Runner(profile, FormattableString.Invariant($"a-{index}")), null))
            .ToArray();
        var capacity = new ListenerCapacity(1, 0) { ReadGuards = runners };
        var handler = new GitHubListenerDeliveryHandler(store, owned, session, _ => Task.FromResult(capacity), clock);
        await InitializeAsync(handler, owned, clock.Now, 1, token);

        table.Wire.Clear();
        var delivery = Offer(owned, clock.Now, 0, FirstRequest);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Deferred, await handler.HandleAsync(delivery, token));
        Assert.Single(table.Wire.Requests, request => request.Operation == "TransactWriteItems");
        Assert.Empty(session.Acquisitions);
        var receiptKey = LifecycleKey.Message(profile, owned.Identity.SessionId, 0);
        var receipt = Assert.IsType<LifecycleRecord>(await store.ReadRecordAsync(receiptKey, token));

        capacity = new ListenerCapacity(3, 0)
        {
            ReadGuards =
            [
                .. runners.Take(92), runners[0],
                new(receiptKey, receipt.Revision),
                new(LifecycleKey.Admission(profile), null),
                new(LifecycleKey.Acquisition(profile, FirstRequest), null),
                new(LifecycleKey.Job(profile, "6000000001"), null),
            ],
        };
        table.Wire.Clear();
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(delivery, token));
        Assert.Single(table.Wire.Requests, request => request.Operation == "TransactWriteItems" &&
            request.Request.GetProperty("TransactItems").GetArrayLength() == 100);
        Assert.Equal(FirstRequest, Assert.Single(Assert.Single(session.Acquisitions)));
        Assert.Equal(1, await HeldCountAsync(store, profile, token));
    }

    private static async Task<OwnedSessionFence> OpenAsync(
        DynamoDbLifecycleStore store, DeploymentLeaseFence lease, LifecycleProfile profile, Guid sessionId, CancellationToken token,
        SessionRecord? previous = null)
    {
        var binding = new SessionBinding(profile, Scope, GitHubWire.SetId);
        var ready = await store.AdvanceEffectRebuildAsync(profile, lease, token);
        Assert.Equal(EffectIndexState.Ready, ready.Head?.State);
        var opened = await store.BeginSessionOpenAsync(binding, lease, previous, "owner",
            MiniStackTable.Now, MiniStackTable.Now.AddMinutes(30), token);
        Assert.Equal(LifecycleWriteOutcome.Committed, opened.Outcome);
        var permission = Assert.IsType<SessionSendAuthorization>(opened.Permission);
        Assert.True(permission.TryConsume(out _));
        var confirmed = await store.ConfirmSessionOpenedAsync(permission, sessionId, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, confirmed.Outcome);
        return Assert.IsType<OwnedSessionFence>(confirmed.Session?.Fence);
    }

    private static async Task<OwnedSessionFence> ReplaceAsync(
        DynamoDbLifecycleStore store, OwnedSessionFence owned, CancellationToken token)
    {
        var prior = Assert.IsType<SessionRecord>(await store.ReadSessionAsync(owned.Identity.Profile, token));
        var close = await store.BeginSessionCloseAsync(prior, owned.Lease, SessionCloseKind.OwnedClient,
            MiniStackTable.Now, MiniStackTable.Now.AddMinutes(30), token);
        var permission = Assert.IsType<SessionSendAuthorization>(close.Permission);
        Assert.True(permission.TryConsume(out _));
        var retired = await store.ConfirmSessionRetiredAsync(permission, token);
        Assert.Equal(SessionPhase.Retired, retired.Session?.Phase);
        return await OpenAsync(store, owned.Lease, owned.Identity.Profile, Guid.NewGuid(), token, retired.Session);
    }

    private static GitHubListenerDeliveryHandler Handler(DynamoDbLifecycleStore store, OwnedSessionFence owned,
        IMessageSessionClient session, TimeProvider clock, int maximum) =>
        new(store, owned, session, _ => Task.FromResult(new ListenerCapacity(maximum, 0)), clock);

    private static async Task InitializeAsync(GitHubListenerDeliveryHandler handler, OwnedSessionFence owned,
        DateTimeOffset observedAt, int assigned, CancellationToken token)
    {
        var statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = assigned };
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(
            new ScaleSetListenerDelivery(Key(owned), ScaleSetListenerDeliveryKind.InitialStatistics, observedAt, initialStatistics: statistics), token));
    }

    private static ScaleSetListenerSessionKey Key(OwnedSessionFence owned) =>
        new(owned.Identity.ScopeIdentity, owned.Identity.ScaleSetId, owned.Identity.SessionId);

    private static ScaleSetListenerDelivery Delivery(OwnedSessionFence owned, DateTimeOffset at, RunnerScaleSetMessage message) =>
        new(Key(owned), ScaleSetListenerDeliveryKind.Message, at, message);

    private static ScaleSetListenerDelivery Offer(OwnedSessionFence owned, DateTimeOffset at, long messageId, params long[] requests) =>
        Offer(owned, at, messageId, requests, 1);

    private static ScaleSetListenerDelivery Offer(OwnedSessionFence owned, DateTimeOffset at, long messageId, long request, int assigned) =>
        Offer(owned, at, messageId, [request], assigned);

    private static ScaleSetListenerDelivery Offer(OwnedSessionFence owned, DateTimeOffset at, long messageId, long[] requests, int assigned) =>
        Delivery(owned, at, new RunnerScaleSetMessage
        {
            MessageId = messageId,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = assigned },
            JobAvailableMessages = requests.Select(request => new JobAvailableMessage
            {
                RunnerRequestId = request,
                AcquireJobUrl = "https://fixture-private-url.invalid/secret",
            }).ToArray(),
        });

    private static async Task<int> HeldCountAsync(DynamoDbLifecycleStore store, LifecycleProfile profile, CancellationToken token) =>
        (await PayloadAsync(store, LifecycleKey.Admission(profile), token)).GetProperty("heldCount").GetInt32();

    private static async Task<JsonElement> PayloadAsync(DynamoDbLifecycleStore store, LifecycleKey key, CancellationToken token)
    {
        var record = Assert.IsType<LifecycleRecord>(await store.ReadRecordAsync(key, token));
        using var document = JsonDocument.Parse(record.Payload!);
        return document.RootElement.Clone();
    }

    private sealed class TestSession(Guid sessionId, IReadOnlyList<long>? accepted = null) : IMessageSessionClient
    {
        public RunnerScaleSetSession Session { get; } = new() { SessionId = sessionId };
        internal List<long[]> Acquisitions { get; } = [];

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquisitions.Add(runnerRequestIds.ToArray());
            return Task.FromResult(accepted ?? []);
        }

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture exercises delivered job batches.");

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture exercises delivered job batches.");
    }
}
