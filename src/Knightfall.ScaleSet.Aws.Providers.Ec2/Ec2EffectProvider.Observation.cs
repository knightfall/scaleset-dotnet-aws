using Amazon.EC2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

public sealed partial class Ec2EffectProvider
{
    /// <summary>Reads one hinted instance. The result does not represent coverage of the entire profile.</summary>
    public Task<Ec2InventorySnapshot> ReadStateChangeAsync(Ec2StateChangeHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        if (hint.ResourceArn != _instanceArnPrefix + hint.InstanceId || !Ec2ProfileInspector.IsResourceId(hint.InstanceId, "i-"))
        {
            throw new ArgumentException("The EC2 state-change hint must target this account and Region.", nameof(hint));
        }
        return _inspector.ReadInventoryAsync(_configuration, _profile.ProfileId,
            [ValueFilter("instance-id", hint.InstanceId)], instance => instance.InstanceId == hint.InstanceId, cancellationToken);
    }

    private async Task<EffectEvidence?> ObserveLaunchAsync(
        EffectEnvelope envelope, Ec2LaunchEffectRequest request, string? expectedInstanceId, CancellationToken cancellationToken)
    {
        using var operation = Ec2Diagnostics.Start("launch_readback", _telemetryProfile, cancellationToken);
        string allocation = request.AllocationId.ToString("D");
        string token = request.ClientToken.ToString("D");
        var result = await _inspector.ReadInventoryAsync(_configuration, _profile.ProfileId,
            [ValueFilter("tag:" + RunnerOwnershipTags.Allocation, allocation), ValueFilter("client-token", token)],
            instance => instance.ClientToken == token && (expectedInstanceId is null || instance.InstanceId == expectedInstanceId) &&
                instance.SubnetId == request.SubnetId && instance.Placement?.AvailabilityZone == request.AvailabilityZone &&
                instance.IamInstanceProfile?.Arn == request.InstanceProfileArn && instance.InstanceType?.Value == request.InstanceType &&
                HasUniqueTag(instance.Tags, RunnerOwnershipTags.BootstrapProtocol, request.OwnershipTags!.BootstrapProtocol),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsComplete || result.Instances is not
            [{ Incarnation: { } incarnation, OriginalLaunchTime: { } launched } entry] ||
            entry.AllocationId != request.AllocationId || entry.LaunchTemplateVersion != request.LaunchTemplateVersion ||
            entry.LaunchTemplateId != request.LaunchTemplateId)
        {
            return null;
        }

        var binding = new ExecutorBinding(_profile, allocation, entry.ResourceArn, incarnation,
            new Ec2ExecutorIdentity(launched, request.LaunchTemplateId, request.LaunchTemplateVersion));
        var evidence = Applied(envelope, binding, entry.State, launched);
        Ec2Diagnostics.RecordEffect(operation, evidence);
        return evidence;
    }

    private async Task<Ec2InventoryEntry?> ReadTerminationTargetAsync(
        Ec2TerminateEffectRequest request, string instanceId, CancellationToken cancellationToken)
    {
        var result = await _inspector.ReadInventoryAsync(_configuration, _profile.ProfileId,
            [ValueFilter("instance-id", instanceId), ValueFilter("tag:" + RunnerOwnershipTags.Allocation, request.AllocationId.ToString("D"))],
            instance => instance.InstanceId == instanceId, cancellationToken).ConfigureAwait(false);
        if (!result.IsComplete || result.Instances is not [var entry] || entry.ResourceArn != request.InstanceArn ||
            entry.AllocationId != request.AllocationId || entry.LaunchTemplateId != request.LaunchTemplateId ||
            entry.LaunchTemplateVersion != request.LaunchTemplateVersion ||
            (entry.OriginalLaunchTime is { } launchTime && launchTime != request.InstanceLaunchTime))
        {
            return null;
        }
        return entry;
    }

    private async Task<EffectEvidence?> SendTerminationAsync(
        EffectEnvelope envelope, Ec2TerminateEffectRequest request, string instanceId, CancellationToken cancellationToken)
    {
        using var operation = Ec2Diagnostics.Start("terminate_instances", _telemetryProfile, cancellationToken);
        // The dispatcher rechecks durable cleanup authority immediately before calling this method.
        try
        {
            await _operations.TerminateInstancesAsync(new TerminateInstancesRequest { InstanceIds = [instanceId] },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Ec2ProfileInspector.IsReadFailure(error, cancellationToken))
        {
            return null;
        }
        var evidence = await ObserveTerminationAsync(envelope, request, instanceId, cancellationToken).ConfigureAwait(false);
        Ec2Diagnostics.RecordEffect(operation, evidence);
        return evidence;
    }

    private async Task<EffectEvidence?> ObserveTerminationAsync(
        EffectEnvelope envelope, Ec2TerminateEffectRequest request, string instanceId, CancellationToken cancellationToken)
    {
        using var operation = Ec2Diagnostics.Start("termination_readback", _telemetryProfile, cancellationToken);
        var target = await ReadTerminationTargetAsync(request, instanceId, cancellationToken).ConfigureAwait(false);
        var binding = RetainedTerminationBinding(request, instanceId);
        var evidence = target?.CorroboratesTerminal(binding, _configuration.Aws.Account, _configuration.Aws.Region, _clock.GetUtcNow()) == true
            ? Applied(envelope, binding, Ec2InstanceState.Terminated, request.InstanceLaunchTime) : null;
        Ec2Diagnostics.RecordEffect(operation, evidence);
        return evidence;
    }

    private ExecutorBinding RetainedTerminationBinding(Ec2TerminateEffectRequest request, string instanceId)
    {
        var incarnation = Ec2ExecutorIncarnation.Compute(_configuration.Aws.Account, _configuration.Aws.Region, instanceId,
            request.InstanceLaunchTime, request.LaunchTemplateVersion);
        return new ExecutorBinding(_profile, request.AllocationId.ToString("D"), request.InstanceArn, incarnation,
            new Ec2ExecutorIdentity(request.InstanceLaunchTime, request.LaunchTemplateId, request.LaunchTemplateVersion));
    }

    private EffectEvidence Applied(EffectEnvelope envelope, ExecutorBinding binding, Ec2InstanceState state, DateTimeOffset start)
    {
        var observed = _clock.GetUtcNow();
        var resourceState = state switch
        {
            Ec2InstanceState.Pending => ExecutorResourceState.Starting,
            Ec2InstanceState.Running => ExecutorResourceState.Running,
            Ec2InstanceState.Terminated => ExecutorResourceState.IrreversiblyTerminated,
            _ => ExecutorResourceState.Unknown,
        };
        return new EffectEvidence(EffectResolutionKind.Applied, envelope.Sha256, envelope.Target, observed)
        {
            ExecutorBinding = binding,
            ResourceObservation = new ExecutorResourceObservation(resourceState, observed, start, Guid.NewGuid()),
        };
    }

    private static bool HasUniqueTag(List<Tag>? tags, string key, string expected) =>
        tags?.Where(tag => tag?.Key == key).Take(2).ToArray() is [{ Value: var value }] && value == expected;

    private static Filter ValueFilter(string name, string value) => new() { Name = name, Values = [value] };
}
