using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed class EffectEnvelope
{
    public const int SchemaVersion = 1;

    private readonly EnvelopeBody _body;

    private EffectEnvelope(EnvelopeBody body, string json)
    {
        _body = body;
        CanonicalJson = json;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    internal static JsonSerializerOptions Serializer { get; } = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LifecycleProfile Profile => new(_body.Deployment, _body.ProfileId);
    public string IntentId => _body.IntentId;
    public long Generation => _body.Generation;
    public EffectEnvelopeRequest Request => _body.Request;
    public EffectTargetBinding Target => Request.Target;
    public DateTimeOffset EarliestPossibleFirstSend => _body.EarliestPossibleFirstSend;
    public DateTimeOffset? ProviderReplayDeadline => _body.ProviderReplayDeadline;
    public string CanonicalJson { get; }
    public string Sha256 { get; }

    public static EffectEnvelope Create(
        LifecycleProfile profile,
        string intentId,
        long generation,
        EffectEnvelopeRequest request,
        DateTimeOffset earliestPossibleFirstSend,
        DateTimeOffset? providerReplayDeadline)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ = LifecycleKey.Operation(profile, intentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentNullException.ThrowIfNull(request);
        if (earliestPossibleFirstSend == default)
        {
            throw new ArgumentException("The earliest send time is required.", nameof(earliestPossibleFirstSend));
        }

        if (providerReplayDeadline is { } deadline &&
            (!request.SupportsReplay || deadline <= earliestPossibleFirstSend ||
             (request is FargateLaunchEffectRequest && deadline - earliestPossibleFirstSend > TimeSpan.FromMinutes(50))))
        {
            throw new ArgumentException("The provider replay cutoff does not fit the exact request.", nameof(providerReplayDeadline));
        }

        var body = new EnvelopeBody(
            SchemaVersion, profile.Deployment, profile.ProfileId, intentId, generation, request.Normalize(),
            earliestPossibleFirstSend.ToUniversalTime(), providerReplayDeadline?.ToUniversalTime());
        return new EffectEnvelope(body, JsonSerializer.Serialize(body, Serializer));
    }

    public static EffectEnvelope Read(string canonicalJson, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        try
        {
            var body = JsonSerializer.Deserialize<EnvelopeBody>(canonicalJson, Serializer);
            if (body is null || body.Version != SchemaVersion)
            {
                throw InvalidEnvelope();
            }

            var envelope = Create(new LifecycleProfile(body.Deployment, body.ProfileId), body.IntentId, body.Generation,
                body.Request, body.EarliestPossibleFirstSend, body.ProviderReplayDeadline);
            if (!envelope.MatchesContent(canonicalJson, expectedSha256))
            {
                throw InvalidEnvelope();
            }

            return envelope;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw InvalidEnvelope();
        }
    }

    public bool Matches(EffectEnvelope other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return MatchesContent(other.CanonicalJson, other.Sha256);
    }

    private bool MatchesContent(string canonicalJson, string sha256) =>
        StringComparer.Ordinal.Equals(CanonicalJson, canonicalJson) && StringComparer.Ordinal.Equals(Sha256, sha256);

    private static InvalidDataException InvalidEnvelope() =>
        new("The durable effect envelope is malformed or does not match its original digest and canonical bytes.");

    private sealed record EnvelopeBody(
        int Version,
        string Deployment,
        string ProfileId,
        string IntentId,
        long Generation,
        EffectEnvelopeRequest Request,
        DateTimeOffset EarliestPossibleFirstSend,
        DateTimeOffset? ProviderReplayDeadline);
}
