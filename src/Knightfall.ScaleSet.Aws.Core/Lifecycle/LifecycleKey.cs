namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public enum LifecycleItemKind
{
    Lease = 0,
    Profile = 1,
    Claim = 2,
    Session = 3,
    Demand = 4,
    Admission = 5,
    Effects = 6,
    EffectRebuild = 7,
    ActiveOperation = 8,
    Checkpoint = 9,
    Message = 10,
    Acquisition = 11,
    Job = 12,
    Runner = 13,
    RunnerIdentity = 14,
    Operation = 15,
    Hello = 16,
    Offer = 17,
    Status = 18,
    RunnerInventory = 19,
    ActiveRunner = 20,
    Drain = 21,
    Handoff = 22,
    HandoffHead = 23,
    Ec2LaunchLineage = 24,
}

public sealed record LifecycleProfile
{
    public LifecycleProfile(string deployment, string profileId)
    {
        Deployment = LifecycleKey.Segment(deployment, nameof(deployment));
        ProfileId = LifecycleKey.Segment(profileId, nameof(profileId));
    }

    public string Deployment { get; }
    public string ProfileId { get; }
    public string PartitionKey => $"PROFILE#{Deployment}#{ProfileId}";
}

public sealed record LifecycleKey
{
    private LifecycleKey(string partitionKey, string sortKey, LifecycleItemKind kind, string? deployment)
    {
        PartitionKey = partitionKey;
        SortKey = sortKey;
        Kind = kind;
        Deployment = deployment;
    }

    public string PartitionKey { get; }
    public string SortKey { get; }
    public LifecycleItemKind Kind { get; }
    public string? Deployment { get; }

    public static LifecycleKey Lease(string deployment) =>
        new("ENV#" + Segment(deployment, nameof(deployment)), "LEASE", LifecycleItemKind.Lease, deployment);

    public static LifecycleKey Profile(LifecycleProfile profile) =>
        new("ENV#" + profile.Deployment, "PROFILE#" + profile.ProfileId, LifecycleItemKind.Profile, profile.Deployment);

    public static LifecycleKey Claim(string canonicalRemoteId) =>
        new("CLAIM#" + Identity(canonicalRemoteId, nameof(canonicalRemoteId)), "OWNER", LifecycleItemKind.Claim, null);

    public static LifecycleKey Session(LifecycleProfile profile) =>
        ProfileItem(profile, "SESSION", LifecycleItemKind.Session);

    public static LifecycleKey Demand(LifecycleProfile profile) =>
        ProfileItem(profile, "DEMAND", LifecycleItemKind.Demand);

    public static LifecycleKey Admission(LifecycleProfile profile) =>
        ProfileItem(profile, "ADMISSION", LifecycleItemKind.Admission);

    public static LifecycleKey Effects(LifecycleProfile profile) =>
        ProfileItem(profile, "EFFECTS", LifecycleItemKind.Effects);

    public static LifecycleKey EffectRebuild(LifecycleProfile profile) =>
        ProfileItem(profile, "EFFECT-REBUILD", LifecycleItemKind.EffectRebuild);

    public static LifecycleKey ActiveOperation(LifecycleProfile profile, Guid generation, string intentId) =>
        ProfileItem(profile, ActiveOperationPrefix(generation) + Identity(intentId, nameof(intentId)), LifecycleItemKind.ActiveOperation);

    public static LifecycleKey Checkpoint(LifecycleProfile profile, Guid sessionId) =>
        ProfileItem(profile, "CHECKPOINT#" + GuidIdentity(sessionId, nameof(sessionId)), LifecycleItemKind.Checkpoint);

