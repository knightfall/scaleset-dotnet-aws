using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using ScaleSet.Client;
using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed class ControllerProfileSession : IAsyncDisposable
{
    private const int MaximumUnexpectedPollFailures = 4;
    private const int MaximumPollRecoveryDelaySeconds = 30;
    private static readonly TimeSpan PollRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollRecoveryReadBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RotationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DemandFreshness = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LogFlushGrace = TimeSpan.FromSeconds(30);
    private static readonly ListenerCapacity NoAdmission = new(0, 0, IsReady: false);

    private readonly RuntimeConfiguration _configuration;
    private readonly RunnerProfile _profile;
    private readonly LifecycleProfile _key;
    private readonly GitHubConnectionConfiguration _github;
    private readonly long _generation;
    private readonly ControllerLeaseOwnership _ownership;
    private readonly DynamoDbLifecycleStore _store;
    private readonly GitHubSecretLoader _secrets;
    private readonly GitHubScaleSetClientFactory _clients;
    private readonly GitHubScaleSetRegistration _registration;
    private readonly GitHubListenerSupervisor _supervisor;
    private readonly GitHubCredentialRotator _rotation;
    private readonly GitHubCredentialCooldown _cooldown;
    private readonly TimeProvider _clock;
    private readonly TelemetryProfile? _telemetry;
    private readonly string _scope;
    private readonly string _runnerGroup;
    private readonly string _scaleSet;
    private readonly string[] _labels;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly object _disposalGate = new();
    private readonly CancellationTokenSource _admissions = new();
    private readonly CancellationTokenSource _polls;
    private readonly CancellationToken _processStopping;
    private GitHubScaleSetBinding? _binding;
    private GitHubScaleSetConnection? _connection;
    private GitHubListenerLane? _lane;
    private GitHubListenerLaneResult? _listenerResult;
    private Task? _pollTask;
    private Task? _disposal;
    private DateTimeOffset _nextRotation;
    private int _disposed;

    internal ControllerProfileSession(RuntimeConfiguration configuration, RunnerProfile profile, long generation,
        ControllerLeaseOwnership ownership, DynamoDbLifecycleStore store, GitHubSecretLoader secrets, GitHubScaleSetClientFactory clients,
        GitHubScaleSetRegistration registration, GitHubListenerSupervisor supervisor, GitHubCredentialRotator rotation,
        GitHubCredentialCooldown cooldown, TimeProvider clock, CancellationToken processStopping = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (configuration.Mode != ControllerMode.Active || profile.State == RunnerProfileState.Disabled ||
            !configuration.Profiles.Contains(profile))
        {
            throw new ArgumentException("An enabled active-mode runtime profile is required.", nameof(profile));
        }

        _configuration = configuration;
        _profile = profile;
        _generation = generation;
        _ownership = ownership;
        _store = store;
        _secrets = secrets;
        _clients = clients;
        _registration = registration;
        _supervisor = supervisor;
        _rotation = rotation;
        _cooldown = cooldown;
        _clock = clock;
        _processStopping = processStopping;
        _polls = CancellationTokenSource.CreateLinkedTokenSource(processStopping);
        _telemetry = TelemetryProfile.From(profile);
        _key = new LifecycleProfile(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), profile.Id);
        _github = configuration.GitHub[profile.GitHub.Connection];
        _scope = GitHubRoutingIdentity.Scope(_github.ScopeUrl);
        _runnerGroup = GitHubRoutingIdentity.Name(profile.GitHub.RunnerGroup);
        _scaleSet = GitHubRoutingIdentity.Name(profile.GitHub.ScaleSet);
        _labels = profile.GitHub.Labels.Select(GitHubRoutingIdentity.Name).Order(StringComparer.Ordinal).ToArray();
    }

    internal GitHubListenerLaneResult? ListenerResult => Volatile.Read(ref _listenerResult);

    internal Action? ListenerHealthChanged { get; set; }

    internal bool RequiresListenerHealth => _profile.State == RunnerProfileState.Active;

    internal bool IsListenerAvailable => ListenerFailure is null && ReadCredentialCooldown() is null;

    internal string? ListenerFailure => DescribeListenerFailure(ListenerResult?.Status);

    internal DateTimeOffset? ReadCredentialCooldown()
    {
        var current = _cooldown.Read(_github.SecretArn);
        var listener = Volatile.Read(ref _lane)?.ReadCredentialCooldown();
        return current is null || listener > current ? listener : current;
    }

    internal void RecordCredentialFailure(GitHubOperationException failure) => _cooldown.Record(_github.SecretArn, failure);

    internal async Task<GitHubScaleSetBinding?> EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        if (AdmissionsStopped || _polls.IsCancellationRequested)
        {
            return null;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _admissions.Token, _ownership.OwnershipLost);
        var token = linked.Token;
        await _serial.WaitAsync(token).ConfigureAwait(false);
        using var telemetry = TelemetryProfileScope.Enter(_telemetry);
        try
        {
            if (AdmissionsStopped || _polls.IsCancellationRequested || ReadCredentialCooldown() is not null ||
                await ReadCatalogAsync(allowClosing: false, token).ConfigureAwait(false) is not { } catalog)
            {
                return null;
            }

            if (_binding is { } alreadyBound)
            {
                return MatchesBound(catalog, alreadyBound) ? alreadyBound : null;
            }

            return await RegisterAsync(catalog, token).ConfigureAwait(false);
        }
        catch (GitHubOperationException failure)
        {
            RecordCredentialFailure(failure);
            return null;
        }
        catch (GitHubSecretLoadException failure)
        {
            RecordCredentialFailure(new GitHubOperationException(GitHubOperationError.CredentialUnavailable, failure.StatusCode));
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && AdmissionsStopped)
        {
            return null;
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Gets the connection owned by this profile's serial reconciliation loop.</summary>
    internal async Task<GitHubScaleSetConnection?> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (AdmissionsStopped || ReadCredentialCooldown() is not null)
        {
            return null;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _admissions.Token, _ownership.OwnershipLost);
        var token = linked.Token;
        await _serial.WaitAsync(token).ConfigureAwait(false);
        using var telemetry = TelemetryProfileScope.Enter(_telemetry);
        try
        {
            if (_connection is not { } current || _binding is not { } binding ||
                await ReadCatalogAsync(allowClosing: true, token).ConfigureAwait(false) is not { } catalog || !MatchesBound(catalog, binding))
            {
                return null;
            }

            if (_clock.GetUtcNow() >= _nextRotation)
            {
                _nextRotation = _clock.GetUtcNow() + RotationInterval;
                var check = await _rotation.CheckAsync(_configuration, _profile.GitHub, binding, current, token).ConfigureAwait(false);
                if (check.Failure?.RateLimit is not null)
                {
                    RecordCredentialFailure(check.Failure);
                }
                if (check.Candidate is { } candidate)
                {
                    current.Dispose();
                    _connection = candidate;
                }
            }

            return !AdmissionsStopped && ReadCredentialCooldown() is null && ReadLocalOwnership() is not null ? _connection : null;
        }
        finally
        {
            _serial.Release();
        }
    }

    internal Task StopAdmissionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _admissions.CancelAsync();
    }

    internal Task CancelPollsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _polls.CancelAsync();
    }

    internal async Task JoinMutatorsAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        var poll = _pollTask;
        _serial.Release();
        if (poll is not null)
        {
            try
            {
                await poll.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (poll.IsFaulted)
            {
                RecordPollFailure();
            }
        }
    }

    internal async Task CloseOwnedSessionsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _lane) is { } lane)
        {
            PublishListenerResult(await lane.StopAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    internal async Task<GitHubListenerLaneResult?> RetireDrainingSessionAsync(ProfileRetirementCoordinator retirement, CancellationToken cancellationToken)
    {
        if (AdmissionsStopped || _profile.State != RunnerProfileState.Draining)
        {
            return null;
        }
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        GitHubListenerLane? lane;
        try
        {
            var catalog = await ReadCatalogAsync(allowClosing: true, cancellationToken).ConfigureAwait(false);
            if (catalog is not { State: CatalogProfileState.ClosureHeld })
            {
                return null;
            }
            lane = _lane ?? await RegisterClosureLaneAsync(catalog, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
        if (lane is null)
        {
            return null;
        }

        await CancelPollsAsync(cancellationToken).ConfigureAwait(false);
        await JoinMutatorsAsync(cancellationToken).ConfigureAwait(false);
        var result = await lane.RetireDrainingSessionAsync(retirement, cancellationToken).ConfigureAwait(false);
        PublishListenerResult(result);
        return result;
    }

    private bool AdmissionsStopped => Volatile.Read(ref _disposed) != 0 || _admissions.IsCancellationRequested ||
        _processStopping.IsCancellationRequested || _ownership.OwnershipLost.IsCancellationRequested;

    private async Task<GitHubScaleSetBinding?> RegisterAsync(CatalogProfile catalog, CancellationToken cancellationToken)
    {
        if (await ReadDurableOwnershipAsync(cancellationToken).ConfigureAwait(false) is not { } owner ||
            await _secrets.LoadAsync(_configuration, _profile.GitHub.Connection, cancellationToken).ConfigureAwait(false) is not { } version)
        {
            return null;
        }
        GitHubScaleSetConnection? candidate = _clients.Create(version);
        try
        {
            var result = await _registration.ResolveAsync(_configuration, _profile.Id, candidate, owner, FromCatalog(catalog), cancellationToken)
                .ConfigureAwait(false);
            if (result.Failure is { } failure)
            {
                RecordCredentialFailure(failure);
            }
            if (result is not { Status: GitHubRegistrationStatus.Bound, Binding: { } binding } || AdmissionsStopped)
            {
                return null;
            }

            var saved = await _store.BindCatalogScaleSetAsync(owner.Lease.Fence, catalog, binding.RunnerGroupId, binding.ScaleSetId, cancellationToken)
                .ConfigureAwait(false);
            if (saved is not (LifecycleWriteOutcome.Committed or LifecycleWriteOutcome.Indeterminate) ||
                await ReadCatalogAsync(allowClosing: false, cancellationToken).ConfigureAwait(false) is not { } published ||
                !MatchesBound(published, binding) || AdmissionsStopped || _polls.IsCancellationRequested)
            {
                return null;
            }

            var lane = RegisterLane(binding, version, owner.Lease.Fence);
            _binding = binding;
            _connection = candidate;
            _nextRotation = _clock.GetUtcNow() + RotationInterval;
            candidate = null;
            _pollTask = PollAsync(lane);
            return binding;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private async Task<GitHubListenerLane?> RegisterClosureLaneAsync(CatalogProfile catalog, CancellationToken cancellationToken)
    {
        if (FromCatalog(catalog) is not { } binding ||
            await ReadDurableOwnershipAsync(cancellationToken).ConfigureAwait(false) is not { } owner ||
            await _secrets.LoadAsync(_configuration, _profile.GitHub.Connection, cancellationToken).ConfigureAwait(false) is not { } version)
        {
            return null;
        }
        using var check = _clients.Create(version);
        _ = await check.LookupAsync(_profile.GitHub, binding, cancellationToken).ConfigureAwait(false);
        return !AdmissionsStopped && await ReadCatalogAsync(allowClosing: true, cancellationToken).ConfigureAwait(false) is
            { State: CatalogProfileState.ClosureHeld } current && MatchesBound(current, binding)
            ? RegisterLane(binding, version, owner.Lease.Fence) : null;
    }

    private GitHubListenerLane RegisterLane(GitHubScaleSetBinding binding, GitHubSecretVersion version, DeploymentLeaseFence owner)
    {
        var connection = _clients.Create(version);
        try
        {
            var lane = _supervisor.Register(new GitHubListenerProfile(_configuration, _profile.Id, binding, connection, owner,
                ReadListenerOwnershipAsync, ReadListenerCapacityAsync, CanOpenAsync, _ownership.OwnershipLost));
            Volatile.Write(ref _lane, lane);
            return lane;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task PollAsync(GitHubListenerLane lane)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_polls.Token, _ownership.OwnershipLost);
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _admissions.Token);
        var token = cancellation.Token;
        var rotation = _clock.GetUtcNow() + RotationInterval;
        bool recovering = false;
        int retrySeconds = 1;
        int unexpectedFailures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var attemptToken = recovering ? recoveryCancellation.Token : token;
                if (recovering)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), _clock, attemptToken).ConfigureAwait(false);
                    retrySeconds = Math.Min(retrySeconds * 2, MaximumPollRecoveryDelaySeconds);
                }
                if (ReadCredentialCooldown() is not null)
                {
                    if (!recovering)
                    {
                        await Task.Delay(PollRetry, _clock, token).ConfigureAwait(false);
                    }
                    continue;
                }

                GitHubListenerLaneResult stepped;
                try
                {
                    if (recovering && await ReadRecoveryHoldAsync(attemptToken).ConfigureAwait(false) is { } held)
                    {
                        PublishListenerResult(held);
                        continue;
                    }
                    stepped = await PollOnceAsync(attemptToken).ConfigureAwait(false);
                }
                catch (Exception) when (!attemptToken.IsCancellationRequested)
                {
                    if (++unexpectedFailures >= MaximumUnexpectedPollFailures)
                    {
                        PublishListenerResult(new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired));
                        return;
                    }
                    RecordPollFailure();
                    recovering = true;
                    continue;
                }

                if (stepped.Status == GitHubListenerLaneStatus.Stopped || stepped.Step?.Status is
                    ScaleSetListenerStepStatus.RecoveryRequired or ScaleSetListenerStepStatus.CheckpointConflict)
                {
                    PublishListenerResult(stepped);
                    return;
                }
                if (recovering && stepped is { Status: GitHubListenerLaneStatus.Failed, Failure.StatusCode: 401 } &&
                    !lane.PollCredentialRejected)
                {
                    PublishListenerResult(stepped with { Status = GitHubListenerLaneStatus.RecoveryRequired });
                    return;
                }
                if (stepped.Status == GitHubListenerLaneStatus.StorageRejected || (recovering && stepped is not
                    { Status: GitHubListenerLaneStatus.Stepped, Step.Status: ScaleSetListenerStepStatus.Initialized or
                        ScaleSetListenerStepStatus.EmptyPoll or ScaleSetListenerStepStatus.MessageAcknowledged }))
                {
                    // A deferred operation did not establish current authority over this session.
                    if (DescribeListenerFailure(stepped.Status) is not null)
                    {
                        PublishListenerResult(stepped);
                    }
                    recovering = true;
                    continue;
                }

                recovering = false;
                retrySeconds = 1;
                unexpectedFailures = 0;
                PublishListenerResult(stepped);
                if (stepped.Step?.Status is not (ScaleSetListenerStepStatus.MessageAcknowledged or ScaleSetListenerStepStatus.Initialized))
                {
                    await Task.Delay(PollRetry, _clock, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
        }

        async Task<GitHubListenerLaneResult> PollOnceAsync(CancellationToken attemptToken)
        {
            // Check a sessionless lane before its first resumed mutation. An owned
            // session requires a definitive queue GET after the pump settled its ACK.
            if ((recovering && !lane.HasRetainedSession) ||
                ((!recovering || lane.PollCredentialRejected) && _clock.GetUtcNow() >= rotation))
            {
                var rotated = recovering
                    ? await lane.RotateDuringRecoveryAsync(attemptToken).ConfigureAwait(false)
                    : await lane.RotateAsync(attemptToken).ConfigureAwait(false);
                rotation = _clock.GetUtcNow() + RotationInterval;
                if (rotated.Status == GitHubListenerLaneStatus.StorageRejected ||
                    (recovering && rotated.Status is not (GitHubListenerLaneStatus.Waiting or
                        GitHubListenerLaneStatus.CredentialAdopted or GitHubListenerLaneStatus.CredentialUnchanged)))
                {
                    return rotated;
                }
                if (!recovering)
                {
                    PublishListenerResult(rotated);
                }
            }
            return await lane.StepAsync(attemptToken).ConfigureAwait(false);
        }
    }

    private async Task<GitHubListenerLaneResult?> ReadRecoveryHoldAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(PollRecoveryReadBudget, _clock);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var catalog = await ReadCatalogAsync(allowClosing: false, attempt.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                return new GitHubListenerLaneResult(GitHubListenerLaneStatus.StorageUnavailable);
            }
            return catalog is not null && _binding is { } binding && MatchesBound(catalog, binding)
                ? null : new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.StorageUnavailable);
        }
        catch (AmazonServiceException)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.StorageRejected);
        }
        catch (InvalidDataException)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        catch (IOException)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.StorageUnavailable);
        }
    }

    private static string? DescribeListenerFailure(GitHubListenerLaneStatus? status) => status switch
    {
        GitHubListenerLaneStatus.RecoveryRequired => "listener_recovery_required",
        GitHubListenerLaneStatus.Failed => "listener_poll_failed",
        GitHubListenerLaneStatus.OwnershipUnavailable => "listener_ownership_unavailable",
        GitHubListenerLaneStatus.StorageUnavailable => "listener_storage_unavailable",
        GitHubListenerLaneStatus.StorageRejected => "listener_storage_rejected",
        GitHubListenerLaneStatus.RecoveryCredentialRejected => "listener_credential_rejected",
        GitHubListenerLaneStatus.Stopped => "listener_stopped",
        _ => null,
    };

    private void RecordPollFailure() =>
        PublishListenerResult(new GitHubListenerLaneResult(GitHubListenerLaneStatus.Failed,
            Failure: new GitHubOperationException(GitHubOperationError.RequestFailed)));

    private void PublishListenerResult(GitHubListenerLaneResult result)
    {
        if (Interlocked.Exchange(ref _listenerResult, result)?.Status != result.Status)
        {
            ListenerHealthChanged?.Invoke();
        }
    }

    private async Task<bool> CanOpenAsync(SessionRecord? session, CancellationToken cancellationToken)
    {
        if (AdmissionsStopped || _polls.IsCancellationRequested || session is not (null or { Phase: SessionPhase.Retired }) ||
            _binding is not { } binding || await ReadCatalogAsync(allowClosing: false, cancellationToken).ConfigureAwait(false) is not { } catalog ||
            !MatchesBound(catalog, binding) || await _store.ReadSessionAsync(_key, cancellationToken).ConfigureAwait(false) != session)
        {
            return false;
        }
        return !AdmissionsStopped && !_polls.IsCancellationRequested;
    }

    internal async Task<ListenerCapacity> ReadListenerCapacityAsync(CancellationToken cancellationToken)
    {
        if (AdmissionsStopped || _polls.IsCancellationRequested || _profile.State != RunnerProfileState.Active || _binding is not { } binding ||
            await ReadCatalogAsync(allowClosing: false, cancellationToken).ConfigureAwait(false) is not { State: CatalogProfileState.Active } catalog ||
            !MatchesBound(catalog, binding))
        {
            return NoAdmission;
        }
        var capacity = await _store.ReadListenerCapacityAsync(_key, _profile.Capacity.MaxRunners, DemandFreshness, LogFlushGrace, cancellationToken)
            .ConfigureAwait(false);
        return !AdmissionsStopped && !_polls.IsCancellationRequested ? capacity : NoAdmission;
    }

    private async Task<CatalogProfile?> ReadCatalogAsync(bool allowClosing, CancellationToken cancellationToken)
    {
        if (await ReadDurableOwnershipAsync(cancellationToken).ConfigureAwait(false) is not { } owner ||
            await _store.ReadCatalogRolloutAsync(_key.Deployment, cancellationToken).ConfigureAwait(false) is not { Pending: null } head ||
            head.AppliedGeneration != _generation || head.AppliedDigest != _configuration.Sha256 || head.AppliedEpoch != owner.Lease.Epoch)
        {
            return null;
        }
        var catalog = await _store.ReadCatalogProfileAsync(_key, cancellationToken).ConfigureAwait(false);
        bool stateMatches = (_profile.State, catalog?.State) switch
        {
            (RunnerProfileState.Active, CatalogProfileState.Active) => true,
            (RunnerProfileState.Draining, CatalogProfileState.Draining) => true,
            (RunnerProfileState.Draining, CatalogProfileState.ClosureHeld) => allowClosing,
            _ => false,
        };
        return catalog?.AppliedGeneration == _generation && catalog.Binding.ScopeIdentity == _scope &&
            catalog.Binding.RunnerGroup == _runnerGroup && catalog.Binding.ScaleSet == _scaleSet && catalog.Binding.Labels.SequenceEqual(_labels) &&
            (_profile.GitHub.ScaleSetId is null || _profile.GitHub.ScaleSetId == catalog.Binding.ScaleSetId) &&
            stateMatches && ReadLocalOwnership() is { } current && current.Lease.Fence == owner.Lease.Fence ? catalog : null;
    }

    private GitHubRegistrationLease? ReadLocalOwnership() => _ownership.ReadAuthority() is { } authority && authority.Lease.Deployment == _key.Deployment
        ? new GitHubRegistrationLease(authority.Lease, _generation, authority.Clock) : null;

    private async Task<GitHubRegistrationLease?> ReadDurableOwnershipAsync(CancellationToken cancellationToken)
    {
        var read = await _store.ReadLeaseAsync(_key.Deployment, cancellationToken).ConfigureAwait(false);
        return read is { Mode: ControllerLeaseMode.Active } && ReadLocalOwnership() is { } local && local.Lease.Fence == read.Fence
            ? local with { Lease = read } : null;
    }

    private Task<GitHubRegistrationLease?> ReadListenerOwnershipAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadLocalOwnership());
    }

    private GitHubScaleSetBinding? FromCatalog(CatalogProfile catalog) =>
        catalog.Binding is { RunnerGroupId: long group, ScaleSetId: long set } ?
            new GitHubScaleSetBinding(_github.ScopeUrl, _profile.GitHub.RunnerGroup, group, _profile.GitHub.ScaleSet, set, _profile.GitHub.Labels) : null;

    private static bool MatchesBound(CatalogProfile catalog, GitHubScaleSetBinding binding) =>
        catalog.Binding.RunnerGroupId == binding.RunnerGroupId && catalog.Binding.ScaleSetId == binding.ScaleSetId;

    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            if (_disposal is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposal = DisposeResourcesAsync();
            }
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeResourcesAsync()
    {
        try
        {
            await Task.WhenAll(_admissions.CancelAsync(), _polls.CancelAsync()).ConfigureAwait(false);
        }
        finally
        {
            await JoinMutatorsAsync(CancellationToken.None).ConfigureAwait(false);
            _lane?.Dispose();
            _connection?.Dispose();
            _serial.Dispose();
            _admissions.Dispose();
            _polls.Dispose();
        }
    }
}
