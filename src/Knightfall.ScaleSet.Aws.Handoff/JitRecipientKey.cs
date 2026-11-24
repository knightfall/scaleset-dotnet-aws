using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Knightfall.ScaleSet.Aws.Handoff;

public sealed class JitRecipientKey : IDisposable
{
    private ECDiffieHellman? _privateKey;

    internal JitRecipientKey(ECDiffieHellman privateKey, ImmutableArray<byte> bootNonce)
    {
        Hello = new JitHello(JitOfferBinding.CurrentProtocolVersion, bootNonce,
            ImmutableArray.CreateRange(P256PublicKey.Export(privateKey)));
        JitOfferCryptography.ValidateHello(Hello);
        _privateKey = privateKey;
    }

    public JitHello Hello { get; }

    public static JitRecipientKey Create()
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var nonce = ImmutableArray.CreateRange(RandomNumberGenerator.GetBytes(JitOfferEncoding.BootNonceBytes));
            return new JitRecipientKey(key, nonce);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public JitPlaintext Open(JitOfferEnvelope envelope, string expectedResourceArn, string expectedAwsUserId, TimeProvider timeProvider)
    {
        var key = _privateKey ?? throw new ObjectDisposedException(nameof(JitRecipientKey));
        if (envelope?.Binding?.Identity is not { } identity ||
            identity.ResourceArn != expectedResourceArn || identity.AwsUserId != expectedAwsUserId)
        {
            throw JitOfferEncoding.InvalidBinding();
        }
        return JitOfferCryptography.Open(envelope, Hello, key, timeProvider);
    }

    public JitPlaintext Open(JitOfferEnvelope envelope, JitHandoffIdentity expectedIdentity, TimeProvider timeProvider)
    {
        if (envelope?.Binding?.Identity is not { } identity || identity != expectedIdentity)
        {
            throw JitOfferEncoding.InvalidBinding();
        }
        return Open(envelope, identity.ResourceArn, identity.AwsUserId, timeProvider);
    }

    public void Dispose() => Interlocked.Exchange(ref _privateKey, null)?.Dispose();
}

public sealed class JitPlaintext : IDisposable
{
    private byte[]? _bytes;

    internal JitPlaintext(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(JitPlaintext));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public override string ToString() => nameof(JitPlaintext);
}
