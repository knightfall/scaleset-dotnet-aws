using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Ec2;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed class Ec2RunnerBackend(
    RuntimeConfiguration configuration, RunnerProfile profile, LifecycleProfile key, Ec2ProfileInspector inspector,
    Ec2EffectProvider provider, DynamoDbLifecycleStore store, DynamoDbEffectJournal journal, DynamoDbEffectDispatcher dispatcher,
    TimeProvider clock, EffectDispatcherTiming timing) : IRunnerBackend
{
    private readonly Ec2ProvisionerConfiguration _provisioner = profile.Provisioner as Ec2ProvisionerConfiguration
        ?? throw new ArgumentException("The EC2 backend requires an EC2 profile.", nameof(profile));

    private ActiveEffectCursor? _pendingCursor;
    private bool _sawPending;
    private string? _rejectedCursor;
    private long _rejectedGeneration;
    private long _placementSequence;

    public IReconciliationEffectProvider Effects => provider;

    public async Task<RunnerBackendInventory> ReadAsync(ControllerLease lease, CancellationToken cancellationToken)
    {
        if (lease.Deployment != key.Deployment || lease.Mode == ControllerLeaseMode.Released)
        {
            return new RunnerBackendInventory(clock.GetUtcNow(), false, null, null, []);
        }

        var inventory = await inspector.ReadInventoryAsync(configuration, profile.Id, cancellationToken).ConfigureAwait(false);
        var preflight = inventory.IsComplete
            ? await inspector.ResolvePreflightAsync(configuration, profile.Id, cancellationToken).ConfigureAwait(false) : null;
        var projected = ImmutableArray.CreateBuilder<RunnerBackendResource>(inventory.Instances.Length);
        foreach (var entry in inventory.Instances)
        {
            var resource = ProjectResource(entry, inventory.ObservedAt, preflight?.Template);
            if (resource.Binding is null && entry is { IsAuthoritative: true, ReportedState: Ec2InstanceState.Terminated,
                    OriginalLaunchTime: null } &&
                await store.ReadExecutorBindingForResourceAsync(key.Deployment, entry.ResourceArn, cancellationToken).ConfigureAwait(false) is
                    { Provider: Ec2ExecutorIdentity identity } retained && retained.Profile == key &&
                identity.LaunchTemplateId == _provisioner.LaunchTemplateId &&
                entry.CorroboratesTerminal(retained, configuration.Aws.Account, configuration.Aws.Region, inventory.ObservedAt))
            {
                var terminal = new ExecutorResourceObservation(ExecutorResourceState.IrreversiblyTerminated, inventory.ObservedAt,
                    identity.InstanceLaunchTime, Guid.NewGuid());
                resource = new RunnerBackendResource(entry.ResourceArn, retained, terminal, null);
            }
            projected.Add(resource);
        }
        var resources = projected.MoveToImmutable();
        if (!inventory.IsComplete)
        {
            return new RunnerBackendInventory(inventory.ObservedAt, false, null, null, resources);
        }

        int? headroom = preflight is { IsReady: true, Template.Subnets: { IsDefaultOrEmpty: false } subnets } &&
            subnets.All(static subnet => subnet.AvailableIpAddresses is >= 0)
            ? (int)Math.Min(int.MaxValue, subnets.Sum(static subnet => (long)subnet.AvailableIpAddresses!.Value)) : null;
        var nonterminal = resources.Where(static item => item.Observation?.State != ExecutorResourceState.IrreversiblyTerminated).ToArray();
        int? drift = preflight?.Template is { } resolved && nonterminal.All(static item => item.Binding is not null)
            ? nonterminal.Count(item => item.Binding?.Provider is Ec2ExecutorIdentity identity && identity.LaunchTemplateVersion != resolved.VersionNumber)
            : null;
        return new RunnerBackendInventory(inventory.ObservedAt, true, headroom, drift, resources);
    }

    public async Task ReconcileLaunchesAsync(ActiveControllerAuthority authority, CatalogProfile catalog, bool allowPreparedLaunches,
        bool allowNewLaunch, CancellationToken ownershipLost, CancellationToken cancellationToken)
    {
        if (profile.State != RunnerProfileState.Active || catalog.Profile != key || catalog.State != CatalogProfileState.Active ||
            !allowPreparedLaunches || authority.Lease.Mode != ControllerLeaseMode.Active || ownershipLost.IsCancellationRequested)
        {
            return;
        }

        using var owned = CancellationTokenSource.CreateLinkedTokenSource(ownershipLost, cancellationToken);
        var token = owned.Token;
        var pending = await store.ReadPendingEc2LaunchesAsync(key, authority.Lease.Fence, _pendingCursor, token).ConfigureAwait(false);
        if (pending is null)
        {
            _pendingCursor = null;
            _sawPending = false;
            return;
        }

        bool hadPending = _sawPending || !pending.Launches.IsEmpty;
        _pendingCursor = pending.Continuation;
        _sawPending = _pendingCursor is not null && hadPending;
        if (pending.Launches.FirstOrDefault(static item => item.State == EffectState.Planned) is { } prepared)
        {
            if (await IsCurrentCatalogAsync(catalog, token).ConfigureAwait(false))
            {
                _ = await DispatchPreparedAsync(authority, prepared.AllocationId, ownershipLost, token).ConfigureAwait(false);
            }
            return;
        }
        if (_pendingCursor is not null || hadPending || !allowNewLaunch)
        {
            return;
        }

        if (_rejectedGeneration != catalog.AppliedGeneration)
        {
            _rejectedGeneration = catalog.AppliedGeneration;
            _rejectedCursor = null;
        }
        var rejected = await store.ReadRejectedEc2LaunchesAsync(key, catalog.AppliedGeneration, _rejectedCursor, token).ConfigureAwait(false);
        if (rejected is null)
        {
            _rejectedCursor = null;
            return;
        }

        var previous = rejected.Launches.FirstOrDefault();
        _rejectedCursor = previous is null ? rejected.Continuation : LifecycleKey.Ec2LaunchLineage(key, previous.AllocationId).SortKey;
        Guid allocation = previous?.AllocationId ?? Guid.NewGuid();
        int ordinal = previous is null ? 0 : previous.AttemptOrdinal + 1;
        var now = clock.GetUtcNow();
        var recent = rejected.Launches.Where(launch => launch.ObservedAt <= now && now - launch.ObservedAt <= timing.ObservationFreshness)
            .GroupBy(static launch => launch.AvailabilityZone, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        long sequence = _placementSequence;
        _placementSequence = sequence == long.MaxValue ? 0 : sequence + 1;

        var result = await dispatcher.AdvanceEc2LaunchAsync(authority.Lease, authority.Clock, key, allocation,
            (attempt, excluded, ct) => SelectPlacementAsync(catalog, allocation, ordinal, attempt, sequence, excluded, recent, ct),
            provider, ownershipLost, token).ConfigureAwait(false);
        if (result.Status == Ec2LaunchAdvanceStatus.Planned)
        {
            _ = await DispatchPreparedAsync(authority, allocation, ownershipLost, token).ConfigureAwait(false);
        }
    }

    public async Task CleanupAsync(ActiveControllerAuthority authority, CatalogProfile catalog, ExecutorSnapshot executor,
        CancellationToken ownershipLost, CancellationToken cancellationToken)
    {
        var binding = executor.Binding;
        bool terminal = executor.State is { HasConflict: false, Resource.State: ExecutorResourceState.IrreversiblyTerminated };
        if (catalog.Profile != key || catalog.State is not (CatalogProfileState.Active or CatalogProfileState.Draining) ||
            authority.Lease.Mode != ControllerLeaseMode.Active || ownershipLost.IsCancellationRequested ||
            binding.Profile != key || binding.Provider is not Ec2ExecutorIdentity identity || identity.LaunchTemplateId != _provisioner.LaunchTemplateId ||
            (!terminal && !ExecutorClassifier.Classify(executor.State, clock.GetUtcNow(), timing.ObservationFreshness,
                timing.LogFlushGrace).CompletedCleanupCandidate))
        {
            return;
        }

        using var owned = CancellationTokenSource.CreateLinkedTokenSource(ownershipLost, cancellationToken);
        var token = owned.Token;
        if (!await IsCurrentCatalogAsync(catalog, token).ConfigureAwait(false) ||
            await store.ReadExecutorAsync(key, binding.AllocationId, token).ConfigureAwait(false) != executor)
        {
            return;
        }
        if (!terminal && !await HasQualifiedExitAsync(executor, identity, token).ConfigureAwait(false))
        {
            return;
        }

        var request = new Ec2TerminateEffectRequest(Guid.ParseExact(binding.AllocationId, "D"), binding.ResourceArn,
            identity.InstanceLaunchTime, identity.LaunchTemplateId, identity.LaunchTemplateVersion);
        string intentId = EffectIntentIdentity.ForAllocation(request)!;
        var existing = await journal.ReadAsync(key, intentId, token).ConfigureAwait(false);
        if (terminal)
        {
            if (existing is { Entry.State: EffectState.Planned } && existing.Entry.Envelope.Request == request)
            {
                var planned = existing.Entry.Envelope;
                var evidence = new EffectEvidence(EffectResolutionKind.CanceledBeforeSend, planned.Sha256, planned.Target, clock.GetUtcNow());
                _ = await journal.ResolveAsync(authority.Lease, existing, Guid.NewGuid(), evidence, token).ConfigureAwait(false);
            }
            return;
        }
        if (existing is null)
        {
            var envelope = EffectEnvelope.Create(key, intentId, catalog.AppliedGeneration, request, clock.GetUtcNow(), null);
            if (await journal.PlanAsync(authority.Lease, envelope, Guid.NewGuid(), token).ConfigureAwait(false) != LifecycleWriteOutcome.Committed)
            {
                return;
            }
        }
        else if (existing.Entry.State != EffectState.Planned || existing.Entry.Envelope.Request != request)
        {
            return;
        }

        _ = await dispatcher.DispatchPlannedAsync(authority.Lease, authority.Clock, key, intentId, provider, ownershipLost, token).ConfigureAwait(false);
    }

    private Task<Ec2LaunchAdvanceResult> DispatchPreparedAsync(
        ActiveControllerAuthority authority, Guid allocation, CancellationToken ownershipLost, CancellationToken cancellationToken) =>
        dispatcher.AdvanceEc2LaunchAsync(authority.Lease, authority.Clock, key, allocation,
            static (_, _, _) => Task.FromResult<Ec2LaunchEffectRequest?>(null), provider, ownershipLost, cancellationToken);

    private async Task<Ec2LaunchEffectRequest?> SelectPlacementAsync(CatalogProfile catalog, Guid allocation,
        int expectedOrdinal, int ordinal, long sequence, IReadOnlySet<string> excluded, IReadOnlyDictionary<string, int> recent,
        CancellationToken cancellationToken)
    {
        if (ordinal != expectedOrdinal)
        {
            return null;
        }
        var request = await provider.CreateLaunchRequestAsync(allocation, ordinal, sequence, excluded, recent, cancellationToken).ConfigureAwait(false);
        return request is not null && await IsCurrentCatalogAsync(catalog, cancellationToken).ConfigureAwait(false) ? request : null;
    }

    private async Task<bool> IsCurrentCatalogAsync(CatalogProfile expected, CancellationToken cancellationToken)
    {
        var current = await store.ReadCatalogProfileAsync(key, cancellationToken).ConfigureAwait(false);
        return current is not null && current.State == expected.State && current.AppliedGeneration == expected.AppliedGeneration &&
            current.Revision == expected.Revision && current.TransitionId == expected.TransitionId;
    }

    private async Task<bool> HasQualifiedExitAsync(ExecutorSnapshot executor, Ec2ExecutorIdentity identity, CancellationToken cancellationToken)
    {
        if (_provisioner.StatusQualification is not { } qualification || executor.State.Process is not { State: ExecutorProcessState.Exited } exit ||
            await store.ReadJitHandoffAsync(key, Guid.ParseExact(executor.Binding.AllocationId, "D"), cancellationToken).ConfigureAwait(false) is not { } handoff ||
            handoff.Identity.ResourceArn != executor.Binding.ResourceArn || handoff.Identity.Incarnation != executor.Binding.Incarnation ||
            !handoff.Identity.AwsUserId.StartsWith(qualification.RoleId + ":", StringComparison.Ordinal) ||
            await store.ReadEc2BootstrapStatusAsync(handoff, qualification.EvidenceSha256, cancellationToken).ConfigureAwait(false)
                is not { Exited: true } confirmed || confirmed.RunnerId != exit.RunnerId ||
            confirmed.ObservedAt != exit.ObservedAt)
        {
            return false;
        }
        var preflight = await inspector.ResolvePreflightAsync(configuration, profile.Id, cancellationToken).ConfigureAwait(false);
        return preflight is { IsReady: true, Template: { } template } && template.VersionNumber == identity.LaunchTemplateVersion;
    }

    private RunnerBackendResource ProjectResource(Ec2InventoryEntry entry, DateTimeOffset observedAt, Ec2ResolvedTemplate? template)
    {
        if (!entry.IsAuthoritative || entry.AllocationId is not Guid allocation || entry.Incarnation is not Guid incarnation ||
            entry.LaunchTemplateId != _provisioner.LaunchTemplateId || entry.LaunchTemplateVersion is not long version ||
            entry.OriginalLaunchTime is not { } launchTime || launchTime > observedAt)
        {
            return new RunnerBackendResource(entry.ResourceArn, null, null, null);
        }
        var state = entry.State switch
        {
            Ec2InstanceState.Pending => ExecutorResourceState.Starting,
            Ec2InstanceState.Running => ExecutorResourceState.Running,
            Ec2InstanceState.Terminated => ExecutorResourceState.IrreversiblyTerminated,
            _ => ExecutorResourceState.Unknown,
        };
        var binding = new ExecutorBinding(key, allocation.ToString("D"), entry.ResourceArn, incarnation,
            new Ec2ExecutorIdentity(launchTime, _provisioner.LaunchTemplateId, version));
        var observation = new ExecutorResourceObservation(state, observedAt, launchTime, Guid.NewGuid());
        string? credential = entry.InstanceProfileArn == _provisioner.InstanceProfileArn ? entry.InstanceProfileArn : null;
        return new RunnerBackendResource(entry.ResourceArn, binding, observation, credential)
        {
            Ec2StatusQualificationSha256 = _provisioner.StatusQualification is { } qualification && template?.VersionNumber == version &&
                entry.ImageId == qualification.ImageId && credential is not null ? qualification.EvidenceSha256 : null,
        };
    }
}
