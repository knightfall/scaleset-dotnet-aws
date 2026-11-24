using System.Globalization;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public sealed record GitHubJitGenerateEffectRequest(
    string ScopeIdentity, long ScaleSetId, Guid AllocationId, string ResourceArn, Guid Incarnation,
    string AwsUserId, string HelloSha256, string OfferId, string RunnerName) : EffectEnvelopeRequest
{
    public string WorkFolder { get; } = "_work";

    [JsonIgnore]
    public override EffectTargetBinding Target => GitHubRunnerEffectBinding.Target(ScopeIdentity, ScaleSetId, Incarnation, RunnerName);

    internal override EffectEnvelopeRequest Normalize()
    {
        GitHubRunnerEffectBinding.Validate(ScopeIdentity, ScaleSetId, AllocationId, ResourceArn, Incarnation, RunnerName);
        EffectRequestIdentifiers.Match(HelloSha256, @"\A[a-f0-9]{64}\z", nameof(HelloSha256));
        EffectRequestIdentifiers.Match(OfferId, @"\A[a-f0-9]{64}\z", nameof(OfferId));
        EffectRequestIdentifiers.Match(AwsUserId, @"\AAROA[A-Z0-9]+:(?:i-(?:[a-f0-9]{8}|[a-f0-9]{17})|[a-f0-9]{32})\z", nameof(AwsUserId));
        if (!AwsUserId.AsSpan(AwsUserId.IndexOf(':') + 1).SequenceEqual(ResourceArn.AsSpan(ResourceArn.LastIndexOf('/') + 1)) ||
            RunnerName != "ss-" + OfferId[..48])
        {
            throw new ArgumentException("The handoff must identify the exact executor and deterministic runner.");
        }
        return this;
    }
}

public sealed record GitHubRunnerRemoveEffectRequest(
    string ScopeIdentity, long ScaleSetId, Guid AllocationId, string ResourceArn, Guid Incarnation, string RunnerName,
    long RunnerId) : EffectEnvelopeRequest
{
    [JsonIgnore]
    public override EffectTargetBinding Target =>
        GitHubRunnerEffectBinding.Target(ScopeIdentity, ScaleSetId, Incarnation, RunnerName + "/" + RunnerId.ToString(CultureInfo.InvariantCulture));

    internal override EffectEnvelopeRequest Normalize()
    {
        GitHubRunnerEffectBinding.Validate(ScopeIdentity, ScaleSetId, AllocationId, ResourceArn, Incarnation, RunnerName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RunnerId);
        return this;
    }
}

internal static class GitHubRunnerEffectBinding
{
    internal static EffectTargetBinding Target(string scope, long scaleSetId, Guid incarnation, string runner) =>
        new(scope, FormattableString.Invariant($"{scaleSetId}/{incarnation:D}/{runner}"));

    internal static void Validate(
        string scope, long scaleSetId, Guid allocationId, string resourceArn, Guid incarnation, string runnerName)
    {
        if (GitHubRoutingIdentity.Scope("https://" + scope, nameof(scope)) != scope)
        {
            throw new ArgumentException("An exact canonical GitHub scope is required.", nameof(scope));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);
        EffectRequestIdentifiers.Guid(allocationId, nameof(allocationId));
        EffectRequestIdentifiers.Guid(incarnation, nameof(incarnation));
        EffectRequestIdentifiers.Match(resourceArn,
            @"\A(?:" + EffectRequestIdentifiers.Ec2InstanceArn + "|" + EffectRequestIdentifiers.EcsTaskArn + @")\z", nameof(resourceArn));
        EffectRequestIdentifiers.Match(runnerName, @"\Ass-[a-f0-9]{48}\z", nameof(runnerName));
    }
}
