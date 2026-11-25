using System.Collections.Immutable;
using System.Globalization;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed record RunnerBackendResource(
    string ResourceArn, ExecutorBinding? Binding, ExecutorResourceObservation? Observation, string? BootstrapCredentialArn)
{
    public string? Ec2StatusQualificationSha256 { get; init; }

    public bool IsStoppedEc2 { get; init; }
}

internal sealed record RunnerBackendInventory(
    DateTimeOffset ObservedAt, bool IsComplete, int? LaunchHeadroom, int? DriftCount, ImmutableArray<RunnerBackendResource> Resources);

internal static class RunnerCapacityProjection
{
    internal static InventorySample Project(RunnerProfile profile, ExecutorInventorySnapshot snapshot, DateTimeOffset observedAt,
        int unowned, bool complete, int? launchHeadroom, DateTimeOffset now, EffectDispatcherTiming timing)
    {
        var slots = ImmutableArray.CreateBuilder<RunnerSlot>();
        foreach (var executor in snapshot.Executors)
        {
            var classified = ExecutorClassifier.Classify(executor.State, now, timing.ObservationFreshness, timing.LogFlushGrace);
            if (classified.Occupied)
            {
                // The sample's cut-off can precede a concurrent event update. Such a start adds no credible pending capacity.
                var start = executor.State.StartedAt <= observedAt ? executor.State.StartedAt : null;
                slots.Add(new RunnerSlot(executor.Binding.ResourceArn, classified.SlotState, OwnershipVerified: true, start)
                {
                    CoversIdleReserve = classified.CoversIdleReserve,
                });
            }
        }
        for (int index = 0; index < unowned; index++)
        {
            slots.Add(new RunnerSlot("unowned-" + index.ToString(CultureInfo.InvariantCulture), RunnerSlotState.Unknown, OwnershipVerified: false));
        }
        int? ceiling = complete && launchHeadroom is int headroom
            ? (int)Math.Min(profile.Capacity.MaxRunners, (long)slots.Count + headroom) : null;
        return new InventorySample(observedAt, complete, ceiling, complete ? launchHeadroom : null, slots.ToImmutable());
    }
}

internal interface IRunnerBackend
{
    IReconciliationEffectProvider Effects { get; }

    IReconciliationEffectProvider ObservationEffects => Effects;

    Task<RunnerBackendInventory> ReadAsync(ControllerLease lease, CancellationToken cancellationToken);

    Task ReconcileLaunchesAsync(ActiveControllerAuthority authority, CatalogProfile catalog, bool allowPreparedLaunches,
        bool allowNewLaunch, CancellationToken ownershipLost, CancellationToken cancellationToken);

    Task CleanupAsync(ActiveControllerAuthority authority, CatalogProfile catalog, ExecutorSnapshot executor,
        CancellationToken ownershipLost, CancellationToken cancellationToken);
}

/// <summary>Limits background effect reconciliation to reads. Eligible Fargate resends use the launch lane.</summary>
internal sealed class RunnerEffectReadback(IReconciliationEffectProvider provider) : IReconciliationEffectProvider
{
    public Task<EffectDispatchPrecondition?> CheckAsync(EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken) =>
        Task.FromResult<EffectDispatchPrecondition?>(null);

    public Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult<EffectEvidence?>(null);

    public Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken) =>
        provider.ObserveAsync(envelope, attempt, cancellationToken);
}
