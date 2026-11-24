using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    private const string CatalogProfilePrefix = "PROFILE#";
    private const string CatalogProfileIdentity = "effectProfileId";

    internal async Task<IReadOnlyList<LifecycleProfile>> ReadEffectProfilesAsync(
        string deployment, CancellationToken cancellationToken)
    {
        string partition = LifecycleKey.Lease(deployment).PartitionKey;
        var profiles = new List<LifecycleProfile>();
        string? after = null;
        do
        {
            var page = await ReadBasePageAsync(partition, CatalogProfilePrefix, after, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                profiles.Add(DecodeEffectProfile(item, deployment, partition));
            }
            after = page.Continuation;
        }
        while (after is not null);

        return profiles;
    }

    internal async Task<LifecycleWriteOutcome> EnsureEffectProfileAsync(
        ControllerLease lease, LifecycleProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        RequireProfileLease(profile, lease.Fence);
        if (lease.Mode != ControllerLeaseMode.Active)
        {
            return LifecycleWriteOutcome.LeaseRejected;
        }

        var key = LifecycleKey.Profile(profile);
        if (await IsEffectProfilePresentAsync(key, profile, cancellationToken).ConfigureAwait(false))
        {
            return LifecycleWriteOutcome.Committed;
        }

        var transition = Guid.NewGuid();
        var item = Record(key, profile.Deployment, 1, transition);
        item.Add(CatalogProfileIdentity, StringValue(profile.ProfileId));
        item.Add("payload", StringValue("{}"));
        var outcome = await WriteAsync(
            [LeaseCondition(lease.Fence), CompareAndPut(key, item, null, profile.Deployment)],
            transition, cancellationToken).ConfigureAwait(false);

        if (outcome is LifecycleWriteOutcome.Conflict or LifecycleWriteOutcome.Indeterminate &&
            await IsEffectProfilePresentAsync(key, profile, cancellationToken).ConfigureAwait(false))
        {
            return LifecycleWriteOutcome.Committed;
        }
        return outcome;
    }

    private async Task<bool> IsEffectProfilePresentAsync(
        LifecycleKey key, LifecycleProfile profile, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(key, cancellationToken).ConfigureAwait(false);
        return item is not null && DecodeEffectProfile(item, profile.Deployment, key.PartitionKey) == profile;
    }

    private static LifecycleProfile DecodeEffectProfile(
        Dictionary<string, AttributeValue> item, string deployment, string partition)
    {
        string sort = Text(item, "SK");
        RequireKey(item, partition, sort);
        CheckNoExpiration(item);
        if (Text(item, "deployment") != deployment || !sort.StartsWith(CatalogProfilePrefix, StringComparison.Ordinal))
        {
            throw InvalidRecord();
        }

        string id = sort[CatalogProfilePrefix.Length..];
        // A valid pre-roster catalog row already names the same durable profile in its full key.
        if (item.ContainsKey(CatalogProfileIdentity) && Text(item, CatalogProfileIdentity) != id)
        {
            throw InvalidRecord();
        }
        _ = Revision(item);
        _ = GuidValue(item, "transitionId");
        _ = Text(item, "payload");
        try
        {
            return new LifecycleProfile(deployment, id);
        }
        catch (ArgumentException)
        {
            throw InvalidRecord();
        }
    }
}
