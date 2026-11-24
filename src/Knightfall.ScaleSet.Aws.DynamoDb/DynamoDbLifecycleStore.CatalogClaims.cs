using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    public async Task<LifecycleWriteOutcome> BindCatalogScaleSetAsync(
        DeploymentLeaseFence lease, CatalogProfile expected, long runnerGroupId, long scaleSetId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        RequireProfileLease(expected.Profile, lease);
        ArgumentOutOfRangeException.ThrowIfNegative(runnerGroupId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);
        var current = await ReadCatalogProfileAsync(expected.Profile, cancellationToken).ConfigureAwait(false);
        if (!SameProfile(current, expected) || expected.State is CatalogProfileState.ClosureHeld or CatalogProfileState.Tombstoned ||
            (expected.Binding.RunnerGroupId is { } priorGroup && priorGroup != runnerGroupId) ||
            (expected.Binding.ScaleSetId is { } priorSet && priorSet != scaleSetId))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var claim = CatalogSetClaim(expected.Binding.ScopeIdentity, scaleSetId);
        var existingClaim = await ReadCatalogClaimAsync(claim, cancellationToken).ConfigureAwait(false);
        if (existingClaim is not null && !ClaimMatches(existingClaim, claim, expected.Profile))
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var row = await ReadExactCatalogRowAsync(expected, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return LifecycleWriteOutcome.Conflict;
        }

        var next = CatalogProfileData.From(expected) with
        {
            Binding = expected.Binding with { RunnerGroupId = runnerGroupId, ScaleSetId = scaleSetId },
        };
        Guid transition = Guid.NewGuid();
        var leaseCondition = LeaseCondition(lease);
        RequireAppliedCatalog(leaseCondition, expected);
        var profileWrite = existingClaim is not null && SameBinding(next.Binding, expected.Binding)
            ? CatalogProfileCondition(expected)
            : WriteCatalogRow(expected.Profile, row, next, null, transition);
        var claimWrite = existingClaim is null ? CreateCatalogClaim(claim, expected.Profile, null, transition) :
            CatalogClaimCondition(claim, expected.Profile, existingClaim.Revision);
        return await WriteAsync([leaseCondition, profileWrite, claimWrite], Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogRow?> ReadExactCatalogRowAsync(CatalogProfile expected, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.Profile(expected.Profile), cancellationToken).ConfigureAwait(false);
        var row = item is null ? null : DecodeCatalogRow(expected.Profile, item);
        return row is not null && row.Revision == expected.Revision && row.TransitionId == expected.TransitionId ? row : null;
    }

    private async Task<StoredCatalogClaim?> ReadCatalogClaimAsync(CatalogClaim claim, CancellationToken cancellationToken)
    {
        var key = CatalogClaimKey(claim);
        var item = await ReadItemAsync(key, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        CheckNoExpiration(item);
        var identity = CatalogDeserialize<CatalogClaim>(Text(item, "catalogClaimIdentity"));
        if (CatalogClaimKey(identity) != key)
        {
            throw InvalidRecord();
        }
        var profile = new LifecycleProfile(Text(item, "deployment"), Text(item, "catalogClaimProfile"));
        long? generation = item.ContainsKey("catalogClaimGeneration") ? Number(item, "catalogClaimGeneration") : null;
        string? plan = item.ContainsKey("catalogClaimPlan") ? Text(item, "catalogClaimPlan") : null;
        if (generation is <= 0 || generation.HasValue != IsCatalogDigest(plan) ||
            (generation is null && plan is not null))
        {
            throw InvalidRecord();
        }
        return new StoredCatalogClaim(identity, profile, Revision(item), generation, plan);
    }

    private TransactionAction CreateCatalogClaim(
        CatalogClaim claim, LifecycleProfile profile, CatalogRolloutPending? pending, Guid transition)
    {
        var key = CatalogClaimKey(claim);
        var item = Record(key, profile.Deployment, 1, transition);
        item.Add("catalogClaimIdentity", StringValue(JsonSerializer.Serialize(claim)));
        item.Add("catalogClaimProfile", StringValue(profile.ProfileId));
        item.Add("payload", StringValue("{}"));
        if (pending is not null)
        {
            item.Add("catalogClaimGeneration", NumberValue(pending.Generation));
            item.Add("catalogClaimPlan", StringValue(pending.PlanDigest));
        }
        return CompareAndPut(key, item, null, profile.Deployment);
    }

    private TransactionAction CatalogClaimCondition(CatalogClaim claim, LifecycleProfile owner, long revision)
    {
        var key = CatalogClaimKey(claim);
        return new TransactionAction(key, new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                Key = Key(key),
                TableName = _tableName,
                ConditionExpression = "#revision = :wantedRevision AND #deployment = :deployment AND #profile = :profile",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["#revision"] = "revision",
                    ["#deployment"] = "deployment",
                    ["#profile"] = "catalogClaimProfile",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":wantedRevision"] = NumberValue(revision),
                    [":deployment"] = StringValue(owner.Deployment),
                    [":profile"] = StringValue(owner.ProfileId),
                },
            },
        });
    }

    private static LifecycleKey CatalogClaimKey(CatalogClaim claim) =>
        LifecycleKey.Claim(CatalogClaimPrefix + claim.Kind + "#" + CatalogDigest(claim));

    private static bool ClaimMatches(StoredCatalogClaim row, CatalogClaim claim, LifecycleProfile profile) =>
        row.Claim == claim && row.Profile == profile;

    private sealed record StoredCatalogClaim(
        CatalogClaim Claim, LifecycleProfile Profile, long Revision, long? PendingGeneration, string? PendingPlanDigest);
}
