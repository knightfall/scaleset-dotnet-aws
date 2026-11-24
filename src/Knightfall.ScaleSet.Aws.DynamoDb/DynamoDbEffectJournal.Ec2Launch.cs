using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbEffectJournal
{
    internal Task<LifecycleWriteOutcome> ResolveEc2ZonalRejectionAsync(
        ControllerLease lease, EffectJournalSnapshot expected, EffectSendAuthorization sent, Guid transitionId,
        EffectEvidence evidence, CancellationToken cancellationToken)
    {
        if (lease.Mode != ControllerLeaseMode.Active || lease.Fence != sent.Lease || !sent.HasStartedSending ||
            sent.Attempt.LeaseEpoch != lease.Epoch || expected.Revision != sent.Revision ||
            expected.TransitionId != sent.BeginTransitionId || expected.Entry.State != EffectState.Executing ||
            expected.Entry.Attempt != sent.Attempt || !expected.Entry.Envelope.Matches(sent.Envelope) ||
            evidence.Ec2LaunchRejection is null || !expected.Entry.CanResolve(evidence))
        {
            return Task.FromResult(LifecycleWriteOutcome.Conflict);
        }

        return ResolveCoreAsync(lease, expected, transitionId, evidence, cancellationToken);
    }
}
