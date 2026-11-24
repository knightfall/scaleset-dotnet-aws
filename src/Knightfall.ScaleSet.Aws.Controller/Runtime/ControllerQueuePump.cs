using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.SQS;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.Providers.Fargate;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

internal sealed class ControllerQueuePump(RuntimeConfiguration configuration, IAmazonSQS sqs, DynamoDbLifecycleStore store,
    IReadOnlyDictionary<string, Ec2EffectProvider> ec2Effects, FargateProfileInspector? fargateInspector, TimeProvider clock)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private readonly string _deployment = RunnerOwnershipTags.DeploymentIdentity(configuration);

    internal Task RunEc2Async(string queueUrl, ControllerLeaseOwnership ownership, CancellationToken runToken)
    {
        var consumer = new Ec2StateChangeConsumer(configuration, sqs, queueUrl, clock);
        return RunOwnedAsync(ownership, ct => consumer.ReceiveOnceAsync(
            (hint, token) => RefreshEc2Async(ownership, hint, token), token => HasCurrentAuthorityAsync(ownership, token), ct), runToken);
    }

    internal async Task RunFargateAsync(string queueUrl, ControllerLeaseOwnership ownership, CancellationToken runToken)
    {
        using var consumer = new FargateTaskEventConsumer(sqs, fargateInspector ??
            throw new InvalidOperationException("The Fargate queue requires an enabled Fargate backend."), configuration, queueUrl, clock);
        await RunOwnedAsync(ownership, ct => consumer.PollAsync(
            (binding, observation, token) => ApplyFargateAsync(ownership, binding, observation, token),
            token => HasCurrentAuthorityAsync(ownership, token), ct), runToken).ConfigureAwait(false);
    }

    private async Task RunOwnedAsync(
        ControllerLeaseOwnership ownership, Func<CancellationToken, Task> receive, CancellationToken runToken)
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(runToken, ownership.OwnershipLost);
        var token = owned.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (await HasCurrentAuthorityAsync(ownership, token).ConfigureAwait(false))
                    {
                        await receive(token).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is IOException or InvalidDataException or
                    HttpRequestException or SocketException or AmazonServiceException or AmazonClientException or TimeoutException or OperationCanceledException)
                {
                    // A failed observation or queue request leaves the message available for redelivery.
                }
                await Task.Delay(RetryDelay, clock, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> RefreshEc2Async(ControllerLeaseOwnership ownership, Ec2StateChangeHint hint, CancellationToken cancellationToken)
    {
        var binding = await store.ReadExecutorBindingForResourceAsync(_deployment, hint.ResourceArn, cancellationToken).ConfigureAwait(false);
        if (binding?.Provider is not Ec2ExecutorIdentity identity || !ec2Effects.TryGetValue(binding.Profile.ProfileId, out var provider))
        {
            // The account-wide EC2 rule includes unrelated instances. A hint does not create an executor claim.
            return true;
        }
        var inventory = await provider.ReadStateChangeAsync(hint, cancellationToken).ConfigureAwait(false);
        if (!inventory.IsComplete || inventory.Instances is not [{ IsAuthoritative: true, OriginalLaunchTime: { } launchTime } instance] ||
            instance.ResourceArn != binding.ResourceArn || instance.AllocationId?.ToString("D") != binding.AllocationId ||
            instance.Incarnation != binding.Incarnation || instance.LaunchTemplateId != identity.LaunchTemplateId ||
            instance.LaunchTemplateVersion != identity.LaunchTemplateVersion || launchTime != identity.InstanceLaunchTime ||
            launchTime > inventory.ObservedAt)
        {
            return false;
        }

        var state = instance.State switch
        {
            Ec2InstanceState.Pending => ExecutorResourceState.Starting,
            Ec2InstanceState.Running => ExecutorResourceState.Running,
            Ec2InstanceState.Terminated => ExecutorResourceState.IrreversiblyTerminated,
            _ => ExecutorResourceState.Unknown,
        };
        var observation = new ExecutorResourceObservation(state, inventory.ObservedAt, launchTime, Guid.NewGuid());
        return await ApplyAsync(ownership, binding, observation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ApplyFargateAsync(ControllerLeaseOwnership ownership, ExecutorBinding binding,
        ExecutorResourceObservation observation, CancellationToken cancellationToken) =>
        binding.Provider is FargateExecutorIdentity && binding.Profile.Deployment == _deployment &&
        await store.ReadExecutorBindingForResourceAsync(_deployment, binding.ResourceArn, cancellationToken).ConfigureAwait(false) == binding &&
        await ApplyAsync(ownership, binding, observation, cancellationToken).ConfigureAwait(false);

    private async Task<bool> ApplyAsync(ControllerLeaseOwnership ownership, ExecutorBinding binding,
        ExecutorResourceObservation observation, CancellationToken cancellationToken)
    {
        var authority = await ReadCurrentAuthorityAsync(ownership, cancellationToken).ConfigureAwait(false);
        return authority is not null &&
            await store.ObserveResourceAsync(authority.Lease.Fence, binding, observation, cancellationToken).ConfigureAwait(false) ==
                LifecycleWriteOutcome.Committed;
    }

    private async Task<bool> HasCurrentAuthorityAsync(ControllerLeaseOwnership ownership, CancellationToken cancellationToken) =>
        await ReadCurrentAuthorityAsync(ownership, cancellationToken).ConfigureAwait(false) is not null;

    private async Task<ActiveControllerAuthority?> ReadCurrentAuthorityAsync(
        ControllerLeaseOwnership ownership, CancellationToken cancellationToken)
    {
        if (ownership.ReadAuthority() is not { } expected || expected.Lease.Deployment != _deployment)
        {
            return null;
        }
        var current = await store.ReadLeaseAsync(_deployment, cancellationToken).ConfigureAwait(false);
        var local = ownership.ReadAuthority();
        return current is { Mode: ControllerLeaseMode.Active } && local is not null && local.Lease.Fence == current.Fence &&
            local.Clock.TryGetUpperBound(clock.GetUtcNow(), current.ValidUntil, TimeSpan.Zero, out _) ? local : null;
    }
}
