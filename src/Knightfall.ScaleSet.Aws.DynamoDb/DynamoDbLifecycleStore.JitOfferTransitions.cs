using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    public async Task<JitHandoffChange> PublishJitHandoffAsync(
        ControllerLease lease, CatalogProfile catalog, JitHandoffSnapshot handoff, EffectJournalSnapshot attempt,
        long runnerId, JitOfferEnvelope offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runnerId);
        JitOfferCryptography.ValidateEnvelope(offer);
        var profile = catalog.Profile;
        RequireProfileLease(profile, lease.Fence);

        var now = _timeProvider.GetUtcNow();
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.LeaseRejected);
        }
        if (handoff.State != JitHandoffState.Staged || !MatchesJitCatalog(catalog, handoff.Identity) ||
            !MatchesJitRequest(attempt.Entry.Envelope, handoff) || attempt.Entry.State != EffectState.Executing ||
            attempt.Entry.Attempt is not { } send || send.LeaseEpoch != lease.Epoch || send.Deadline <= now ||
            offer.Binding.Identity != handoff.Identity || offer.Binding.OfferId != handoff.OfferId ||
            offer.Binding.ProtocolVersion != handoff.Hello.ProtocolVersion ||
            !offer.Binding.BootNonce.AsSpan().SequenceEqual(handoff.Hello.BootNonce.AsSpan()) ||
            offer.Binding.ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds())
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }
        var executor = await ReadExecutorAsync(profile, handoff.Identity.AllocationId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (executor is null || !MatchesJitExecutor(executor, handoff.Identity) || !MatchesJitProvider(catalog, executor) ||
            executor.State is not { HasConflict: false, Resource.State: ExecutorResourceState.Running })
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }

        var offerKey = LifecycleKey.Offer(handoff.Identity.AwsUserId);
        var item = BootstrapItems.EncodeOffer(offer, handoff.HelloSha256);
        var next = handoff with
        {
            State = JitHandoffState.Offered,
            RunnerId = runnerId,
            OfferSha256 = BootstrapItems.Text(item, "offerSha256"),
            JitAttemptId = send.Id,
            Revision = NextRevision(handoff.Revision),
            TransitionId = Guid.NewGuid(),
        };
        var ownership = LeaseCondition(lease.Fence, validThrough: send.Deadline);
        RequireAppliedCatalog(ownership, catalog);
        var actions = new List<TransactionAction>
        {
            ownership,
            CatalogProfileCondition(catalog),
            ExactJitAttemptCondition(attempt),
            ExactJitHelloCondition(handoff),
            WriteExactJitHandoff(profile, handoff, next),
            CompareAndPut(offerKey, item, null, profile.Deployment),
        };
        if (!await AddJitRunnerAssociationAsync(actions, executor, runnerId, next.TransitionId, cancellationToken).ConfigureAwait(false))
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }
        return await CommitJitHandoffChangeAsync(profile, next, actions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JitHandoffChange> RecordUnissuedJitAsync(
        ControllerLease lease, JitHandoffSnapshot handoff, EffectJournalSnapshot attempt,
        long runnerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runnerId);
        var profile = attempt.Entry.Envelope.Profile;
        RequireProfileLease(profile, lease.Fence);
        if (lease.Mode == ControllerLeaseMode.Released)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.LeaseRejected);
        }
        if (!MatchesJitRequest(attempt.Entry.Envelope, handoff) ||
            attempt.Entry.State is not (EffectState.Executing or EffectState.Uncertain) || attempt.Entry.Attempt is not { } send ||
            (lease.Mode == ControllerLeaseMode.Active ? send.LeaseEpoch != lease.Epoch : send.LeaseEpoch >= lease.Epoch))
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }
        var stored = await ReadJitHandoffAsync(profile, handoff.Identity.AllocationId, cancellationToken).ConfigureAwait(false);
        if (stored is { State: JitHandoffState.RegisteredWithoutOffer } && stored.Identity == handoff.Identity &&
            stored.JitAttemptId == send.Id && stored.RunnerId == runnerId && stored.HelloSha256 == handoff.HelloSha256)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Committed, stored);
        }
        if (handoff.State != JitHandoffState.Staged || !MatchesHandoff(handoff, stored))
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }
        var executor = await ReadExecutorAsync(profile, handoff.Identity.AllocationId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (executor is null || !MatchesJitExecutor(executor, handoff.Identity))
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }

        var next = handoff with
        {
            State = JitHandoffState.RegisteredWithoutOffer,
            RunnerId = runnerId,
            JitAttemptId = send.Id,
            Revision = NextRevision(handoff.Revision),
            TransitionId = Guid.NewGuid(),
        };
        var actions = new List<TransactionAction>
        {
            LeaseCondition(lease.Fence, lease.Mode == ControllerLeaseMode.RecoveryOnly ? LeaseAccess.Recovery : LeaseAccess.Active),
            ExactJitAttemptCondition(attempt),
            WriteExactJitHandoff(profile, handoff, next),
            OfferAbsentCondition(handoff),
        };
        if (!await AddJitRunnerAssociationAsync(actions, executor, runnerId, next.TransitionId, cancellationToken).ConfigureAwait(false))
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }
        return await CommitJitHandoffChangeAsync(profile, next, actions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JitHandoffChange> RevokeJitHandoffAsync(
        ControllerLease lease, JitHandoffSnapshot handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(handoff);
        var profile = new LifecycleProfile(handoff.Identity.Deployment, handoff.Identity.ProfileId);
        RequireProfileLease(profile, lease.Fence);
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.LeaseRejected);
        }
        var current = await ReadJitHandoffAsync(profile, handoff.Identity.AllocationId, cancellationToken).ConfigureAwait(false);
        if (current is { State: JitHandoffState.Revoked, OfferSha256: not null } && current.Identity == handoff.Identity &&
            current.OfferSha256 == handoff.OfferSha256 && current.JitAttemptId == handoff.JitAttemptId)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Committed, current);
        }
        if (handoff.State != JitHandoffState.Offered || !MatchesHandoff(handoff, current))
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }

        var key = LifecycleKey.Offer(handoff.Identity.AwsUserId);
        var next = handoff with
        {
            State = JitHandoffState.Revoked,
            Revision = NextRevision(handoff.Revision),
            TransitionId = Guid.NewGuid(),
        };
        var delete = new TransactionAction(key, new TransactWriteItem
        {
            Delete = new Delete
            {
                TableName = _tableName,
                Key = Key(key),
                ConditionExpression = "offerId = :offer AND offerSha256 = :digest AND helloSha256 = :hello AND " +
                    "awsUserId = :principal AND resourceArn = :resource",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":offer"] = StringValue(handoff.OfferId),
                    [":digest"] = StringValue(handoff.OfferSha256!),
                    [":hello"] = StringValue(handoff.HelloSha256),
                    [":principal"] = StringValue(handoff.Identity.AwsUserId),
                    [":resource"] = StringValue(handoff.Identity.ResourceArn),
                },
            },
        });
        // Revocation removes the offer. The counted handoff remains until the executor and registration have been reconciled.
        return await CommitJitHandoffChangeAsync(profile, next,
            [LeaseCondition(lease.Fence), WriteExactJitHandoff(profile, handoff, next), delete], cancellationToken).ConfigureAwait(false);
    }

    public async Task<JitHandoffChange> CompleteTerminatedJitHandoffAsync(
        ControllerLease lease, JitHandoffSnapshot handoff, EffectJournalSnapshot? generation, EffectJournalSnapshot? removal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(handoff);
        var profile = new LifecycleProfile(handoff.Identity.Deployment, handoff.Identity.ProfileId);
        RequireProfileLease(profile, lease.Fence);
        if (lease.Mode == ControllerLeaseMode.Released)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.LeaseRejected);
        }
        var stored = await ReadJitHandoffAsync(profile, handoff.Identity.AllocationId, cancellationToken).ConfigureAwait(false);
        if (stored is { State: JitHandoffState.ResourceTerminated } && stored.Identity == handoff.Identity &&
            stored.HelloSha256 == handoff.HelloSha256)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Committed, stored);
        }
        if (!MatchesHandoff(handoff, stored) || !MatchesTerminalJitEffects(handoff, generation, removal))
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }
        var executor = await ReadExecutorAsync(profile, handoff.Identity.AllocationId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (executor is null || !MatchesJitExecutor(executor, handoff.Identity) || executor.State.RunnerId != handoff.RunnerId ||
            executor.State is not { HasConflict: false, Resource.State: ExecutorResourceState.IrreversiblyTerminated } ||
            executor.State.Job is { Phase: not ExecutorJobPhase.Completed } or { HasConflict: true })
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }

        var head = await ReadJitHandoffHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        var next = handoff with
        {
            State = JitHandoffState.ResourceTerminated,
            Revision = NextRevision(handoff.Revision),
            TransitionId = Guid.NewGuid(),
        };
        var actions = new List<TransactionAction>
        {
            LeaseCondition(lease.Fence, lease.Mode == ControllerLeaseMode.RecoveryOnly ? LeaseAccess.Recovery : LeaseAccess.Active),
            RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Runner(profile, executor.Binding.AllocationId), executor.Revision)),
            generation is null ? RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Operation(profile, handoff.IntentId), null)) :
                ExactJitAttemptCondition(generation),
            WriteExactJitHandoff(profile, handoff, next),
            OfferAbsentCondition(handoff),
            WriteJitHandoffHead(profile, head, -1, next.TransitionId),
        };
        if (removal is not null)
        {
            actions.Add(ExactJitAttemptCondition(removal));
        }
        return await CommitJitHandoffChangeAsync(profile, next, actions, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesTerminalJitEffects(JitHandoffSnapshot handoff, EffectJournalSnapshot? generation, EffectJournalSnapshot? removal)
    {
        if (generation is not null && (generation.Entry.State != EffectState.Settled || !MatchesJitRequest(generation.Entry.Envelope, handoff)))
        {
            return false;
        }
        if (handoff.RunnerId is not long runnerId)
        {
            return removal is null && (generation is null ||
                generation.Entry.Resolution is { Kind: EffectResolutionKind.Absent or EffectResolutionKind.CanceledBeforeSend or EffectResolutionKind.Rejected });
        }
        var identity = handoff.Identity;
        return generation is { Entry.State: EffectState.Settled, Entry.Resolution.Kind: EffectResolutionKind.Applied } &&
            generation.Entry.Attempt?.Id == handoff.JitAttemptId &&
            removal is { Entry.State: EffectState.Settled, Entry.Resolution.Kind: EffectResolutionKind.Applied or EffectResolutionKind.Absent } &&
            removal.Entry.Envelope.IntentId == "remove-" + handoff.OfferId &&
            IsHandoffProfile(removal.Entry.Envelope.Profile, identity) &&
            removal.Entry.Envelope.Request is GitHubRunnerRemoveEffectRequest request &&
            request.ScopeIdentity == identity.ScopeIdentity && request.ScaleSetId == identity.ScaleSetId &&
            request.AllocationId == identity.AllocationId && request.ResourceArn == identity.ResourceArn &&
            request.Incarnation == identity.Incarnation && request.RunnerName == handoff.RunnerName && request.RunnerId == runnerId;
    }

    private TransactionAction OfferAbsentCondition(JitHandoffSnapshot handoff) =>
        RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Offer(handoff.Identity.AwsUserId), null));

    private TransactionAction ExactJitAttemptCondition(EffectJournalSnapshot attempt) =>
        ExactJitCondition(LifecycleKey.Operation(attempt.Entry.Envelope.Profile, attempt.Entry.Envelope.IntentId),
            "revision = :revision AND transitionId = :transition AND effectState = :state AND payload = :payload",
            new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":revision"] = NumberValue(attempt.Revision),
                [":transition"] = StringValue(attempt.TransitionId.ToString("D")),
                [":state"] = StringValue(EffectStateValue(attempt.Entry.State)),
                [":payload"] = StringValue(attempt.Entry.WritePayload()),
            });

    private async Task<bool> AddJitRunnerAssociationAsync(List<TransactionAction> actions, ExecutorSnapshot executor,
        long runnerId, Guid transition, CancellationToken cancellationToken)
    {
        var binding = executor.Binding;
        var profile = binding.Profile;
        var key = LifecycleKey.RunnerIdentity(profile, runnerId);
        var runner = await ReadRecordAsync(key, cancellationToken).ConfigureAwait(false);
        var association = new ExecutorRunnerBinding(binding.AllocationId, binding.Incarnation);
        if (executor.State.RunnerId is long existing)
        {
            if (existing != runnerId || runner is null || ParseExecutorRecord<ExecutorRunnerBinding>(runner) != association)
            {
                return false;
            }
            actions.Add(RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Runner(profile, binding.AllocationId), executor.Revision)));
            actions.Add(RecordRevisionCondition(new LifecycleRecordCondition(key, runner.Revision)));
            return true;
        }
        if (runner is not null)
        {
            return false;
        }

        var inventory = await ReadExecutorInventoryHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        if (inventory is null)
        {
            return false;
        }
        actions.Add(ExecutorWrite(key, association, null, transition, profile.Deployment));
        int change = AddExecutorActions(actions, executor, executor.State with { RunnerId = runnerId }, transition);
        actions.Add(WriteExecutorInventoryHead(profile, inventory, change, transition));
        return true;
    }

    public async Task<JitEc2BootstrapStatus?> ReadEc2BootstrapStatusAsync(JitHandoffSnapshot handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        if (handoff.State != JitHandoffState.Offered || handoff.RunnerId is not long runnerId)
        {
            return null;
        }
        var identity = handoff.Identity;
        BootstrapItems.RequirePrincipal(identity.AwsUserId, identity.ResourceArn, ec2Only: true);
        var key = LifecycleKey.Status(identity.AwsUserId);
        var row = await ReadItemAsync(key, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        try
        {
            BootstrapItems.RequireKey(key, row);
            var status = BootstrapItems.Deserialize<JitEc2BootstrapStatus>(BootstrapItems.Text(row, "payload"));
            DynamoDbEc2BootstrapStatusClient.ValidateStatus(status);
            return status.RunnerId == runnerId && status.OfferId == handoff.OfferId &&
                BootstrapItems.Text(row, "helloSha256") == handoff.HelloSha256 &&
                BootstrapItems.Text(row, "awsUserId") == identity.AwsUserId &&
                BootstrapItems.Text(row, "resourceArn") == identity.ResourceArn &&
                BootstrapItems.Text(row, "offerId") == status.OfferId &&
                BootstrapItems.Integer(row, "runnerId") == status.RunnerId &&
                BootstrapItems.Integer(row, "sequence") == status.Sequence &&
                BootstrapItems.Integer(row, "observedAt") == status.ObservedAt.ToUnixTimeMilliseconds() &&
                row.TryGetValue("exited", out var exited) && exited.BOOL == status.Exited &&
                status.ObservedAt <= _timeProvider.GetUtcNow() ? status : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw InvalidRecord();
        }
    }
}
