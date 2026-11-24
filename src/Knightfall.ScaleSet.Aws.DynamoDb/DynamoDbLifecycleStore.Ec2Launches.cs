using System.Collections.Immutable;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record Ec2LaunchLineageSnapshot(
    LifecycleProfile Profile, Guid AllocationId, int AttemptOrdinal, string IntentId, string EnvelopeSha256,
    ImmutableArray<string> TriedAvailabilityZones, ImmutableArray<Guid> ClientTokens, long Revision, Guid TransitionId);

public sealed record PendingEc2Launch(Guid AllocationId, int AttemptOrdinal, string IntentId, EffectState State);

public sealed record PendingEc2LaunchPage(ImmutableArray<PendingEc2Launch> Launches, long IndexRevision, ActiveEffectCursor? Continuation);

public sealed partial class DynamoDbLifecycleStore
{
    /// <summary>Reads one pinned active-index page of EC2 capacity reservations for placement planning.</summary>
    public async Task<PendingEc2LaunchPage?> ReadPendingEc2LaunchesAsync(
        LifecycleProfile profile, DeploymentLeaseFence lease, ActiveEffectCursor? continuation, CancellationToken cancellationToken)
    {
        RequireProfileLease(profile, lease);
        var head = await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false);
        var page = await ReadActiveOperationsAsync(profile, lease, continuation, cancellationToken).ConfigureAwait(false);
        if (head is not { State: EffectIndexState.Ready } || page is null)
        {
            return null;
        }
        var pending = ImmutableArray.CreateBuilder<PendingEc2Launch>();
        foreach (string intentId in page.IntentIds)
        {
            var stored = await ReadEffectAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
            if (stored is not { IsUnfinished: true })
            {
                return null;
            }
            var entry = EffectJournalEntry.Read(profile, stored);
            if (entry.Envelope.Request is not Ec2LaunchEffectRequest request)
            {
                continue;
            }
            if (request.OwnershipTags is not null && !MatchesEc2Lineage(
                await ReadEc2LaunchLineageAsync(profile, request.AllocationId, cancellationToken).ConfigureAwait(false), entry.Envelope))
            {
                return null;
            }
            pending.Add(new PendingEc2Launch(request.AllocationId, request.AttemptOrdinal, intentId, entry.State));
        }
        return await ReadEffectIndexAsync(profile, cancellationToken).ConfigureAwait(false) == head
            ? new PendingEc2LaunchPage(pending.ToImmutable(), head.Revision, page.Continuation) : null;
    }

    public async Task<Ec2LaunchLineageSnapshot?> ReadEc2LaunchLineageAsync(
        LifecycleProfile profile, Guid allocationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var key = LifecycleKey.Ec2LaunchLineage(profile, allocationId);
        var item = await ReadItemAsync(key, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        CheckNoExpiration(item);
        try
        {
            var state = JsonSerializer.Deserialize<Ec2LaunchLineageData>(Text(item, "payload"), ExecutorJson) ?? throw InvalidRecord();
            if (Text(item, "deployment") != profile.Deployment || state.AttemptOrdinal < 0 || state.AttemptOrdinal == int.MaxValue ||
                state.TriedAvailabilityZones.IsDefault || state.TriedAvailabilityZones.Length != state.AttemptOrdinal + 1 ||
                state.ClientTokens.IsDefault || state.ClientTokens.Length != state.TriedAvailabilityZones.Length ||
                state.TriedAvailabilityZones.Any(string.IsNullOrWhiteSpace) ||
                state.TriedAvailabilityZones.Distinct(StringComparer.Ordinal).Count() != state.TriedAvailabilityZones.Length ||
                state.ClientTokens.Contains(Guid.Empty) || state.ClientTokens.Distinct().Count() != state.ClientTokens.Length ||
                state.EnvelopeSha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigitLower) ||
                state.IntentId != EffectIntentIdentity.ForEc2Launch(allocationId, state.AttemptOrdinal))
            {
                throw InvalidRecord();
            }
            return new Ec2LaunchLineageSnapshot(profile, allocationId, state.AttemptOrdinal, state.IntentId, state.EnvelopeSha256,
                state.TriedAvailabilityZones, state.ClientTokens, Revision(item), GuidValue(item, "transitionId"));
        }
        catch (JsonException)
        {
            throw InvalidRecord();
        }
    }

    internal async Task<LifecycleWriteOutcome> PlanTrackedEc2LaunchAsync(ControllerLease lease, EffectEnvelope envelope,
        EffectJournalSnapshot? predecessor, Guid transitionId, CancellationToken cancellationToken)
    {
        var profile = envelope.Profile;
        RequireProfileLease(profile, lease.Fence);
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        if (envelope.Request is not Ec2LaunchEffectRequest { OwnershipTags: not null } request ||
            envelope.IntentId != EffectIntentIdentity.ForAllocation(request))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var catalog = await ReadCatalogProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        if (catalog is null || catalog.AppliedGeneration != envelope.Generation || catalog.Binding.ScaleSetId is not > 0 ||
            catalog.State is not (CatalogProfileState.Active or CatalogProfileState.Draining) ||
            catalog.Binding.ProvisionerType != RunnerProvisionerType.Ec2 || catalog.Binding.ProviderDefinition != request.LaunchTemplateId ||
            catalog.Binding.BootstrapProtocol != request.OwnershipTags.BootstrapProtocol)
        {
            return LifecycleWriteOutcome.Conflict;
        }
        var current = await ReadEc2LaunchLineageAsync(profile, request.AllocationId, cancellationToken).ConfigureAwait(false);
        if (current is not null && MatchesEc2Lineage(current, envelope))
        {
            var planned = await ReadEffectAsync(profile, envelope.IntentId, cancellationToken).ConfigureAwait(false);
            return planned?.TransitionId == transitionId ? LifecycleWriteOutcome.Committed : LifecycleWriteOutcome.Conflict;
        }
        if (!CanAppendEc2Launch(current, predecessor, request))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var actions = new List<TransactionAction>
        {
            RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Runner(profile, request.AllocationId.ToString("D")), null)),
            WriteEc2LaunchLineage(current, envelope, request, transitionId),
            CreateEc2ClientTokenClaim(envelope, request, transitionId),
        };
        if (predecessor is not null)
        {
            actions.Add(ExactPredecessorCondition(profile, predecessor));
        }
        var entry = EffectJournalEntry.Plan(envelope);
        return await WriteEffectAsync(profile, lease.Fence, transitionId,
            new EffectWrite(envelope.IntentId, null, entry.State, entry.WritePayload()), LeaseAccess.Active, cancellationToken,
            requiredCatalog: catalog, accompanying: actions, requestToken: Guid.NewGuid()).ConfigureAwait(false);
    }

    internal static bool MatchesEc2Lineage(Ec2LaunchLineageSnapshot? lineage, EffectEnvelope envelope) =>
        lineage is not null && envelope.Request is Ec2LaunchEffectRequest request && lineage.Profile == envelope.Profile &&
        lineage.AllocationId == request.AllocationId && lineage.AttemptOrdinal == request.AttemptOrdinal &&
        lineage.IntentId == envelope.IntentId && lineage.EnvelopeSha256 == envelope.Sha256 &&
        lineage.TriedAvailabilityZones[^1] == request.AvailabilityZone && lineage.ClientTokens[^1] == request.ClientToken;

    private static bool CanAppendEc2Launch(Ec2LaunchLineageSnapshot? current, EffectJournalSnapshot? predecessor,
        Ec2LaunchEffectRequest request)
    {
        if (current is null)
        {
            return request.AttemptOrdinal == 0 && predecessor is null;
        }
        return predecessor is { Entry.State: EffectState.Settled, Entry.Resolution: { Kind: EffectResolutionKind.Rejected, Ec2LaunchRejection: not null } } &&
            MatchesEc2Lineage(current, predecessor.Entry.Envelope) && current.AttemptOrdinal == request.AttemptOrdinal - 1 &&
            request.AttemptOrdinal > 0 && request.AttemptOrdinal < int.MaxValue &&
            !current.TriedAvailabilityZones.Contains(request.AvailabilityZone, StringComparer.Ordinal) &&
            !current.ClientTokens.Contains(request.ClientToken);
    }

    private TransactionAction WriteEc2LaunchLineage(Ec2LaunchLineageSnapshot? current, EffectEnvelope envelope, Ec2LaunchEffectRequest request,
        Guid transition)
    {
        var profile = envelope.Profile;
        var key = LifecycleKey.Ec2LaunchLineage(profile, request.AllocationId);
        var row = Record(key, profile.Deployment, NextRevision(current?.Revision), transition);
        ImmutableArray<string> zones = current?.TriedAvailabilityZones ?? [];
        ImmutableArray<Guid> tokens = current?.ClientTokens ?? [];
        var data = new Ec2LaunchLineageData(request.AttemptOrdinal, envelope.IntentId, envelope.Sha256,
            zones.Add(request.AvailabilityZone), tokens.Add(request.ClientToken));
        row.Add("payload", StringValue(JsonSerializer.Serialize(data, ExecutorJson)));
        return CompareAndPut(key, row, current?.Revision, profile.Deployment);
    }

    private TransactionAction ExactEc2LineageCondition(Ec2LaunchLineageSnapshot lineage) =>
        RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Ec2LaunchLineage(lineage.Profile, lineage.AllocationId), lineage.Revision));

    private const string Ec2TokenClaimPrefix = "CLAIM#ec2-launch-token#";

    private TransactionAction CreateEc2ClientTokenClaim(EffectEnvelope envelope, Ec2LaunchEffectRequest request, Guid transition)
    {
        string account = request.InstanceProfileArn.Split(':', 6)[4];
        var key = LifecycleKey.Claim($"ec2-launch-token#{account}:{request.Region}:{request.ClientToken:D}");
        var row = Record(key, envelope.Profile.Deployment, 1, transition);
        row.Add("payload", StringValue(envelope.Sha256));
        return CompareAndPut(key, row, null, envelope.Profile.Deployment);
    }

    private TransactionAction ExactPredecessorCondition(LifecycleProfile profile, EffectJournalSnapshot predecessor)
    {
        var key = LifecycleKey.Operation(profile, predecessor.Entry.Envelope.IntentId);
        return new TransactionAction(key, new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = _tableName,
                Key = Key(key),
                ConditionExpression = "revision = :revision AND transitionId = :transition AND effectState = :settled AND payload = :payload",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":revision"] = NumberValue(predecessor.Revision),
                    [":transition"] = StringValue(predecessor.TransitionId.ToString("D")),
                    [":settled"] = StringValue(EffectStateValue(EffectState.Settled)),
                    [":payload"] = StringValue(predecessor.Entry.WritePayload()),
                },
            },
        });
    }

    private sealed record Ec2LaunchLineageData(int AttemptOrdinal, string IntentId, string EnvelopeSha256,
        ImmutableArray<string> TriedAvailabilityZones, ImmutableArray<Guid> ClientTokens);
}
