using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public enum LeaseActivationStatus
{
    Activated = 0,
    AlreadyActive = 1,
    LeaseChanged = 2,
    ClockUnverified = 3,
    PriorTaskUnisolated = 4,
    ProfileRecoveryPending = 5,
}

public enum ProfileRecoveryStatus
{
    Ready = 0,
    RebuildPending = 1,
    UncertainRead = 2,
    AmbiguousEffects = 3,
}

public sealed record ProfileRecoveryObservation(string ProfileId, ProfileRecoveryStatus Status);

public sealed record LeaseActivationResult(
    LeaseActivationStatus Status,
    ControllerLease? Lease,
    ImmutableArray<ProfileRecoveryObservation> Profiles);

public sealed class ControllerLeaseRecoveryCoordinator
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly IControllerTaskFenceObserver _tasks;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _writeMargin;

    public ControllerLeaseRecoveryCoordinator(
        DynamoDbLifecycleStore store,
        IControllerTaskFenceObserver tasks,
        TimeProvider clock,
        TimeSpan writeMargin)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(clock);
        if (writeMargin <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(writeMargin), "Use a positive reserved write margin.");
        }
        _store = store;
        _tasks = tasks;
        _clock = clock;
        _writeMargin = writeMargin;
    }

    public static string DeploymentKey(RuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return $"{configuration.Aws.Account}:{configuration.Aws.Region}:{configuration.EnvironmentName}";
    }

    public async Task<LeaseActivationResult> TryActivateAsync(
        ControllerLease expected,
        RuntimeConfiguration configuration,
        ControllerClockEvidence? clockEvidence,
        IReadOnlyCollection<VerifiedControllerIsolation> isolation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(isolation);
        if (expected.Deployment != DeploymentKey(configuration))
        {
            throw new ArgumentException("The runtime configuration and lease must identify the same deployment.", nameof(configuration));
        }
        if (configuration.Mode != ControllerMode.Active)
        {
            throw new ArgumentException("An active runtime configuration is required to activate a lease.", nameof(configuration));
        }

        var current = await _store.ReadLeaseAsync(expected.Deployment, cancellationToken).ConfigureAwait(false);
        if (current is null || current != expected || current.Mode == ControllerLeaseMode.Released)
        {
            return new LeaseActivationResult(LeaseActivationStatus.LeaseChanged, current, []);
        }

        if (!TryAuthorizeClock(current, clockEvidence, _clock.GetUtcNow(), out _))
        {
            return new LeaseActivationResult(LeaseActivationStatus.ClockUnverified, current, []);
        }
        if (current.Mode == ControllerLeaseMode.Active)
        {
            return new LeaseActivationResult(LeaseActivationStatus.AlreadyActive, current, []);
        }

        var fence = await ObservePriorTasksAsync(current, clockEvidence, isolation, cancellationToken).ConfigureAwait(false);
        if (fence.BlockedBy is { } blocked)
        {
            return new LeaseActivationResult(blocked, current, []);
        }

        var registeredProfiles = await _store.ReadEffectProfilesAsync(current.Deployment, cancellationToken).ConfigureAwait(false);
        var recoveryProfiles = configuration.Profiles.Select(profile => new LifecycleProfile(current.Deployment, profile.Id))
            .Concat(registeredProfiles).Distinct().ToArray();
        var profiles = ImmutableArray.CreateBuilder<ProfileRecoveryObservation>(recoveryProfiles.Length);
        foreach (var profile in recoveryProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryAuthorizeClock(current, clockEvidence, _clock.GetUtcNow(), out _))
            {
                return new LeaseActivationResult(LeaseActivationStatus.ClockUnverified, current, profiles.ToImmutable());
            }
            var result = await InspectProfileAsync(profile, current, cancellationToken).ConfigureAwait(false);
            profiles.Add(new ProfileRecoveryObservation(profile.ProfileId, result));
        }

        var observations = profiles.MoveToImmutable();
        if (observations.Any(profile => profile.Status != ProfileRecoveryStatus.Ready))
        {
            return new LeaseActivationResult(LeaseActivationStatus.ProfileRecoveryPending, current, observations);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        if (!TryAuthorizeClock(current, clockEvidence, now, out DateTimeOffset latestNow))
        {
            return new LeaseActivationResult(LeaseActivationStatus.ClockUnverified, current, observations);
        }
        if (!StillIsolated(fence.Tasks, isolation, now, latestNow))
        {
            return new LeaseActivationResult(LeaseActivationStatus.PriorTaskUnisolated, current, observations);
        }

        var activation = await _store.PromoteLeaseAsync(current, latestNow, cancellationToken).ConfigureAwait(false);
        return new LeaseActivationResult(
            activation.Outcome == LifecycleWriteOutcome.Committed && activation.Lease is { Mode: ControllerLeaseMode.Active }
                ? LeaseActivationStatus.Activated
                : LeaseActivationStatus.LeaseChanged,
            activation.Lease,
            observations);
    }

    /// <summary>Obtains fresh effect evidence after the previous task is isolated.</summary>
    public async Task<LifecycleWriteOutcome> ResolveFencedEffectAsync(
        ControllerLease expected,
        EffectJournalSnapshot effect,
        Guid transitionId,
        ControllerClockEvidence? clockEvidence,
        IReadOnlyCollection<VerifiedControllerIsolation> isolation,
        Func<EffectJournalSnapshot, CancellationToken, Task<EffectEvidence?>> observe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(isolation);
        ArgumentNullException.ThrowIfNull(observe);
        if (effect.Entry.Envelope.Profile.Deployment != expected.Deployment ||
            effect.Entry.State is not (EffectState.Executing or EffectState.Uncertain) ||
            expected.Mode != ControllerLeaseMode.RecoveryOnly)
        {
            throw new ArgumentException("An ambiguous effect and recovery lease for the same deployment are required.", nameof(effect));
        }
        if (await _store.ReadLeaseAsync(expected.Deployment, cancellationToken).ConfigureAwait(false) != expected ||
            !TryAuthorizeClock(expected, clockEvidence, _clock.GetUtcNow(), out _))
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }

        var fence = await ObservePriorTasksAsync(expected, clockEvidence, isolation, cancellationToken).ConfigureAwait(false);
        if (fence.BlockedBy.HasValue)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }

        var started = _clock.GetUtcNow();
        var evidence = await observe(effect, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (clockEvidence is not { } proof || !TryAuthorizeClock(expected, proof, now, out var latestNow) ||
            !StillIsolated(fence.Tasks, isolation, now, latestNow))
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }
        if (evidence is null || evidence.ObservedAt < started || evidence.ObservedAt > now)
        {
            return LifecycleWriteOutcome.Indeterminate;
        }

        if (evidence.Kind == EffectResolutionKind.Absent)
        {
            var earliestObservedAt = evidence.ObservedAt - proof.MaximumAbsoluteSkew;
            if (earliestObservedAt < effect.Entry.LatestAuthorizedDeadline)
            {
                return LifecycleWriteOutcome.Indeterminate;
            }
            evidence = evidence with { EarliestObservedAt = earliestObservedAt };
        }

        var journal = new DynamoDbEffectJournal(_store, _clock);
        return await journal.ResolveRecoveryAsync(expected, effect, transitionId, evidence, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(LeaseActivationStatus? BlockedBy, List<TaskObservation> Tasks)> ObservePriorTasksAsync(
        ControllerLease lease,
        ControllerClockEvidence? clockEvidence,
        IReadOnlyCollection<VerifiedControllerIsolation> isolation,
        CancellationToken cancellationToken)
    {
        var priorTasks = new List<(string Arn, DateTimeOffset? AcquiredAt)>(2);
        if (lease.PreviousOwner is { CleanRelease: false } previous)
        {
            priorTasks.Add((previous.OwnerTaskArn, previous.AcquiredAt));
        }
        if (lease.RequiredFencingTaskArn is { } required && !priorTasks.Any(task => task.Arn == required))
        {
            priorTasks.Add((required, lease.RequiredFencingTaskAcquiredAt));
        }

        var observations = new List<TaskObservation>(priorTasks.Count);
        foreach (var (taskArn, acquiredAt) in priorTasks)
        {
            var result = await _tasks.ObserveAsync(taskArn, acquiredAt, cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            if (!TryAuthorizeClock(lease, clockEvidence, now, out var latestNow))
            {
                return (LeaseActivationStatus.ClockUnverified, observations);
            }
            var observation = new TaskObservation(taskArn, result);
            if (!IsIsolated(observation, isolation, now, latestNow))
            {
                return (LeaseActivationStatus.PriorTaskUnisolated, observations);
            }
            observations.Add(observation);
        }

        return (null, observations);
    }

    private async Task<ProfileRecoveryStatus> InspectProfileAsync(
        LifecycleProfile profile, ControllerLease lease, CancellationToken cancellationToken)
    {
        var pass = await _store.TryReuseEffectIndexAsync(profile, lease, cancellationToken).ConfigureAwait(false);
        if (pass.Outcome == LifecycleWriteOutcome.RebuildRequired)
        {
            pass = await _store.AdvanceEffectRebuildAsync(profile, lease.Fence, cancellationToken).ConfigureAwait(false);
        }
        if (pass.Outcome != LifecycleWriteOutcome.Committed)
        {
            return ProfileRecoveryStatus.UncertainRead;
        }
        if (pass.Head is not { State: EffectIndexState.Ready })
        {
            return ProfileRecoveryStatus.RebuildPending;
        }

        ActiveEffectCursor? cursor = null;
        do
        {
            var page = await _store.ReadActiveOperationsAsync(profile, lease.Fence, cursor, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return ProfileRecoveryStatus.UncertainRead;
            }
            foreach (string intentId in page.IntentIds)
            {
                var effect = await _store.ReadEffectAsync(profile, intentId, cancellationToken).ConfigureAwait(false);
                if (effect is not { IsUnfinished: true })
                {
                    return ProfileRecoveryStatus.UncertainRead;
                }
                if (effect.State is EffectState.Executing or EffectState.Uncertain)
                {
                    return ProfileRecoveryStatus.AmbiguousEffects;
                }
            }
            cursor = page.Continuation;
        }
        while (cursor is not null);

        return ProfileRecoveryStatus.Ready;
    }

    private bool TryAuthorizeClock(
        ControllerLease lease, ControllerClockEvidence? evidence, DateTimeOffset now, out DateTimeOffset latestNow)
    {
        latestNow = default;
        return evidence is not null && evidence.TryGetUpperBound(now, lease.ValidUntil, _writeMargin, out latestNow);
    }

    private static bool StillIsolated(
        List<TaskObservation> observations,
        IReadOnlyCollection<VerifiedControllerIsolation> isolation,
        DateTimeOffset now,
        DateTimeOffset latestNow) => observations.All(task => IsIsolated(task, isolation, now, latestNow));

    private static bool IsIsolated(
        TaskObservation task,
        IReadOnlyCollection<VerifiedControllerIsolation> isolation,
        DateTimeOffset now,
        DateTimeOffset latestNow)
    {
        if (task.Evidence.Status == ControllerTaskFenceStatus.Stopped)
        {
            return task.Evidence.ValidUntil is null || task.Evidence.ValidUntil > latestNow;
        }
        return task.Evidence.Status is ControllerTaskFenceStatus.Missing or ControllerTaskFenceStatus.Unknown &&
            isolation.Any(proof => proof.TaskArn == task.TaskArn && proof.ObservedAt <= now && proof.ValidUntil > latestNow &&
                !string.IsNullOrWhiteSpace(proof.AwsEvidenceId) && !string.IsNullOrWhiteSpace(proof.GitHubEvidenceId));
    }

    private sealed record TaskObservation(string TaskArn, ControllerTaskFenceObservation Evidence);
}
