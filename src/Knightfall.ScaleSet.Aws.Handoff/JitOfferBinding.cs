using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Knightfall.ScaleSet.Aws.Handoff;

public sealed record JitHandoffIdentity(
    string Deployment,
    string ProfileId,
    string ScopeIdentity,
    long ScaleSetId,
    Guid AllocationId,
    string ResourceArn,
    Guid Incarnation,
    string AwsUserId);

public sealed record JitHello(
    int ProtocolVersion, ImmutableArray<byte> BootNonce, ImmutableArray<byte> RecipientPublicKey)
{
    public override string ToString() => nameof(JitHello);
}

public sealed record JitOfferBinding(
    int ProtocolVersion,
    JitHandoffIdentity Identity,
    ImmutableArray<byte> BootNonce,
    string OfferId,
    long ExpiresAtUnixMilliseconds)
{
    public const int CurrentProtocolVersion = 1;

    public static JitOfferBinding Create(JitHandoffIdentity identity, ImmutableArray<byte> bootNonce, DateTimeOffset expiresAt)
    {
        var expiry = expiresAt.ToUnixTimeMilliseconds();
        if (!JitOfferEncoding.IsBootNonce(bootNonce) || expiry <= 0)
        {
            throw JitOfferEncoding.InvalidBinding();
        }
        return new JitOfferBinding(CurrentProtocolVersion, identity, bootNonce, GetOfferId(identity), expiry);
    }

    public static string GetOfferId(JitHandoffIdentity identity) =>
        Convert.ToHexStringLower(SHA256.HashData(JitOfferEncoding.EncodeIdentity(identity)));

    public override string ToString() => nameof(JitOfferBinding);
}

public sealed record JitOfferEnvelope(
    JitOfferBinding Binding,
    ImmutableArray<byte> EphemeralPublicKey,
    ImmutableArray<byte> Nonce,
    ImmutableArray<byte> Ciphertext,
    ImmutableArray<byte> Tag)
{
    public override string ToString() => nameof(JitOfferEnvelope);
}

internal static class JitOfferEncoding
{
    internal const int BootNonceBytes = 32;
    private const int MaximumTextBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    internal static bool IsBootNonce(ImmutableArray<byte> value) =>
        !value.IsDefault && value.Length == BootNonceBytes && value.AsSpan().IndexOfAnyExcept((byte)0) >= 0;

    internal static void ValidateBinding(JitOfferBinding binding)
    {
        if (binding is null || binding.ProtocolVersion != JitOfferBinding.CurrentProtocolVersion || !IsBootNonce(binding.BootNonce) ||
            binding.ExpiresAtUnixMilliseconds <= 0 || binding.ExpiresAtUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            binding.OfferId != JitOfferBinding.GetOfferId(binding.Identity))
        {
            throw InvalidBinding();
        }
    }

    internal static byte[] EncodeIdentity(JitHandoffIdentity identity)
    {
        using var encoded = new MemoryStream();
        encoded.Write("knightfall.scaleset.aws/jit/offer-id"u8);
        encoded.WriteByte(JitOfferBinding.CurrentProtocolVersion);
        WriteIdentity(encoded, identity);
        return encoded.ToArray();
    }

    internal static byte[] EncodeAuthenticatedData(JitOfferBinding binding)
    {
        using var encoded = new MemoryStream();
        encoded.Write("knightfall.scaleset.aws/jit/aad"u8);
        encoded.WriteByte((byte)binding.ProtocolVersion);
        WriteIdentity(encoded, binding.Identity);
        encoded.Write(binding.BootNonce.AsSpan());
        encoded.Write(Convert.FromHexString(binding.OfferId));
        WriteNumber(encoded, binding.ExpiresAtUnixMilliseconds);
        return encoded.ToArray();
    }

    private static void WriteIdentity(Stream encoded, JitHandoffIdentity identity)
    {
        if (identity is null || identity.ScaleSetId <= 0 || identity.AllocationId == Guid.Empty || identity.Incarnation == Guid.Empty)
        {
            throw InvalidBinding();
        }

        WriteText(encoded, identity.Deployment);
        WriteText(encoded, identity.ProfileId);
        WriteText(encoded, identity.ScopeIdentity);
        WriteNumber(encoded, identity.ScaleSetId);
        encoded.Write(identity.AllocationId.ToByteArray(bigEndian: true));
        WriteText(encoded, identity.ResourceArn);
        encoded.Write(identity.Incarnation.ToByteArray(bigEndian: true));
        WriteText(encoded, identity.AwsUserId);
        if (!IsResourcePrincipal(identity.ResourceArn, identity.AwsUserId))
        {
            throw InvalidBinding();
        }
    }

    private static void WriteText(Stream encoded, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTextBytes || value != value.Trim() || value.Any(char.IsControl))
        {
            throw InvalidBinding();
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw InvalidBinding();
        }
        if (bytes.Length > MaximumTextBytes)
        {
            throw InvalidBinding();
        }
        Span<byte> length = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);
        encoded.Write(length);
        encoded.Write(bytes);
    }

    private static void WriteNumber(Stream encoded, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        encoded.Write(bytes);
    }

    private static bool IsResourcePrincipal(string resourceArn, string awsUserId)
    {
        var arn = resourceArn.Split(':', 6);
        if (arn.Length != 6 || arn[0] != "arn" || (arn[1] != "aws" && !arn[1].StartsWith("aws-", StringComparison.Ordinal)) ||
            arn[1].Any(character => !char.IsAsciiLetterLower(character) && character != '-') ||
            arn[3].Length == 0 || arn[3].Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-') ||
            arn[4].Length != 12 || arn[4].Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        string? resourceId = (arn[2], arn[5].Split('/')) switch
        {
            ("ec2", ["instance", var instance]) when instance.Length is 10 or 19 &&
                instance.StartsWith("i-", StringComparison.Ordinal) && IsLowerHex(instance.AsSpan(2)) => instance,
            ("ecs", ["task", var cluster, var task]) when cluster.Length > 0 &&
                cluster.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') &&
                task.Length == 32 && IsLowerHex(task) => task,
            _ => null,
        };
        var role = awsUserId.Split(':', 2);
        return resourceId is not null && role.Length == 2 && role[1] == resourceId && role[0].Length > 4 &&
            role[0].StartsWith("AROA", StringComparison.Ordinal) &&
            role[0].All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character));
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value) => value.IndexOfAnyExcept(LowerHex) < 0;

    internal static CryptographicException InvalidBinding() => new("The JIT offer binding is invalid.");
}