    public static LifecycleKey Message(LifecycleProfile profile, Guid sessionId, long messageId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageId);
        return ProfileItem(profile, FormattableString.Invariant($"MSG#{GuidIdentity(sessionId, nameof(sessionId))}#{messageId}"), LifecycleItemKind.Message);
    }

    public static LifecycleKey Acquisition(LifecycleProfile profile, long requestId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        return ProfileItem(profile, FormattableString.Invariant($"ACQUIRE#{requestId}"), LifecycleItemKind.Acquisition);
    }

    public static LifecycleKey Job(LifecycleProfile profile, string attempt) =>
        ProfileItem(profile, "JOB#" + Identity(attempt, nameof(attempt)), LifecycleItemKind.Job);

    public static LifecycleKey Runner(LifecycleProfile profile, string allocationId) =>
        ProfileItem(profile, "RUNNER#" + Identity(allocationId, nameof(allocationId)), LifecycleItemKind.Runner);

    public static LifecycleKey RunnerInventory(LifecycleProfile profile) =>
        ProfileItem(profile, "RUNNER-INVENTORY", LifecycleItemKind.RunnerInventory);

    public static LifecycleKey ActiveRunner(LifecycleProfile profile, string allocationId) =>
        ProfileItem(profile, "ACTIVE-RUNNER#" + Identity(allocationId, nameof(allocationId)), LifecycleItemKind.ActiveRunner);

    public static LifecycleKey Drain(LifecycleProfile profile) =>
        ProfileItem(profile, "DRAIN", LifecycleItemKind.Drain);

    public static LifecycleKey Handoff(LifecycleProfile profile, Guid allocationId) =>
        ProfileItem(profile, "HANDOFF#" + GuidIdentity(allocationId, nameof(allocationId)), LifecycleItemKind.Handoff);

    public static LifecycleKey HandoffHead(LifecycleProfile profile) =>
        ProfileItem(profile, "HANDOFFS", LifecycleItemKind.HandoffHead);

    public static LifecycleKey Ec2LaunchLineage(LifecycleProfile profile, Guid allocationId) =>
        ProfileItem(profile, "EC2-LAUNCH#" + GuidIdentity(allocationId, nameof(allocationId)), LifecycleItemKind.Ec2LaunchLineage);

    public static LifecycleKey RunnerIdentity(LifecycleProfile profile, long runnerId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runnerId);
        return ProfileItem(profile, FormattableString.Invariant($"RUNNER-ID#{runnerId}"), LifecycleItemKind.RunnerIdentity);
    }

    public static LifecycleKey Operation(LifecycleProfile profile, string intentId) =>
        ProfileItem(profile, "OP#" + Identity(intentId, nameof(intentId)), LifecycleItemKind.Operation);

    public static LifecycleKey Hello(string awsUserId) =>
        BootstrapItem("HELLO#", awsUserId, LifecycleItemKind.Hello);

    public static LifecycleKey Offer(string awsUserId) =>
        BootstrapItem("OFFER#", awsUserId, LifecycleItemKind.Offer);

    public static LifecycleKey Status(string awsUserId) =>
        BootstrapItem("STATUS#", awsUserId, LifecycleItemKind.Status);

    public static string ActiveOperationPrefix(Guid generation) =>
        $"ACTIVE-OP#{GuidIdentity(generation, nameof(generation))}#";

    private static LifecycleKey ProfileItem(LifecycleProfile profile, string sortKey, LifecycleItemKind kind) =>
        new(profile.PartitionKey, sortKey, kind, profile.Deployment);

    private static LifecycleKey BootstrapItem(string prefix, string awsUserId, LifecycleItemKind kind) =>
        new(prefix + Segment(awsUserId, nameof(awsUserId)), "BOOTSTRAP", kind, null);

    private static string GuidIdentity(Guid id, string parameterName) =>
        id != Guid.Empty ? id.ToString("D") : throw new ArgumentException("An exact identifier is required.", parameterName);

    internal static string Segment(string value, string parameterName)
    {
        if (Identity(value, parameterName).Contains('#'))
        {
            throw new ArgumentException("The identifier must not contain the key delimiter.", parameterName);
        }
        return value;
    }

    private static string Identity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException("A nonempty identifier without control characters is required.", parameterName);
        }
        return value;
    }
}
