using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    public async Task<bool> HasAcquisitionHistoryAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var page = await ReadBasePageAsync(profile.PartitionKey, "ACQUIRE#", null, cancellationToken).ConfigureAwait(false);
        return page.Items.Count != 0 || page.Continuation is not null;
    }
}
