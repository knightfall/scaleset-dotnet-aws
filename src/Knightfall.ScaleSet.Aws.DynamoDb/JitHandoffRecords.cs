using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public enum JitHandoffState
{
    Staged = 0,
    Offered = 1,
    RegisteredWithoutOffer = 2,
    Revoked = 3,
    ResourceTerminated = 4,
}

public sealed record JitHandoffSnapshot(
    JitHandoffIdentity Identity, JitHello Hello, string HelloSha256, string RunnerName, JitHandoffState State,
    long? RunnerId, string? OfferSha256, Guid? JitAttemptId, long Revision, Guid TransitionId)
{
    public string OfferId => JitOfferBinding.GetOfferId(Identity);

    public string IntentId => "jit-" + OfferId;

    internal static bool IsLowercaseDigest(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigitLower(character));
}

public sealed record JitHandoffChange(LifecycleWriteOutcome Outcome, JitHandoffSnapshot? Handoff = null);
