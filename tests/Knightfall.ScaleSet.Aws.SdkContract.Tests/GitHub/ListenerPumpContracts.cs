using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.ListenerConsumer;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

public sealed class ListenerPumpContracts
{
    private const long MessageId = 9_000_000_004;
    private static readonly ScaleSetListenerSessionKey Key = new(
        "github.com/owner/repository", GitHubWire.SetId, GitHubWire.SessionId);

    [Fact]
    public async Task ObservedQueuePreservesReleasedPumpSpansAndMeasuresPollAndAcknowledgement()
    {
        var token = TestContext.Current.CancellationToken;
        var runtime = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), token));
        var profile = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(Assert.Single(runtime.Profiles)));
        using var scope = TelemetryProfileScope.Enter(profile);
        var spans = new ConcurrentQueue<Activity>();
        using var parent = new Activity("listener-telemetry-fixture").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "ScaleSet.Listener" or "Knightfall.ScaleSet.Aws.GitHub",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (span.TraceId == parent.TraceId)
                {
                    spans.Enqueue(span);
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);
        var measurements = new ConcurrentQueue<(string Operation, string? Unit, double Value)>();
        using var meter = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Knightfall.ScaleSet.Aws.GitHub" &&
                    instrument.Name == "scaleset.aws.listener.operation.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meter.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var fields = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (fields.GetValueOrDefault("profile") is string id && id == profile.Id)
            {
                Assert.Equal("fargate", fields["provider"]);
                Assert.DoesNotContain(fields.Keys, key => key is not ("profile" or "provider" or "operation" or "outcome" or "reason"));
                measurements.Enqueue(((string)fields["operation"]!, instrument.Unit, value));
            }
        });
        meter.Start();
        var clock = new ContractClock();
        var wireClock = new QueueTimestampClock();
        var queue = new QueueSession
        {
            Poll = _ =>
            {
                wireClock.Advance(TimeSpan.FromSeconds(2));
                return Task.FromResult<RunnerScaleSetMessage?>(new() { MessageId = MessageId });
            },
            Delete = () => wireClock.Advance(TimeSpan.FromMilliseconds(250)),
        };
        var store = StartStore(owner: CancellationToken.None);
        var complete = new ConsumerDeliveryHandler(static (_, _) => Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed));
        var pump = CreatePump(new ObservedMessageSession(queue, wireClock), store, complete, clock, CancellationToken.None);

        var result = await pump.StepAsync(token);

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, result.Status);
        Assert.Equal((0L, 7), Assert.Single(queue.Polls));
        Assert.Equal(MessageId, Assert.Single(queue.Deletes));
        var poll = Assert.Single(spans, span => span.OperationName == "scaleset.aws.listener.poll");
        var ack = Assert.Single(spans, span => span.OperationName == "scaleset.aws.listener.acknowledge");
        Assert.Equal("received", poll.GetTagItem("outcome"));
        Assert.Equal("returned", ack.GetTagItem("outcome"));
        foreach (var span in new[] { poll, ack })
        {
            Assert.Equal("scaleset.listener.operation", span.Parent?.OperationName);
            Assert.Equal("ScaleSet.Listener", span.Parent?.Source.Name);
            Assert.Equal(profile.Id, span.GetTagItem("profile"));
        }
        Assert.Collection(measurements,
            value => Assert.Equal(("poll", "s", 2.0), value),
            value => Assert.Equal(("acknowledge", "s", 0.25), value));
    }

    [Fact]
    public async Task ReceivedIsDurableBeforeHandlingAndReplayRetainsObservationTime()
    {
        var clock = new ContractClock();
        var observedAt = clock.Now;
        var queue = new QueueSession();
        var store = StartStore(owner: CancellationToken.None);
        var deliveries = new List<ScaleSetListenerDelivery>();
        var handler = new ConsumerDeliveryHandler((delivery, _) =>
        {
            var receipt = Assert.Single(store.Committed);
            Assert.Equal(ScaleSetListenerPendingPhase.Received, receipt.Pending?.Phase);
            Assert.Equal(Key, receipt.SessionKey);
            Assert.Equal(MessageId, receipt.Pending?.Delivery.Message?.MessageId);
            Assert.Equal(observedAt, receipt.Pending?.Delivery.ObservedAt);
            deliveries.Add(delivery);
            return Task.FromResult(delivery.IsReplay
                ? ScaleSetListenerDeliveryDisposition.Completed : ScaleSetListenerDeliveryDisposition.Deferred);
        });

        var first = await CreatePump(queue, store, handler, clock, CancellationToken.None).StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ScaleSetListenerStepStatus.Deferred, first.Status);
        Assert.Empty(queue.Deletes);
        Assert.False(Assert.Single(deliveries).IsReplay);
        clock.Now = observedAt.AddMinutes(5);

        var replay = await CreatePump(queue, store, handler, clock, CancellationToken.None).StepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ScaleSetListenerStepResult(ScaleSetListenerStepStatus.MessageAcknowledged, MessageId), replay);
        Assert.Equal((0L, 7), Assert.Single(queue.Polls));
        Assert.Equal(MessageId, Assert.Single(queue.Deletes));
        Assert.Collection(deliveries, delivery => Assert.False(delivery.IsReplay), delivery => Assert.True(delivery.IsReplay));
        Assert.All(deliveries, delivery => Assert.Equal(observedAt, delivery.ObservedAt));
        Assert.Collection(store.Committed,
            checkpoint => Assert.Equal(ScaleSetListenerPendingPhase.Received, checkpoint.Pending?.Phase),
            checkpoint => Assert.Equal(ScaleSetListenerPendingPhase.Processed, checkpoint.Pending?.Phase),
            checkpoint => Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, checkpoint.Pending?.Phase),
            checkpoint =>
            {
                Assert.Null(checkpoint.Pending);
                Assert.Equal(MessageId, checkpoint.LastAcknowledgedMessageId);
            });
        Assert.Equal(new long[] { 3, 4, 5, 6 }, store.Committed.Select(checkpoint => checkpoint.Revision));
        Assert.Equal(4, store.Committed.Select(checkpoint => checkpoint.TransitionId).Distinct().Count());
    }

    [Fact]
    public async Task ProcessedResumesAtAcknowledgementWithoutPollingOrHandling()
    {
        var clock = new ContractClock();
        var queue = new QueueSession();
        var store = StartStore(phase: ScaleSetListenerPendingPhase.Processed, clock: clock, owner: CancellationToken.None);
        var pump = CreatePump(queue, store, UnexpectedDelivery(), clock, CancellationToken.None);

        var result = await pump.StepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, result.Status);
        Assert.Empty(queue.Polls);
        Assert.Equal(MessageId, Assert.Single(queue.Deletes));
        Assert.Collection(store.Committed,
            checkpoint =>
            {
                Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, checkpoint.Pending?.Phase);
                Assert.Equal(18L, checkpoint.LastAcknowledgedMessageId);
            },
            checkpoint =>
            {
                Assert.Null(checkpoint.Pending);
                Assert.Equal(MessageId, checkpoint.LastAcknowledgedMessageId);
            });
    }

    [Fact]
    public async Task LostQueueDeleteResponseRemainsAcknowledgingAfterReconstruction()
    {
        var clock = new ContractClock();
        var store = StartStore(phase: ScaleSetListenerPendingPhase.Processed, clock: clock, owner: CancellationToken.None);
        var commits = new List<GitHubRequest>();
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Uri.Host == GitHubWire.QueueHost)
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, Assert.Single(store.Committed).Pending?.Phase);
                commits.Add(request);
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Queue DELETE response lost."));
            }
            return GitHubWire.Reply(HttpStatusCode.OK, GitHubWire.SessionJson());
        });
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = await client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", cancellationToken);
        using var telemetryParent = new Activity("ambiguous-ack-fixture").SetIdFormat(ActivityIdFormat.W3C).Start();
        var observations = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Knightfall.ScaleSet.Aws.GitHub",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (span.TraceId == telemetryParent.TraceId)
                {
                    observations.Enqueue(span);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var observed = new ObservedMessageSession(session, clock);
        var pump = CreatePump(observed, store, UnexpectedDelivery(), clock, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ScaleSetApiException>(() => pump.StepAsync(cancellationToken));
        var reconstructed = await CreatePump(observed, store, UnexpectedDelivery(), clock, CancellationToken.None).StepAsync(cancellationToken);

        var acknowledge = Assert.Single(observations);
        Assert.Equal("scaleset.aws.listener.acknowledge", acknowledge.OperationName);
        Assert.Equal("unknown", acknowledge.GetTagItem("outcome"));
        Assert.DoesNotContain(acknowledge.TagObjects, tag => tag.Value?.ToString()?.Contains(GitHubWire.QueueToken, StringComparison.Ordinal) == true);

        Assert.Null(error.StatusCode);
        Assert.True(error.IsTransientTransportFailure);
        Assert.Equal(new ScaleSetListenerStepResult(ScaleSetListenerStepStatus.RecoveryRequired,
            MessageId, ScaleSetListenerPendingPhase.Acknowledging), reconstructed);
        var committed = Assert.Single(commits);
        Assert.Equal($"{GitHubWire.QueuePath}/{MessageId}", committed.Uri.AbsolutePath);
        Assert.Equal("Bearer " + GitHubWire.QueueToken, committed.Authorization);
        Assert.Single(wire.Requests, request => request.Uri.Host == GitHubWire.QueueHost);
        var checkpoint = Assert.Single(store.Committed);
        Assert.Equal(18L, checkpoint.LastAcknowledgedMessageId);
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, checkpoint.Pending?.Phase);
    }

    [Fact(Timeout = 10_000)]
    public async Task FixedOwnerCancellationEndsPollAndBlocksLaterSteps()
    {
        using var ownershipLost = new CancellationTokenSource();
        var advertised = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new QueueSession
        {
            Poll = async token =>
            {
                advertised.SetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };
        var store = StartStore(owner: ownershipLost.Token);
        var pump = CreatePump(queue, store, UnexpectedDelivery(), new ContractClock(), ownershipLost.Token);
        var cancellationToken = TestContext.Current.CancellationToken;
        var active = pump.StepAsync(cancellationToken);
        var operation = await advertised.Task.WaitAsync(cancellationToken);

        await ownershipLost.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.True(operation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.StepAsync(cancellationToken));
        Assert.Single(queue.Polls);
        Assert.Empty(queue.Deletes);
        Assert.Empty(store.Committed);
    }

    [Theory]
    [InlineData(ScaleSetListenerDeliveryDisposition.Completed)]
    [InlineData(ScaleSetListenerDeliveryDisposition.Deferred)]
    public async Task GracefulStopSettlesOnlyACompletedHandler(ScaleSetListenerDeliveryDisposition disposition)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var queue = new QueueSession();
        var store = StartStore(owner: CancellationToken.None);
        var handler = new ConsumerDeliveryHandler(async (_, token) =>
        {
            await stopping.CancelAsync();
            Assert.True(token.IsCancellationRequested);
            return disposition;
        });
        var pump = CreatePump(queue, store, handler, new ContractClock(), CancellationToken.None);

        if (disposition == ScaleSetListenerDeliveryDisposition.Completed)
        {
            var result = await pump.StepAsync(stopping.Token);
            Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, result.Status);
            Assert.Equal(MessageId, Assert.Single(queue.Deletes));
            Assert.Equal(MessageId, store.Committed[^1].LastAcknowledgedMessageId);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.StepAsync(stopping.Token));
            Assert.Empty(queue.Deletes);
            Assert.Equal(ScaleSetListenerPendingPhase.Received, Assert.Single(store.Committed).Pending?.Phase);
        }
    }

    [Fact]
    public async Task ChangedSessionGuidCannotResumeTheOldAcknowledgement()
    {
        var clock = new ContractClock();
        var queue = new QueueSession
        {
            Session = new RunnerScaleSetSession
            {
                SessionId = Guid.Parse("69efdd12-5aaa-448a-8824-e74107e0cb77"),
                Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 3 },
            },
        };
        var store = StartStore(phase: ScaleSetListenerPendingPhase.Processed, clock: clock, owner: CancellationToken.None);
        var pump = CreatePump(queue, store, UnexpectedDelivery(), clock, CancellationToken.None);
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.StepAsync(cancellationToken));

        Assert.Empty(queue.Polls);
        Assert.Empty(queue.Deletes);
        Assert.Empty(store.Committed);
        var replacement = Key with { SessionId = queue.Session.SessionId };
        var newPump = new ScaleSetListenerPump(queue, new ScaleSetListenerPumpOptions(replacement, default), store,
            new ConsumerDeliveryHandler((delivery, _) =>
            {
                Assert.Equal(replacement, delivery.SessionKey);
                Assert.Equal(3, delivery.InitialStatistics?.TotalAssignedJobs);
                Assert.Null(delivery.Message);
                return Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed);
            }), new ConsumerExecutionPolicy(clock, 7, default), clock);

        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await newPump.StepAsync(cancellationToken)).Status);
        var fresh = await store.ReadAsync(replacement, cancellationToken);
        Assert.NotNull(fresh);
        Assert.True(fresh.InitialStatisticsCompleted);
        Assert.Null(fresh.LastAcknowledgedMessageId);
        Assert.Null(fresh.Pending);
        var old = await store.ReadAsync(Key, cancellationToken);
        Assert.NotNull(old);
        Assert.Equal(ScaleSetListenerPendingPhase.Processed, old.Pending?.Phase);
        Assert.Equal(18L, old.LastAcknowledgedMessageId);
        Assert.Empty(queue.Polls);
        Assert.Empty(queue.Deletes);
    }

    private static ConsumerCheckpointStore StartStore(
        ScaleSetListenerPendingPhase? phase = null, ContractClock? clock = null, CancellationToken owner = default)
    {
        ScaleSetListenerPendingDelivery? pending = phase is null ? null
            : new ScaleSetListenerPendingDelivery(
                new ScaleSetListenerDelivery(Key, ScaleSetListenerDeliveryKind.Message, clock!.Now,
                    new RunnerScaleSetMessage { MessageId = MessageId }), phase.Value);
        return new ConsumerCheckpointStore(new ScaleSetListenerCheckpoint(Key,
            ScaleSetListenerCheckpoint.CurrentSchemaVersion, pending is null ? 2 : 4,
            Guid.Parse("3fd72640-0a4e-4027-a7bf-dc1c54598caf"), true, pending is null ? null : 18L, pending), owner);
    }

    private static ScaleSetListenerPump CreatePump(
        IMessageSessionClient session, ConsumerCheckpointStore store, ConsumerDeliveryHandler handler,
        ContractClock clock, CancellationToken owner) =>
        new(session, new ScaleSetListenerPumpOptions(Key, owner), store, handler,
            new ConsumerExecutionPolicy(clock, 7, owner), clock);

    private static ConsumerDeliveryHandler UnexpectedDelivery() =>
        new(static (_, _) => throw new InvalidOperationException("The delivery was already processed."));

    private sealed class QueueTimestampClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class QueueSession : IMessageSessionClient
    {
        public RunnerScaleSetSession Session { get; init; } = new() { SessionId = Key.SessionId };

        internal List<(long Cursor, int Capacity)> Polls { get; } = [];
        internal List<long> Deletes { get; } = [];
        internal Func<CancellationToken, Task<RunnerScaleSetMessage?>>? Poll { get; init; }
        internal Action? Delete { get; init; }

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
        {
            Polls.Add((lastMessageId, maxCapacity));
            return Poll?.Invoke(cancellationToken) ?? Task.FromResult<RunnerScaleSetMessage?>(new() { MessageId = MessageId });
        }

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deletes.Add(messageId);
            Delete?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The fixture did not schedule acquisition.");
        }
    }
}
