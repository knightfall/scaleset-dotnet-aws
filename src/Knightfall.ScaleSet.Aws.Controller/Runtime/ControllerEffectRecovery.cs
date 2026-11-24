using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed class ControllerEffectRecovery(
    RuntimeConfiguration configuration, IReadOnlyDictionary<LifecycleProfile, IRunnerBackend> backends,
    DynamoDbLifecycleStore store, DynamoDbEffectJournal journal, DynamoDbEffectDispatcher dispatcher,
    EffectRecoveryAuthority recovery, GitHubSecretLoader secrets, GitHubScaleSetClientFactory clients,
    GitHubScaleSetRegistration registration, GitHubJitHandoff handoff)
{
    private readonly Dictionary<LifecycleProfile, EffectCandidateCursor?> _awsPasses = [];
    private readonly Dictionary<LifecycleProfile, ActiveEffectCursor?> _githubPasses = [];
    private long _epoch;

    internal async Task<bool> AdvanceAsync(ControllerLease lease, ControllerClockEvidence clock, CancellationToken cancellationToken)
    {
        if (_epoch != lease.Epoch)
        {
            _awsPasses.Clear();
            _githubPasses.Clear();
            _epoch = lease.Epoch;
        }
        foreach (var (key, backend) in backends)
        {
            var profile = configuration.Profiles.Single(profile => profile.Id == key.ProfileId);
            using var scope = TelemetryProfileScope.Enter(TelemetryProfile.From(profile));
            var result = await dispatcher.ReconcilePassAsync(lease, clock, key, _awsPasses.GetValueOrDefault(key),
                backend.ObservationEffects, recovery, cancellationToken).ConfigureAwait(false);
            _awsPasses[key] = result.Continuation;
            if (result.Outcome != LifecycleWriteOutcome.LeaseRejected)
            {
                await RecoverGitHubPageAsync(lease, clock, key, profile, cancellationToken).ConfigureAwait(false);
            }
        }
        // Promotion rechecks all configured and durable profiles, including profiles omitted from this runtime artifact.
        return true;
    }

    private async Task RecoverGitHubPageAsync(ControllerLease lease, ControllerClockEvidence clock, LifecycleProfile key,
        RunnerProfile profile, CancellationToken cancellationToken)
    {
        var page = await store.ReadActiveOperationsAsync(key, lease.Fence, _githubPasses.GetValueOrDefault(key), cancellationToken)
            .ConfigureAwait(false);
        if (page is null)
        {
            _githubPasses.Remove(key);
            return;
        }
        _githubPasses[key] = page.Continuation;
        foreach (var id in page.IntentIds)
        {
            var effect = await journal.ReadAsync(key, id, cancellationToken).ConfigureAwait(false);
            if (effect is not { Entry.State: EffectState.Executing or EffectState.Uncertain,
                    Entry.Envelope.Request: GitHubScaleSetCreateEffectRequest or GitHubJitGenerateEffectRequest or GitHubRunnerRemoveEffectRequest })
            {
                continue;
            }
            if (await RecoverGitHubEffectAsync(lease, clock, profile, effect, cancellationToken).ConfigureAwait(false))
            {
                _githubPasses.Remove(key);
            }
        }
    }

    private async Task<bool> RecoverGitHubEffectAsync(ControllerLease lease, ControllerClockEvidence clock, RunnerProfile profile,
        EffectJournalSnapshot effect, CancellationToken cancellationToken)
    {
        try
        {
            var version = await secrets.LoadAsync(configuration, profile.GitHub.Connection, cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                return false;
            }
            using var connection = clients.Create(version);
            if (connection.ScopeIdentity != effect.Entry.Envelope.Target.Scope)
            {
                return false;
            }
            var result = effect.Entry.Envelope.Request switch
            {
                GitHubScaleSetCreateEffectRequest => await recovery.Coordinator.ResolveFencedEffectAsync(lease, effect, Guid.NewGuid(),
                    clock, recovery.Isolation, (attempt, token) => registration.ObserveCreateAsync(connection, attempt, token), cancellationToken)
                    .ConfigureAwait(false),
                GitHubJitGenerateEffectRequest => await handoff.RecoverFencedAsync(lease, clock, effect, recovery, connection, cancellationToken)
                    .ConfigureAwait(false),
                GitHubRunnerRemoveEffectRequest => await handoff.RecoverFencedRemovalAsync(lease, clock, effect, recovery, connection, cancellationToken)
                    .ConfigureAwait(false),
                _ => LifecycleWriteOutcome.Conflict,
            };
            return result == LifecycleWriteOutcome.Committed;
        }
        catch (Exception error) when (error is GitHubSecretLoadException or GitHubOperationException)
        {
            return false;
        }
    }
}
