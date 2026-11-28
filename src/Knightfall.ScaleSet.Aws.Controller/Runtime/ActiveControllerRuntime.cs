using System.Collections.Concurrent;
using System.Collections.Immutable;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Controller.Health;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal static class ActiveControllerRuntime
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProfilePassBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(30);
    private static readonly EffectDispatcherTiming EffectTiming = new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2),
        TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));

    internal static async Task RunAsync(ControllerRunContext context, HttpClient? githubHttpClient = null)
    {
        var clock = TimeProvider.System;
        var log = new ControllerRuntimeLog(context.Telemetry.Logging.CreateLogger(typeof(ActiveControllerRuntime).FullName!), clock);
        var configuration = context.Configuration;
        var configured = configuration.Profiles.Where(profile => profile.State != RunnerProfileState.Disabled).ToArray();
        var emptyHealth = configured.Select(profile => new ControllerProfileHealth(profile.Id, Ready: false)).ToImmutableArray();
        context.PublishHealth(new ControllerHealthSnapshot(true, true, true, false, emptyHealth));

        using var metadata = new EcsControllerMetadata(configuration, clock,
            Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4"), allowLoopback: context.AwsLoopbackEndpoint is not null);
        // Verify the local task and clock before constructing any active service clients.
        var observation = await WaitForTaskAsync(metadata, clock, context.Stopping).ConfigureAwait(false);
        using var clients = await ConnectAsync(configuration.Aws, context.AwsLoopbackEndpoint, clock, log, context.Stopping).ConfigureAwait(false);
        var store = new DynamoDbLifecycleStore(clients.DynamoDb(), configuration.TrackingTable.Name, clock);
        var ecs = clients.Ecs();
        IControllerTaskFenceObserver tasks = new EcsControllerTaskFenceObserver(ecs, metadata.ClusterArn);
        if (configuration.Controller.PredecessorCapture is { } capture)
        {
            tasks = new CapturedEcsControllerTaskFenceObserver(tasks, clients.ControllerCapture(), configuration.Aws,
                metadata.ClusterArn, capture, clock, context.Telemetry.Logging.CreateLogger<CapturedEcsControllerTaskFenceObserver>());
        }
        var adapter = new ControllerAuthorityAdapter(configuration, store, tasks, clock, LeaseDuration, EffectTiming.WriteReserve);
        var journal = new DynamoDbEffectJournal(store, clock);
        var dispatcher = new DynamoDbEffectDispatcher(store, clock, EffectTiming);
        var deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration);
        var ec2Effects = new Dictionary<string, Ec2EffectProvider>(StringComparer.Ordinal);
        var ec2Inspector = configured.Any(profile => profile.Provisioner.Type == RunnerProvisionerType.Ec2) ? clients.Ec2Inventory(clock) : null;
        var fargateKeys = configured.Where(profile => profile.Provisioner.Type == RunnerProvisionerType.Fargate)
            .Select(profile => new LifecycleProfile(deployment, profile.Id)).ToArray();
        var iam = clients.Iam();
        var fargateInspector = fargateKeys.Length != 0
            ? new FargateProfileInspector(ecs, iam, new FargateNetworkReader(clients.Ec2Network()), clock) : null;
        var fargateEvidence = fargateInspector is null ? null : FargateRuntimeEvidence.Load(configuration,
            Environment.GetEnvironmentVariable(FargateRuntimeEvidence.EnvironmentVariable));
        var fargateQuota = fargateEvidence is null ? null : new FargateQuotaReader(configuration.Aws, fargateEvidence,
            fargateEvidence.QuotaSource is null ? null : clients.S3(), clock);
        var fargateInventory = new ConcurrentDictionary<string, FargateInventorySnapshot>(StringComparer.Ordinal);
        var backends = new Dictionary<LifecycleProfile, IRunnerBackend>();
        foreach (var profile in configured)
        {
            var key = new LifecycleProfile(deployment, profile.Id);
            if (profile.Provisioner.Type == RunnerProvisionerType.Ec2)
            {
                var provider = clients.Ec2Effects(configuration, key, clock);
                ec2Effects.Add(profile.Id, provider);
                backends.Add(key, new Ec2RunnerBackend(configuration, profile, key, ec2Inspector!, provider,
                    store, journal, dispatcher, clock, EffectTiming, log));
            }
            else
            {
                backends.Add(key, new FargateRunnerBackend(configuration, profile, key, fargateInspector!, fargateEvidence!,
                    fargateQuota!, fargateKeys, fargateInventory, store, journal, dispatcher, clock, EffectTiming, log));
            }
        }

        var secrets = new GitHubSecretLoader(clients.Secrets());
        var gitHub = new GitHubScaleSetClientFactory(githubHttpClient, timeProvider: clock);
        var registration = new GitHubScaleSetRegistration(store, clock, EffectTiming.WriteReserve, EffectTiming.OperationBudget);
        var handoff = new GitHubJitHandoff(store, clock, new GitHubJitTiming(EffectTiming.WriteReserve, EffectTiming.OperationBudget,
            EffectTiming.ObservationFreshness, TimeSpan.FromMinutes(1)));
        var recovery = new EffectRecoveryAuthority(new ControllerLeaseRecoveryCoordinator(store, tasks, clock, EffectTiming.WriteReserve), []);
        var recoveryWork = new ControllerEffectRecovery(configuration, backends, store, journal, dispatcher, recovery,
            secrets, gitHub, registration, handoff);
        var shutdown = new ControllerShutdownCoordinator(store, dispatcher);
        using var metrics = new ProfileTelemetry(configuration, clock);
        using var owner = await AcquireAsync(adapter, metadata, observation, recoveryWork, clock, log, context.Stopping).ConfigureAwait(false);
        var rotation = new GitHubCredentialRotator(secrets, gitHub, context.Telemetry.Logging.CreateLogger<GitHubCredentialRotator>());
        var cooldown = new GitHubCredentialCooldown(clock, CooldownDuration);
        var supervisor = new GitHubListenerSupervisor(store, clock,
            new GitHubListenerTiming(EffectTiming.WriteReserve, EffectTiming.OperationBudget, CooldownDuration), rotation, cooldown);
        var jit = new ControllerJitCoordinator(store, new AwsBootstrapPrincipalResolver(iam), handoff,
            context.Telemetry.Logging.CreateLogger<ControllerJitCoordinator>());
        var retirement = new ProfileRetirementCoordinator(store, clock,
            new ProfileRetirementTiming(RetryInterval, EffectTiming.ObservationFreshness, EffectTiming.LogFlushGrace, EffectTiming.WriteReserve),
            async (key, token) => await store.ReadProfileHandoffObservationAsync(key, token).ConfigureAwait(false));
        var profiles = configured.Select(profile =>
        {
            var key = new LifecycleProfile(deployment, profile.Id);
            var session = new ControllerProfileSession(configuration, profile, context.Generation, owner, store,
                secrets, gitHub, registration, supervisor, rotation, cooldown, clock, context.Stopping);
            return new ControllerProfileReconciler(configuration, profile, key, context.Generation, backends[key], session,
                owner, store, dispatcher, jit, handoff, retirement, metrics, clock, EffectTiming,
                context.Telemetry.Logging.CreateLogger<ControllerProfileReconciler>());
        }).ToArray();
        var queuePump = context.EventQueues is { Ec2: not null } or { Ecs: not null }
            ? new ControllerQueuePump(configuration, clients.Queue(), store, ec2Effects, fargateInspector, clock, log) : null;
        await RunOwnedAsync(context, metadata, observation.TaskArn, owner, store, dispatcher, shutdown, profiles, queuePump,
            metrics, clock, log).ConfigureAwait(false);
    }

    private static async Task<ControllerTaskAuthority> WaitForTaskAsync(EcsControllerMetadata metadata, TimeProvider clock, CancellationToken token)
    {
        while (true)
        {
            if (await metadata.ReadAsync(token).ConfigureAwait(false) is { } observation)
            {
                return observation;
            }
            await Task.Delay(RetryInterval, clock, token).ConfigureAwait(false);
        }
    }

    private static async Task<ControllerCloudClients> ConnectAsync(AwsEnvironment environment, Uri? endpoint, TimeProvider clock,
        ControllerRuntimeLog log, CancellationToken token)
    {
        while (true)
        {
            try
            {
                using var deadline = new CancellationTokenSource(ProfilePassBudget, clock);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                return await ControllerCloudClients.ConnectAsync(environment, endpoint, attempt.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (IsServiceOutage(error))
            {
                token.ThrowIfCancellationRequested();
                log.Write(LogLevel.Warning, "connect", "unavailable", "aws_unavailable", throttle: true);
                await Task.Delay(RetryInterval, clock, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<ControllerLeaseOwnership> AcquireAsync(ControllerAuthorityAdapter adapter, EcsControllerMetadata metadata,
        ControllerTaskAuthority initial, ControllerEffectRecovery recovery, TimeProvider clock, ControllerRuntimeLog log, CancellationToken token)
    {
        var observation = initial;
        while (true)
        {
            if (await adapter.TryActivateAsync(observation, recovery.AdvanceAsync, token).ConfigureAwait(false) is { } owner)
            {
                log.Write(LogLevel.Information, "lease-acquire", "success", "ownership");
                return owner;
            }
            token.ThrowIfCancellationRequested();
            log.Write(LogLevel.Information, "lease-acquire", "deferred", "ownership_unavailable", throttle: true);
            await Task.Delay(RetryInterval, clock, token).ConfigureAwait(false);
            observation = await WaitForTaskAsync(metadata, clock, token).ConfigureAwait(false);
        }
    }

    private static async Task RunOwnedAsync(ControllerRunContext context, EcsControllerMetadata metadata, string taskArn,
        ControllerLeaseOwnership owner, DynamoDbLifecycleStore store, DynamoDbEffectDispatcher dispatcher,
        ControllerShutdownCoordinator shutdown, ControllerProfileReconciler[] profiles, ControllerQueuePump? queuePump, ProfileTelemetry metrics,
        TimeProvider clock, ControllerRuntimeLog log)
    {
        if (owner.ReadAuthority() is not { } initial)
        {
            log.Write(LogLevel.Warning, "lease-loss", "failed", "ownership_unavailable");
            throw new InvalidOperationException("The controller lost authority before starting its workers.");
        }
        using var heartbeatsStop = new CancellationTokenSource();
        using var workStop = CancellationTokenSource.CreateLinkedTokenSource(owner.OwnershipLost, context.Stopping);
        var health = new ActiveHealth(context, owner, profiles, clock);
        foreach (var profile in profiles)
        {
            profile.Session.ListenerHealthChanged = health.Pulse;
        }
        var heartbeat = owner.RunHeartbeatsAsync(HeartbeatInterval, async token =>
        {
            var proof = await metadata.ReadClockAsync(taskArn, token).ConfigureAwait(false);
            health.Pulse();
            return proof;
        }, heartbeatsStop.Token);
        var workers = RunProfilesAsync(context, owner, store, dispatcher, profiles, queuePump, metrics, health, clock, log, workStop.Token);
        using var stopSignal = CancellationTokenSource.CreateLinkedTokenSource(context.Stopping, owner.OwnershipLost);
        var quitting = Task.Delay(Timeout.InfiniteTimeSpan, stopSignal.Token);
        bool supervisionFailed;
        try
        {
            var finished = await Task.WhenAny(heartbeat, workers, quitting).ConfigureAwait(false);
            supervisionFailed = !context.Stopping.IsCancellationRequested;
            if (finished != quitting)
            {
                await finished.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopSignal.IsCancellationRequested)
        {
            supervisionFailed = !context.Stopping.IsCancellationRequested;
        }
        catch (Exception)
        {
            supervisionFailed = true;
        }

        if (supervisionFailed && owner.OwnershipLost.IsCancellationRequested)
        {
            log.Write(LogLevel.Warning, "lease-loss", "failed", "ownership_unavailable");
        }
        else if (supervisionFailed)
        {
            log.Write(LogLevel.Error, "supervise", "failed", "runtime_failure");
        }
        context.RequestStop();
        health.Stop();
        using var localDeadline = new CancellationTokenSource(CleanupBudget, clock);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(context.ShutdownDeadline, localDeadline.Token);
        var work = new ActiveControllerShutdownWork(profiles, workers, workStop);
        var stopping = shutdown.StopAsync(owner, profiles.Select(profile => profile.Key).ToArray(), work, cleanup.Token);
        ControllerShutdownStatus? stopped = null;
        try
        {
            stopped = await stopping.WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            supervisionFailed = true;
        }
        finally
        {
            metrics.EndOwnership(initial.Lease.Fence);
            supervisionFailed |= !await ObserveCompletionAsync(Task.WhenAll(workStop.CancelAsync(), heartbeatsStop.CancelAsync()))
                .ConfigureAwait(false);
            // The host deadline can return a failed exit while the tasks are still unwinding. Keep their clients alive until they join.
            supervisionFailed |= !await ObserveCompletionAsync(Task.WhenAll(stopping, workers, heartbeat)).ConfigureAwait(false);
            supervisionFailed |= !await ObserveCompletionAsync(Task.WhenAll(profiles.Select(profile => profile.Session.DisposeAsync().AsTask())))
                .ConfigureAwait(false);
        }
        if (supervisionFailed || stopped != ControllerShutdownStatus.Released)
        {
            if (stopped != ControllerShutdownStatus.Released)
            {
                log.Write(LogLevel.Warning, "supervise", "unknown", "shutdown_unconfirmed");
            }
            throw new InvalidOperationException("Controller supervision ended or shutdown could not be confirmed.");
        }
    }

    private static async Task RunProfilesAsync(ControllerRunContext context, ControllerLeaseOwnership owner, DynamoDbLifecycleStore store,
        DynamoDbEffectDispatcher dispatcher, ControllerProfileReconciler[] profiles, ControllerQueuePump? queuePump, ProfileTelemetry metrics, ActiveHealth health, TimeProvider clock,
        ControllerRuntimeLog log, CancellationToken cancellationToken)
    {
        var keys = profiles.Select(profile => profile.Key).ToArray();
        if (!await PrepareConfigurationAsync(context, owner, store, dispatcher, keys, clock, log, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        using var eventStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ec2 = queuePump is not null && context.EventQueues.Ec2 is { } ec2Url
            ? queuePump.RunEc2Async(ec2Url, owner, eventStop.Token) : Task.CompletedTask;
        var fargate = queuePump is not null && context.EventQueues.Ecs is { } ecsUrl
            ? queuePump.RunFargateAsync(ecsUrl, owner, eventStop.Token) : Task.CompletedTask;
        try
        {
            while (owner.ReadAuthority() is { } authority && !cancellationToken.IsCancellationRequested)
            {
                metrics.SetOwnership(authority.Lease, authority.Clock, EffectTiming.WriteReserve);
                foreach (var profile in profiles)
                {
                    using var timeout = new CancellationTokenSource(ProfilePassBudget, clock);
                    using var pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                    using var activity = ControllerRuntimeLog.StartAttempt("reconcile");
                    profile.TelemetryIdentity?.SetTags(activity);
                    bool ready;
                    try
                    {
                        ready = await profile.ReconcileAsync(pass.Token).ConfigureAwait(false);
                        activity?.SetTag("outcome", ready ? "success" : "deferred");
                    }
                    catch (Exception error) when (IsServiceOutage(error) || error is GitHubOperationException or GitHubSecretLoadException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string reason = error switch
                        {
                            GitHubOperationException or GitHubSecretLoadException => "github_unavailable",
                            OperationCanceledException or TimeoutException => "timeout",
                            _ => "unavailable",
                        };
                        activity?.SetTag("outcome", "unavailable").SetTag("reason", reason);
                        log.Write(LogLevel.Warning, "reconcile", "unavailable", reason, profile.TelemetryIdentity, throttle: true);
                        ready = false;
                    }
                    health.Observe(profile.Key.ProfileId, ready);
                }
                await Task.Delay(RetryInterval, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await eventStop.CancelAsync().ConfigureAwait(false);
            await JoinCancelledAsync(Task.WhenAll(ec2, fargate), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<bool> PrepareConfigurationAsync(ControllerRunContext context, ControllerLeaseOwnership owner,
        DynamoDbLifecycleStore store, DynamoDbEffectDispatcher dispatcher, LifecycleProfile[] keys, TimeProvider clock,
        ControllerRuntimeLog log, CancellationToken cancellationToken)
    {
        while (owner.ReadAuthority() is { } current && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var applied = await store.AdvanceCatalogRolloutAsync(current.Lease.Fence, context.Configuration,
                    context.Generation, cancellationToken).ConfigureAwait(false);
                switch (applied.Status)
                {
                    case CatalogRolloutStatus.Applied:
                        // The replacement session requires a clear effect index. An inherited unstarted launch has no current demand.
                        if (await dispatcher.CancelInheritedLaunchesAsync(owner, keys, cancellationToken).ConfigureAwait(false))
                        {
                            return true;
                        }
                        break;
                    case CatalogRolloutStatus.Staging:
                        continue;
                    case CatalogRolloutStatus.StaleGeneration:
                    case CatalogRolloutStatus.BindingConflict:
                    case CatalogRolloutStatus.RetirementRequired:
                    case CatalogRolloutStatus.PendingConflict:
                        string reason = applied.Status switch
                        {
                            CatalogRolloutStatus.StaleGeneration => "stale_generation",
                            CatalogRolloutStatus.BindingConflict => "binding_conflict",
                            CatalogRolloutStatus.RetirementRequired => "retirement_required",
                            _ => "pending_conflict",
                        };
                        log.Write(LogLevel.Error, "catalog-rollout", "rejected", reason);
                        throw new InvalidOperationException("The deployment catalog rejected the requested configuration generation.");
                }
            }
            catch (Exception error) when (IsServiceOutage(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                log.Write(LogLevel.Warning, "catalog-rollout", "unavailable", "aws_unavailable", throttle: true);
            }
            await Task.Delay(RetryInterval, clock, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsServiceOutage(Exception error) => error is AmazonClientException or AmazonServiceException or IOException or
        HttpRequestException or TimeoutException or OperationCanceledException;

    private static async Task JoinCancelledAsync(Task work, CancellationToken cancellationToken)
    {
        try
        {
            await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (work.IsCanceled)
        {
        }
    }

    private static async Task<bool> ObserveCompletionAsync(Task work)
    {
        try
        {
            await JoinCancelledAsync(work, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class ActiveHealth(ControllerRunContext context, ControllerLeaseOwnership owner,
        ControllerProfileReconciler[] profiles, TimeProvider clock)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, DateTimeOffset> _readyUntil = new(StringComparer.Ordinal);
        private bool _stopped;

        internal void Observe(string profile, bool ready)
        {
            lock (_gate)
            {
                var now = clock.GetUtcNow();
                _readyUntil[profile] = ready ? now + EffectTiming.ObservationFreshness : now;
                Publish(now);
            }
        }

        internal void Pulse()
        {
            lock (_gate)
            {
                Publish(clock.GetUtcNow());
            }
        }

        internal void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
                Publish(clock.GetUtcNow());
            }
        }

        private void Publish(DateTimeOffset now) => context.PublishHealth(new ControllerHealthSnapshot(true, true, true,
            !_stopped && owner.ReadAuthority() is not null, profiles.Select(entry => new ControllerProfileHealth(entry.Key.ProfileId,
                !_stopped && _readyUntil.GetValueOrDefault(entry.Key.ProfileId) > now &&
                (!entry.Session.RequiresListenerHealth || entry.Session.IsListenerAvailable))
            {
                ListenerFailure = entry.Session.RequiresListenerHealth ? entry.Session.ListenerFailure : null,
            }).ToImmutableArray()));
    }

    private sealed class ActiveControllerShutdownWork(ControllerProfileReconciler[] profiles, Task workers,
        CancellationTokenSource workerStop) : IControllerShutdownWork
    {
        public async Task StopAdmissionsAsync(CancellationToken cancellationToken)
        {
            var admissions = Task.WhenAll(profiles.Select(profile => profile.Session.StopAdmissionsAsync(cancellationToken)));
            await Task.WhenAll(admissions, workerStop.CancelAsync()).ConfigureAwait(false);
        }

        public Task CancelPollsAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(profiles.Select(profile => profile.Session.CancelPollsAsync(cancellationToken)));

        public async Task JoinMutatorsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await JoinCancelledAsync(workers, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (workers.IsFaulted)
            {
                // A terminal worker fault is observed by the supervisor and cannot leave that worker in flight.
            }
            await Task.WhenAll(profiles.Select(profile => profile.Session.JoinMutatorsAsync(cancellationToken))).ConfigureAwait(false);
        }

        public Task CloseOwnedSessionsAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(profiles.Select(profile => profile.Session.CloseOwnedSessionsAsync(cancellationToken)));
    }
}
