using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

public static class RuntimeArtifactLoader
{
    private const int MaximumS3Bytes = 64 * 1024 * 1024;

    public static Task<RuntimeConfiguration> LoadAsync(RuntimeArtifact artifact, CancellationToken cancellationToken) =>
        LoadAsync(artifact, CreateS3Client, cancellationToken);

    internal static async Task<RuntimeConfiguration> LoadAsync(
        RuntimeArtifact artifact, Func<string?, IAmazonS3> createS3Client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var bytes = artifact.S3 is { } s3
            ? await ReadS3Async(s3, region: null, createS3Client, cancellationToken)
            : await File.ReadAllBytesAsync(artifact.LocalPath!, cancellationToken);
        var configuration = RuntimeConfigurationLoader.Load(bytes, artifact.ExpectedSha256);
        if (artifact.S3?.ExpectedBucketOwner is { } owner &&
            !string.Equals(owner, configuration.Aws.Account, StringComparison.Ordinal))
        {
            throw new RuntimeConfigurationException("owner", "/spec/aws/account",
                "The runtime artifact bucket owner must match the configured deployment account.");
        }
        return configuration;
    }

    internal static async Task<byte[]> ReadS3Async(
        VersionedS3Artifact source, string? region, Func<string?, IAmazonS3> createS3Client, CancellationToken cancellationToken)
    {
        using var client = createS3Client(region);
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = source.Bucket,
            Key = source.Key,
            VersionId = source.VersionId,
            ExpectedBucketOwner = source.ExpectedBucketOwner,
        }, cancellationToken);

        if (!string.Equals(response.VersionId, source.VersionId, StringComparison.Ordinal))
        {
            throw new IOException("S3 returned a different object version than requested.");
        }
        if (response.ContentLength > MaximumS3Bytes)
        {
            throw new IOException("The S3 artifact exceeds the supported size.");
        }

        await using var bytes = new MemoryStream();
        byte[] buffer = new byte[81_920];
        while (true)
        {
            int count = await response.ResponseStream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return bytes.ToArray();
            }
            if (bytes.Length + count > MaximumS3Bytes)
            {
                throw new IOException("The S3 artifact exceeds the supported size.");
            }
            bytes.Write(buffer, 0, count);
        }
    }

    internal static IAmazonS3 CreateS3Client(string? region) =>
        CreateS3Client(region, ControllerAwsClientConfiguration.ReadLoopbackEndpoint(Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL")));

    internal static IAmazonS3 CreateS3Client(string? region, Uri? loopbackEndpoint)
    {
        if (region is not null || loopbackEndpoint is not null)
        {
            // A remote runtime artifact has not yet supplied the region. Use the SDK's normal region chain.
            region ??= new AmazonS3Config().RegionEndpoint?.SystemName ??
                throw new ArgumentException("Configure an AWS SDK region for the local S3 endpoint.", nameof(region));
            var pinned = ControllerAwsClientConfiguration.Create<AmazonS3Config>(region, loopbackEndpoint);
            pinned.ForcePathStyle = loopbackEndpoint is not null;
            return new AmazonS3Client(pinned);
        }

        var options = new AmazonS3Config
        {
            AllowAutoRedirect = false,
            IgnoreConfiguredEndpointUrls = true,
            DisableClockSkewCorrection = true,
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
        };
        return new AmazonS3Client(options);
    }
}
