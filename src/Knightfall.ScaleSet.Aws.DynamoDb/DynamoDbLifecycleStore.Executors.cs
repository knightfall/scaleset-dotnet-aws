using System.Collections.Immutable;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    private const string ActiveRunnerPrefix = "ACTIVE-RUNNER#";
    private const int MaximumExecutorWriteAttempts = 3;

    private static readonly JsonSerializerOptions ExecutorJson = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ExecutorSnapshot?> ReadExecutorAsync(
        LifecycleProfile profile, string allocationId, CancellationToken cancellationToken)
    {
        var record = await ReadRecordAsync(LifecycleKey.Runner(profile, allocationId), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }
        var state = ParseExecutorRecord<ExecutorState>(record);
        try
        {
            state.Binding.Validate();
        }
        catch (ArgumentException)
        {
            throw InvalidRecord();
        }
        if (state.Binding.Profile != profile || state.Binding.AllocationId != allocationId || state.RunnerId <= 0 ||
            (state.Process is not null && state.Process.RunnerId != state.RunnerId) ||
            (state.Job is not null && state.Job.RunnerId != state.RunnerId))
        {
            throw InvalidRecord();
        }
        return new ExecutorSnapshot(state, record.Revision, record.TransitionId);
    }

    public async Task<LifecycleWriteOutcome> BindExecutorAsync(
        DeploymentLeaseFence lease, ExecutorBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        RequireProfileLease(binding.Profile, lease);
        var claimKey = LifecycleKey.Claim("aws-resource#" + binding.ResourceArn);
        for (int attempt = 0; attempt < MaximumExecutorWriteAttempts; attempt++)
        {
            var present = await ReadExecutorAsync(binding.Profile, binding.AllocationId, cancellationToken).ConfigureAwait(false);
            if (present is not null)
            {
                return present.Binding == binding ? LifecycleWriteOutcome.Committed : LifecycleWriteOutcome.Conflict;
            }
            if (await ReadRecordAsync(claimKey, cancellationToken).ConfigureAwait(false) is not null)
            {
                return LifecycleWriteOutcome.Conflict;
            }
            var inventory = await ReadExecutorInventoryHeadAsync(binding.Profile, cancellationToken).ConfigureAwait(false);
            var next = new ExecutorState(binding, StartedAt: (binding.Provider as Ec2ExecutorIdentity)?.InstanceLaunchTime);
            var transition = Guid.NewGuid();
            var actions = new List<TransactionAction>
            {
                LeaseCondition(lease),
                ExecutorWrite(claimKey, binding, null, transition, lease.Deployment),
            };
            int change = AddExecutorActions(actions, null, next, transition);
            actions.Add(WriteExecutorInventoryHead(binding.Profile, inventory, change, transition));
            var outcome = await WriteAsync(actions, transition, cancellationToken).ConfigureAwait(false);
            if (outcome != LifecycleWriteOutcome.Conflict)
            {
                return await ResolveExecutorWriteAsync(binding, transition, outcome, cancellationToken).ConfigureAwait(false);
            }
        }
        return LifecycleWriteOutcome.Conflict;
    }

    private TransactionAction ExactExecutorClaimCondition(LifecycleKey key, LifecycleRecord claim, string deployment) =>
        new(key, new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = _tableName,
                Key = Key(key),
                ConditionExpression = "#deployment = :deployment AND #revision = :revision AND #transition = :transition AND #payload = :payload",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["#deployment"] = "deployment",
                    ["#revision"] = "revision",
                    ["#transition"] = "transitionId",
                    ["#payload"] = "payload",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":deployment"] = StringValue(deployment),
                    [":revision"] = NumberValue(claim.Revision),
                    [":transition"] = StringValue(claim.TransitionId.ToString("D")),
                    [":payload"] = StringValue(claim.Payload ?? throw InvalidRecord()),
                },
            },
        });

    public Task<LifecycleWriteOutcome> BindExecutorRunnerAsync(
        DeploymentLeaseFence lease, ExecutorBinding binding, long runnerId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runnerId);
        return UpdateExecutorAsync(lease, binding, state => state with { RunnerId = runnerId },
            runnerId, cancellationToken);
    }

    public Task<LifecycleWriteOutcome> ObserveResourceAsync(
        DeploymentLeaseFence lease, ExecutorBinding binding, ExecutorResourceObservation observation,
        CancellationToken cancellationToken) =>
        UpdateExecutorAsync(lease, binding, state => ExecutorObservations.ObserveResource(state, observation, _timeProvider.GetUtcNow()),
            null, cancellationToken);

    public Task<LifecycleWriteOutcome> ObserveProcessAsync(
        DeploymentLeaseFence lease, ExecutorBinding binding, ExecutorProcessObservation observation,
        CancellationToken cancellationToken) =>
        UpdateExecutorAsync(lease, binding, state => ExecutorObservations.ObserveProcess(state, observation, _timeProvider.GetUtcNow()),
            null, cancellationToken);

    public async Task<ExecutorInventorySnapshot> ReadExecutorInventoryAsync(
        LifecycleProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var inventory = await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        if (inventory is null)
        {
            return new ExecutorInventorySnapshot(null, null, [], IsConsistent: false);
        }

        var executors = ImmutableArray.CreateBuilder<ExecutorSnapshot>();
        string? continuation = null;
        do
        {
            var page = await ReadBasePageAsync(profile.PartitionKey, ActiveRunnerPrefix, continuation, cancellationToken)
                .ConfigureAwait(false);
            if ((long)executors.Count + page.Items.Count > inventory.Payload.ActiveCount ||
                (page.Items.Count == 0 && page.Continuation is not null))
            {
                return UnstableInventory(inventory);
            }
            foreach (var marker in page.Items)
            {
                string sortKey = Text(marker, "SK");
                RequireKey(marker, profile.PartitionKey, sortKey);
                if (!sortKey.StartsWith(ActiveRunnerPrefix, StringComparison.Ordinal))
                {
                    throw InvalidRecord();
                }
                string allocationId = sortKey[ActiveRunnerPrefix.Length..];
                var executor = await ReadExecutorAsync(profile, allocationId, cancellationToken).ConfigureAwait(false);
                if (executor is null || !IsActiveExecutor(executor.State) || executor.Binding.Incarnation != GuidValue(marker, "incarnation") ||
                    Text(marker, "deployment") != profile.Deployment)
                {
                    return UnstableInventory(inventory);
                }
                executors.Add(executor);
            }
            continuation = page.Continuation;
        }
        while (continuation is not null);

        if (executors.Count != inventory.Payload.ActiveCount ||
            await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false) != inventory)
        {
            return UnstableInventory(inventory);
        }
        return new ExecutorInventorySnapshot(inventory.Record.Revision, inventory.Payload.Coverage, executors.ToImmutable(), IsConsistent: true);
    }

    public async Task<LifecycleWriteOutcome> PublishExecutorInventoryAsync(
        LifecycleProfile profile, DeploymentLeaseFence lease, long? expectedRevision,
        ExecutorInventoryObservation coverage, CancellationToken cancellationToken)
    {
        RequireProfileLease(profile, lease);
        ArgumentNullException.ThrowIfNull(coverage);
        if (!ValidExecutorCoverage(coverage) || coverage.ObservedAt > _timeProvider.GetUtcNow())
        {
            throw new ArgumentException("A valid locally timed inventory observation is required.", nameof(coverage));
        }

        var current = await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        if (current?.Record.Revision != expectedRevision || current?.Payload.Coverage?.ObservedAt > coverage.ObservedAt)
        {
            return LifecycleWriteOutcome.Conflict;
        }
        if (current?.Payload.Coverage == coverage)
        {
            return LifecycleWriteOutcome.Committed;
        }

        var transition = Guid.NewGuid();
        var head = new ExecutorInventoryHead(current?.Payload.ActiveCount ?? 0, coverage);
        return await WriteAsync(
            [LeaseCondition(lease, LeaseAccess.ActiveOrRecovery),
             ExecutorWrite(LifecycleKey.RunnerInventory(profile), head, expectedRevision, transition, profile.Deployment)],
            transition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ListenerCapacity> ReadListenerCapacityAsync(
        LifecycleProfile profile, int maximum, TimeSpan freshness, TimeSpan logFlushGrace, CancellationToken cancellationToken)
    {
        var demandRecord = await ReadRecordAsync(LifecycleKey.Demand(profile), cancellationToken).ConfigureAwait(false);
        var demand = demandRecord is null ? null : ParseExecutorRecord<SessionDemandObservation>(demandRecord);
        var session = await ReadSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        if (session?.Phase != SessionPhase.Active || session.SessionId != demand?.SessionId)
        {
            demand = null;
        }
        var inventory = await ReadExecutorInventoryAsync(profile, cancellationToken).ConfigureAwait(false);
        var capacity = ExecutorClassifier.ProjectListenerCapacity(
            inventory, demand, maximum, _timeProvider.GetUtcNow(), freshness, logFlushGrace);
        return capacity with
        {
            ReadGuards =
            [
                new LifecycleRecordCondition(LifecycleKey.Demand(profile), demandRecord?.Revision),
                new LifecycleRecordCondition(LifecycleKey.RunnerInventory(profile), inventory.Revision),
            ],
        };
    }

    private async Task<LifecycleWriteOutcome> UpdateExecutorAsync(
        DeploymentLeaseFence lease, ExecutorBinding binding, Func<ExecutorState, ExecutorState> update,
        long? bindingRunnerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        RequireProfileLease(binding.Profile, lease);
        for (int attempt = 0; attempt < MaximumExecutorWriteAttempts; attempt++)
        {
            var current = await ReadExecutorAsync(binding.Profile, binding.AllocationId, cancellationToken).ConfigureAwait(false);
            if (current?.Binding != binding)
            {
                return LifecycleWriteOutcome.Conflict;
            }
            if (bindingRunnerId is not null && current.State.RunnerId is long existing && existing != bindingRunnerId)
            {
                return LifecycleWriteOutcome.Conflict;
            }
            var next = update(current.State);
            if (next == current.State)
            {
                return LifecycleWriteOutcome.Committed;
            }

            var inventory = await ReadExecutorInventoryHeadAsync(binding.Profile, cancellationToken).ConfigureAwait(false) ?? throw InvalidRecord();
            var transition = Guid.NewGuid();
            var actions = new List<TransactionAction>
            {
                LeaseCondition(lease, bindingRunnerId is null ? LeaseAccess.ActiveOrRecovery : LeaseAccess.Active),
            };
            if (bindingRunnerId is long runnerId)
            {
                var key = LifecycleKey.RunnerIdentity(binding.Profile, runnerId);
                if (await ReadRecordAsync(key, cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }
                actions.Add(ExecutorWrite(key, new ExecutorRunnerBinding(binding.AllocationId, binding.Incarnation), null,
                    transition, lease.Deployment));
            }
            int change = AddExecutorActions(actions, current, next, transition);
            actions.Add(WriteExecutorInventoryHead(binding.Profile, inventory, change, transition));
            var outcome = await WriteAsync(actions, transition, cancellationToken).ConfigureAwait(false);
            if (outcome != LifecycleWriteOutcome.Conflict)
            {
                return await ResolveExecutorWriteAsync(binding, transition, outcome, cancellationToken).ConfigureAwait(false);
            }
        }
        return LifecycleWriteOutcome.Conflict;
    }

    private async Task<LifecycleWriteOutcome> ResolveExecutorWriteAsync(
        ExecutorBinding binding, Guid transition, LifecycleWriteOutcome outcome, CancellationToken cancellationToken) =>
        outcome == LifecycleWriteOutcome.Indeterminate &&
        (await ReadExecutorAsync(binding.Profile, binding.AllocationId, cancellationToken).ConfigureAwait(false))?.TransitionId == transition
            ? LifecycleWriteOutcome.Committed : outcome;

    private int AddExecutorActions(
        List<TransactionAction> actions, ExecutorSnapshot? previous, ExecutorState next, Guid transition)
    {
        var binding = next.Binding;
        var key = LifecycleKey.Runner(binding.Profile, binding.AllocationId);
        actions.Add(ExecutorWrite(key, next, previous?.Revision, transition, binding.Profile.Deployment));
        bool wasActive = previous is not null && IsActiveExecutor(previous.State);
        bool isActive = IsActiveExecutor(next);
        if (isActive == wasActive)
        {
            return 0;
        }

        var marker = LifecycleKey.ActiveRunner(binding.Profile, binding.AllocationId);
        var incarnation = StringValue(binding.Incarnation.ToString("D"));
        if (isActive)
        {
            var item = Record(marker, binding.Profile.Deployment, 1, transition);
            item.Add("incarnation", incarnation);
            actions.Add(CompareAndPut(marker, item, null, binding.Profile.Deployment));
            return 1;
        }
        var delete = new Delete
        {
            TableName = _tableName,
            Key = Key(marker),
            ConditionExpression = "#incarnation = :incarnation",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#incarnation"] = "incarnation" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal) { [":incarnation"] = incarnation },
        };
        actions.Add(new TransactionAction(marker, new TransactWriteItem { Delete = delete }));
        return -1;
    }

    private async Task<ExecutorInventoryEntry?> ReadExecutorInventoryHeadAsync(
        LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var record = await ReadRecordAsync(LifecycleKey.RunnerInventory(profile), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }
        var head = ParseExecutorRecord<ExecutorInventoryHead>(record);
        if (head.ActiveCount < 0 || (head.Coverage is not null && !ValidExecutorCoverage(head.Coverage)))
        {
            throw InvalidRecord();
        }
        return new ExecutorInventoryEntry(record, head);
    }

    private TransactionAction WriteExecutorInventoryHead(
        LifecycleProfile profile, ExecutorInventoryEntry? previous, int activeChange, Guid transition)
    {
        int active = checked((previous?.Payload.ActiveCount ?? 0) + activeChange);
        if (active < 0)
        {
            throw InvalidRecord();
        }
        var head = new ExecutorInventoryHead(active, previous?.Payload.Coverage);
        return ExecutorWrite(LifecycleKey.RunnerInventory(profile), head, previous?.Record.Revision, transition, profile.Deployment);
    }

    private TransactionAction ExecutorWrite<T>(
        LifecycleKey key, T value, long? revision, Guid transition, string deployment) =>
        BuildRecordWrite(deployment, transition, new LifecycleRecordWrite(key, revision, JsonSerializer.Serialize(value, ExecutorJson)));

    private static T ParseExecutorRecord<T>(LifecycleRecord record) where T : class
    {
        try
        {
            return record.Payload is { } payload && JsonSerializer.Deserialize<T>(payload, ExecutorJson) is { } value
                ? value : throw InvalidRecord();
        }
        catch (JsonException)
        {
            throw InvalidRecord();
        }
    }

    private static ExecutorInventorySnapshot UnstableInventory(ExecutorInventoryEntry inventory) =>
        new(inventory.Record.Revision, inventory.Payload.Coverage, [], IsConsistent: false);

    private static bool IsActiveExecutor(ExecutorState state) =>
        state.HasConflict || state.Resource?.State != ExecutorResourceState.IrreversiblyTerminated;

    private static bool ValidExecutorCoverage(ExecutorInventoryObservation coverage) =>
        coverage.ObservedAt != default && coverage.UnownedOccupied >= 0 &&
        coverage.BackendCapacityCeiling is not < 0 && coverage.BackendLaunchHeadroom is not < 0;

    private sealed record ExecutorInventoryHead(int ActiveCount, ExecutorInventoryObservation? Coverage);
    private sealed record ExecutorInventoryEntry(LifecycleRecord Record, ExecutorInventoryHead Payload);
    private sealed record ExecutorRunnerBinding(string AllocationId, Guid Incarnation);
}
