using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

/// <summary>Identifies the exact runtime input to verify before creating controller services.</summary>
public sealed class RuntimeArtifact
{
    private RuntimeArtifact(string? localPath, string? expectedSha256, VersionedS3Artifact? s3)
    {
        LocalPath = localPath;
        ExpectedSha256 = expectedSha256;
        S3 = s3;
    }

    public string? LocalPath { get; }
    public string? ExpectedSha256 { get; }
    internal VersionedS3Artifact? S3 { get; }

    public static RuntimeArtifact FromLocal(string path, string? expectedSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateDigest(expectedSha256);
        return new RuntimeArtifact(Path.GetFullPath(path), expectedSha256, null);
    }

    public static RuntimeArtifact FromS3(string uri, string versionId, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ValidateDigest(expectedSha256);
        return new RuntimeArtifact(null, expectedSha256, VersionedS3Artifact.Parse(uri, versionId));
    }

    private static void ValidateDigest(string? sha256)
    {
        if (sha256 is not null && (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit)))
        {
            throw new RuntimeConfigurationException("digest", string.Empty,
                "Expected SHA-256 must contain exactly 64 hexadecimal digits.");
        }
    }
}

internal sealed record VersionedS3Artifact(string Bucket, string Key, string VersionId)
{
    internal static VersionedS3Artifact Parse(string uri, string versionId)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (string.IsNullOrWhiteSpace(versionId) || versionId == "null" || versionId.Length > 1_024 ||
            versionId.Any(char.IsControl))
        {
            throw new ArgumentException("A specific S3 object version is required.", nameof(versionId));
        }

        const string scheme = "s3://";
        int delimiter = uri.IndexOf('/', Math.Min(scheme.Length, uri.Length));
        if (!uri.StartsWith(scheme, StringComparison.Ordinal) || delimiter < 0 || delimiter == uri.Length - 1 ||
            uri.Any(static character => char.IsControl(character) || character is '?' or '#' or '\\'))
        {
            throw new ArgumentException("Use an S3 URI containing a bucket and an object key.", nameof(uri));
        }

        string bucket = uri[scheme.Length..delimiter];
        if (bucket.Length is < 3 or > 63 || !char.IsAsciiLetterOrDigit(bucket[0]) ||
            !char.IsAsciiLetterOrDigit(bucket[^1]) ||
            bucket.Any(static character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) &&
                character is not ('.' or '-')))
        {
            throw new ArgumentException("Use a valid S3 bucket name.", nameof(uri));
        }

        string escapedKey = uri[(delimiter + 1)..];
        for (int index = escapedKey.IndexOf('%'); index >= 0; index = escapedKey.IndexOf('%', index + 3))
        {
            if (index + 2 >= escapedKey.Length || !char.IsAsciiHexDigit(escapedKey[index + 1]) ||
                !char.IsAsciiHexDigit(escapedKey[index + 2]))
            {
                throw new ArgumentException("Use valid percent encoding in the S3 object key.", nameof(uri));
            }
        }
        string key = Uri.UnescapeDataString(escapedKey);
        if (key.Any(char.IsControl))
        {
            throw new ArgumentException("The S3 object key contains a control character.", nameof(uri));
        }
        return new VersionedS3Artifact(bucket, key, versionId);
    }
}
