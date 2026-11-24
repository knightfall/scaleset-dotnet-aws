using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    private const int CatalogActionsPerPass = 64;
    private const int CatalogBatchByteBudget = 2_000_000;
    private const int CatalogItemByteBudget = 380_000;

    public async Task<CatalogRolloutChange> AdvanceCatalogRolloutAsync(
        DeploymentLeaseFence lease, RuntimeConfiguration configuration, long generation, CancellationToken cancellationToken)
    {
        RequireCatalogRequest(lease, configuration, generation);
        var existing = await ReadCatalogRolloutAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
        var head = existing ?? new CatalogRolloutHead(lease.Deployment, 0, 0, null, 0, null);
        if (generation < head.AppliedGeneration ||
            (generation == head.AppliedGeneration && head.AppliedDigest != configuration.Sha256))
        {
            return new CatalogRolloutChange(CatalogRolloutStatus.StaleGeneration, head);
        }
        if (head.Pending is { } pending && (pending.Generation != generation || pending.Digest != configuration.Sha256 || pending.Aborting))
        {
            return new CatalogRolloutChange(CatalogRolloutStatus.PendingConflict, head);
        }
        if (lease.Epoch < head.AppliedEpoch || (generation > head.AppliedGeneration && lease.Epoch == head.AppliedEpoch))
        {
            return new CatalogRolloutChange(CatalogRolloutStatus.LeaseRejected, head);
        }
        if (generation == head.AppliedGeneration)
        {
            if (lease.Epoch == head.AppliedEpoch)
            {
                var observed = await ReadLeaseAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
                return new CatalogRolloutChange(
                    observed is { Mode: ControllerLeaseMode.Active } && observed.Fence == lease &&
                    observed.ValidUntil > ConservativeLatest(DateTimeOffset.MinValue)
                        ? CatalogRolloutStatus.Applied : CatalogRolloutStatus.LeaseRejected, head);
            }

            var rebound = head with { AppliedEpoch = lease.Epoch, Revision = checked(head.Revision + 1) };
            return await CommitCatalogBatchAsync(lease, head, rebound, [], CatalogRolloutStatus.Applied, cancellationToken).ConfigureAwait(false);
        }

        var rows = await ReadCatalogRowsAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
        var plan = await BuildCatalogPlanAsync(lease.Deployment, head, configuration, rows, cancellationToken).ConfigureAwait(false);
        if (plan.Status is { } rejected)
        {
            return new CatalogRolloutChange(rejected, head);
        }

        var actions = plan.Actions;
        string planDigest = CatalogDigest(actions);
        if (head.Pending is null)
        {
            if (actions.Count == 0)
            {
                var empty = head with
                {
                    AppliedGeneration = generation,
                    AppliedDigest = configuration.Sha256,
                    AppliedEpoch = lease.Epoch,
                    Revision = checked(head.Revision + 1),
                };
                return await CommitCatalogBatchAsync(lease, existing, empty, [], CatalogRolloutStatus.Applied, cancellationToken)
                    .ConfigureAwait(false);
            }
            foreach (var action in actions)
            {
                rows.TryGetValue(action.ProfileId, out var row);
                if (EstimatedCatalogSize(action, row, head) > CatalogItemByteBudget)
                {
                    return new CatalogRolloutChange(CatalogRolloutStatus.BindingConflict, head);
                }
                if (action.Claim is { } claim && await ReadCatalogClaimAsync(claim, cancellationToken).ConfigureAwait(false) is { } owner &&
                    !ClaimMatches(owner, claim, new LifecycleProfile(lease.Deployment, action.ProfileId)))
                {
                    return new CatalogRolloutChange(CatalogRolloutStatus.BindingConflict, head);
                }
            }
            var proposed = new CatalogRolloutPending(generation, configuration.Sha256, planDigest, 0, actions.Count, false);
            var starting = head with { Revision = checked(head.Revision + 1), Pending = proposed };
            return await CommitCatalogBatchAsync(lease, existing, starting, [], CatalogRolloutStatus.Staging, cancellationToken)
                .ConfigureAwait(false);
        }
        if (head.Pending.PlanDigest != planDigest || head.Pending.ActionCount != actions.Count)
        {
            return new CatalogRolloutChange(CatalogRolloutStatus.PendingConflict, head);
        }
        if (head.Pending.NextAction == actions.Count)
        {
            var published = head with
            {
                AppliedGeneration = generation,
                AppliedDigest = configuration.Sha256,
                AppliedEpoch = lease.Epoch,
                Revision = checked(head.Revision + 1),
                Pending = null,
            };
            return await CommitCatalogBatchAsync(lease, head, published, [], CatalogRolloutStatus.Applied, cancellationToken)
                .ConfigureAwait(false);
        }

        return await StageCatalogBatchAsync(lease, head, actions, rows, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogRolloutChange> StageCatalogBatchAsync(
        DeploymentLeaseFence lease, CatalogRolloutHead head, List<CatalogAction> plan, Dictionary<string, CatalogRow> rows,
        CancellationToken cancellationToken)
    {
        var pending = head.Pending!;
        int next = pending.NextAction;
        Guid transition = Guid.NewGuid();
        int bytes = 0;
        List<TransactionAction> batch = [];
        while (next < plan.Count && batch.Count < CatalogActionsPerPass)
        {
            var step = plan[next];
            var profile = new LifecycleProfile(lease.Deployment, step.ProfileId);
            rows.TryGetValue(step.ProfileId, out var existing);
            int estimated = EstimatedCatalogSize(step, existing, head);
            if (estimated > CatalogItemByteBudget)
            {
                return new CatalogRolloutChange(CatalogRolloutStatus.BindingConflict, head);
            }
            if (bytes + estimated > CatalogBatchByteBudget)
            {
                break;
            }

            TransactionAction action;
            if (step.Claim is { } claim)
            {
                var observed = await ReadCatalogClaimAsync(claim, cancellationToken).ConfigureAwait(false);
                if (observed is not null && !ClaimMatches(observed, claim, profile))
                {
                    return new CatalogRolloutChange(CatalogRolloutStatus.BindingConflict, head);
                }
                action = observed is null ? CreateCatalogClaim(claim, profile, pending, transition) :
                    CatalogClaimCondition(claim, profile, observed.Revision);
            }
            else
            {
                var prior = existing?.Effective(head);
                var staged = new StagedCatalogProfile(pending.Generation, pending.Digest, step.Data!);
                action = WriteCatalogRow(profile, existing, prior is null ? null : CatalogProfileData.From(prior), staged, transition);
            }

            bytes += estimated;
            batch.Add(action);
            next++;
        }

        var nextHead = head with { Revision = checked(head.Revision + 1), Pending = pending with { NextAction = next } };
        return await CommitCatalogBatchAsync(lease, head, nextHead, batch, CatalogRolloutStatus.Staging, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CatalogPlan> BuildCatalogPlanAsync(
        string deployment, CatalogRolloutHead head, RuntimeConfiguration configuration, Dictionary<string, CatalogRow> rows,
        CancellationToken cancellationToken)
    {
        var desired = configuration.Profiles.Where(profile => profile.State != RunnerProfileState.Disabled)
            .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        foreach (var row in rows.Values)
        {
            if (row.Effective(head) is { State: not CatalogProfileState.Tombstoned } catalog && !desired.ContainsKey(catalog.Profile.ProfileId))
            {
                return new CatalogPlan(CatalogRolloutStatus.RetirementRequired, []);
            }
            if (row.Effective(head) is null && !desired.ContainsKey(row.Profile.ProfileId) &&
                await ReadSessionAsync(row.Profile, cancellationToken).ConfigureAwait(false) is { Phase: not SessionPhase.Retired })
            {
                return new CatalogPlan(CatalogRolloutStatus.RetirementRequired, []);
            }
        }

        List<CatalogAction> actions = [];
        var claims = new HashSet<CatalogClaim>();
        foreach (var requested in desired.Values.OrderBy(profile => profile.Id, StringComparer.Ordinal))
        {
            rows.TryGetValue(requested.Id, out var row);
            var effective = row?.Effective(head);
            var binding = ConfigurationBinding(configuration, requested);
            var state = requested.State == RunnerProfileState.Active ? CatalogProfileState.Active : CatalogProfileState.Draining;
            DateTimeOffset? heldAt = null;
            if (effective is not null)
            {
                if (effective.State == CatalogProfileState.Tombstoned ||
                    (effective.State == CatalogProfileState.ClosureHeld && state == CatalogProfileState.Active) ||
                    (binding.ScaleSetId is { } pin && effective.Binding.ScaleSetId is { } exact && exact != pin))
                {
                    return new CatalogPlan(CatalogRolloutStatus.BindingConflict, []);
                }
                binding = binding with
                {
                    RunnerGroupId = effective.Binding.RunnerGroupId,
                    ScaleSetId = effective.Binding.ScaleSetId ?? binding.ScaleSetId,
                };
                if (!SameBinding(effective.Binding with { ScaleSetId = binding.ScaleSetId }, binding))
                {
                    return new CatalogPlan(CatalogRolloutStatus.BindingConflict, []);
                }
                if (effective.State == CatalogProfileState.ClosureHeld)
                {
                    state = CatalogProfileState.ClosureHeld;
                    heldAt = effective.ClosureHeldAt;
                }
            }
            else
            {
                var profile = new LifecycleProfile(deployment, requested.Id);
                if (await ReadSessionAsync(profile, cancellationToken).ConfigureAwait(false) is { } session)
                {
                    if (session.Binding.ScopeIdentity != binding.ScopeIdentity ||
                        (binding.ScaleSetId is { } id && id != session.Binding.ScaleSetId))
                    {
                        return new CatalogPlan(CatalogRolloutStatus.BindingConflict, []);
                    }
                    binding = binding with { ScaleSetId = session.Binding.ScaleSetId };
                }
            }

            foreach (var claim in CatalogClaims(binding))
            {
                if (!claims.Add(claim))
                {
                    return new CatalogPlan(CatalogRolloutStatus.BindingConflict, []);
                }
                actions.Add(new CatalogAction(requested.Id, null, claim));
            }
            actions.Add(new CatalogAction(requested.Id, new CatalogProfileData(binding, state, heldAt), null));
        }
        return new CatalogPlan(null, actions);
    }

    private async Task<CatalogRolloutChange> CommitCatalogBatchAsync(
        DeploymentLeaseFence lease,
        CatalogRolloutHead? expected,
        CatalogRolloutHead replacement,
        List<TransactionAction> batch,
        CatalogRolloutStatus result,
        CancellationToken cancellationToken)
    {
        batch.Insert(0, WriteCatalogHead(lease, expected, replacement));
        var outcome = await WriteAsync(batch, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (outcome == LifecycleWriteOutcome.Committed)
        {
            return new CatalogRolloutChange(result, replacement);
        }

        var observed = await ReadCatalogRolloutAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
        if (observed == replacement)
        {
            return new CatalogRolloutChange(result, replacement);
        }
        if (outcome == LifecycleWriteOutcome.Indeterminate)
        {
            return new CatalogRolloutChange(CatalogRolloutStatus.Indeterminate, observed);
        }
        var currentLease = await ReadLeaseAsync(lease.Deployment, cancellationToken).ConfigureAwait(false);
        return new CatalogRolloutChange(
            currentLease is { Mode: ControllerLeaseMode.Active } && currentLease.Fence == lease &&
            currentLease.ValidUntil > ConservativeLatest(DateTimeOffset.MinValue)
                ? CatalogRolloutStatus.PendingConflict : CatalogRolloutStatus.LeaseRejected, observed);
    }

    private static int EstimatedCatalogSize(CatalogAction step, CatalogRow? row, CatalogRolloutHead head)
    {
        string serialized = JsonSerializer.Serialize(step);
        int priorSize = 0;
        if (row is not null && step.Claim is null)
        {
            priorSize = Encoding.UTF8.GetByteCount(row.Payload) + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(row.Effective(head)));
        }
        return checked(Encoding.UTF8.GetByteCount(serialized) + priorSize + 1536);
    }

    private static void RequireCatalogRequest(DeploymentLeaseFence lease, RuntimeConfiguration configuration, long generation)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (configuration.Mode != ControllerMode.Active ||
            lease.Deployment != ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration) || !IsCatalogDigest(configuration.Sha256))
        {
            throw new ArgumentException("Use the validated active configuration for the exact lease deployment.", nameof(configuration));
        }
    }

    private static IEnumerable<CatalogClaim> CatalogClaims(CatalogBinding binding)
    {
        yield return new CatalogClaim("set-name", binding.ScopeIdentity, binding.RunnerGroup, binding.ScaleSet);
        if (binding.ScaleSetId is long id)
        {
            yield return CatalogSetClaim(binding.ScopeIdentity, id);
        }
        foreach (string label in binding.Labels)
        {
            yield return new CatalogClaim("label", binding.ScopeIdentity, label, null);
        }
    }

    private static CatalogClaim CatalogSetClaim(string scope, long scaleSetId) =>
        new("set-id", scope, scaleSetId.ToString(CultureInfo.InvariantCulture), null);

    private sealed record CatalogClaim(string Kind, string Scope, string Primary, string? Secondary);

    private sealed record CatalogAction(string ProfileId, CatalogProfileData? Data, CatalogClaim? Claim);

    private sealed record CatalogPlan(CatalogRolloutStatus? Status, List<CatalogAction> Actions);
}
