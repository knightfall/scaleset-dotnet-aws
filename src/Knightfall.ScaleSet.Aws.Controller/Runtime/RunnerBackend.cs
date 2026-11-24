using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed record RunnerBackendResource(
    string ResourceArn, ExecutorBinding? Binding, ExecutorResourceObservation? Observation, string? BootstrapCredentialArn);

internal sealed record RunnerBackendInventory(
    DateTimeOffset ObservedAt, bool IsComplete, int? LaunchHeadroom, int? DriftCount, ImmutableArray<RunnerBackendResource> Resources);

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
