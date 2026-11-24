using System.Collections.Concurrent;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed record GitHubListenerProfile(
    RuntimeConfiguration Configuration,
    string ProfileId,
    GitHubScaleSetBinding Binding,
    GitHubScaleSetConnection Connection,
    DeploymentLeaseFence Lease,
    Func<CancellationToken, Task<GitHubRegistrationLease?>> ReadOwnership,
    Func<CancellationToken, Task<ListenerCapacity>> ReadCapacity,
    Func<SessionRecord?, CancellationToken, Task<bool>> CanOpen,
    CancellationToken OwnershipLost);

public sealed record GitHubListenerTiming(TimeSpan WriteReserve, TimeSpan SessionSendWindow, TimeSpan UnauthorizedCooldown);

public enum GitHubListenerLaneStatus
{
    Stepped = 0,
    Waiting = 1,
    RecoveryRequired = 2,
    OwnershipUnavailable = 3,
    Stopped = 4,
    Failed = 5,
    CredentialUnchanged = 6,
    CredentialRejected = 7,
    CredentialAdopted = 8,
    StorageUnavailable = 9,
}

public sealed record GitHubListenerLaneResult(
    GitHubListenerLaneStatus Status,
    ScaleSetListenerStepResult? Step = null,
    DateTimeOffset? RetryAt = null,
    GitHubOperationException? Failure = null);

public sealed class GitHubListenerSupervisor
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly TimeProvider _clock;
    private readonly GitHubListenerTiming _timing;
    private readonly GitHubCredentialRotator? _rotation;
    private readonly GitHubCredentialCooldown _cooldown;
    private readonly ConcurrentDictionary<LifecycleProfile, GitHubListenerLane> _lanes = new();

    public GitHubListenerSupervisor(
        DynamoDbLifecycleStore store, TimeProvider clock, GitHubListenerTiming timing, GitHubCredentialRotator? rotation = null)
        : this(store, clock, timing, rotation, cooldown: null)
    {
    }

    public GitHubListenerSupervisor(DynamoDbLifecycleStore store, TimeProvider clock, GitHubListenerTiming timing,
        GitHubCredentialRotator? rotation, GitHubCredentialCooldown? cooldown)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timing);
        if (timing.WriteReserve <= TimeSpan.Zero || timing.SessionSendWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timing), "Session mutation windows and reserves must be positive.");
        }
        _store = store;
        _clock = clock;
        _timing = timing;
        _rotation = rotation;
        _cooldown = cooldown ?? new GitHubCredentialCooldown(clock, timing.UnauthorizedCooldown);
    }

    public GitHubListenerLane Register(GitHubListenerProfile definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var configuration = definition.Configuration;
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var profile = configuration.Profiles.SingleOrDefault(value => value.Id == definition.ProfileId);
        if (configuration.Mode != ControllerMode.Active || profile is null || profile.State == RunnerProfileState.Disabled ||
            definition.Lease.Deployment != deployment)
        {
            throw new ArgumentException("An enabled active-mode profile under its deployment owner is required.", nameof(definition));
        }

        var connection = configuration.GitHub[profile.GitHub.Connection];
        string scope = GitHubRoutingIdentity.Scope(connection.ScopeUrl);
        if (definition.Connection.ScopeIdentity != scope || definition.Connection.SecretArn != connection.SecretArn ||
            definition.Connection.AuthType != connection.AuthType ||
            GitHubRoutingIdentity.Scope(definition.Binding.ScopeUrl) != scope ||
            definition.Binding.RunnerGroupId < 0 || definition.Binding.ScaleSetId <= 0 ||
            (profile.GitHub.ScaleSetId.HasValue && profile.GitHub.ScaleSetId != definition.Binding.ScaleSetId) ||
            GitHubRoutingIdentity.Name(definition.Binding.RunnerGroup) != GitHubRoutingIdentity.Name(profile.GitHub.RunnerGroup) ||
            GitHubRoutingIdentity.Name(definition.Binding.ScaleSet) != GitHubRoutingIdentity.Name(profile.GitHub.ScaleSet) ||
            definition.Binding.Labels.IsDefaultOrEmpty || definition.Binding.Labels.Length != profile.GitHub.Labels.Length ||
            !definition.Binding.Labels.Select(GitHubRoutingIdentity.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(profile.GitHub.Labels.Select(GitHubRoutingIdentity.Name)))
        {
            throw new ArgumentException("The listener connection and binding must match the exact runtime profile.", nameof(definition));
        }

        var key = new LifecycleProfile(deployment, profile.Id);
        var lane = new GitHubListenerLane(_store, definition, profile, new SessionBinding(key, scope, definition.Binding.ScaleSetId),
            _clock, _timing, _cooldown, _rotation);
        if (!_lanes.TryAdd(key, lane))
        {
            throw new InvalidOperationException("A listener lane already owns this deployment profile.");
        }
        return lane;
    }

    public Task RunAsync(TimeSpan retryDelay, TimeSpan rotationInterval, TimeSpan cleanupBudget, CancellationToken stoppingToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rotationInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanupBudget, TimeSpan.Zero);
        return Task.WhenAll(_lanes.Values.Select(lane => lane.RunAsync(retryDelay, rotationInterval, cleanupBudget, stoppingToken)));
    }
}
