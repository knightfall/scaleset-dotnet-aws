using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public enum RetirementPreflightStatus
{
    CatalogUnavailable = 0,
    BindingMismatch = 1,
    RetirementRequired = 2,
    Verified = 3,
}

public sealed record RetirementPreflightResult(
    RetirementPreflightStatus Status, IReadOnlyList<string> Profiles);

public sealed partial class DynamoDbLifecycleStore
{
    /// <summary>Verifies that every profile losing its deployed permissions has a durable tombstone.</summary>
    public async Task<RetirementPreflightResult> VerifyRetirementAsync(
        RuntimeConfiguration current, long currentGeneration, RuntimeConfiguration candidate, long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        if (currentGeneration <= 0 || generation <= currentGeneration || current.Aws != candidate.Aws ||
            current.EnvironmentName != candidate.EnvironmentName || current.TrackingTable != candidate.TrackingTable ||
            _tableName != current.TrackingTable.Name)
        {
            return new RetirementPreflightResult(RetirementPreflightStatus.BindingMismatch, []);
        }

        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(current);
        var head = await ReadCatalogRolloutAsync(deployment, cancellationToken).ConfigureAwait(false);
        if (head?.Pending is not null ||
            (current.Mode == ControllerMode.Active &&
            (head is null || head.AppliedGeneration != currentGeneration || head.AppliedDigest != current.Sha256)) ||
            (current.Mode == ControllerMode.DryRun && head?.AppliedGeneration >= currentGeneration))
        {
            return new RetirementPreflightResult(RetirementPreflightStatus.CatalogUnavailable, []);
        }
        var rows = await ReadCatalogRowsAsync(deployment, cancellationToken).ConfigureAwait(false);
        if (await ReadCatalogRolloutAsync(deployment, cancellationToken).ConfigureAwait(false) != head ||
            (head is null && rows.Count != 0))
        {
            return new RetirementPreflightResult(RetirementPreflightStatus.CatalogUnavailable, []);
        }

        var previouslyGranted = GrantedProfiles(current).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var nextGranted = GrantedProfiles(candidate).Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        var blocked = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, profile) in previouslyGranted)
        {
            var stored = head is not null && rows.TryGetValue(id, out var row) ? row.Effective(head) : null;
            if (stored is null || !MatchesDeployedProfile(current, profile, stored))
            {
                return new RetirementPreflightResult(RetirementPreflightStatus.CatalogUnavailable, [id]);
            }
            if (!nextGranted.Contains(id) && stored.State != CatalogProfileState.Tombstoned)
            {
                blocked.Add(id);
            }
        }

        foreach (var row in rows.Values)
        {
            if (head is not null && row.Effective(head) is { State: not CatalogProfileState.Tombstoned } stored &&
                !previouslyGranted.ContainsKey(stored.Profile.ProfileId))
            {
                // Extra live catalog rows make the deployed grant set incomplete.
                blocked.Add(stored.Profile.ProfileId);
            }
        }
        return blocked.Count == 0
            ? new RetirementPreflightResult(RetirementPreflightStatus.Verified, [])
            : new RetirementPreflightResult(RetirementPreflightStatus.RetirementRequired, [.. blocked]);
    }

    private static IEnumerable<RunnerProfile> GrantedProfiles(RuntimeConfiguration configuration) =>
        configuration.Mode == ControllerMode.Active
            ? configuration.Profiles.Where(static profile => profile.State != RunnerProfileState.Disabled)
            : [];

    private static bool MatchesDeployedProfile(RuntimeConfiguration current, RunnerProfile profile, CatalogProfile stored)
    {
        var expected = ConfigurationBinding(current, profile);
        return SameBinding(expected with
        {
            ScaleSetId = expected.ScaleSetId ?? stored.Binding.ScaleSetId,
            RunnerGroupId = stored.Binding.RunnerGroupId,
        }, stored.Binding);
    }
}
