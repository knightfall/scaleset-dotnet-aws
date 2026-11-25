using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

internal sealed class Ec2ImdsV2(HttpClient client, Uri endpoint, TimeProvider clock)
{
    private const int MaximumResponseBytes = 16 * 1024;

    internal static readonly Uri InstanceEndpoint = new("http://169.254.169.254/");

    internal static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    internal async Task<Ec2MetadataIdentity> ReadAsync(string roleName, CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
        var token = tokenSource.Token;
        try
        {
            string imdsToken = await ReadTokenAsync(token).ConfigureAwait(false);
            var document = await ReadIdentityAsync(imdsToken, token).ConfigureAwait(false);
            var credentials = await ReadRoleCredentialsAsync(imdsToken, roleName, token).ConfigureAwait(false);
            return new Ec2MetadataIdentity(document, credentials);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or DecoderFallbackException ||
                                      (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw InvalidMetadata();
        }
    }

    private async Task<string> ReadTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(endpoint, "latest/api/token"));
        request.Headers.Add("X-aws-ec2-metadata-token-ttl-seconds", "60");
        var bytes = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if (bytes.Length == 0 || bytes.Any(static value => value is < 0x21 or > 0x7e))
            {
                throw InvalidMetadata();
            }
            return Encoding.ASCII.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<Ec2InstanceDocument> ReadIdentityAsync(string imdsToken, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync("latest/dynamic/instance-identity/document", imdsToken, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        EnsureUniqueObject(root);
        return new Ec2InstanceDocument(Field(root, "accountId"), Field(root, "region"), Field(root, "instanceId"),
            root.TryGetProperty("imageId", out _) ? Field(root, "imageId") : null);
    }

    private async Task<SessionAWSCredentials> ReadRoleCredentialsAsync(string imdsToken, string roleName, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync("latest/meta-data/iam/security-credentials/" + Uri.EscapeDataString(roleName),
            imdsToken, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            EnsureUniqueObject(root);
            if (Field(root, "Code") != "Success" || !root.TryGetProperty("Expiration", out var expiration) ||
                expiration.ValueKind != JsonValueKind.String || !expiration.TryGetDateTimeOffset(out var expiresAt) ||
                expiresAt <= clock.GetUtcNow().AddSeconds(30))
            {
                throw InvalidMetadata();
            }
            string accessId = Field(root, "AccessKeyId");
            string secret = Field(root, "SecretAccessKey");
            string credentialToken = Field(root, "Token");
            if (!accessId.StartsWith("ASIA", StringComparison.Ordinal) || !accessId.All(char.IsAsciiLetterOrDigit) ||
                !VisibleAscii(secret) || !VisibleAscii(credentialToken))
            {
                throw InvalidMetadata();
            }
            return new SessionAWSCredentials(accessId, secret, credentialToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<byte[]> ReadAsync(string path, string imdsToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        request.Headers.Add("X-aws-ec2-metadata-token", imdsToken);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw InvalidMetadata();
        }

        await using var content = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return content.ToArray();
                }
                if (content.Length + read > MaximumResponseBytes)
                {
                    throw InvalidMetadata();
                }
                content.Write(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (content.TryGetBuffer(out var copied))
            {
                CryptographicOperations.ZeroMemory(copied.AsSpan());
            }
        }
    }

    private static string Field(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } field)
        {
            return field;
        }
        throw InvalidMetadata();
    }

    private static void EnsureUniqueObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidMetadata();
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw InvalidMetadata();
            }
        }
    }

    private static bool VisibleAscii(string value) => value.All(static character => character is >= '!' and <= '~');

    private static IOException InvalidMetadata() => new("EC2 instance metadata was unavailable or invalid.");
}

internal sealed record Ec2InstanceDocument(string Account, string Region, string InstanceId, string? ImageId = null);

internal sealed record Ec2MetadataIdentity(Ec2InstanceDocument Document, SessionAWSCredentials Credentials)
{
    public override string ToString() => nameof(Ec2MetadataIdentity);
}
