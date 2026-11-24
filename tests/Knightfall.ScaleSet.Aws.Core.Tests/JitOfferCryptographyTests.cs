using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Knightfall.ScaleSet.Aws.Handoff;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class JitOfferCryptographyTests
{
    private const string TaskId = "0123456789abcdef0123456789abcdef";
    private const string ResourceArn = "arn:aws:ecs:ap-southeast-2:111122223333:task/engineering-shell/" + TaskId;
    private const string AwsUserId = "AROAEXAMPLE0123456789:" + TaskId;
    private const string OfferId = "3ab8bcead9cfb3be9f9187c679c2cfecb451b67fd7113cea323f193197d185e1";
    private const string RecipientPoint =
        "046b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296" +
        "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5";
    private const string SenderPoint =
        "047cf27b188d034f7e8a52380304b51ac3c08969e277f21b35a60b48fc47669978" +
        "07775510db8ed040293d9ac69f7430dbba7dade63ce982299e04b79d227873d1";
    private const string Ciphertext = "ad8ce1bb20352a0f134027a22f7291d1d74c45a16a8ed200";
    private const string Tag = "6de8a4fbe0be66824fff1b7842512252";
    private const long ExpiryUnixMilliseconds = 2_524_705_505_678;
    private static readonly ImmutableArray<byte> BootNonce = Hex("a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf");
    private static readonly ImmutableArray<byte> Nonce = Hex("000102030405060708090a0b");
    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeMilliseconds(ExpiryUnixMilliseconds);
    private static readonly TimeProvider Clock = new FixedClock(Expiry.AddMinutes(-1));
    private static readonly JitHandoffIdentity Identity = new(
        "111122223333:ap-southeast-2:engineering-dev", "linux-shell", "example.ghe.com/engineering/tools",
        9_007_199_254_740_993, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), ResourceArn,
        Guid.Parse("fedcba98-7654-3210-aaaa-001122334455"), AwsUserId);

    private static ReadOnlySpan<byte> EncodedJit => "synthetic-encoded-jit-v1"u8;

    [Fact]
    public void MatchesIndependentOpenSslVectorForSealAndOpen()
    {
        // Node/OpenSSL createECDH('prime256v1'), hkdfSync('sha256') and createCipheriv('aes-256-gcm').
        using var recipient = new JitRecipientKey(PrivateKey(RecipientPoint, 1), BootNonce);
        using var ephemeral = PrivateKey(SenderPoint, 2);
        var binding = JitOfferBinding.Create(Identity, BootNonce, Expiry);
        var aad = JitOfferEncoding.EncodeAuthenticatedData(binding);
        Assert.Equal(OfferId, binding.OfferId);
        Assert.Equal(ExpiryUnixMilliseconds, binding.ExpiresAtUnixMilliseconds);
        Assert.Equal("4e8c11bc58b4d42d846ce45f9611d37cbe299129d98e2ab957dee519926a4056",
            Convert.ToHexStringLower(SHA256.HashData(aad)));
        Assert.Equal(RecipientPoint, Convert.ToHexStringLower(recipient.Hello.RecipientPublicKey.AsSpan()));

        var sealedOffer = JitOfferCryptography.SealWithEphemeral(binding, recipient.Hello, EncodedJit, Clock, ephemeral, Nonce.AsSpan());
        Assert.Equal(SenderPoint, Convert.ToHexStringLower(sealedOffer.EphemeralPublicKey.AsSpan()));
        Assert.Equal(Ciphertext, Convert.ToHexStringLower(sealedOffer.Ciphertext.AsSpan()));
        Assert.Equal(Tag, Convert.ToHexStringLower(sealedOffer.Tag.AsSpan()));
        Assert.Equal(Nonce.ToArray(), sealedOffer.Nonce.ToArray());
        using var opened = recipient.Open(VectorOffer(), Identity, Clock);
        Assert.Equal(EncodedJit.ToArray(), opened.Bytes.ToArray());
    }

    [Fact]
    public void ProductionKeysAndNoncesAreFreshAndPlaintextIsClearedOnDispose()
    {
        using var recipient = JitRecipientKey.Create();
        using var secondRecipient = JitRecipientKey.Create();
        Assert.NotEqual(Convert.ToHexString(recipient.Hello.RecipientPublicKey.AsSpan()), Convert.ToHexString(secondRecipient.Hello.RecipientPublicKey.AsSpan()));
        Assert.NotEqual(Convert.ToHexString(recipient.Hello.BootNonce.AsSpan()), Convert.ToHexString(secondRecipient.Hello.BootNonce.AsSpan()));

        var binding = JitOfferBinding.Create(Identity, recipient.Hello.BootNonce, Expiry);
        var first = JitOfferCryptography.Seal(binding, recipient.Hello, EncodedJit, Clock);
        var second = JitOfferCryptography.Seal(binding, recipient.Hello, EncodedJit, Clock);
        Assert.NotEqual(Convert.ToHexString(first.EphemeralPublicKey.AsSpan()), Convert.ToHexString(second.EphemeralPublicKey.AsSpan()));
        Assert.NotEqual(Convert.ToHexString(first.Nonce.AsSpan()), Convert.ToHexString(second.Nonce.AsSpan()));
        Assert.Equal(12, first.Nonce.Length);
        Assert.Equal(16, first.Tag.Length);

        using var plaintext = recipient.Open(first, ResourceArn, AwsUserId, Clock);
        var bytes = plaintext.Bytes;
        Assert.Equal(EncodedJit.ToArray(), bytes.ToArray());
        Assert.Equal(nameof(JitHello), recipient.Hello.ToString());
        Assert.Equal(nameof(JitOfferBinding), binding.ToString());
        Assert.Equal(nameof(JitOfferEnvelope), first.ToString());
        Assert.Equal(nameof(JitPlaintext), plaintext.ToString());
        plaintext.Dispose();
        Assert.Equal(new byte[EncodedJit.Length], bytes.ToArray());
        Assert.Throws<ObjectDisposedException>(() => _ = plaintext.Bytes.Length);
    }

    [Fact]
    public void SameBootNonceAndDifferentValidPrivateKeyCannotOpenOffer()
    {
        using var wrong = new JitRecipientKey(PrivateKey(SenderPoint, 2), BootNonce);
        Assert.Throws<AuthenticationTagMismatchException>(() => wrong.Open(VectorOffer(), Identity, Clock));
    }

    [Fact]
    public void LocalResourcePrincipalAndFullIdentityMustMatchBeforeOpen()
    {
        using var recipient = new JitRecipientKey(PrivateKey(RecipientPoint, 1), BootNonce);
        using var differentBoot = new JitRecipientKey(PrivateKey(RecipientPoint, 1), FlipFirstByte(BootNonce));
        var offer = VectorOffer();
        Assert.Throws<CryptographicException>(() => recipient.Open(offer, ResourceArn.Replace("ap-southeast-2", "us-east-1", StringComparison.Ordinal), AwsUserId, Clock));
        Assert.Throws<CryptographicException>(() => recipient.Open(offer, ResourceArn, "AROAOTHER:" + TaskId, Clock));
        Assert.Throws<CryptographicException>(() => recipient.Open(offer, Identity with { ProfileId = "other" }, Clock));
        Assert.Throws<CryptographicException>(() => differentBoot.Open(offer, Identity, Clock));

        using var result = recipient.Open(offer, ResourceArn, AwsUserId, Clock);
        Assert.Equal(EncodedJit.ToArray(), result.Bytes.ToArray());
    }

    [Theory]
    [InlineData("deployment")]
    [InlineData("profile")]
    [InlineData("scope")]
    [InlineData("set")]
    [InlineData("allocation")]
    [InlineData("resource")]
    [InlineData("incarnation")]
    [InlineData("principal")]
    [InlineData("boot")]
    [InlineData("expiry")]
    public void AuthenticatesAllBindingFieldsEvenWithRecomputedOfferId(string tamper)
    {
        var identity = tamper switch
        {
            "deployment" => Identity with { Deployment = "other-deployment" },
            "profile" => Identity with { ProfileId = "other-profile" },
            "scope" => Identity with { ScopeIdentity = "github.com/other" },
            "set" => Identity with { ScaleSetId = Identity.ScaleSetId + 1 },
            "allocation" => Identity with { AllocationId = Guid.NewGuid() },
            "resource" => Identity with { ResourceArn = ResourceArn.Replace("ap-southeast-2", "us-east-1", StringComparison.Ordinal) },
            "incarnation" => Identity with { Incarnation = Guid.NewGuid() },
            "principal" => Identity with { AwsUserId = "AROAOTHER:" + TaskId },
            _ => Identity,
        };
        var bootNonce = tamper == "boot" ? FlipFirstByte(BootNonce) : BootNonce;
        var expiresAt = tamper == "expiry" ? Expiry.AddMilliseconds(1) : Expiry;
        var edited = VectorOffer() with { Binding = JitOfferBinding.Create(identity, bootNonce, expiresAt) };
        using var recipient = new JitRecipientKey(PrivateKey(RecipientPoint, 1), bootNonce);
        Assert.Throws<AuthenticationTagMismatchException>(() => recipient.Open(edited, identity, Clock));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("negatedPoint")]
    public void DetectsCorruptedCarrierAndADifferentValidPointWithSameSharedX(string tamper)
    {
        var original = VectorOffer();
        var edited = tamper switch
        {
            "nonce" => original with { Nonce = FlipFirstByte(original.Nonce) },
            "ciphertext" => original with { Ciphertext = FlipFirstByte(original.Ciphertext) },
            "tag" => original with { Tag = FlipFirstByte(original.Tag) },
            _ => original with { EphemeralPublicKey = Hex(SenderPoint[..66] + "f888aaee24712fc0d6c26539608bcf244582521ac3167dd661fb4862dd878c2e") },
        };
        JitOfferCryptography.ValidateEnvelope(edited);
        using var recipient = new JitRecipientKey(PrivateKey(RecipientPoint, 1), BootNonce);
        Assert.Throws<AuthenticationTagMismatchException>(() => recipient.Open(edited, Identity, Clock));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("infinity")]
    [InlineData("compressed")]
    [InlineData("trailing")]
    [InlineData("prefix")]
    [InlineData("outsideField")]
    [InlineData("offCurve")]
    public void RejectsMalformedPointInHelloAndOffer(string fault)
    {
        var point = fault switch
        {
            "zero" => Hex("04" + new string('0', 128)),
            "infinity" => Hex("00"),
            "compressed" => Hex("02" + RecipientPoint[2..66]),
            "trailing" => Hex(RecipientPoint + "00"),
            "prefix" => Hex("06" + RecipientPoint[2..]),
            "outsideField" => Hex("04ffffffff00000001000000000000000000000000ffffffffffffffffffffffff" + RecipientPoint[66..]),
            _ => Hex(RecipientPoint[..^2] + "01"),
        };
        var hello = new JitHello(JitOfferBinding.CurrentProtocolVersion, BootNonce, point);
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateHello(hello));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateEnvelope(VectorOffer() with { EphemeralPublicKey = point }));
    }

    [Fact]
    public void ExpiryIsExclusiveForSealAndOpen()
    {
        using var recipient = new JitRecipientKey(PrivateKey(RecipientPoint, 1), BootNonce);
        using var valid = recipient.Open(VectorOffer(), Identity, new FixedClock(Expiry.AddMilliseconds(-1)));
        Assert.Equal(EncodedJit.ToArray(), valid.Bytes.ToArray());

        var expired = new FixedClock(Expiry);
        Assert.Throws<CryptographicException>(() => recipient.Open(VectorOffer(), Identity, expired));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.Seal(VectorOffer().Binding, recipient.Hello, EncodedJit, expired));
    }

    [Fact]
    public void CryptoTelemetryCarriesOnlyBoundedEvidenceAndMeasuresWithoutSampling()
    {
        const string source = "Knightfall.ScaleSet.Aws.Handoff";
        using var parent = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start();
        var spans = new List<Activity>();
        bool sample = true;
        using var traces = new ActivityListener
        {
            ShouldListenTo = item => item.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => sample && options.Parent == parent.Context
                ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(traces);

        var operations = new List<(long Value, KeyValuePair<string, object?>[] Tags)>();
        var durations = new List<(double Value, KeyValuePair<string, object?>[] Tags)>();
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == source)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "scaleset.aws.handoff.operations" && Activity.Current?.TraceId == parent.TraceId)
            {
                operations.Add((value, tags.ToArray()));
            }
        });
        metrics.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "scaleset.aws.handoff.duration" && Activity.Current?.TraceId == parent.TraceId)
            {
                durations.Add((value, tags.ToArray()));
            }
        });
        metrics.Start();

        using var recipient = new JitRecipientKey(PrivateKey(RecipientPoint, 1), BootNonce);
        var offer = JitOfferCryptography.Seal(VectorOffer().Binding, recipient.Hello, EncodedJit, Clock);
        using var opened = recipient.Open(offer, Identity, Clock);
        var tampered = offer with { Tag = FlipFirstByte(offer.Tag) };
        Assert.Throws<AuthenticationTagMismatchException>(() => recipient.Open(tampered, Identity, Clock));
        sample = false;
        Assert.Throws<CryptographicException>(() => recipient.Open(offer, Identity, new FixedClock(Expiry)));

        (string Operation, string Outcome)[] expected = [("seal", "success"), ("open", "success"), ("open", "rejected"), ("open", "expired")];
        Assert.Equal(expected[..3].Select(item => "scaleset.aws.handoff." + item.Operation), spans.Select(span => span.OperationName));
        for (int index = 0; index < spans.Count; index++)
        {
            Assert.Equal(parent.SpanId, spans[index].ParentSpanId);
            AssertTags(spans[index].TagObjects, expected[index]);
        }
        Assert.Equal(expected.Length, operations.Count);
        Assert.Equal(expected.Length, durations.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(1, operations[index].Value);
            Assert.True(double.IsFinite(durations[index].Value) && durations[index].Value >= 0);
            AssertTags(operations[index].Tags, expected[index]);
            AssertTags(durations[index].Tags, expected[index]);
        }

        static void AssertTags(IEnumerable<KeyValuePair<string, object?>> tags, (string Operation, string Outcome) expected) =>
            Assert.Collection(tags,
                item => Assert.Equal(new KeyValuePair<string, object?>("operation", expected.Operation), item),
                item => Assert.Equal(new KeyValuePair<string, object?>("outcome", expected.Outcome), item));
    }

    [Fact]
    public void RejectsInvalidVersionBootNonceOfferIdAndCarrierBounds()
    {
        using var recipient = new JitRecipientKey(PrivateKey(RecipientPoint, 1), BootNonce);
        var hello = recipient.Hello;
        var offer = VectorOffer();
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateHello(hello with { ProtocolVersion = 2 }));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateHello(hello with { BootNonce = Hex(new string('0', 64)) }));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateEnvelope(offer with { Binding = offer.Binding with { ProtocolVersion = 2 } }));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateEnvelope(offer with { Binding = offer.Binding with { OfferId = new string('0', 64) } }));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateEnvelope(offer with { Nonce = offer.Nonce.RemoveAt(0) }));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateEnvelope(offer with { Tag = offer.Tag.RemoveAt(0) }));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateEnvelope(offer with { Ciphertext = [] }));
        var oversized = ImmutableArray.CreateRange(new byte[JitOfferCryptography.MaximumCiphertextBytes + 1]);
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.ValidateEnvelope(offer with { Ciphertext = oversized }));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.Seal(offer.Binding, hello, oversized.AsSpan(), Clock));
        Assert.Throws<CryptographicException>(() => JitOfferCryptography.Seal(offer.Binding, hello with { BootNonce = FlipFirstByte(hello.BootNonce) }, EncodedJit, Clock));
    }

    [Fact]
    public void OfferIdIsStableAcrossFreshBootsAndRequiresAnExactResourcePrincipal()
    {
        Assert.Equal(OfferId, JitOfferBinding.GetOfferId(Identity));
        Assert.Equal(OfferId, JitOfferBinding.Create(Identity, FlipFirstByte(BootNonce), Expiry.AddMinutes(1)).OfferId);
        Assert.Throws<CryptographicException>(() => JitOfferBinding.GetOfferId(Identity with { AwsUserId = "AROAOTHER:different-task" }));
        Assert.Throws<CryptographicException>(() => JitOfferBinding.GetOfferId(Identity with { ScopeIdentity = "github.com/\ud800" }));
        var ec2 = Identity with
        {
            ResourceArn = "arn:aws:ec2:ap-southeast-2:111122223333:instance/i-0123456789abcdef0",
            AwsUserId = "AROAEXAMPLE0123456789:i-0123456789abcdef0",
        };
        Assert.NotEqual(OfferId, JitOfferBinding.GetOfferId(ec2));
    }

    private static JitOfferEnvelope VectorOffer() => new(
        new JitOfferBinding(JitOfferBinding.CurrentProtocolVersion, Identity, BootNonce, OfferId, ExpiryUnixMilliseconds),
        Hex(SenderPoint), Nonce, Hex(Ciphertext), Hex(Tag));

    private static ImmutableArray<byte> Hex(string hex) => ImmutableArray.CreateRange(Convert.FromHexString(hex));

    private static ImmutableArray<byte> FlipFirstByte(ImmutableArray<byte> bytes) => bytes.SetItem(0, (byte)(bytes[0] ^ 0x80));

    private static ECDiffieHellman PrivateKey(string point, byte scalar)
    {
        var publicKey = Convert.FromHexString(point);
        var d = new byte[32];
        d[^1] = scalar;
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] },
            D = d,
        });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
