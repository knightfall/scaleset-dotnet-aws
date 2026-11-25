using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

internal sealed class Ec2BootstrapStatusReporter(VerifiedEc2BootstrapIdentity original, string qualification, TimeProvider clock,
    Func<CancellationToken, Task<VerifiedEc2BootstrapIdentity>> verify,
    Func<VerifiedEc2BootstrapIdentity, JitHello, JitEc2BootstrapStatus, CancellationToken, Task<LifecycleWriteOutcome>> publish)
{
    private DateTimeOffset _latestObservation;

    internal async Task<LifecycleWriteOutcome> ReportAsync(BootstrapRunnerObservation observation, CancellationToken cancellationToken)
    {
        var observedAt = clock.GetUtcNow();
        if (_latestObservation != default && observedAt <= _latestObservation)
        {
            // The controller requires exit to follow readiness. One millisecond also orders the
            // DynamoDB numeric timestamp, while the clock check below prevents a future write.
            observedAt = _latestObservation.AddMilliseconds(1);
        }
        // A previous observation may have committed even if its response was lost.
        _latestObservation = observedAt;
        var status = new JitEc2BootstrapStatus(observation.OfferId, observation.RunnerId, observation.Exited ? 2 : 1,
            observation.Exited, observedAt) { QualificationSha256 = qualification };
        int maximumAttempts = observation.Exited ? 3 : 1;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt != 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 1 : 3), clock, cancellationToken).ConfigureAwait(false);
            }
            if (clock.GetUtcNow() < observedAt)
            {
                // Wait for wall time to catch up. A future or newly minted observation cannot prove this exit.
                continue;
            }
            try
            {
                var current = await verify(cancellationToken).ConfigureAwait(false);
                if (current.ResourceArn != original.ResourceArn || current.AwsUserId != original.AwsUserId)
                {
                    return LifecycleWriteOutcome.Conflict;
                }
                if (clock.GetUtcNow() >= observedAt)
                {
                    var outcome = await publish(current, observation.Hello, status, cancellationToken).ConfigureAwait(false);
                    if (outcome != LifecycleWriteOutcome.Indeterminate)
                    {
                        return outcome;
                    }
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                error is IOException or AmazonClientException or AmazonServiceException or HttpRequestException or TimeoutException or OperationCanceledException)
            {
                // Retry the same observation with independently verified identity and freshly read credentials.
            }
        }
        return LifecycleWriteOutcome.Indeterminate;
    }
}
