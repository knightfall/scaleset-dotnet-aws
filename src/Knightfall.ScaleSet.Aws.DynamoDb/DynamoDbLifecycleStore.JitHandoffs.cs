using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    public async Task<JitHandoffSnapshot?> ReadJitHandoffAsync(
        LifecycleProfile profile, Guid allocationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var item = await ReadItemAsync(LifecycleKey.Handoff(profile, allocationId), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        try
        {
            CheckNoExpiration(item);
            var payload = BootstrapItems.Deserialize<JitHandoffPayload>(Text(item, "payload"));
            if (payload.Identity is null || payload.Hello is null || payload.Identity.AllocationId != allocationId ||
                !IsHandoffProfile(profile, payload.Identity) || Text(item, "deployment") != profile.Deployment ||
                !ValidHandoffPayload(payload))
            {
                throw InvalidRecord();
            }
            return new JitHandoffSnapshot(payload.Identity, payload.Hello, payload.HelloSha256, payload.RunnerName, payload.State,
                payload.RunnerId, payload.OfferSha256, payload.JitAttemptId, Revision(item), GuidValue(item, "transitionId"));
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        {
            throw InvalidRecord();
        }
    }

    public async Task<JitHandoffChange> StageJitHandoffAsync(
        ControllerLease lease, CatalogProfile catalog, ExecutorSnapshot executor, JitHandoffIdentity identity,
        string runnerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(identity);
        var profile = catalog.Profile;
        RequireProfileLease(profile, lease.Fence);
        BootstrapItems.RequirePrincipal(identity.AwsUserId, identity.ResourceArn);
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.LeaseRejected);
        }
        if (!MatchesJitCatalog(catalog, identity) || !MatchesJitExecutor(executor, identity) || !MatchesJitProvider(catalog, executor) ||
            executor.State is not { RunnerId: null, HasConflict: false, Resource.State: ExecutorResourceState.Running } ||
            runnerName != "ss-" + JitOfferBinding.GetOfferId(identity)[..48])
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }

        var hello = await ReadJitHelloAsync(identity, cancellationToken).ConfigureAwait(false);
        if (hello is null)
        {
            return new JitHandoffChange(LifecycleWriteOutcome.Conflict);
        }
        string helloDigest = BootstrapItems.HelloDigest(hello);
        var prior = await ReadJitHandoffAsync(profile, identity.AllocationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            return prior.State == JitHandoffState.Staged && prior.Identity == identity && prior.HelloSha256 == helloDigest &&
                prior.RunnerName == runnerName
                ? new JitHandoffChange(LifecycleWriteOutcome.Committed, prior)
                : new JitHandoffChange(LifecycleWriteOutcome.Conflict, prior);
        }

        var head = await ReadJitHandoffHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        var transition = Guid.NewGuid();
        var staged = new JitHandoffSnapshot(identity, hello, helloDigest, runnerName, JitHandoffState.Staged, null, null, null, 1, transition);
        var ownership = LeaseCondition(lease.Fence);
        RequireAppliedCatalog(ownership, catalog);
        var actions = new List<TransactionAction>
        {
            ownership,
            CatalogProfileCondition(catalog),
            RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Runner(profile, executor.Binding.AllocationId), executor.Revision)),
            ExactJitHelloCondition(staged),
            CompareAndPut(LifecycleKey.Handoff(profile, identity.AllocationId), HandoffItem(profile, staged), null, profile.Deployment),
            WriteJitHandoffHead(profile, head, 1, transition),
        };
        return await CommitJitHandoffChangeAsync(profile, staged, actions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfileHandoffObservation> ReadProfileHandoffObservationAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var head = await ReadJitHandoffHeadAsync(profile, cancellationToken).ConfigureAwait(false);
        return new ProfileHandoffObservation(_timeProvider.GetUtcNow(), IsComplete: true, head?.Outstanding ?? 0,
            [new LifecycleRecordCondition(LifecycleKey.HandoffHead(profile), head?.Revision)]);
    }

    private async Task<JitHello?> ReadJitHelloAsync(JitHandoffIdentity identity, CancellationToken cancellationToken)
    {
        var key = LifecycleKey.Hello(identity.AwsUserId);
        var item = await ReadItemAsync(key, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        try
        {
            return BootstrapItems.DecodeHello(key, item);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        {
            throw InvalidRecord();
        }
    }

    private IReadOnlyList<TransactionAction> BuildHandoffBeginConditions(JitHandoffSnapshot handoff)
    {
        if (handoff.State != JitHandoffState.Staged)
        {
            throw new InvalidOperationException("JIT generation requires a staged handoff.");
        }
        return [ExactJitHandoffCondition(handoff), ExactJitHelloCondition(handoff)];
    }

    internal static bool MatchesJitRequest(EffectEnvelope envelope, JitHandoffSnapshot handoff) =>
        envelope.IntentId == handoff.IntentId && IsHandoffProfile(envelope.Profile, handoff.Identity) &&
        envelope.Request is GitHubJitGenerateEffectRequest request &&
        request.ScopeIdentity == handoff.Identity.ScopeIdentity && request.ScaleSetId == handoff.Identity.ScaleSetId &&
        request.AllocationId == handoff.Identity.AllocationId && request.ResourceArn == handoff.Identity.ResourceArn &&
        request.Incarnation == handoff.Identity.Incarnation && request.AwsUserId == handoff.Identity.AwsUserId &&
        request.HelloSha256 == handoff.HelloSha256 && request.OfferId == handoff.OfferId && request.RunnerName == handoff.RunnerName;

    private TransactionAction ExactJitHelloCondition(JitHandoffSnapshot handoff)
    {
        var key = LifecycleKey.Hello(handoff.Identity.AwsUserId);
        return ExactJitCondition(key, "awsUserId = :principal AND helloSha256 = :hello AND protocolVersion = :version AND payload = :payload " +
            "AND attribute_not_exists(expiresAt)", new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":principal"] = StringValue(handoff.Identity.AwsUserId),
                [":hello"] = StringValue(handoff.HelloSha256),
                [":version"] = NumberValue(handoff.Hello.ProtocolVersion),
                [":payload"] = StringValue(BootstrapItems.Serialize(handoff.Hello)),
            });
    }

    private TransactionAction ExactJitHandoffCondition(JitHandoffSnapshot handoff)
    {
        var profile = new LifecycleProfile(handoff.Identity.Deployment, handoff.Identity.ProfileId);
        var key = LifecycleKey.Handoff(profile, handoff.Identity.AllocationId);
        return ExactJitCondition(key, "revision = :revision AND transitionId = :transition AND payload = :payload",
            HandoffWitness(handoff));
    }

    private TransactionAction ExactJitCondition(LifecycleKey key, string expression, Dictionary<string, AttributeValue> values) =>
        new(key, new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = _tableName,
                Key = Key(key),
                ConditionExpression = expression,
                ExpressionAttributeValues = values,
            },
        });

    private static Dictionary<string, AttributeValue> HandoffItem(LifecycleProfile profile, JitHandoffSnapshot snapshot)
    {
        var item = Record(LifecycleKey.Handoff(profile, snapshot.Identity.AllocationId), profile.Deployment,
            snapshot.Revision, snapshot.TransitionId);
        item.Add("payload", StringValue(BootstrapItems.Serialize(JitHandoffPayload.From(snapshot))));
        return item;
    }

    private TransactionAction WriteExactJitHandoff(LifecycleProfile profile, JitHandoffSnapshot prior, JitHandoffSnapshot next)
    {
        var key = LifecycleKey.Handoff(profile, prior.Identity.AllocationId);
        return new TransactionAction(key, new TransactWriteItem
        {
            Put = new Put
            {
                TableName = _tableName,
                Item = HandoffItem(profile, next),
                ConditionExpression = "revision = :revision AND transitionId = :transition AND payload = :payload",
                ExpressionAttributeValues = HandoffWitness(prior),
            },
        });
    }

    private static Dictionary<string, AttributeValue> HandoffWitness(JitHandoffSnapshot handoff) =>
        new(StringComparer.Ordinal)
        {
            [":revision"] = NumberValue(handoff.Revision),
            [":transition"] = StringValue(handoff.TransitionId.ToString("D")),
            [":payload"] = StringValue(BootstrapItems.Serialize(JitHandoffPayload.From(handoff))),
        };

    private async Task<JitHandoffHead?> ReadJitHandoffHeadAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.HandoffHead(profile), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        CheckNoExpiration(item);
        long outstanding = Number(item, "outstanding");
        return Text(item, "deployment") == profile.Deployment && outstanding is >= 0 and <= int.MaxValue
            ? new JitHandoffHead(Revision(item), (int)outstanding) : throw InvalidRecord();
    }

    private TransactionAction WriteJitHandoffHead(LifecycleProfile profile, JitHandoffHead? head, int delta, Guid transition)
    {
        var key = LifecycleKey.HandoffHead(profile);
        int count = checked((head?.Outstanding ?? 0) + delta);
        if (count < 0)
        {
            throw InvalidRecord();
        }
        var item = Record(key, profile.Deployment, NextRevision(head?.Revision), transition);
        item.Add("outstanding", NumberValue(count));
        return CompareAndPut(key, item, head?.Revision, profile.Deployment);
    }

    private async Task<JitHandoffChange> CommitJitHandoffChangeAsync(LifecycleProfile profile, JitHandoffSnapshot next,
        IReadOnlyList<TransactionAction> actions, CancellationToken cancellationToken)
    {
        var outcome = await WriteAsync(actions, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (outcome == LifecycleWriteOutcome.Indeterminate)
        {
            var stored = await ReadJitHandoffAsync(profile, next.Identity.AllocationId, cancellationToken).ConfigureAwait(false);
            if (stored is not null && MatchesHandoff(next, stored))
            {
                return new JitHandoffChange(LifecycleWriteOutcome.Committed, stored);
            }
        }
        return new JitHandoffChange(outcome, outcome == LifecycleWriteOutcome.Committed ? next : null);
    }

    private static bool MatchesHandoff(JitHandoffSnapshot expected, JitHandoffSnapshot? actual) =>
        actual is not null && expected.Revision == actual.Revision && expected.TransitionId == actual.TransitionId &&
        BootstrapItems.Serialize(JitHandoffPayload.From(expected)) == BootstrapItems.Serialize(JitHandoffPayload.From(actual));

    private static bool MatchesJitCatalog(CatalogProfile catalog, JitHandoffIdentity identity) =>
        IsHandoffProfile(catalog.Profile, identity) && catalog.State is CatalogProfileState.Active or CatalogProfileState.Draining &&
        catalog.Binding.ScopeIdentity == identity.ScopeIdentity && catalog.Binding.ScaleSetId == identity.ScaleSetId &&
        catalog.Binding.BootstrapProtocol == "v1";

    private static bool MatchesJitExecutor(ExecutorSnapshot executor, JitHandoffIdentity identity) =>
        IsHandoffProfile(executor.Binding.Profile, identity) && executor.Binding.AllocationId == identity.AllocationId.ToString("D") &&
        executor.Binding.Incarnation == identity.Incarnation && executor.Binding.ResourceArn == identity.ResourceArn;

    private static bool MatchesJitProvider(CatalogProfile catalog, ExecutorSnapshot executor) => executor.Binding.Provider switch
    {
        Ec2ExecutorIdentity ec2 => catalog.Binding.ProvisionerType == RunnerProvisionerType.Ec2 &&
            catalog.Binding.ProviderDefinition == ec2.LaunchTemplateId,
        FargateExecutorIdentity fargate => catalog.Binding.ProvisionerType == RunnerProvisionerType.Fargate &&
            catalog.Binding.ProviderDefinition == fargate.TaskDefinitionArn,
        _ => false,
    };

    private static bool IsHandoffProfile(LifecycleProfile profile, JitHandoffIdentity identity) =>
        profile.Deployment == identity.Deployment && profile.ProfileId == identity.ProfileId;

    private static bool ValidHandoffPayload(JitHandoffPayload payload)
    {
        BootstrapItems.RequirePrincipal(payload.Identity.AwsUserId, payload.Identity.ResourceArn);
        if (payload.HelloSha256 != BootstrapItems.HelloDigest(payload.Hello) ||
            payload.RunnerName != "ss-" + JitOfferBinding.GetOfferId(payload.Identity)[..48] ||
            payload.RunnerId is <= 0 || payload.JitAttemptId == Guid.Empty ||
            (payload.OfferSha256 is not null && !JitHandoffSnapshot.IsLowercaseDigest(payload.OfferSha256)))
        {
            return false;
        }
        return payload.State switch
        {
            JitHandoffState.Staged => payload is { RunnerId: null, OfferSha256: null, JitAttemptId: null },
            JitHandoffState.Offered or JitHandoffState.Revoked =>
                payload is { RunnerId: not null, OfferSha256: not null, JitAttemptId: not null },
            JitHandoffState.RegisteredWithoutOffer => payload is { RunnerId: not null, OfferSha256: null, JitAttemptId: not null },
            JitHandoffState.ResourceTerminated => (payload.RunnerId is null) == (payload.JitAttemptId is null) &&
                (payload.OfferSha256 is null || payload.RunnerId is not null),
            _ => false,
        };
    }

    private sealed record JitHandoffHead(long Revision, int Outstanding);

    private sealed record JitHandoffPayload(JitHandoffIdentity Identity, JitHello Hello, string HelloSha256, string RunnerName,
        JitHandoffState State, long? RunnerId, string? OfferSha256, Guid? JitAttemptId)
    {
        internal static JitHandoffPayload From(JitHandoffSnapshot snapshot) =>
            new(snapshot.Identity, snapshot.Hello, snapshot.HelloSha256, snapshot.RunnerName, snapshot.State, snapshot.RunnerId,
                snapshot.OfferSha256, snapshot.JitAttemptId);
    }
}
