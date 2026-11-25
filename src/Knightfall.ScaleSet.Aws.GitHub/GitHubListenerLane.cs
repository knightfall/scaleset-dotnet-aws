using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Client;
using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubListenerLane : IDisposable
{
    private readonly DynamoDbLifecycleStore _store;
    private readonly GitHubListenerProfile _definition;
    private readonly RunnerProfile _profile;
    private readonly TelemetryProfile? _telemetryProfile;
    private readonly SessionBinding _binding;
    private readonly TimeProvider _clock;
    private readonly GitHubListenerTiming _timing;
    private readonly GitHubCredentialCooldown _cooldown;
    private readonly GitHubCredentialRotator? _rotation;
    private readonly SemaphoreSlim _serial = new(1);
    private GitHubScaleSetConnection _connection;
    private IMessageSession? _ownedSession;
    private OwnedSessionFence? _ownedFence;
    private GitHubListenerCheckpointStore? _checkpoint;
    private ScaleSetListenerPump? _pump;
    private KnownSessionResponse? _known;
    private GitHubScaleSetConnection? _validatedCandidate;
    private bool _recoveryRequired;
    private int _stopping;

    internal GitHubListenerLane(
        DynamoDbLifecycleStore store,
        GitHubListenerProfile definition,
        RunnerProfile profile,
        SessionBinding binding,
        TimeProvider clock,
        GitHubListenerTiming timing,
        GitHubCredentialCooldown cooldown,
        GitHubCredentialRotator? rotation)
    {
        _store = store;
        _definition = definition;
        _profile = profile;
        _telemetryProfile = TelemetryProfile.From(profile);
        _binding = binding;
        _clock = clock;
        _timing = timing;
        _cooldown = cooldown;
        _rotation = rotation;
        _connection = definition.Connection;
    }

    public GitHubListenerLaneResult? LastResult { get; private set; }

    public DateTimeOffset? ReadCredentialCooldown() =>
        Volatile.Read(ref _stopping) == 0 && !_definition.OwnershipLost.IsCancellationRequested
            ? _cooldown.Read(_connection.SecretArn) : null;

    public async Task<GitHubListenerLaneResult> StepAsync(CancellationToken stoppingToken)
    {
        await _serial.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var profileTelemetry = TelemetryProfileScope.Enter(_telemetryProfile);
        try
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.Stopped));
            }
            _definition.OwnershipLost.ThrowIfCancellationRequested();
            using var administration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _definition.OwnershipLost);
            if (await EnsureSessionAsync(administration.Token).ConfigureAwait(false) is { } waiting)
            {
                return Report(waiting);
            }

            // The shared supervisor owns backoff across profile lanes with the same credential.
            using var retry = ScaleSetRequestRetry.Configure(static options => options with { MaxRetries = 0 });
            using var telemetry = GitHubListenerDiagnostics.Start("step", _clock, stoppingToken);
            var result = await _pump!.StepAsync(stoppingToken).ConfigureAwait(false);
            telemetry.Complete(result.Status switch
            {
                ScaleSetListenerStepStatus.Initialized => "initialized",
                ScaleSetListenerStepStatus.EmptyPoll => "empty",
                ScaleSetListenerStepStatus.MessageAcknowledged => "acknowledged",
                ScaleSetListenerStepStatus.Deferred => "deferred",
                ScaleSetListenerStepStatus.CheckpointConflict => "conflict",
                _ => "unknown",
            });
            _recoveryRequired = result.Status is ScaleSetListenerStepStatus.RecoveryRequired or ScaleSetListenerStepStatus.CheckpointConflict;
            return Report(new GitHubListenerLaneResult(_recoveryRequired ? GitHubListenerLaneStatus.RecoveryRequired : GitHubListenerLaneStatus.Stepped,
                result, result.RetryAt));
        }
        catch (OperationCanceledException error) when (!stoppingToken.IsCancellationRequested && !_definition.OwnershipLost.IsCancellationRequested)
        {
            return Report(Failed(error));
        }
        catch (Exception error) when (IsLaneFailure(error))
        {
            return Report(Failed(error));
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<GitHubListenerLaneResult> RotateAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var profileTelemetry = TelemetryProfileScope.Enter(_telemetryProfile);
        try
        {
            if (_rotation is null || Volatile.Read(ref _stopping) != 0 || _recoveryRequired || _known is not null)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.Waiting));
            }
            if (_cooldown.Read(_connection.SecretArn) is { } retryAt)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.Waiting, RetryAt: retryAt));
            }

            using var administration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _definition.OwnershipLost);
            var token = administration.Token;
            if (await ReadOwnershipAsync(token).ConfigureAwait(false) is not { } ownership ||
                (_checkpoint is not null && await _checkpoint.ReadAsync(_checkpoint.SessionKey, token).ConfigureAwait(false) is { Pending: not null }) ||
                SendWindow(ownership) is not { } window)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.Waiting));
            }

            using var validationTimeout = new CancellationTokenSource(window.Deadline - window.UpperNow, _clock);
            using var validation = CancellationTokenSource.CreateLinkedTokenSource(token, validationTimeout.Token);
            using var retries = ScaleSetRequestRetry.Configure(static options => options with { MaxRetries = 0 });
            var result = await _rotation.CheckAsync(_definition.Configuration, _profile.GitHub, _definition.Binding, _connection, validation.Token)
                .ConfigureAwait(false);
            if (result.Candidate is not { } candidate)
            {
                if (result.Failure is { RateLimit: not null } rateLimited)
                {
                    _cooldown.Record(_connection.SecretArn, rateLimited);
                }
                return Report(new GitHubListenerLaneResult(result.Status == GitHubCredentialRotationStatus.CandidateRejected
                    ? GitHubListenerLaneStatus.CredentialRejected : GitHubListenerLaneStatus.CredentialUnchanged, Failure: result.Failure));
            }

            try
            {
                if (_ownedSession is not null && await CloseOwnedAsync(ownership, token).ConfigureAwait(false) is { } waiting)
                {
                    return Report(waiting);
                }
                Adopt(candidate);
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.CredentialAdopted));
            }
            finally
            {
                if (_connection != candidate)
                {
                    if (_known is { Authorization.Session.Phase: SessionPhase.Closing })
                    {
                        _validatedCandidate = candidate;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && !_definition.OwnershipLost.IsCancellationRequested)
        {
            return Report(Failed(error));
        }
        catch (Exception error) when (IsLaneFailure(error))
        {
            return Report(Failed(error));
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<GitHubListenerLaneResult> StopAsync(CancellationToken cleanupToken)
    {
        Interlocked.Exchange(ref _stopping, 1);
        await _serial.WaitAsync(cleanupToken).ConfigureAwait(false);
        using var profileTelemetry = TelemetryProfileScope.Enter(_telemetryProfile);
        try
        {
            if (_definition.OwnershipLost.IsCancellationRequested)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable));
            }
            using var administration = CancellationTokenSource.CreateLinkedTokenSource(cleanupToken, _definition.OwnershipLost);
            var token = administration.Token;
            if (_known is not null && !await ConfirmKnownAsync(token).ConfigureAwait(false))
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired));
            }
            if (_ownedSession is null)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.Stopped));
            }
            if (await ReadOwnershipAsync(token).ConfigureAwait(false) is not { } ownership)
            {
                return Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable));
            }

            return Report(await CloseOwnedAsync(ownership, token).ConfigureAwait(false) ?? new GitHubListenerLaneResult(GitHubListenerLaneStatus.Stopped));
        }
        catch (OperationCanceledException error) when (!cleanupToken.IsCancellationRequested && !_definition.OwnershipLost.IsCancellationRequested)
        {
            return Report(Failed(error));
        }
        catch (Exception error) when (IsLaneFailure(error))
        {
            return Report(Failed(error));
        }
        finally
        {
            _serial.Release();
        }
    }

    internal async Task RunAsync(TimeSpan retryDelay, TimeSpan rotationInterval, TimeSpan cleanupBudget, CancellationToken stoppingToken)
    {
        var nextRotation = _clock.GetUtcNow() + rotationInterval;
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _definition.OwnershipLost);
        try
        {
            while (Volatile.Read(ref _stopping) == 0 && !stoppingToken.IsCancellationRequested && !_definition.OwnershipLost.IsCancellationRequested)
            {
                if (_rotation is not null && _clock.GetUtcNow() >= nextRotation)
                {
                    _ = await RotateAsync(stoppingToken).ConfigureAwait(false);
                    nextRotation = _clock.GetUtcNow() + rotationInterval;
                }
                var result = await StepAsync(stoppingToken).ConfigureAwait(false);
                if (result.Status == GitHubListenerLaneStatus.StorageRejected)
                {
                    return;
                }
                if (result.Step?.Status is not (ScaleSetListenerStepStatus.MessageAcknowledged or ScaleSetListenerStepStatus.Initialized))
                {
                    await Task.Delay(retryDelay, _clock, delayCancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _definition.OwnershipLost.IsCancellationRequested)
        {
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(cleanupBudget, _clock);
            try
            {
                _ = await StopAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested || _definition.OwnershipLost.IsCancellationRequested)
            {
                _ = Report(new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired));
            }
        }
    }

    public void Dispose()
    {
        _validatedCandidate?.Dispose();
        _connection.Dispose();
        _serial.Dispose();
    }

    private async Task<GitHubListenerLaneResult?> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_known is not null && !await ConfirmKnownAsync(cancellationToken).ConfigureAwait(false))
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        if (_recoveryRequired)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        if (_pump is not null)
        {
            return null;
        }
        if (_cooldown.Read(_connection.SecretArn) is { } retryAt)
        {
            GitHubListenerDiagnostics.Count("session-open", "deferred", "cooldown");
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.Waiting, RetryAt: retryAt);
        }
        if (await ReadOwnershipAsync(cancellationToken).ConfigureAwait(false) is not { } ownership)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable);
        }

        var durable = await _store.ReadSessionAsync(_binding.Profile, cancellationToken).ConfigureAwait(false);
        if (durable is not null && durable.Binding != _binding)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        if (durable is { Phase: SessionPhase.OpenIntent or SessionPhase.Closing })
        {
            using var telemetry = GitHubListenerDiagnostics.Start("session-quarantine", _clock, cancellationToken);
            var change = await _store.QuarantineSessionAsync(durable, ownership.Lease.Fence, cancellationToken).ConfigureAwait(false);
            telemetry.Complete(GitHubListenerDiagnostics.WriteOutcome(change.Outcome));
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        if (durable is { Phase: SessionPhase.Quarantined })
        {
            GitHubListenerDiagnostics.Count("session-quarantine", "deferred", "held");
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        if (durable is { Phase: SessionPhase.Active })
        {
            if (durable.Owner.Epoch >= ownership.Lease.Epoch)
            {
                return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
            }
            var retirement = await BeginRetirementAsync(ownership, durable, null, cancellationToken).ConfigureAwait(false);
            if (retirement is not null)
            {
                return retirement;
            }
            durable = await _store.ReadSessionAsync(_binding.Profile, cancellationToken).ConfigureAwait(false);
            if (durable is not { Phase: SessionPhase.Retired })
            {
                return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
            }
        }

        var effects = await _store.ReadEffectIndexAsync(_binding.Profile, cancellationToken).ConfigureAwait(false);
        if (effects is not { State: EffectIndexState.Ready, UnfinishedCount: 0 } ||
            effects.OwnerEpoch != ownership.Lease.Epoch || effects.WriterProtocol != ownership.Lease.WriterProtocol ||
            !await ListenerDeliveryLedger.HasCoherentAdmissionAsync(_store, _binding.Profile, cancellationToken).ConfigureAwait(false) ||
            !await _definition.CanOpen(durable, cancellationToken).ConfigureAwait(false))
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.Waiting);
        }
        if (SendWindow(ownership) is not { } window)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable);
        }

        string ownerName = $"controller-{Guid.NewGuid():N}";
        var open = await _store.BeginSessionOpenAsync(_binding, ownership.Lease.Fence, durable, ownerName,
            window.UpperNow, window.Deadline, cancellationToken).ConfigureAwait(false);
        if (open.Permission is not { } permission)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        var session = await _connection.OpenSessionAsync(permission, cancellationToken).ConfigureAwait(false);
        _known = new KnownSessionResponse(permission, session);
        return await ConfirmKnownAsync(cancellationToken).ConfigureAwait(false) && !_recoveryRequired
            ? null : new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
    }

    private async Task<GitHubListenerLaneResult?> CloseOwnedAsync(GitHubRegistrationLease ownership, CancellationToken cancellationToken)
    {
        var durable = await _store.ReadSessionAsync(_binding.Profile, cancellationToken).ConfigureAwait(false);
        if (_ownedSession is not { } session || _ownedFence is not { } fence || durable is null || durable.Fence != fence ||
            durable.Owner != ownership.Lease.Fence)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable);
        }
        return await BeginRetirementAsync(ownership, durable, session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubListenerLaneResult?> BeginRetirementAsync(
        GitHubRegistrationLease ownership, SessionRecord durable, IMessageSession? owned, CancellationToken cancellationToken)
    {
        if (SendWindow(ownership) is not { } window)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.OwnershipUnavailable);
        }
        var kind = owned is null ? SessionCloseKind.FencedRetirement : SessionCloseKind.OwnedClient;
        var close = await _store.BeginSessionCloseAsync(durable, ownership.Lease.Fence, kind, window.UpperNow, window.Deadline,
            cancellationToken).ConfigureAwait(false);
        if (close.Permission is not { } permission)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        ClearPump();
        await _connection.RetireSessionAsync(permission, owned, cancellationToken).ConfigureAwait(false);
        _known = new KnownSessionResponse(permission, null);
        return await ConfirmKnownAsync(cancellationToken).ConfigureAwait(false)
            ? null : new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
    }

    private async Task<bool> ConfirmKnownAsync(CancellationToken cancellationToken)
    {
        var known = _known!;
        var achieved = known.OpenedSession is { } opened
            ? await _store.ConfirmSessionOpenedAsync(known.Authorization, opened.Session.SessionId, cancellationToken).ConfigureAwait(false)
            : await _store.ConfirmSessionRetiredAsync(known.Authorization, cancellationToken).ConfigureAwait(false);
        if (achieved.Outcome != LifecycleWriteOutcome.Committed || achieved.Session is not { } session)
        {
            return false;
        }
        if (known.OpenedSession is { } confirmed && session.Fence is { } fence)
        {
            Attach(confirmed, fence);
        }
        else if (_validatedCandidate is { } candidate)
        {
            Adopt(candidate);
        }
        _known = null;
        return true;
    }

    private void Attach(IMessageSession session, OwnedSessionFence fence)
    {
        _ownedSession = session;
        _ownedFence = fence;
        if (session.Session.Statistics is null)
        {
            _recoveryRequired = true;
            return;
        }
        _checkpoint = new GitHubListenerCheckpointStore(_store, fence, _clock);
        var handler = new GitHubListenerDeliveryHandler(_store, fence, session, ReadCapacityAsync, _clock,
            releaseOnAssignment: false, runnerConnection: _connection);
        var execution = new GitHubListenerExecutionPolicy(_store, fence, _checkpoint.SessionKey, _cooldown, _connection.SecretArn,
            ReadOwnershipAsync, token => ReadPollCapacityAsync(handler, token), _clock, _timing.WriteReserve, _definition.OwnershipLost);
        var options = new ScaleSetListenerPumpOptions(_checkpoint.SessionKey, _definition.OwnershipLost);
        _pump = new ScaleSetListenerPump(new ObservedMessageSession(session, _clock), options, _checkpoint, handler, execution, _clock);
    }

    private void ClearPump()
    {
        _ownedSession = null;
        _ownedFence = null;
        _checkpoint = null;
        _pump = null;
    }

    private void Adopt(GitHubScaleSetConnection candidate)
    {
        _connection.Dispose();
        _connection = candidate;
        _validatedCandidate = null;
    }

    private async Task<ListenerCapacity> ReadCapacityAsync(CancellationToken cancellationToken)
    {
        if (_profile.State == RunnerProfileState.Draining)
        {
            return new ListenerCapacity(0, 0);
        }
        var capacity = await _definition.ReadCapacity(cancellationToken).ConfigureAwait(false);
        return capacity with { Maximum = Math.Min(capacity.Maximum, _profile.Capacity.MaxRunners) };
    }

    private async Task<GitHubRegistrationLease?> ReadOwnershipAsync(CancellationToken cancellationToken)
    {
        var upper = await _definition.ReadOwnership(cancellationToken).ConfigureAwait(false);
        if (upper is not { Lease.Mode: ControllerLeaseMode.Active } || upper.Lease.Fence != _definition.Lease)
        {
            return null;
        }
        var read = await _store.ReadLeaseAsync(_binding.Profile.Deployment, cancellationToken).ConfigureAwait(false);
        return read is { Mode: ControllerLeaseMode.Active } && read.Fence == _definition.Lease
            ? upper with { Lease = read } : null;
    }

    private (DateTimeOffset UpperNow, DateTimeOffset Deadline)? SendWindow(GitHubRegistrationLease ownership)
    {
        var now = _clock.GetUtcNow();
        var remaining = ListenerTime.Remaining(ownership, now, _timing.WriteReserve);
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }
        var upperNow = now + ownership.Clock.MaximumAbsoluteSkew;
        return (upperNow, upperNow + (remaining < _timing.SessionSendWindow ? remaining : _timing.SessionSendWindow));
    }

    private GitHubListenerLaneResult Report(GitHubListenerLaneResult result)
    {
        LastResult = result;
        if (result.Status == GitHubListenerLaneStatus.RecoveryRequired)
        {
            GitHubListenerDiagnostics.Count("supervise", "unknown", "recovery_required");
        }
        return result;
    }

    private GitHubListenerLaneResult Failed(Exception error)
    {
        if (error is InvalidDataException)
        {
            _recoveryRequired = true;
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.RecoveryRequired);
        }
        if (error is IOException)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.StorageUnavailable);
        }
        if (error is AmazonServiceException)
        {
            return new GitHubListenerLaneResult(GitHubListenerLaneStatus.StorageRejected);
        }
        var safe = GitHubOperationException.From(error);
        _cooldown.Record(_connection.SecretArn, safe);
        return new GitHubListenerLaneResult(GitHubListenerLaneStatus.Failed, RetryAt: _cooldown.Read(_connection.SecretArn), Failure: safe);
    }

    private static bool IsLaneFailure(Exception error) =>
        error is GitHubOperationException or ScaleSetApiException or IOException or InvalidDataException or TimeoutException or AmazonServiceException;

    private sealed record KnownSessionResponse(SessionSendAuthorization Authorization, IMessageSession? OpenedSession);
}
