using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    /// <summary>Reverses one bounded batch of a rollout that never published.</summary>
    public async Task<CatalogRolloutChange> AbortCatalogRolloutAsync(
        DeploymentLeaseFence lease, RuntimeConfiguration configuration, long generation, CancellationToken cancellationToken)
    {
        RequireCatalogRequest(lease, configuration, generation);
        var head = await ReadCatalogRolloutAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
        if (head is null || head.Pending is not { } pending || pending.Generation != generation || pending.Digest != configuration.Sha256)
        {
            return new CatalogRolloutChange(CatalogRolloutStatus.PendingConflict, head);
        }
        if (pending.NextAction == 0)
        {
            var cleared = head with { Revision = checked(head.Revision + 1), Pending = null };
            return await CommitCatalogBatchAsync(lease, head, cleared, [], CatalogRolloutStatus.Aborted, cancellationToken)
                .ConfigureAwait(false);
        }
        if (!pending.Aborting)
        {
            var canceling = head with { Revision = checked(head.Revision + 1), Pending = pending with { Aborting = true } };
            return await CommitCatalogBatchAsync(lease, head, canceling, [], CatalogRolloutStatus.Staging, cancellationToken)
                .ConfigureAwait(false);
        }

        var rows = await ReadCatalogRowsAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
        var observedPlan = await BuildCatalogPlanAsync(lease.Deployment, head, configuration, rows, cancellationToken).ConfigureAwait(false);
        var plan = observedPlan.Actions;
        if (observedPlan.Status is not null || plan.Count != pending.ActionCount || CatalogDigest(plan) != pending.PlanDigest)
        {
            return new CatalogRolloutChange(CatalogRolloutStatus.PendingConflict, head);
        }

        int next = pending.NextAction;
        int reversed = 0;
        int bytes = 0;
        Guid transition = Guid.NewGuid();
        List<TransactionAction> reversedActions = [];
        while (next > 0 && reversed < CatalogActionsPerPass)
        {
            var step = plan[next - 1];
            var profile = new LifecycleProfile(lease.Deployment, step.ProfileId);
            rows.TryGetValue(step.ProfileId, out var row);
            int estimated = EstimatedCatalogSize(step, row, head);
            if (bytes + estimated > CatalogBatchByteBudget)
            {
                break;
            }
            if (step.Claim is { } claim)
            {
                var stored = await ReadCatalogClaimAsync(claim, cancellationToken).ConfigureAwait(false);
                if (stored is null || !ClaimMatches(stored, claim, profile))
                {
                    return new CatalogRolloutChange(CatalogRolloutStatus.PendingConflict, head);
                }
                if (stored.PendingGeneration == pending.Generation && stored.PendingPlanDigest == pending.PlanDigest)
                {
                    reversedActions.Add(DeleteProvisionalCatalogClaim(claim, stored, pending));
                }
            }
            else if (row?.Candidate is { } staged && staged.Generation == pending.Generation && staged.Digest == pending.Digest)
            {
                reversedActions.Add(WriteCatalogRow(profile, row, row.Published, null, transition));
            }
            else
            {
                return new CatalogRolloutChange(CatalogRolloutStatus.PendingConflict, head);
            }

            bytes += estimated;
            next--;
            reversed++;
        }
        var progressed = head with { Revision = checked(head.Revision + 1), Pending = pending with { NextAction = next } };
        return await CommitCatalogBatchAsync(lease, head, progressed, reversedActions, CatalogRolloutStatus.Staging, cancellationToken)
            .ConfigureAwait(false);
    }

    private TransactionAction DeleteProvisionalCatalogClaim(CatalogClaim claim, StoredCatalogClaim stored, CatalogRolloutPending pending)
    {
        var auth = CatalogClaimCondition(claim, stored.Profile, stored.Revision);
        var check = auth.Write.ConditionCheck;
        check.ExpressionAttributeNames.Add("#generation", "catalogClaimGeneration");
        check.ExpressionAttributeNames.Add("#plan", "catalogClaimPlan");
        check.ExpressionAttributeValues.Add(":generation", NumberValue(pending.Generation));
        check.ExpressionAttributeValues.Add(":plan", StringValue(pending.PlanDigest));
        return new TransactionAction(auth.Key, new TransactWriteItem
        {
            Delete = new Delete
            {
                TableName = check.TableName,
                Key = check.Key,
                ConditionExpression = check.ConditionExpression + " AND #generation = :generation AND #plan = :plan",
                ExpressionAttributeNames = check.ExpressionAttributeNames,
                ExpressionAttributeValues = check.ExpressionAttributeValues,
            },
        });
    }
}
