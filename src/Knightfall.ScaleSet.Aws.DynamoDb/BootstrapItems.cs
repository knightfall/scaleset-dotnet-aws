using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

internal static partial class BootstrapItems
{
    private const int MaximumPayloadCharacters = 380_000;
    private const int MaximumHelloCharacters = 1024;
    internal const string TrustedHelloIssuer = "iam-url-v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static void RequireClient(IAmazonDynamoDB client, string tableName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (client.Config.MaxErrorRetry != 0 || client.Config.MaxStaleConnectionRetries != 0)
        {
            throw new ArgumentException("Bootstrap writes require an AWS client with both retry budgets disabled.", nameof(client));
        }
    }

    internal static void RequirePrincipal(string awsUserId, string resourceArn, bool ec2Only = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(awsUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceArn);
        int separator = awsUserId.IndexOf(':', StringComparison.Ordinal);
        if (separator < 5 || !RoleId().IsMatch(awsUserId[..separator]))
        {
            throw new ArgumentException("An exact STS role principal is required.", nameof(awsUserId));
        }

        var match = Ec2Arn().Match(resourceArn);
        if (!match.Success && !ec2Only)
        {
            match = EcsArn().Match(resourceArn);
        }
        if (!match.Success || !awsUserId.AsSpan(separator + 1).SequenceEqual(match.Groups["resourceId"].ValueSpan))
        {
            throw new ArgumentException("The resource ARN must match the STS principal's exact short identity.", nameof(resourceArn));
        }
    }

    internal static string HelloDigest(JitHello hello)
    {
        JitOfferCryptography.ValidateHello(hello);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hello, Json)));
    }

    internal static JitHello DecodeHello(LifecycleKey key, Dictionary<string, AttributeValue> item, DateTimeOffset now)
    {
        RequireKey(key, item, allowExpiration: true);
        if (Text(item, "helloIssuer") != TrustedHelloIssuer || Integer(item, "expiresAt") <= now.ToUnixTimeSeconds())
        {
            throw InvalidItem();
        }
        string payload = Text(item, "payload");
        if (payload.Length > MaximumHelloCharacters)
        {
            throw InvalidItem();
        }
        var hello = Deserialize<JitHello>(payload);
        if (key != LifecycleKey.Hello(Text(item, "awsUserId")) ||
            Text(item, "helloSha256") != HelloDigest(hello) || Integer(item, "protocolVersion") != hello.ProtocolVersion)
        {
            throw InvalidItem();
        }
        return hello;
    }

    internal static Dictionary<string, AttributeValue> EncodeOffer(JitOfferEnvelope offer, string helloDigest)
    {
        JitOfferCryptography.ValidateEnvelope(offer);
        var identity = offer.Binding.Identity;
        var key = LifecycleKey.Offer(identity.AwsUserId);
        var item = Key(key);
        item.Add("awsUserId", String(identity.AwsUserId));
        item.Add("resourceArn", String(identity.ResourceArn));
        item.Add("helloSha256", String(helloDigest));
        item.Add("protocolVersion", Number(offer.Binding.ProtocolVersion));
        item.Add("offerId", String(offer.Binding.OfferId));
        item.Add("offerExpiresAt", Number(offer.Binding.ExpiresAtUnixMilliseconds));
        item.Add("payload", String(Serialize(new OfferPayload(offer.Binding, offer.EphemeralPublicKey.ToArray(),
            offer.Nonce.ToArray(), offer.Ciphertext.ToArray(), offer.Tag.ToArray()))));
        item.Add("offerSha256", String(OfferDigest(item)));
        return item;
    }

    internal static JitOfferEnvelope DecodeOffer(
        LifecycleKey key, Dictionary<string, AttributeValue> item, string helloDigest)
    {
        RequireKey(key, item);
        var payload = Deserialize<OfferPayload>(Text(item, "payload"));
        if (payload.Binding is null || payload.EphemeralPublicKey is null || payload.Nonce is null ||
            payload.Ciphertext is null || payload.Tag is null)
        {
            throw InvalidItem();
        }
        var envelope = new JitOfferEnvelope(payload.Binding, ImmutableArray.Create(payload.EphemeralPublicKey),
            ImmutableArray.Create(payload.Nonce), ImmutableArray.Create(payload.Ciphertext), ImmutableArray.Create(payload.Tag));
        JitOfferCryptography.ValidateEnvelope(envelope);
        var identity = payload.Binding.Identity;
        if (key != LifecycleKey.Offer(identity.AwsUserId) || Text(item, "awsUserId") != identity.AwsUserId ||
            Text(item, "resourceArn") != identity.ResourceArn || Text(item, "helloSha256") != helloDigest ||
            Text(item, "offerId") != payload.Binding.OfferId || Text(item, "offerSha256") != OfferDigest(item) ||
            Integer(item, "protocolVersion") != payload.Binding.ProtocolVersion ||
            Integer(item, "offerExpiresAt") != payload.Binding.ExpiresAtUnixMilliseconds)
        {
            throw InvalidItem();
        }
        return envelope;
    }

    private static string OfferDigest(Dictionary<string, AttributeValue> item) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Text(item, "payload"))));

    internal static bool IsIndeterminate(Exception error) => error switch
    {
        AmazonServiceException service => IsTransientStatus(service.StatusCode) || service.Retryable is { Throttling: true } ||
            service.ErrorCode is "ProvisionedThroughputExceededException" or "ProvisionedThroughputExceeded" or
                "RequestLimitExceeded" or "RequestLimitExceededException" or "ThrottlingException",
        HttpRequestException http => http.StatusCode is not { } status || IsTransientStatus(status),
        IOException or TimeoutException or OperationCanceledException => true,
        _ => false,
    };

    internal static Dictionary<string, AttributeValue> Key(LifecycleKey key) =>
        new(StringComparer.Ordinal) { ["PK"] = String(key.PartitionKey), ["SK"] = String(key.SortKey) };

    internal static AttributeValue String(string text) => new() { S = text };

    internal static AttributeValue Number(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    internal static string Text(Dictionary<string, AttributeValue> item, string field) =>
        item.TryGetValue(field, out var attribute) && attribute is { S: { } value } ? value : throw InvalidItem();

    internal static long Integer(Dictionary<string, AttributeValue> item, string field) =>
        item.TryGetValue(field, out var attribute) && attribute is not null &&
        long.TryParse(attribute.N, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : throw InvalidItem();

    internal static void RequireKey(LifecycleKey key, Dictionary<string, AttributeValue> item, bool allowExpiration = false)
    {
        if (Text(item, "PK") != key.PartitionKey || Text(item, "SK") != key.SortKey || (!allowExpiration && item.ContainsKey("expiresAt")))
        {
            throw InvalidItem();
        }
    }

    internal static string Serialize<T>(T value)
    {
        string payload = JsonSerializer.Serialize(value, Json);
        return payload.Length <= MaximumPayloadCharacters ? payload : throw InvalidItem();
    }

    internal static T Deserialize<T>(string payload) where T : class =>
        payload.Length <= MaximumPayloadCharacters && JsonSerializer.Deserialize<T>(payload, Json) is { } value
            ? value : throw InvalidItem();

    internal static InvalidDataException InvalidItem() => new("Invalid durable bootstrap record.");

    private static bool IsTransientStatus(HttpStatusCode status) => (int)status is 408 or 429 or >= 500 and <= 599;

    [GeneratedRegex("\\AAROA[A-Z0-9]{1,60}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleId();

    [GeneratedRegex("\\Aarn:aws(?:-[a-z0-9-]+)?:ec2:[a-z0-9-]+:[0-9]{12}:instance/(?<resourceId>i-(?:[0-9a-f]{8}|[0-9a-f]{17}))\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Ec2Arn();

    [GeneratedRegex("\\Aarn:aws(?:-[a-z0-9-]+)?:ecs:[a-z0-9-]+:[0-9]{12}:task/[A-Za-z0-9_-]+/(?<resourceId>[0-9a-f]{32})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EcsArn();

    private sealed record OfferPayload(JitOfferBinding Binding, byte[] EphemeralPublicKey, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
