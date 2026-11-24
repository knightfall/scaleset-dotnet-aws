using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.GitHub;

public enum GitHubRegistrationStatus
{
    Bound = 0,
    Missing = 1,
    DryRun = 2,
    Disabled = 3,
    LeaseUnavailable = 4,
    RecoveryRequired = 5,
}

public sealed record GitHubRegistrationResult(
    GitHubRegistrationStatus Status,
    GitHubScaleSetBinding? Binding = null,
    GitHubOperationException? Failure = null);

public sealed record GitHubRegistrationLease(ControllerLease Lease, long Generation, ControllerClockEvidence Clock);

public sealed class GitHubScaleSetRegistration
{
    private const string CreateIntent = "github-scale-set-create";
    private readonly DynamoDbLifecycleStore _store;
    private readonly DynamoDbEffectJournal _journal;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _writeReserve;
    private readonly TimeSpan _sendWindow;

    public GitHubScaleSetRegistration(
        DynamoDbLifecycleStore store, TimeProvider clock, TimeSpan writeReserve, TimeSpan sendWindow)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(writeReserve, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sendWindow, TimeSpan.Zero);
        _store = store;
        _journal = new DynamoDbEffectJournal(store, clock);
        _clock = clock;
        _writeReserve = writeReserve;
        _sendWindow = sendWindow;
    }

    public async Task<GitHubRegistrationResult> ResolveAsync(
        RuntimeConfiguration configuration,
        string profileId,
        IGitHubScaleSetRemote? remote,
        GitHubRegistrationLease? ownership,
        GitHubScaleSetBinding? previouslyBound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Mode == ControllerMode.DryRun)
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.DryRun);
        }

        var profile = configuration.Profiles.SingleOrDefault(profile => profile.Id == profileId)
            ?? throw new ArgumentException("An exact runtime profile is required.", nameof(profileId));
        if (profile.State == RunnerProfileState.Disabled)
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.Disabled);
        }

        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        if (ownership is not { Lease.Mode: ControllerLeaseMode.Active, Generation: > 0 } ||
            ownership.Lease.Deployment != deployment || !TryGetTime(ownership, out _) ||
            await _store.ReadLeaseAsync(deployment, cancellationToken).ConfigureAwait(false) != ownership.Lease)
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.LeaseUnavailable);
        }

        string scopeUrl = configuration.GitHub[profile.GitHub.Connection].ScopeUrl;
        if (remote is not { } connection || connection.ScopeIdentity != GitHubRoutingIdentity.Scope(scopeUrl))
        {
            throw new ArgumentException("The remote client must belong to the configured GitHub scope.", nameof(remote));
        }

        var position = await connection.LookupAsync(profile.GitHub, previouslyBound, cancellationToken).ConfigureAwait(false);
        var request = new GitHubScaleSetCreateEffectRequest(scopeUrl, profile.GitHub.RunnerGroup,
            position.RunnerGroupId, profile.GitHub.ScaleSet, profile.GitHub.Labels);
        var profileKey = new LifecycleProfile(deployment, profile.Id);
        var snapshot = await _journal.ReadAsync(profileKey, CreateIntent, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null && (snapshot.Entry.Envelope.Request is not GitHubScaleSetCreateEffectRequest ||
            snapshot.Entry.Envelope.Target != request.Target))
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired);
        }

        if (position.Binding is { } existing)
        {
            return await BindObservedAsync(ownership, connection, existing, snapshot, cancellationToken).ConfigureAwait(false);
        }
        if (snapshot is { Entry.State: EffectState.Executing or EffectState.Uncertain or EffectState.Settled })
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired);
        }
        if (profile.State != RunnerProfileState.Active || profile.GitHub.Registration != GitHubRegistration.CreateIfMissing ||
            profile.GitHub.ScaleSetId.HasValue || previouslyBound is not null)
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.Missing);
        }

        return await CreateAsync(ownership, connection, profileKey, request, snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the exact original create target for fenced recovery after process loss.</summary>
    public async Task<EffectEvidence?> ObserveCreateAsync(
        IGitHubScaleSetRemote remote, EffectJournalSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Entry.Envelope.Request is not GitHubScaleSetCreateEffectRequest request || request.Target.Scope != remote.ScopeIdentity)
        {
            throw new ArgumentException("The journal entry and remote client must identify the same scale-set creation.", nameof(snapshot));
        }
        long? found = await remote.FindExactAsync(request, cancellationToken).ConfigureAwait(false);
        return found is > 0 ? Applied(snapshot.Entry.Envelope) : null;
    }

    private async Task<GitHubRegistrationResult> BindObservedAsync(
        GitHubRegistrationLease ownership,
        IGitHubScaleSetRemote remote,
        GitHubScaleSetBinding binding,
        EffectJournalSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is { Entry.State: not EffectState.Settled, Entry.Envelope.Request: GitHubScaleSetCreateEffectRequest request })
        {
            long? exact = await remote.FindExactAsync(request, cancellationToken).ConfigureAwait(false);
            if (exact != binding.ScaleSetId || !TryGetTime(ownership, out _))
            {
                return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired);
            }
            var envelope = snapshot.Entry.Envelope;
            var evidence = snapshot.Entry.State == EffectState.Planned
                ? new EffectEvidence(EffectResolutionKind.CanceledBeforeSend, envelope.Sha256, envelope.Target, _clock.GetUtcNow())
                : Applied(envelope);
            if (await _journal.ResolveAsync(ownership.Lease, snapshot, Guid.NewGuid(), evidence, cancellationToken)
                .ConfigureAwait(false) != LifecycleWriteOutcome.Committed)
            {
                return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired);
            }
        }

        return TryGetTime(ownership, out _) &&
            await _store.ReadLeaseAsync(ownership.Lease.Deployment, cancellationToken).ConfigureAwait(false) == ownership.Lease
            ? new GitHubRegistrationResult(GitHubRegistrationStatus.Bound, binding)
            : new GitHubRegistrationResult(GitHubRegistrationStatus.LeaseUnavailable);
    }

    private async Task<GitHubRegistrationResult> CreateAsync(
        GitHubRegistrationLease ownership,
        IGitHubScaleSetRemote remote,
        LifecycleProfile profile,
        GitHubScaleSetCreateEffectRequest request,
        EffectJournalSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (!TryGetTime(ownership, out var latest))
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.LeaseUnavailable);
        }

        var sampledAt = latest - ownership.Clock.MaximumAbsoluteSkew;
        var earliest = snapshot?.Entry.Envelope.EarliestPossibleFirstSend ?? sampledAt - ownership.Clock.MaximumAbsoluteSkew;
        var original = EffectEnvelope.Create(profile, CreateIntent, ownership.Generation, request, earliest, null);
        if (snapshot is null)
        {
            var outcome = await _journal.PlanAsync(ownership.Lease, original, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            if (outcome != LifecycleWriteOutcome.Committed)
            {
                return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired);
            }
            snapshot = await _journal.ReadAsync(profile, CreateIntent, cancellationToken).ConfigureAwait(false);
        }
        if (snapshot is not { Entry.State: EffectState.Planned } || !snapshot.Entry.Envelope.Matches(original))
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired);
        }
        if (!TryGetTime(ownership, out latest))
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.LeaseUnavailable);
        }

        var window = ownership.Lease.ValidUntil - latest <= _sendWindow ? ownership.Lease.ValidUntil : latest + _sendWindow;
        if (ownership.Clock.ValidUntil < window)
        {
            window = ownership.Clock.ValidUntil;
        }
        var begin = await _journal.BeginAsync(ownership.Lease, snapshot, Guid.NewGuid(), Guid.NewGuid(), latest, window,
            _writeReserve, cancellationToken).ConfigureAwait(false);
        if (begin.Permission is not { } committed || !TryGetTime(ownership, out latest) || latest >= committed.Attempt.Deadline)
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired);
        }

        return await SendAndObserveAsync(ownership, remote, committed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubRegistrationResult> SendAndObserveAsync(
        GitHubRegistrationLease ownership, IGitHubScaleSetRemote remote, EffectSendAuthorization committed, CancellationToken cancellationToken)
    {
        var request = (GitHubScaleSetCreateEffectRequest)committed.Envelope.Request;
        GitHubScaleSetBinding? binding = null;
        GitHubOperationException? failure = null;
        try
        {
            binding = await remote.CreateScaleSetAsync(committed, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubOperationException error)
        {
            failure = error;
        }

        var current = await _journal.ReadAsync(committed.Envelope.Profile, committed.Envelope.IntentId, cancellationToken).ConfigureAwait(false);
        if (current is not { Entry.Attempt.Id: var currentAttempt } || currentAttempt != committed.Attempt.Id ||
            !current.Entry.Envelope.Matches(committed.Envelope) ||
            !TryGetTime(ownership, out _))
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired, Failure: failure);
        }

        if (binding is null)
        {
            if (current.Entry.State == EffectState.Executing)
            {
                var marked = await _journal.MarkUncertainAsync(ownership.Lease, current, Guid.NewGuid(), committed.Attempt.Id,
                    cancellationToken).ConfigureAwait(false);
                if (marked != LifecycleWriteOutcome.Committed)
                {
                    return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired, Failure: failure);
                }
                current = await _journal.ReadAsync(committed.Envelope.Profile, committed.Envelope.IntentId, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                if (await remote.FindExactAsync(request, cancellationToken).ConfigureAwait(false) is > 0 and var id)
                {
                    binding = new GitHubScaleSetBinding(request.ScopeUrl, request.RunnerGroupName, request.RunnerGroupId,
                        request.ScaleSetName, id, request.Labels);
                }
            }
            catch (GitHubOperationException error)
            {
                failure ??= error;
            }
        }

        if (binding is null || current is not { Entry.State: EffectState.Executing or EffectState.Uncertain } ||
            !TryGetTime(ownership, out _) ||
            await _journal.ResolveAsync(ownership.Lease, current, Guid.NewGuid(), Applied(committed.Envelope), cancellationToken)
                .ConfigureAwait(false) != LifecycleWriteOutcome.Committed)
        {
            return new GitHubRegistrationResult(GitHubRegistrationStatus.RecoveryRequired, Failure: failure);
        }

        return new GitHubRegistrationResult(GitHubRegistrationStatus.Bound, binding);
    }

    private bool TryGetTime(GitHubRegistrationLease ownership, out DateTimeOffset latest) =>
        ownership.Clock.TryGetUpperBound(_clock.GetUtcNow(), ownership.Lease.ValidUntil, _writeReserve, out latest);

    private EffectEvidence Applied(EffectEnvelope envelope) =>
        new(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, _clock.GetUtcNow());
}
