using Knightfall.ScaleSet.Aws.Core.Catalog;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public static class EffectIntentIdentity
{
    public static string? ForAllocation(EffectEnvelopeRequest request) => request switch
    {
        Ec2LaunchEffectRequest effect => ForEc2Launch(effect.AllocationId, effect.AttemptOrdinal),
        Ec2TerminateEffectRequest effect => "ec2-terminate-" + effect.AllocationId.ToString("D"),
        FargateLaunchEffectRequest effect => "fargate-launch-" + effect.AllocationId.ToString("D"),
        FargateStopEffectRequest effect => "fargate-stop-" + effect.AllocationId.ToString("D"),
        _ => null,
    };

    public static string ForEc2Launch(Guid allocationId, int attemptOrdinal) =>
        attemptOrdinal == 0 ? "ec2-launch-" + allocationId.ToString("D") :
            FormattableString.Invariant($"ec2-launch-{allocationId:D}-{attemptOrdinal}");
}

public sealed record EffectDispatchPrecondition(
    string EnvelopeSha256, EffectTargetBinding Target, DateTimeOffset ObservedAt, bool Eligible, EffectEvidence? AlreadyApplied = null)
{
    public bool DefinitivelyObsolete { get; init; }
}

public interface IReconciliationEffectProvider
{
    Task<EffectDispatchPrecondition?> CheckAsync(
        EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken);

    Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken);

    Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken);
}

/// <summary>A complete answer from exact allocation correlation, with optional positive resource evidence.</summary>
public sealed record FargateReplayObservation(string EnvelopeSha256, EffectTargetBinding Target, DateTimeOffset ObservedAt,
    bool IsComplete, EffectEvidence? Evidence = null);

public interface IFargateReplayEffectProvider : IReconciliationEffectProvider
{
    /// <summary>Rechecks the immutable launch inputs before a possible resend of an existing reservation.</summary>
    Task<EffectDispatchPrecondition?> CheckReplayAsync(EffectEnvelope envelope, CatalogBinding binding,
        CancellationToken cancellationToken) => CheckAsync(envelope, binding, cancellationToken);

    Task<FargateReplayObservation?> ObserveForReplayAsync(EffectEnvelope envelope, EffectAttempt attempt,
        CancellationToken cancellationToken);
}
