using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Amazon.Runtime;
using Amazon.S3;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Controller.Health;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.DryRun;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Microsoft.AspNetCore.Builder;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static class RunCommand
{
    private const string Usage = "Use run --config <runtime.json> or --config-s3-uri <s3://bucket/key> " +
        "--config-version-id <version>, followed by --sha256 <hex> --generation <positive-integer> " +
        "[--health-addr <loopback-ip:port>] [--otlp-endpoint <url>] " +
        "[--ec2-event-queue-url <url>] [--ecs-event-queue-url <url>].";
    private static readonly TimeSpan ReadOnlyInterval = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, Func<ControllerRunContext, Task> runActive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(runActive);
        var options = RunOptions.Parse(args);
        if (options is null)
        {
            await stderr.WriteLineAsync(Usage.AsMemory(), cancellationToken);
            return 2;
        }

        using var lifetime = new ControllerRunLifetime();
        await using var cancellation = cancellationToken.Register(lifetime.RequestStop);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, lifetime.OnSignal);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, lifetime.OnSignal);
        var cleanup = new RunResources(lifetime);
        try
        {
            var address = LoopbackHealthAddress.Parse(options.HealthAddress);
            var artifact = options.ConfigPath is { } path
                ? RuntimeArtifact.FromLocal(path, options.Sha256)
                : RuntimeArtifact.FromS3(options.S3Uri!, options.VersionId!, options.Sha256);
            string? configuredEndpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");
            var endpoint = artifact.S3 is null ? null : ControllerAwsClientConfiguration.ReadLoopbackEndpoint(configuredEndpoint);
            IAmazonS3 CreateS3(string? region) => RuntimeArtifactLoader.CreateS3Client(region, endpoint);

            var (configuration, settings) = await Task.Run(() => RuntimeInput.LoadAsync(artifact,
                options.OtlpEndpoint, CreateS3, null, lifetime.Stopping), CancellationToken.None)
                .WaitAsync(lifetime.ShutdownDeadline);
            lifetime.Stopping.ThrowIfCancellationRequested();
            bool active = configuration.Mode == ControllerMode.Active;
            if (artifact.S3 is null && (active || configuration.DryRun.InspectAws ||
                configuration.DryRun.Demand is S3SnapshotDemandConfiguration))
            {
                endpoint = ControllerAwsClientConfiguration.ReadLoopbackEndpoint(configuredEndpoint);
            }
            var queues = options.BindQueues(configuration, endpoint);
            var starting = new ControllerHealthSnapshot(true, true, active, false,
                configuration.Profiles.Select(profile => new ControllerProfileHealth(
                    profile.Id, profile.State == RunnerProfileState.Disabled)).ToImmutableArray());
            var snapshot = starting;
            void Publish(ControllerHealthSnapshot next) => Volatile.Write(ref snapshot, next);
            ControllerHealthSnapshot Read()
            {
                var latest = Volatile.Read(ref snapshot);
                return lifetime.Stopping.IsCancellationRequested ? latest with { Live = false, LeaseActive = false } : latest;
            }

            cleanup.Telemetry = TelemetryPipeline.Start(settings, stderr);
            await cleanup.StartHealthAsync(address, Read);
            var context = new ControllerRunContext(configuration, options.Generation, cleanup.Telemetry, queues, endpoint, Publish, lifetime);
            var work = Task.Run(() =>
            {
                lifetime.Stopping.ThrowIfCancellationRequested();
                return active ? runActive(context) : RunReadOnlyAsync(context, artifact, CreateS3, starting);
            }, CancellationToken.None);
            try
            {
                await work.WaitAsync(lifetime.ShutdownDeadline);
            }
            catch (OperationCanceledException) when (lifetime.ShutdownDeadline.IsCancellationRequested && !work.IsCompleted)
            {
                // Any terminal exception remains observed even after the process stops waiting for lifecycle work.
                _ = ObserveAsync(work);
                return 1;
            }
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.Stopping.IsCancellationRequested)
        {
            return lifetime.ShutdownDeadline.IsCancellationRequested ? 1 : 0;
        }
        catch (RuntimeConfigurationException error)
        {
            await cleanup.ReportAsync(() => ConfigVerifyCommand.WriteErrorAsync(error, stderr, lifetime.ExitDeadline));
            return 1;
        }
        catch (TelemetrySettingsException error)
        {
            await cleanup.ReportAsync(() => stderr.WriteLineAsync(error.Message.AsMemory(), lifetime.ExitDeadline));
            return 1;
        }
        catch (Exception error) when (IsRuntimeFailure(error))
        {
            await cleanup.ReportAsync(() => stderr.WriteLineAsync(
                "The controller could not start or continue safely.".AsMemory(), lifetime.ExitDeadline));
            return 1;
        }
        finally
        {
            await cleanup.StopAsync();
        }
    }

    private static async Task RunReadOnlyAsync(
        ControllerRunContext context, RuntimeArtifact artifact, Func<string?, IAmazonS3> createS3,
        ControllerHealthSnapshot starting)
    {
        using var interval = new PeriodicTimer(ReadOnlyInterval);
        do
        {
            context.Stopping.ThrowIfCancellationRequested();
            try
            {
                var report = await DryRunCommand.CreateReportAsync(context.Configuration, artifact, createS3,
                    (configuration, cancellationToken) => ReadOnlyRunnerInventory.ReadAsync(
                        configuration, context.AwsLoopbackEndpoint, cancellationToken), null, context.Stopping);
                bool inspectionExpected = context.Configuration.DryRun.InspectAws &&
                    context.Configuration.DryRun.Demand is NoDryRunDemandConfiguration;
                var profiles = report.Profiles.Select(profile => new ControllerProfileHealth(profile.ProfileId,
                    profile.State == RunnerProfileState.Disabled ||
                    !inspectionExpected || profile.Capacity.InventoryStatus == ObservationStatus.Fresh)).ToImmutableArray();
                context.PublishHealth(starting with { Profiles = profiles });
            }
            catch (Exception error) when (IsRuntimeFailure(error))
            {
                // Missing current evidence degrades readiness while the isolated read-only supervisor continues.
                context.PublishHealth(starting);
            }
        } while (await interval.WaitForNextTickAsync(context.Stopping));
    }

    private static bool IsRuntimeFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            AmazonServiceException or AmazonClientException or TimeoutException or DryRunEvidenceException;

    private static async Task ObserveAsync(Task work)
    {
        // WhenAny completes even when the underlying task was canceled or faulted.
        _ = await Task.WhenAny(work).ConfigureAwait(false);
        _ = work.Exception;
    }

    private sealed class RunResources(ControllerRunLifetime lifetime)
    {
        internal TelemetryPipeline? Telemetry { get; set; }
        private Task<WebApplication>? _health;

        internal async Task StartHealthAsync(LoopbackHealthAddress address, Func<ControllerHealthSnapshot> read)
        {
            _health = Task.Run(() => ControllerHealthServer.StartAsync(address, read, lifetime.Stopping));
            _ = await _health.WaitAsync(lifetime.ShutdownDeadline);
        }

        internal async Task ReportAsync(Func<Task> write)
        {
            lifetime.RequestStop();
            await ObserveAsync(ObserveAsync(Task.Run(write)).WaitAsync(TimeSpan.FromMilliseconds(200), lifetime.ExitDeadline));
        }

        internal async Task StopAsync()
        {
            lifetime.RequestStop();
            var telemetry = Task.Run(async () =>
            {
                if (Telemetry is { } pipeline)
                {
                    _ = await pipeline.StopAsync();
                }
            });
            var health = Task.Run(async () =>
            {
                if (_health is { } started)
                {
                    var host = await started;
                    try
                    {
                        await host.StopAsync(lifetime.ExitDeadline);
                    }
                    finally
                    {
                        await host.DisposeAsync();
                    }
                }
            });
            await ObserveAsync(ObserveAsync(Task.WhenAll(telemetry, health)).WaitAsync(lifetime.ExitDeadline));
        }
    }
}
