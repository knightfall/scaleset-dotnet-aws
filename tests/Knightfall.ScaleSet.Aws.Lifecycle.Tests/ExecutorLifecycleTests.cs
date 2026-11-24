using System.Security.Cryptography;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;
using static Knightfall.ScaleSet.Aws.Lifecycle.Tests.ListenerDeliveryTests;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class ExecutorLifecycleTests
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ExecutorPreservesIndependentTerminalFactsAndOriginalStartTime()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("executor-observation");
        var binding = new ExecutorBinding(profile, Guid.NewGuid().ToString("D"),
            "arn:aws:ecs:us-east-1:111122223333:task/runners/10000000000000000000000000000001", Guid.NewGuid(),
            new FargateExecutorIdentity("arn:aws:ecs:us-east-1:111122223333:cluster/runners",
                "arn:aws:ecs:us-east-1:111122223333:task-definition/runner:1"));
        const long runner = 101;
        await BindAsync(store, table.Lease, binding, runner, token);
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        var session = new TestSession(owned.Identity.SessionId);
        var handler = Handler(store, owned, session, clock);
        await InitializeAsync(handler, owned, clock.Now, 0, token);

        var start = clock.Now.AddSeconds(-10);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding,
            new ExecutorResourceObservation(ExecutorResourceState.Starting, clock.Now, start, Guid.NewGuid()), token));
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding,
            Resource(ExecutorResourceState.Running, clock.Now), token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveProcessAsync(table.Lease, binding,
            new ExecutorProcessObservation(runner, ExecutorProcessState.Ready, clock.Now, Guid.NewGuid()), token));
        var ready = await ReadAsync(store, binding, token);
        Assert.Equal(start, ready.State.StartedAt);
        Assert.Equal(RunnerSlotState.Idle, Classify(ready, clock.Now).SlotState);
        Assert.False(Classify(ready, clock.Now).CompletedCleanupCandidate);
        Assert.Equal(RunnerSlotState.Unknown, Classify(ready, clock.Now.Add(Freshness).AddTicks(1)).SlotState);

        clock.Now = clock.Now.AddSeconds(1);
        var exitAt = clock.Now;
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveProcessAsync(table.Lease, binding,
            new ExecutorProcessObservation(runner, ExecutorProcessState.Exited, exitAt, Guid.NewGuid()), token));
        var exited = await ReadAsync(store, binding, token);
        Assert.True(Classify(exited, clock.Now).Occupied);
        Assert.True(Classify(exited, clock.Now).Recovering);
        Assert.False(Classify(exited, clock.Now).CompletedCleanupCandidate);

        var completion = new RunnerScaleSetMessage
        {
            MessageId = 0,
            JobCompletedMessages = [new JobCompletedMessage { RunnerRequestId = 200 }],
            JobStartedMessages = [new JobStartedMessage { RunnerRequestId = 200, RunnerId = runner }],
        };
        var priorFacts = new
        {
            Assigned = (int?)null,
            OfferedIds = Array.Empty<long>(),
            Jobs = new[] { new { RequestId = 200L, Phase = ExecutorJobPhase.Completed, RunnerId = (long?)null } },
        };
        var priorReceipt = new
        {
            digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(priorFacts))),
            observedAt = clock.Now,
            jobOffset = 0,
            admission = (object?)null,
        };
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.WriteSessionRecordsAsync(owned, Guid.NewGuid(),
            [new LifecycleRecordWrite(LifecycleKey.Message(profile, owned.Identity.SessionId, 0), null, JsonSerializer.Serialize(priorReceipt))],
            token));
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(Delivery(owned, clock.Now, completion), token));
        var complete = await ReadAsync(store, binding, token);
        Assert.Equal(ExecutorJobPhase.Completed, complete.State.Job?.Phase);
        Assert.Equal(exitAt, complete.State.Process?.ObservedAt);
        Assert.False(Classify(complete, exitAt.Add(Grace).AddTicks(-1)).CompletedCleanupCandidate);
        Assert.True(Classify(complete, exitAt.Add(Grace)).CompletedCleanupCandidate);

        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding,
            Resource(ExecutorResourceState.IrreversiblyTerminated, clock.Now), token));
        var gone = await store.ReadExecutorInventoryAsync(profile, token);
        Assert.True(gone.IsConsistent);
        Assert.Empty(gone.Executors);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding,
            Resource(ExecutorResourceState.Running, clock.Now), token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveProcessAsync(table.Lease, binding,
            new ExecutorProcessObservation(runner, ExecutorProcessState.Ready, clock.Now, Guid.NewGuid()), token));
        var conflict = Assert.Single((await store.ReadExecutorInventoryAsync(profile, token)).Executors);
        Assert.True(conflict.State.HasConflict);
        Assert.Equal(start, conflict.State.StartedAt);
        Assert.Equal(ExecutorResourceState.IrreversiblyTerminated, conflict.State.Resource?.State);
        Assert.Equal(ExecutorProcessState.Exited, conflict.State.Process?.State);
        Assert.True(Classify(conflict, clock.Now).Recovering);
        Assert.False(Classify(conflict, clock.Now).CompletedCleanupCandidate);
        Assert.Equal(LifecycleWriteOutcome.Conflict, await store.BindExecutorAsync(table.Lease,
            binding with { AllocationId = Guid.NewGuid().ToString("D") }, token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadyAfterIrreversibleTerminationPreservesOccupiedConflictInEitherOrder(bool observeTerminationFirst)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now.AddSeconds(2) };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("executor-cross-axis-conflict");
        var binding = Ec2(profile, 1);
        const long runner = 109;
        await BindAsync(store, table.Lease, binding, runner, token);
        var terminal = Resource(ExecutorResourceState.IrreversiblyTerminated, clock.Now.AddSeconds(-1));
        var ready = new ExecutorProcessObservation(runner, ExecutorProcessState.Ready, clock.Now, Guid.NewGuid());

        if (observeTerminationFirst)
        {
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding, terminal, token));
            Assert.Empty((await store.ReadExecutorInventoryAsync(profile, token)).Executors);
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveProcessAsync(table.Lease, binding, ready, token));
        }
        else
        {
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveProcessAsync(table.Lease, binding, ready, token));
            Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding, terminal, token));
        }

        var inventory = await store.ReadExecutorInventoryAsync(profile, token);
        Assert.True(inventory.IsConsistent);
        var executor = Assert.Single(inventory.Executors);
        Assert.Equal(terminal, executor.State.Resource);
        Assert.Equal(ready, executor.State.Process);
        Assert.True(executor.State.HasConflict);
        var classification = Classify(executor, clock.Now);
        Assert.True(classification.Occupied);
        Assert.True(classification.Recovering);
        Assert.False(classification.CompletedCleanupCandidate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentDeliveryAndResourceUpdatesPreserveBothFacts(bool delayDelivery)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("executor-race");
        var binding = Ec2(profile, 1);
        const long runner = 109;
        await BindAsync(store, table.Lease, binding, runner, token);
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        var handler = Handler(store, owned, new TestSession(owned.Identity.SessionId), clock);
        await InitializeAsync(handler, owned, clock.Now, 1, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding,
            Resource(ExecutorResourceState.Running, clock.Now), token));
        clock.Now = clock.Now.AddSeconds(1);
        var resource = Resource(ExecutorResourceState.Unknown, clock.Now);
        var delivery = Delivery(owned, clock.Now, new RunnerScaleSetMessage
        {
            MessageId = 0,
            JobStartedMessages = [new JobStartedMessage { RunnerRequestId = 427, RunnerId = runner }],
        });
        var pause = table.Wire.PauseNextRead(LifecycleKey.Runner(profile, binding.AllocationId));
        if (delayDelivery)
        {
            var processing = handler.HandleAsync(delivery, token);
            try
            {
                await pause.Captured.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding, resource, token));
            }
            finally
            {
                pause.Resume();
            }
            Assert.Equal(ScaleSetListenerDeliveryDisposition.Deferred, await processing);
            Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(delivery, token));
        }
        else
        {
            var observing = store.ObserveResourceAsync(table.Lease, binding, resource, token);
            try
            {
                await pause.Captured.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(delivery, token));
            }
            finally
            {
                pause.Resume();
            }
            Assert.Equal(LifecycleWriteOutcome.Committed, await observing);
        }

        var executor = await ReadAsync(store, binding, token);
        Assert.Equal(resource, executor.State.Resource);
        Assert.Equal(ExecutorJobPhase.Started, executor.State.Job?.Phase);
        Assert.Equal(427, executor.State.Job?.RequestId);
        Assert.True(Classify(executor, clock.Now).Recovering);
        Assert.NotNull(await store.ReadRecordAsync(LifecycleKey.Job(profile, "427"), token));
    }

    [Fact]
    public async Task CapacitySubtractsOnlyExecutorsProvenInTheCurrentDemandSample()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("executor-cap-10");
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        for (int index = 1; index <= 4; index++)
        {
            var binding = Ec2(profile, index);
            await BindAsync(store, table.Lease, binding, index, token);
            if (index <= 2)
            {
                Assert.Equal(LifecycleWriteOutcome.Committed, await store.ObserveResourceAsync(table.Lease, binding,
                    Resource(ExecutorResourceState.Running, clock.Now), token));
            }
        }
        await PublishAsync(store, table.Lease, profile, 10, clock.Now, token);
        var session = new TestSession(owned.Identity.SessionId);
        var handler = new GitHubListenerDeliveryHandler(store, owned, session,
            cancellation => store.ReadListenerCapacityAsync(profile, 10, Freshness, Grace, cancellation), clock);
        await InitializeAsync(handler, owned, clock.Now, 8, token);
        var delivery = new RunnerScaleSetMessage
        {
            MessageId = 0,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 8 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 83 }],
            JobStartedMessages =
            [
                new JobStartedMessage { RunnerRequestId = 81, RunnerId = 1 },
                new JobStartedMessage { RunnerRequestId = 82, RunnerId = 2 },
            ],
        };
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(Delivery(owned, clock.Now, delivery), token));
        var capacity = await store.ReadListenerCapacityAsync(profile, 10, Freshness, Grace, token);
        Assert.True(capacity.IsReady);
        Assert.Equal(10, capacity.Maximum);
        Assert.Equal(2, capacity.OutsideAssigned);
        Assert.Empty(session.Acquisitions);

        var newer = new RunnerScaleSetMessage { MessageId = 1, Statistics = delivery.Statistics };
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(Delivery(owned, clock.Now, newer), token));
        Assert.Equal(4, (await store.ReadListenerCapacityAsync(profile, 10, Freshness, Grace, token)).OutsideAssigned);
    }

    [Fact]
    public async Task AdmissionRejectsAnInventoryRevisionThatPrecedesANewExecutor()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var clock = new ContractClock { Now = MiniStackTable.Now };
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var profile = table.Profile("executor-phantom");
        var owned = await OpenAsync(store, table.Lease, profile, Guid.NewGuid(), token);
        await PublishAsync(store, table.Lease, profile, 1, clock.Now, token);
        var session = new TestSession(owned.Identity.SessionId);
        bool insertAfterProjection = true;
        var handler = new GitHubListenerDeliveryHandler(store, owned, session, async cancellation =>
        {
            var capacity = await store.ReadListenerCapacityAsync(profile, 1, Freshness, Grace, cancellation);
            if (insertAfterProjection)
            {
                insertAfterProjection = false;
                Assert.Equal(0, capacity.OutsideAssigned);
                await BindAsync(store, table.Lease, Ec2(profile, 1), 109, cancellation);
            }
            return capacity;
        }, clock);
        await InitializeAsync(handler, owned, clock.Now, 0, token);
        var delivery = Delivery(owned, clock.Now, new RunnerScaleSetMessage
        {
            MessageId = 0,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 0 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 501 }],
        });
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Deferred, await handler.HandleAsync(delivery, token));
        Assert.Empty(session.Acquisitions);
        Assert.Equal(ScaleSetListenerDeliveryDisposition.Completed, await handler.HandleAsync(delivery, token));
        Assert.Empty(session.Acquisitions);
        Assert.Null(await store.ReadRecordAsync(LifecycleKey.Acquisition(profile, 501), token));
    }

    private static ExecutorBinding Ec2(LifecycleProfile profile, int instance) =>
        new(profile, Guid.NewGuid().ToString("D"),
            FormattableString.Invariant($"arn:aws:ec2:us-east-1:111122223333:instance/i-{instance:x17}"), Guid.NewGuid(),
            new Ec2ExecutorIdentity(MiniStackTable.Now, "lt-0123456789abcdef0", 1));

    private static async Task BindAsync(
        DynamoDbLifecycleStore store, DeploymentLeaseFence lease, ExecutorBinding binding, long runnerId, CancellationToken token)
    {
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindExecutorAsync(lease, binding, token));
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.BindExecutorRunnerAsync(lease, binding, runnerId, token));
    }

    private static async Task PublishAsync(
        DynamoDbLifecycleStore store, DeploymentLeaseFence lease, LifecycleProfile profile,
        int capacity, DateTimeOffset now, CancellationToken token)
    {
        var observed = await store.ReadExecutorInventoryAsync(profile, token);
        Assert.Equal(LifecycleWriteOutcome.Committed, await store.PublishExecutorInventoryAsync(profile, lease, observed.Revision,
            new ExecutorInventoryObservation(now, IsComplete: true, BackendCapacityCeiling: capacity), token));
    }

    private static ExecutorResourceObservation Resource(ExecutorResourceState state, DateTimeOffset now) =>
        new(state, now, null, Guid.NewGuid());

    private static async Task<ExecutorSnapshot> ReadAsync(DynamoDbLifecycleStore store, ExecutorBinding binding, CancellationToken token) =>
        Assert.IsType<ExecutorSnapshot>(await store.ReadExecutorAsync(binding.Profile, binding.AllocationId, token));

    private static ExecutorClassification Classify(ExecutorSnapshot executor, DateTimeOffset now) =>
        ExecutorClassifier.Classify(executor.State, now, Freshness, Grace);

    private static GitHubListenerDeliveryHandler Handler(
        DynamoDbLifecycleStore store, OwnedSessionFence owned, TestSession session, TimeProvider time) =>
        new(store, owned, session, _ => Task.FromResult(new ListenerCapacity(0, 0)), time);
}
