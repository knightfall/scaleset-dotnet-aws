using System.Security.Cryptography;
using System.Text;
using Amazon.EC2.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

public sealed partial class Ec2ProfileInspector
{
    private const string NativeTemplateId = "aws:ec2launchtemplate:id";
    private const string NativeTemplateVersion = "aws:ec2launchtemplate:version";

    public async Task<Ec2InventorySnapshot> ReadInventoryAsync(
        RuntimeConfiguration configuration, string profileId, CancellationToken cancellationToken)
    {
        var target = GetTarget(configuration, profileId);
        var filters = new List<Filter>
        {
            TagFilter(RunnerOwnershipTags.Controller, RunnerOwnershipTags.ControllerValue),
            TagFilter(RunnerOwnershipTags.Deployment, target.Deployment),
            TagFilter(RunnerOwnershipTags.Profile, target.Profile.Id),
        };
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var seenAllocations = new HashSet<Guid>();
        var instances = new Dictionary<string, Ec2InventoryEntry>(StringComparer.Ordinal);
        var readStartedAt = _clock.GetUtcNow();
        string? next = null;
        bool sawPage = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await MatchesCallerIdentityAsync(target, cancellationToken).ConfigureAwait(false))
            {
                return BuildInventory(Ec2InventoryStatus.AwsIdentityMismatch, readStartedAt, instances);
            }
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await _ec2.DescribeInstancesAsync(new DescribeInstancesRequest
                {
                    Filters = filters,
                    MaxResults = 1000,
                    NextToken = next,
                }, cancellationToken).ConfigureAwait(false);
                sawPage = true;
                var observedAt = _clock.GetUtcNow();
                foreach (var reservation in response.Reservations ?? [])
                {
                    if (reservation is not { Instances.Count: > 0 } || reservation.OwnerId != target.Aws.Account)
                    {
                        return BuildInventory(Ec2InventoryStatus.Incomplete, readStartedAt, instances);
                    }
                    foreach (var instance in reservation.Instances)
                    {
                        if (GetOwnedEntry(target, instance, observedAt) is not { } entry ||
                            !instances.TryAdd(entry.InstanceId, entry) ||
                            (entry.AllocationId is Guid allocation && !seenAllocations.Add(allocation)))
                        {
                            return BuildInventory(Ec2InventoryStatus.Incomplete, readStartedAt, instances);
                        }
                    }
                }
                next = response.NextToken;
                if (next is { Length: > 0 } && (string.IsNullOrWhiteSpace(next) || !seenTokens.Add(next)))
                {
                    return BuildInventory(Ec2InventoryStatus.Incomplete, readStartedAt, instances);
                }
            }
            while (!string.IsNullOrEmpty(next));
        }
        catch (Exception error) when (IsReadFailure(error, cancellationToken))
        {
            return BuildInventory(sawPage ? Ec2InventoryStatus.Incomplete : Ec2InventoryStatus.AwsUnavailable, readStartedAt, instances);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return BuildInventory(Ec2InventoryStatus.Complete, readStartedAt, instances);
    }

    private static Ec2InventorySnapshot BuildInventory(
        Ec2InventoryStatus status, DateTimeOffset observedAt, Dictionary<string, Ec2InventoryEntry> instances)
    {
        var values = instances.Values.OrderBy(instance => instance.InstanceId, StringComparer.Ordinal);
        return new Ec2InventorySnapshot(status, observedAt,
            [.. values.Select(instance => instance with { IsAuthoritative = status == Ec2InventoryStatus.Complete })]);
    }

    private static Ec2InventoryEntry? GetOwnedEntry(InspectionTarget target, Instance? instance, DateTimeOffset observedAt)
    {
        if (instance is null || !IsResourceId(instance.InstanceId, "i-") || ReadOwnershipTags(instance.Tags) is not { } tags ||
            tags.GetValueOrDefault(RunnerOwnershipTags.Controller) != RunnerOwnershipTags.ControllerValue ||
            tags.GetValueOrDefault(RunnerOwnershipTags.Deployment) != target.Deployment ||
            tags.GetValueOrDefault(RunnerOwnershipTags.Profile) != target.Profile.Id)
        {
            return null;
        }

        string resourceArn = $"arn:{target.Partition}:ec2:{target.Aws.Region}:{target.Aws.Account}:instance/{instance.InstanceId}";
        string? nativeTemplate = tags.GetValueOrDefault(NativeTemplateId);
        long? version = TryPositiveDecimal(tags.GetValueOrDefault(NativeTemplateVersion), out long numeric) ? numeric : null;
        string? allocationTag = tags.GetValueOrDefault(RunnerOwnershipTags.Allocation);
        Guid? allocation = Guid.TryParseExact(allocationTag, "D", out var key) && key != Guid.Empty &&
            allocationTag == key.ToString("D") ? key : null;
        var original = ReadOriginalLaunchTime(instance, observedAt);
        Guid? incarnation = allocation.HasValue && nativeTemplate == target.Provisioner.LaunchTemplateId &&
            version is long verifiedVersion && original is { } initial
            ? ComputeIncarnation(target.Aws, instance.InstanceId, initial, verifiedVersion) : null;
        return new Ec2InventoryEntry(instance.InstanceId, resourceArn, GetReportedState(instance.State),
            allocation, nativeTemplate, version, original, incarnation);
    }

    private static Dictionary<string, string>? ReadOwnershipTags(List<Tag>? source)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in source ?? [])
        {
            if (tag is not { Key: RunnerOwnershipTags.Controller or RunnerOwnershipTags.Deployment or RunnerOwnershipTags.Profile or
                RunnerOwnershipTags.Allocation or NativeTemplateId or NativeTemplateVersion })
            {
                continue;
            }
            if (tag.Value is null || !tags.TryAdd(tag.Key, tag.Value))
            {
                return null;
            }
        }
        return tags;
    }

    private static DateTimeOffset? ReadOriginalLaunchTime(Instance instance, DateTimeOffset observedAt)
    {
        // LaunchTime advances after a stop/start. The primary interface identifies the original launch.
        var primary = instance.NetworkInterfaces?.Where(nic => nic.Attachment?.DeviceIndex == 0)
            .Select(nic => nic.Attachment.AttachTime).Take(2).ToArray();
        if (primary is not [DateTime raw] || raw == default)
        {
            return null;
        }
        var original = raw.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(raw, DateTimeKind.Utc) : raw.ToUniversalTime();
        return original <= observedAt ? original : null;
    }

    private static Guid ComputeIncarnation(AwsEnvironment scope, string instanceId, DateTimeOffset launched, long version)
    {
        string tuple = FormattableString.Invariant($"{scope.Account}/{scope.Region}/{instanceId}/{launched.UtcTicks}/{version}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(tuple));
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static Ec2InstanceState GetReportedState(InstanceState? state)
    {
        var (normalized, expectedCode) = state?.Name?.Value switch
        {
            "pending" => (Ec2InstanceState.Pending, 0),
            "running" => (Ec2InstanceState.Running, 16),
            "shutting-down" => (Ec2InstanceState.ShuttingDown, 32),
            "terminated" => (Ec2InstanceState.Terminated, 48),
            _ => (Ec2InstanceState.Unknown, -1),
        };
        return state?.Code is int code && (code & 255) != expectedCode ? Ec2InstanceState.Unknown : normalized;
    }

    private static Filter TagFilter(string name, string value) => new() { Name = "tag:" + name, Values = [value] };
}
