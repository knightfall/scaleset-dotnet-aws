using System.Security.Cryptography;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

public sealed record RunnerBootstrapTarget(string Deployment, string ProfileId, string ScopeIdentity, long? ScaleSetId)
{
    internal bool Matches(JitHandoffIdentity identity) =>
        identity.Deployment == Deployment && identity.ProfileId == ProfileId &&
        identity.ScopeIdentity == ScopeIdentity && (ScaleSetId is null || identity.ScaleSetId == ScaleSetId);
}

public enum RunnerBootstrapOutcome
{
    Completed = 0,
    AlreadySpent = 1,
    HelloRejected = 2,
    OfferUnavailable = 3,
    OfferRejected = 4,
    RunnerFailed = 5,
}

public sealed record OneJobCompletion(long RunnerId, int ExitCode);

public interface IOneJobRunner
{
    Task<OneJobCompletion> RunAsync(JitPlaintext configuration, string expectedName,
        Func<long, CancellationToken, Task> ready, CancellationToken cancellationToken);
}

public sealed record BootstrapRunnerObservation(JitHello Hello, string OfferId, long RunnerId, bool Exited);

/// <summary>Consumes one exact offer and starts one ephemeral runner for the verified executor.</summary>
public sealed class RunnerBootstrap(
    DynamoDbBootstrapOfferClient offers,
    RunnerBootstrapTarget target,
    string resourceArn,
    string awsUserId,
    Func<bool> reserveSpent,
    IOneJobRunner runner,
    TimeProvider clock,
    TimeSpan offerWait,
    TimeSpan pollDelay,
    Func<JitHello, CancellationToken, Task<LifecycleWriteOutcome>> registerHello,
    Func<BootstrapRunnerObservation, CancellationToken, Task>? observe,
    Func<JitHandoffIdentity, bool>? validateProviderBinding,
    RunnerProvisionerType? telemetryProvider,
    ILogger? logger)
{
    public RunnerBootstrap(DynamoDbBootstrapOfferClient offers, RunnerBootstrapTarget target, string resourceArn,
        string awsUserId, Func<bool> reserveSpent, IOneJobRunner runner, TimeProvider clock, TimeSpan offerWait,
        TimeSpan pollDelay, Func<JitHello, CancellationToken, Task<LifecycleWriteOutcome>> registerHello,
        Func<BootstrapRunnerObservation, CancellationToken, Task>? observe = null,
        Func<JitHandoffIdentity, bool>? validateProviderBinding = null,
        RunnerProvisionerType? telemetryProvider = null)
        : this(offers, target, resourceArn, awsUserId, reserveSpent, runner, clock, offerWait, pollDelay, registerHello,
            observe, validateProviderBinding, telemetryProvider, logger: null)
    {
    }

    public async Task<RunnerBootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        if (offerWait <= TimeSpan.Zero || pollDelay <= TimeSpan.Zero || pollDelay > offerWait)
        {
            throw new InvalidOperationException("The bootstrap offer wait is invalid.");
        }
        using var diagnostics = new RunnerBootstrapDiagnostics.Handoff(clock, telemetryProvider, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!reserveSpent())
        {
            return diagnostics.Complete(RunnerBootstrapOutcome.AlreadySpent, "deferred", "already_spent");
        }

        using var recipient = JitRecipientKey.Create();
        var hello = recipient.Hello;
        var registered = await registerHello(hello, cancellationToken).ConfigureAwait(false);
        if (registered != LifecycleWriteOutcome.Committed)
        {
            return registered == LifecycleWriteOutcome.Indeterminate
                ? diagnostics.Complete(RunnerBootstrapOutcome.HelloRejected, "unknown", "hello_unconfirmed")
                : diagnostics.Complete(RunnerBootstrapOutcome.HelloRejected, "rejected", "hello_rejected");
        }

        var receipt = await WaitForOfferAsync(hello, cancellationToken).ConfigureAwait(false);
        if (receipt.Status == BootstrapOfferStatus.NotAvailable)
        {
            return diagnostics.Complete(RunnerBootstrapOutcome.OfferUnavailable, "unavailable", "no_offer_before_deadline");
        }
        if (receipt is not { Status: BootstrapOfferStatus.Consumed, Offer: { } offer })
        {
            return receipt.Expired
                ? diagnostics.Complete(RunnerBootstrapOutcome.OfferRejected, "expired", "receipt_expired")
                : diagnostics.Complete(RunnerBootstrapOutcome.OfferRejected, "unknown", "receipt_unknown");
        }
        if (!target.Matches(offer.Binding.Identity) || validateProviderBinding?.Invoke(offer.Binding.Identity) == false)
        {
            return diagnostics.Complete(RunnerBootstrapOutcome.OfferRejected, "rejected", "binding_mismatch");
        }

        JitPlaintext plaintext;
        try
        {
            plaintext = recipient.Open(offer, resourceArn, awsUserId, clock);
        }
        catch (CryptographicException)
        {
            return diagnostics.Complete(RunnerBootstrapOutcome.OfferRejected, "rejected", "authentication_failed");
        }
        using (plaintext)
        {
            diagnostics.Authenticated();
            var result = await RunOneJobAsync(runner, plaintext, hello, offer.Binding.OfferId, observe, clock, cancellationToken,
                telemetryProvider, logger).ConfigureAwait(false);
            return result.ExitCode == 0 ? RunnerBootstrapOutcome.Completed : RunnerBootstrapOutcome.RunnerFailed;
        }
    }

    internal static async Task<OneJobCompletion> RunOneJobAsync(IOneJobRunner runner, JitPlaintext plaintext, JitHello hello,
        string offerId, Func<BootstrapRunnerObservation, CancellationToken, Task>? observe, TimeProvider clock,
        CancellationToken cancellationToken, RunnerProvisionerType? telemetryProvider = null, ILogger? logger = null)
    {
        await using var logQueue = BootstrapLogQueue.Start(logger, clock);
        var diagnosticsLogger = logQueue ?? logger;
        long startedAt = clock.GetTimestamp();
        var ready = new TaskCompletionSource<(long Id, long At)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runnerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task readiness = ReportReadinessAsync();
        OneJobCompletion? completion = null;
        try
        {
            completion = await runner.RunAsync(plaintext, "ss-" + offerId[..48], (id, _) =>
            {
                // The output drain only signals readiness. Status I/O has its own lifetime.
                ready.TrySetResult((id, clock.GetTimestamp()));
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ready.TrySetResult(null);
            if (completion is null)
            {
                await runnerLifetime.CancelAsync().ConfigureAwait(false);
            }
            await readiness.ConfigureAwait(false);
            if (await ready.Task.ConfigureAwait(false) is null)
            {
                string outcome = completion switch
                {
                    null when cancellationToken.IsCancellationRequested => "cancelled",
                    { ExitCode: not 0 } => "failed",
                    _ => "unknown",
                };
                RunnerBootstrapDiagnostics.RecordStartup(clock.GetElapsedTime(startedAt), telemetryProvider, outcome,
                    completion is null ? "interrupted" : "runner_exit_without_readiness");
            }
        }

        await ReportAsync(completion.RunnerId, exited: true, cancellationToken).ConfigureAwait(false);
        return completion;

        async Task ReportReadinessAsync()
        {
            if (await ready.Task.ConfigureAwait(false) is { } signal)
            {
                RunnerBootstrapDiagnostics.RecordStartup(clock.GetElapsedTime(startedAt, signal.At), telemetryProvider,
                    "success", "listener_connected");
                await ReportAsync(signal.Id, exited: false, runnerLifetime.Token).ConfigureAwait(false);
            }
        }

        async Task ReportAsync(long runnerId, bool exited, CancellationToken token)
        {
            if (observe is null)
            {
                return;
            }

            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            using var reporting = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
            Task? publication = null;
            try
            {
                reporting.Token.ThrowIfCancellationRequested();
                publication = observe(new BootstrapRunnerObservation(hello, offerId, runnerId, exited), reporting.Token);
                await publication.WaitAsync(reporting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (reporting.IsCancellationRequested)
            {
                if (publication is not null)
                {
                    _ = publication.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if (budget.IsCancellationRequested)
                {
                    BootstrapLog.Write(diagnosticsLogger, BootstrapLog.StatusTimedOut);
                }
            }
            catch (Exception)
            {
                // Telemetry must never replace the outcome of the observed one-job process.
                BootstrapLog.Write(diagnosticsLogger, BootstrapLog.StatusUnavailable);
            }
        }
    }

    private async Task<BootstrapOfferReceipt> WaitForOfferAsync(JitHello hello, CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(offerWait, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            while (true)
            {
                var receipt = await offers.TryConsumeOfferAsync(hello, linked.Token).ConfigureAwait(false);
                if (receipt.Status != BootstrapOfferStatus.NotAvailable)
                {
                    return receipt;
                }
                await Task.Delay(pollDelay, clock, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            return new BootstrapOfferReceipt(BootstrapOfferStatus.NotAvailable);
        }
    }
}
