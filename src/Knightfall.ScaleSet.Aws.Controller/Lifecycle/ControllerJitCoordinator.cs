using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Handoff;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Controller.Lifecycle;

public sealed class ControllerJitCoordinator(
    DynamoDbLifecycleStore store, AwsBootstrapPrincipalResolver principals, GitHubJitHandoff handoff,
    ILogger<ControllerJitCoordinator>? logger = null)
{
    public async Task<GitHubJitResult> AdvanceAsync(
        RuntimeConfiguration configuration, ControllerLeaseOwnership ownership, BootstrapProviderObservation observation,
        GitHubScaleSetConnection connection, CancellationToken cancellationToken)
    {
        if (configuration.Mode != ControllerMode.Active || ownership.ReadAuthority() is not { } authority)
        {
            using var unscoped = TelemetryProfileScope.Enter(null);
            using var skipped = new ControllerJitDiagnostics.Operation(startupAuthorization: false, cancellationToken);
            skipped.Complete("deferred", "ownership_unavailable");
            return new GitHubJitResult(GitHubJitStatus.OwnershipUnavailable);
        }

        // Only the configured profile may supply metric dimensions. Storage and provider observations corroborate its identity.
        var profile = observation.Binding.Profile.Deployment == ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration)
            ? configuration.Profiles.SingleOrDefault(profile => profile.Id == observation.Binding.Profile.ProfileId) : null;
        using var profileScope = TelemetryProfileScope.Enter(profile is null ? null : TelemetryProfile.From(profile));
        using var active = CancellationTokenSource.CreateLinkedTokenSource(ownership.OwnershipLost, cancellationToken);
        var token = active.Token;
        using var diagnostics = new ControllerJitDiagnostics.Operation(startupAuthorization: false, token);
        var identity = await AuthorizeAsync(configuration, observation, token).ConfigureAwait(false);
        if (identity is null)
        {
            diagnostics.Complete("deferred", "identity_unavailable");
            return new GitHubJitResult(GitHubJitStatus.Held);
        }

        var result = await handoff.IssueAsync(authority, identity, observation.ObservedAt, connection, ownership.OwnershipLost, token)
            .ConfigureAwait(false);
        diagnostics.CompleteAdvance(result.Status, logger);
        return result;
    }

    private async Task<JitHandoffIdentity?> AuthorizeAsync(
        RuntimeConfiguration configuration, BootstrapProviderObservation observation, CancellationToken cancellationToken)
    {
        using var authorization = new ControllerJitDiagnostics.Operation(startupAuthorization: true, cancellationToken);
        var catalog = await store.ReadCatalogProfileAsync(observation.Binding.Profile, cancellationToken).ConfigureAwait(false);
        var identity = catalog is null ? null :
            await principals.ResolveAsync(configuration, catalog, observation, cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            authorization.Complete("deferred", "identity_unavailable");
        }
        else
        {
            authorization.Complete("success", "provider_identity_verified");
        }
        return identity;
    }
}
