using System.Numerics;
using System.Security.Cryptography;

namespace Knightfall.ScaleSet.Aws.Handoff;

internal static class P256PublicKey
{
    internal const int CoordinateBytes = 32;
    internal const int EncodedBytes = 1 + (2 * CoordinateBytes);
    private const byte Uncompressed = 4;
    private const string CurveOid = "1.2.840.10045.3.1.7";

    /// <summary>P-256's prime field modulus from NIST SP 800-186, section 3.2.1.3.</summary>
    private static readonly BigInteger Prime = new(Convert.FromHexString("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff"),
        isUnsigned: true, isBigEndian: true);
    private static readonly BigInteger B = new(Convert.FromHexString("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b"),
        isUnsigned: true, isBigEndian: true);

    internal static void Validate(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != EncodedBytes || encoded[0] != Uncompressed)
        {
            throw InvalidKey();
        }
        var x = new BigInteger(encoded.Slice(1, CoordinateBytes), isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(encoded[(1 + CoordinateBytes)..], isUnsigned: true, isBigEndian: true);
        // P-256 has cofactor one, so every finite point on the curve is in the required subgroup.
        var ySquared = y * y;
        var xCubed = x * x * x;
        var rightSide = xCubed - (3 * x) + B;
        var remainder = (ySquared - rightSide) % Prime;
        if (x >= Prime || y >= Prime || !remainder.IsZero)
        {
            throw InvalidKey();
        }
    }

    internal static ECDiffieHellman Import(ReadOnlySpan<byte> encoded)
    {
        Validate(encoded);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = encoded.Slice(1, CoordinateBytes).ToArray(),
                Y = encoded[(1 + CoordinateBytes)..].ToArray(),
            },
        });
    }

    internal static byte[] Export(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (!parameters.Curve.IsNamed || parameters.Curve.Oid.Value != CurveOid ||
            parameters.Q.X is not { Length: CoordinateBytes } x || parameters.Q.Y is not { Length: CoordinateBytes } y)
        {
            throw InvalidKey();
        }

        byte[] encoded = new byte[EncodedBytes];
        encoded[0] = Uncompressed;
        x.AsSpan().CopyTo(encoded.AsSpan(1, CoordinateBytes));
        y.AsSpan().CopyTo(encoded.AsSpan(1 + CoordinateBytes));
        return encoded;
    }

    private static CryptographicException InvalidKey() => new("The JIT public key is invalid.");
}
