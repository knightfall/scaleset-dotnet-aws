using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Providers.Fargate;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

/// <summary>Shares one quota refresh across profiles reconciled serially in a deployment.</summary>
internal sealed class FargateQuotaReader(
    AwsEnvironment aws, FargateRuntimeEvidence evidence, IAmazonS3? s3, TimeProvider clock)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);
    private const int MaximumBytes = 4096;
    private FargateQuotaEvidence? _latest;
    private FargateQuotaEvidence? _current;
    private DateTimeOffset? _lastAttempt;

    internal async ValueTask<FargateQuotaEvidence?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence.QuotaSource is not { } source)
        {
            return Current(evidence.Quota);
        }

        var now = clock.GetUtcNow();
        if (_lastAttempt is not { } last || now < last || now - last >= RefreshInterval)
        {
            _current = null;
            var sample = Current(await ReadObjectAsync(source, cancellationToken).ConfigureAwait(false));
            _lastAttempt = clock.GetUtcNow();
            if (sample is not null && (_latest is null || sample.ObservedAt > _latest.ObservedAt || sample == _latest))
            {
                _current = _latest = sample;
            }
        }
        return Current(_current);
    }

    private FargateQuotaEvidence? Current(FargateQuotaEvidence? sample) =>
        sample?.IsCurrentFor(aws.Account, aws.Region, clock.GetUtcNow()) == true ? sample : null;

    private async Task<FargateQuotaEvidence?> ReadObjectAsync(FargateQuotaObject source, CancellationToken cancellationToken)
    {
        if (s3 is null)
        {
            return null;
        }
        using var budget = new CancellationTokenSource(ReadBudget, clock);
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = source.Bucket,
                Key = source.Key,
                ExpectedBucketOwner = aws.Account,
            }, read.Token).ConfigureAwait(false);
            if (response.ContentLength is <= 0 or > MaximumBytes)
            {
                return null;
            }
            var bytes = new byte[MaximumBytes + 1];
            int count = await response.ResponseStream.ReadAtLeastAsync(
                bytes, bytes.Length, throwOnEndOfStream: false, read.Token).ConfigureAwait(false);
            return count == response.ContentLength ? FargateRuntimeEvidence.ReadQuota(bytes.AsSpan(0, count)) : null;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is AmazonServiceException or AmazonClientException or IOException or HttpRequestException or JsonException
                or OperationCanceledException or TimeoutException)
        {
            return null;
        }
    }
}
