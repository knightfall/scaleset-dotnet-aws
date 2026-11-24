using Knightfall.ScaleSet.Aws.Core.Catalog;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public static class EffectIntentIdentity
{
    public static string? ForAllocation(EffectEnvelopeRequest request) => request switch
    {
        Ec2LaunchEffectRequest effect => "ec2-launch-" + effect.AllocationId.ToString("D"),
        Ec2TerminateEffectRequest effect => "ec2-terminate-" + effect.AllocationId.ToString("D"),
        FargateLaunchEffectRequest effect => "fargate-launch-" + effect.AllocationId.ToString("D"),
        FargateStopEffectRequest effect => "fargate-stop-" + effect.AllocationId.ToString("D"),
        _ => null,
    };
}

public sealed record EffectDispatchPrecondition(
    string EnvelopeSha256, EffectTargetBinding Target, DateTimeOffset ObservedAt, bool Eligible);

public interface IReconciliationEffectProvider
{
    Task<EffectDispatchPrecondition?> CheckAsync(
        EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken);

    Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken);

    Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken);
}
