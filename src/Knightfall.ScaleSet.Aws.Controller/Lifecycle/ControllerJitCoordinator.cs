using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

public sealed class ControllerJitCoordinator(
    DynamoDbLifecycleStore store, AwsBootstrapPrincipalResolver principals, GitHubJitHandoff handoff)
{
    public async Task<GitHubJitResult> AdvanceAsync(
        RuntimeConfiguration configuration, ControllerLeaseOwnership ownership, BootstrapProviderObservation observation,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken)
    {
        if (configuration.Mode != ControllerMode.Active || ownership.ReadAuthority() is not { } authority)
        {
            return new GitHubJitResult(GitHubJitStatus.OwnershipUnavailable);
        }
        using var active = CancellationTokenSource.CreateLinkedTokenSource(ownership.OwnershipLost, cancellationToken);
        var token = active.Token;
        var catalog = await store.ReadCatalogProfileAsync(observation.Binding.Profile, token).ConfigureAwait(false);
        var identity = catalog is null ? null :
            await principals.ResolveAsync(configuration, catalog, observation, token).ConfigureAwait(false);
        return identity is null ? new GitHubJitResult(GitHubJitStatus.Held) :
            await handoff.IssueAsync(authority, identity, observation.ObservedAt, connection, ownership.OwnershipLost, token).ConfigureAwait(false);
    }
}
