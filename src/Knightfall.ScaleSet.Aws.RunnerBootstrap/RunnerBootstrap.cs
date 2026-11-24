using System.Security.Cryptography;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;

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
    Func<BootstrapRunnerObservation, CancellationToken, Task>? observe = null,
    Func<JitHandoffIdentity, bool>? validateProviderBinding = null)
{
    public async Task<RunnerBootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        if (offerWait <= TimeSpan.Zero || pollDelay <= TimeSpan.Zero || pollDelay > offerWait)
        {
            throw new InvalidOperationException("The bootstrap offer wait is invalid.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!reserveSpent())
        {
            return RunnerBootstrapOutcome.AlreadySpent;
        }

        using var recipient = JitRecipientKey.Create();
        var hello = recipient.Hello;
        if (await offers.RegisterHelloAsync(hello, cancellationToken).ConfigureAwait(false) != LifecycleWriteOutcome.Committed)
        {
            return RunnerBootstrapOutcome.HelloRejected;
        }

        var receipt = await WaitForOfferAsync(hello, cancellationToken).ConfigureAwait(false);
        if (receipt.Status == BootstrapOfferStatus.NotAvailable)
        {
            return RunnerBootstrapOutcome.OfferUnavailable;
        }
        if (receipt is not { Status: BootstrapOfferStatus.Consumed, Offer: { } offer } || !target.Matches(offer.Binding.Identity) ||
            validateProviderBinding?.Invoke(offer.Binding.Identity) == false)
        {
            return RunnerBootstrapOutcome.OfferRejected;
        }

        JitPlaintext plaintext;
        try
        {
            plaintext = recipient.Open(offer, resourceArn, awsUserId, clock);
        }
        catch (CryptographicException)
        {
            return RunnerBootstrapOutcome.OfferRejected;
        }
        using (plaintext)
        {
            var result = await runner.RunAsync(plaintext, "ss-" + offer.Binding.OfferId[..48],
                (id, token) => ReportAsync(hello, offer.Binding.OfferId, id, exited: false, token), cancellationToken)
                .ConfigureAwait(false);
            await ReportAsync(hello, offer.Binding.OfferId, result.RunnerId, exited: true, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? RunnerBootstrapOutcome.Completed : RunnerBootstrapOutcome.RunnerFailed;
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

    private Task ReportAsync(JitHello hello, string offerId, long runnerId, bool exited, CancellationToken cancellationToken) =>
        observe is null ? Task.CompletedTask : observe(new BootstrapRunnerObservation(hello, offerId, runnerId, exited), cancellationToken);
}
