using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Knightfall.ScaleSet.Aws.Handoff;

public static class JitOfferCryptography
{
    public const int MaximumCiphertextBytes = 256 * 1024;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static void ValidateHello(JitHello hello)
    {
        if (hello is null || hello.ProtocolVersion != JitOfferBinding.CurrentProtocolVersion ||
            !JitOfferEncoding.IsBootNonce(hello.BootNonce))
        {
            throw new CryptographicException("The JIT hello is invalid.");
        }
        P256PublicKey.Validate(hello.RecipientPublicKey.AsSpan());
    }

    public static void ValidateEnvelope(JitOfferEnvelope envelope)
    {
        if (envelope is null || envelope.Nonce.AsSpan().Length != NonceBytes || envelope.Tag.AsSpan().Length != TagBytes ||
            envelope.Ciphertext.AsSpan().Length is 0 or > MaximumCiphertextBytes)
        {
            throw new CryptographicException("The JIT offer envelope is invalid.");
        }
        JitOfferEncoding.ValidateBinding(envelope.Binding);
        P256PublicKey.Validate(envelope.EphemeralPublicKey.AsSpan());
    }

    public static JitOfferEnvelope Seal(JitOfferBinding binding, JitHello hello, ReadOnlySpan<byte> encodedJit, TimeProvider timeProvider)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Span<byte> nonce = stackalloc byte[NonceBytes];
        RandomNumberGenerator.Fill(nonce);
        return SealWithEphemeral(binding, hello, encodedJit, timeProvider, ephemeral, nonce);
    }

    internal static JitOfferEnvelope SealWithEphemeral(
        JitOfferBinding binding, JitHello hello, ReadOnlySpan<byte> encodedJit, TimeProvider timeProvider,
        ECDiffieHellman ephemeral, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateHello(hello);
        JitOfferEncoding.ValidateBinding(binding);
        if (!binding.BootNonce.AsSpan().SequenceEqual(hello.BootNonce.AsSpan()) ||
            encodedJit.Length is 0 or > MaximumCiphertextBytes || nonce.Length != NonceBytes)
        {
            throw new CryptographicException("The JIT offer cannot be sealed.");
        }
        RequireFresh(binding, timeProvider);

        var publicKey = P256PublicKey.Export(ephemeral);
        var aad = JitOfferEncoding.EncodeAuthenticatedData(binding);
        byte[] ciphertext = new byte[encodedJit.Length];
        Span<byte> tag = stackalloc byte[TagBytes];
        Span<byte> key = stackalloc byte[KeyBytes];
        try
        {
            DeriveKey(ephemeral, hello.RecipientPublicKey.AsSpan(), hello.RecipientPublicKey.AsSpan(), publicKey, aad, key);
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Encrypt(nonce, encodedJit, ciphertext, tag, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new JitOfferEnvelope(binding, ImmutableArray.CreateRange(publicKey), ImmutableArray.Create(nonce),
            ImmutableArray.CreateRange(ciphertext), ImmutableArray.Create<byte>(tag));
    }

    internal static JitPlaintext Open(
        JitOfferEnvelope envelope, JitHello hello, ECDiffieHellman privateKey, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateEnvelope(envelope);
        if (!envelope.Binding.BootNonce.AsSpan().SequenceEqual(hello.BootNonce.AsSpan()))
        {
            throw JitOfferEncoding.InvalidBinding();
        }
        RequireFresh(envelope.Binding, timeProvider);

        var aad = JitOfferEncoding.EncodeAuthenticatedData(envelope.Binding);
        var ephemeral = envelope.EphemeralPublicKey.AsSpan();
        byte[] plaintext = new byte[envelope.Ciphertext.Length];
        Span<byte> key = stackalloc byte[KeyBytes];
        try
        {
            DeriveKey(privateKey, ephemeral, hello.RecipientPublicKey.AsSpan(), ephemeral, aad, key);
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(envelope.Nonce.AsSpan(), envelope.Ciphertext.AsSpan(), envelope.Tag.AsSpan(), plaintext, aad);
            RequireFresh(envelope.Binding, timeProvider);
            return new JitPlaintext(plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void DeriveKey(ECDiffieHellman ownKey, ReadOnlySpan<byte> peerKey, ReadOnlySpan<byte> recipientKey,
        ReadOnlySpan<byte> senderKey, ReadOnlySpan<byte> aad, Span<byte> key)
    {
        using var peer = P256PublicKey.Import(peerKey);
        using var peerPublic = peer.PublicKey;
        byte[] secret = ownKey.DeriveRawSecretAgreement(peerPublic);
        try
        {
            if (secret.Length != P256PublicKey.CoordinateBytes)
            {
                throw new CryptographicException("The JIT shared secret is invalid.");
            }
            ReadOnlySpan<byte> domain = "knightfall.scaleset.aws/jit/key/v1"u8;
            Span<byte> info = stackalloc byte[domain.Length + (2 * P256PublicKey.EncodedBytes)];
            domain.CopyTo(info);
            recipientKey.CopyTo(info[domain.Length..]);
            senderKey.CopyTo(info[(domain.Length + P256PublicKey.EncodedBytes)..]);
            Span<byte> salt = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(aad, salt);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, key, salt, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static void RequireFresh(JitOfferBinding binding, TimeProvider timeProvider)
    {
        if (timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= binding.ExpiresAtUnixMilliseconds)
        {
            throw new CryptographicException("The JIT offer has expired.");
        }
    }
}
