using System.Security.Cryptography;
using System.Text;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed record GitHubJitTiming(TimeSpan WriteReserve, TimeSpan SendWindow, TimeSpan ObservationFreshness, TimeSpan OfferLifetime);

public enum GitHubJitStatus
{
    Held = 0,
    OwnershipUnavailable = 1,
    WaitingForHello = 2,
    RecoveryRequired = 3,
    Offered = 4,
    RegisteredWithoutOffer = 5,
    Finished = 6,
}

public sealed record GitHubJitResult(GitHubJitStatus Status, long? RunnerId = null, GitHubOperationException? Failure = null);

public sealed partial class GitHubJitHandoff
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DynamoDbLifecycleStore _store;
    private readonly DynamoDbEffectJournal _journal;
    private readonly TimeProvider _clock;
    private readonly GitHubJitTiming _timing;

    public GitHubJitHandoff(DynamoDbLifecycleStore store, TimeProvider clock, GitHubJitTiming timing)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timing);
        if (timing.WriteReserve <= TimeSpan.Zero || timing.SendWindow <= TimeSpan.Zero ||
            timing.ObservationFreshness <= TimeSpan.Zero || timing.OfferLifetime <= TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentException("Positive bounded handoff windows are required.", nameof(timing));
        }
        _store = store;
        _clock = clock;
        _timing = timing;
        _journal = new DynamoDbEffectJournal(store, clock);
    }

    public async Task<GitHubJitResult> IssueAsync(
        ActiveControllerAuthority authority, JitHandoffIdentity identity, DateTimeOffset providerObservedAt,
        GitHubScaleSetConnection connection, CancellationToken ownershipLost, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(connection);
        var profile = new LifecycleProfile(identity.Deployment, identity.ProfileId);
        if (identity.ScopeIdentity != connection.ScopeIdentity || authority.Lease.Deployment != profile.Deployment)
        {
            return new GitHubJitResult(GitHubJitStatus.Held);
        }

        using var owner = CancellationTokenSource.CreateLinkedTokenSource(ownershipLost, cancellationToken);
        var token = owner.Token;
        var lease = await ReadCurrentAsync(authority.Lease, token).ConfigureAwait(false);
        if (lease is not { Mode: ControllerLeaseMode.Active } || Window(lease, authority.Clock) is null)
        {
            return new GitHubJitResult(GitHubJitStatus.OwnershipUnavailable);
        }
        var handoff = await _store.ReadJitHandoffAsync(profile, identity.AllocationId, token).ConfigureAwait(false);
        if (handoff is not null && handoff.Identity != identity)
        {
            return new GitHubJitResult(GitHubJitStatus.Held);
        }
        var intent = handoff is null ? null : await _journal.ReadAsync(profile, handoff.IntentId, token).ConfigureAwait(false);
        if (handoff is not null && intent is { Entry.State: EffectState.Executing or EffectState.Uncertain })
        {
            return await RecoverActiveAsync(authority.Clock, lease, intent, connection, token).ConfigureAwait(false);
        }
        if (handoff is { State: not JitHandoffState.Staged } || intent is { Entry.State: EffectState.Settled })
        {
            return Describe(handoff);
        }

        var catalog = await _store.ReadCatalogProfileAsync(profile, token).ConfigureAwait(false);
        var executor = await _store.ReadExecutorAsync(profile, identity.AllocationId.ToString("D"), token).ConfigureAwait(false);
        if (!PermitsOffer(catalog, executor, identity, providerObservedAt, _clock.GetUtcNow()))
        {
            return new GitHubJitResult(GitHubJitStatus.Held);
        }
        if (handoff is null)
        {
            string runnerName = "ss-" + JitOfferBinding.GetOfferId(identity)[..48];
            var staged = await _store.StageJitHandoffAsync(lease, catalog!, executor!, identity, runnerName, token).ConfigureAwait(false);
            if (staged.Outcome != LifecycleWriteOutcome.Committed || (handoff = staged.Handoff) is null)
            {
                return new GitHubJitResult(staged.Outcome == LifecycleWriteOutcome.Conflict
                    ? GitHubJitStatus.WaitingForHello : GitHubJitStatus.RecoveryRequired);
            }
            if (handoff.State != JitHandoffState.Staged)
            {
                return Describe(handoff);
            }
        }

        if (intent is null)
        {
            var request = new GitHubJitGenerateEffectRequest(identity.ScopeIdentity, identity.ScaleSetId, identity.AllocationId,
                identity.ResourceArn, identity.Incarnation, identity.AwsUserId, handoff.HelloSha256, handoff.OfferId, handoff.RunnerName);
            var envelope = EffectEnvelope.Create(profile, handoff.IntentId, catalog!.AppliedGeneration, request, _clock.GetUtcNow(), null);
            var result = await _journal.PlanAsync(lease, envelope, Guid.NewGuid(), token).ConfigureAwait(false);
            if (result != LifecycleWriteOutcome.Committed)
            {
                return new GitHubJitResult(GitHubJitStatus.RecoveryRequired);
            }
            intent = await _journal.ReadAsync(profile, handoff.IntentId, token).ConfigureAwait(false);
        }
        if (intent is not { Entry.State: EffectState.Planned } || Window(lease, authority.Clock) is not { } window)
        {
            return new GitHubJitResult(GitHubJitStatus.RecoveryRequired);
        }
        var evidenceDeadline = Min(providerObservedAt + _timing.ObservationFreshness,
            executor!.State.Resource!.ObservedAt + _timing.ObservationFreshness);
        var deadline = Min(window.Deadline, evidenceDeadline);
        if (window.UpperNow >= deadline)
        {
            return new GitHubJitResult(GitHubJitStatus.Held);
        }

        var begun = await _journal.BeginJitAsync(lease, catalog!, executor, handoff, intent, window.UpperNow, deadline, _timing.WriteReserve, token)
            .ConfigureAwait(false);
        if (begun.Permission is not { } permission)
        {
            return new GitHubJitResult(GitHubJitStatus.RecoveryRequired);
        }
        bool published = false;
        try
        {
            var result = await GenerateAsync(authority.Clock, lease, catalog!, executor, handoff, permission, connection, token)
                .ConfigureAwait(false);
            published = result.Status == GitHubJitStatus.Offered;
            return result;
        }
        finally
        {
            if (!published)
            {
                await _journal.FinalizeInterruptedRunnerEffectAsync(lease, permission, _timing.WriteReserve).ConfigureAwait(false);
            }
        }
    }

    private async Task<GitHubJitResult> GenerateAsync(
        ControllerClockEvidence clock, ControllerLease lease, CatalogProfile catalog, ExecutorSnapshot executor,
        JitHandoffSnapshot handoff, EffectSendAuthorization permission, GitHubScaleSetConnection connection, CancellationToken cancellationToken)
    {
        var profile = catalog.Profile;
        var live = await ReadCurrentAsync(lease, cancellationToken).ConfigureAwait(false);
        var attempt = await _journal.ReadAsync(profile, handoff.IntentId, cancellationToken).ConfigureAwait(false);
        if (live is null || !MatchesAttempt(attempt, permission))
        {
            return new GitHubJitResult(GitHubJitStatus.RecoveryRequired);
        }
        if (Window(live, clock) is null ||
            await _store.ReadCatalogProfileAsync(profile, cancellationToken).ConfigureAwait(false) is not { } current ||
            current.TransitionId != catalog.TransitionId || current.AppliedGeneration != catalog.AppliedGeneration ||
            await _store.ReadExecutorAsync(profile, executor.Binding.AllocationId, cancellationToken).ConfigureAwait(false) != executor ||
            await _store.ReadJitHandoffAsync(profile, handoff.Identity.AllocationId, cancellationToken).ConfigureAwait(false) is not
                { State: JitHandoffState.Staged } staged || staged.TransitionId != handoff.TransitionId)
        {
            return new GitHubJitResult(GitHubJitStatus.RecoveryRequired);
        }

        GitHubOperationException? failure = null;
        try
        {
            var result = await connection.GenerateJitAsync(permission, cancellationToken).ConfigureAwait(false);
            var offer = Seal(result.EncodedJitConfig, handoff);
            var published = await _store.PublishJitHandoffAsync(live, current, staged, attempt!, result.Runner!.Id, offer, cancellationToken)
                .ConfigureAwait(false);
            if (published is { Outcome: LifecycleWriteOutcome.Committed, Handoff.State: JitHandoffState.Offered })
            {
                return await SettlePublishedAsync(clock, live, attempt!, published.Handoff, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (GitHubOperationException error)
        {
            failure = error;
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            // Retain the exact attempt if a response cannot be sealed.
        }
        return new GitHubJitResult(GitHubJitStatus.RecoveryRequired, Failure: failure);
    }

    private JitOfferEnvelope Seal(string encoded, JitHandoffSnapshot handoff)
    {
        var plaintext = StrictUtf8.GetBytes(encoded);
        try
        {
            var binding = JitOfferBinding.Create(handoff.Identity, handoff.Hello.BootNonce, _clock.GetUtcNow() + _timing.OfferLifetime);
            return JitOfferCryptography.Seal(binding, handoff.Hello, plaintext, _clock);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private bool PermitsOffer(
        CatalogProfile? catalog, ExecutorSnapshot? executor, JitHandoffIdentity identity, DateTimeOffset observedAt, DateTimeOffset now) =>
        catalog is { State: CatalogProfileState.Active or CatalogProfileState.Draining, Binding.BootstrapProtocol: "v1" } &&
        catalog.Binding.ScopeIdentity == identity.ScopeIdentity && catalog.Binding.ScaleSetId == identity.ScaleSetId &&
        executor is { State: { HasConflict: false, RunnerId: null, Resource: { State: ExecutorResourceState.Running } resource } } &&
        executor.Binding.AllocationId == identity.AllocationId.ToString("D") && executor.Binding.Incarnation == identity.Incarnation &&
        executor.Binding.ResourceArn == identity.ResourceArn &&
        Fresh(observedAt, now) && Fresh(resource.ObservedAt, now);

    private bool Fresh(DateTimeOffset observedAt, DateTimeOffset now) =>
        observedAt != default && observedAt <= now && now - observedAt <= _timing.ObservationFreshness;

    private async Task<ControllerLease?> ReadCurrentAsync(ControllerLease expected, CancellationToken cancellationToken)
    {
        var current = await _store.ReadLeaseAsync(expected.Deployment, cancellationToken).ConfigureAwait(false);
        return current is not null && current.Fence == expected.Fence && current.Mode == expected.Mode ? current : null;
    }

    private (DateTimeOffset UpperNow, DateTimeOffset Deadline)? Window(ControllerLease lease, ControllerClockEvidence clock)
    {
        var now = _clock.GetUtcNow();
        if (!clock.TryGetUpperBound(now, lease.ValidUntil, _timing.WriteReserve, out var upper))
        {
            return null;
        }
        var deadline = Min(lease.ValidUntil - _timing.WriteReserve, clock.ValidUntil);
        var remaining = deadline - upper;
        return remaining > TimeSpan.Zero
            ? (upper, upper + (remaining < _timing.SendWindow ? remaining : _timing.SendWindow))
            : null;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static bool MatchesAttempt(EffectJournalSnapshot? attempt, EffectSendAuthorization permission) =>
        attempt is { Entry.State: EffectState.Executing } && attempt.Revision == permission.Revision &&
        attempt.Entry.Attempt == permission.Attempt && attempt.Entry.Envelope.Matches(permission.Envelope);

    private static GitHubJitResult Describe(JitHandoffSnapshot? handoff) => new(handoff?.State switch
    {
        JitHandoffState.Offered => GitHubJitStatus.Offered,
        JitHandoffState.RegisteredWithoutOffer => GitHubJitStatus.RegisteredWithoutOffer,
        JitHandoffState.ResourceTerminated or JitHandoffState.Revoked => GitHubJitStatus.Finished,
        _ => GitHubJitStatus.RecoveryRequired,
    }, handoff?.RunnerId);
}
