using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(Ec2LaunchEffectRequest), "ec2-launch")]
[JsonDerivedType(typeof(FargateLaunchEffectRequest), "fargate-launch")]
[JsonDerivedType(typeof(Ec2TerminateEffectRequest), "ec2-terminate")]
[JsonDerivedType(typeof(FargateStopEffectRequest), "fargate-stop")]
[JsonDerivedType(typeof(GitHubSessionRetireEffectRequest), "github-session-retire")]
[JsonDerivedType(typeof(GitHubScaleSetCreateEffectRequest), "github-scale-set-create")]
[JsonDerivedType(typeof(GitHubJitGenerateEffectRequest), "github-jit-generate")]
[JsonDerivedType(typeof(GitHubRunnerRemoveEffectRequest), "github-runner-remove")]
public abstract record EffectEnvelopeRequest
{
    private protected EffectEnvelopeRequest()
    {
    }

    [JsonIgnore]
    public abstract EffectTargetBinding Target { get; }

    internal virtual bool SupportsReplay => false;

    internal abstract EffectEnvelopeRequest Normalize();
}

public sealed record EffectTargetBinding(string Scope, string Incarnation);

public sealed record Ec2LaunchEffectRequest(
    Guid AllocationId,
    string Region,
    string SubnetId,
    string AvailabilityZone,
    string LaunchTemplateId,
    long LaunchTemplateVersion,
    string? InstanceType,
    ImmutableArray<string> SecurityGroupIds,
    string InstanceProfileArn,
    Guid ClientToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int AttemptOrdinal = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Ec2Tenancy? Tenancy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Ec2LaunchOwnershipTags? OwnershipTags = null) : EffectEnvelopeRequest
{
    public int MinCount { get; } = 1;
    public int MaxCount { get; } = 1;

    [JsonIgnore]
    public override EffectTargetBinding Target => new(Region + "/" + AvailabilityZone + "/" + SubnetId, AllocationId.ToString("D"));

    internal override bool SupportsReplay => true;

    internal override EffectEnvelopeRequest Normalize()
    {
        EffectRequestIdentifiers.Guid(AllocationId, nameof(AllocationId));
        EffectRequestIdentifiers.Guid(ClientToken, nameof(ClientToken));
        ArgumentOutOfRangeException.ThrowIfNegative(AttemptOrdinal);
        EffectRequestIdentifiers.Match(Region, @"\A[a-z]{2}(?:-[a-z]+)+-[0-9]+\z", nameof(Region));
        EffectRequestIdentifiers.Match(SubnetId, EffectRequestIdentifiers.SubnetId, nameof(SubnetId));
        ArgumentNullException.ThrowIfNull(AvailabilityZone);
        if (!AvailabilityZone.StartsWith(Region, StringComparison.Ordinal) || AvailabilityZone.Length <= Region.Length ||
            !EffectRequestIdentifiers.IsMatch(AvailabilityZone[Region.Length..], @"\A(?:[a-z]|-[a-z0-9-]+[0-9][a-z])\z"))
        {
            throw new ArgumentException("The Availability Zone must belong to the launch Region.", nameof(AvailabilityZone));
        }

        EffectRequestIdentifiers.Match(LaunchTemplateId, EffectRequestIdentifiers.TemplateId, nameof(LaunchTemplateId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LaunchTemplateVersion);
        if (InstanceType is not null)
        {
            EffectRequestIdentifiers.Match(InstanceType, @"\A[a-z0-9-]+\.[a-z0-9-]+\z", nameof(InstanceType));
        }
        EffectRequestIdentifiers.Match(InstanceProfileArn, @"\Aarn:aws(?:-[a-z0-9-]+)?:iam::[0-9]{12}:instance-profile/[A-Za-z0-9+=,.@_/-]+\z", nameof(InstanceProfileArn));
        if (Tenancy is { } tenancy && !Enum.IsDefined(tenancy))
        {
            throw new ArgumentException("An explicit supported EC2 tenancy is required.", nameof(Tenancy));
        }
        if ((AttemptOrdinal != 0 || Tenancy is not null) && OwnershipTags is null)
        {
            throw new ArgumentException("An EC2 placement requires exact ownership tags.", nameof(OwnershipTags));
        }
        return this with { SecurityGroupIds = EffectRequestIdentifiers.OrderedIds(SecurityGroupIds, EffectRequestIdentifiers.SecurityGroupId, nameof(SecurityGroupIds)) };
    }
}

public sealed record FargateLaunchEffectRequest(
    Guid AllocationId,
    string ClusterArn,
    string TaskDefinitionArn,
    ImmutableArray<string> SubnetIds,
    ImmutableArray<string> SecurityGroupIds,
    string TaskRoleArn,
    string ExecutionRoleArn,
    string RunnerContainer,
    Guid ClientToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FargateLaunchOwnershipTags? OwnershipTags = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CpuUnits = null) : EffectEnvelopeRequest
{
    public int Count { get; } = 1;
    public string LaunchType { get; } = "FARGATE";
    public string AssignPublicIp { get; } = "DISABLED";
    public bool EnableExecuteCommand { get; }
    public string StartedBy => "ss-" + AllocationId.ToString("N");

    [JsonIgnore]
    public override EffectTargetBinding Target => new(ClusterArn, AllocationId.ToString("D"));

    internal override bool SupportsReplay => true;

    internal override EffectEnvelopeRequest Normalize()
    {
        EffectRequestIdentifiers.Guid(AllocationId, nameof(AllocationId));
        EffectRequestIdentifiers.Guid(ClientToken, nameof(ClientToken));
        EffectRequestIdentifiers.Match(ClusterArn, EffectRequestIdentifiers.EcsClusterArn, nameof(ClusterArn));
        EffectRequestIdentifiers.Match(TaskDefinitionArn, EffectRequestIdentifiers.EcsTaskDefinitionArn, nameof(TaskDefinitionArn));
        EffectRequestIdentifiers.Match(TaskRoleArn, EffectRequestIdentifiers.RoleArn, nameof(TaskRoleArn));
        EffectRequestIdentifiers.Match(ExecutionRoleArn, EffectRequestIdentifiers.RoleArn, nameof(ExecutionRoleArn));
        EffectRequestIdentifiers.Match(RunnerContainer, @"\A[A-Za-z0-9_-]{1,255}\z", nameof(RunnerContainer));
        EffectRequestIdentifiers.RequireSameAccount(ClusterArn, TaskDefinitionArn, nameof(TaskDefinitionArn), matchRegion: true);
        EffectRequestIdentifiers.RequireSameAccount(ClusterArn, TaskRoleArn, nameof(TaskRoleArn), matchRegion: false);
        EffectRequestIdentifiers.RequireSameAccount(ClusterArn, ExecutionRoleArn, nameof(ExecutionRoleArn), matchRegion: false);
        if ((OwnershipTags is null) != (CpuUnits is null) || CpuUnits is <= 0 ||
            (OwnershipTags is not null && (SubnetIds.Length > 16 || SecurityGroupIds.Length > 5)))
        {
            throw new ArgumentException("A tracked Fargate launch requires exact ownership and verified CPU and network inputs.");
        }
        return this with
        {
            SubnetIds = EffectRequestIdentifiers.OrderedIds(SubnetIds, EffectRequestIdentifiers.SubnetId, nameof(SubnetIds)),
            SecurityGroupIds = EffectRequestIdentifiers.OrderedIds(SecurityGroupIds, EffectRequestIdentifiers.SecurityGroupId, nameof(SecurityGroupIds)),
        };
    }
}

public sealed record Ec2TerminateEffectRequest(
    Guid AllocationId,
    string InstanceArn,
    DateTimeOffset InstanceLaunchTime,
    string LaunchTemplateId,
    long LaunchTemplateVersion) : EffectEnvelopeRequest
{
    [JsonIgnore]
    public override EffectTargetBinding Target =>
        new(InstanceArn, FormattableString.Invariant($"{AllocationId:D}/{InstanceLaunchTime.UtcTicks}/{LaunchTemplateId}/{LaunchTemplateVersion}"));

    internal override EffectEnvelopeRequest Normalize()
    {
        EffectRequestIdentifiers.Guid(AllocationId, nameof(AllocationId));
        EffectRequestIdentifiers.Match(InstanceArn, EffectRequestIdentifiers.Ec2InstanceArn, nameof(InstanceArn));
        EffectRequestIdentifiers.Match(LaunchTemplateId, EffectRequestIdentifiers.TemplateId, nameof(LaunchTemplateId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LaunchTemplateVersion);
        if (InstanceLaunchTime == default)
        {
            throw new ArgumentException("An exact original launch time is required.", nameof(InstanceLaunchTime));
        }
        return this with { InstanceLaunchTime = InstanceLaunchTime.ToUniversalTime() };
    }
}

public sealed record FargateStopEffectRequest(
    Guid AllocationId,
    string ClusterArn,
    string TaskArn,
    string TaskDefinitionArn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Incarnation = null) : EffectEnvelopeRequest
{
    [JsonIgnore]
    public override EffectTargetBinding Target => new(TaskArn, AllocationId.ToString("D") + "/" + TaskDefinitionArn +
        (Incarnation is Guid incarnation ? "/" + incarnation.ToString("D") : string.Empty));

    internal override EffectEnvelopeRequest Normalize()
    {
        EffectRequestIdentifiers.Guid(AllocationId, nameof(AllocationId));
        EffectRequestIdentifiers.Match(ClusterArn, EffectRequestIdentifiers.EcsClusterArn, nameof(ClusterArn));
        EffectRequestIdentifiers.Match(TaskArn, EffectRequestIdentifiers.EcsTaskArn, nameof(TaskArn));
        EffectRequestIdentifiers.Match(TaskDefinitionArn, EffectRequestIdentifiers.EcsTaskDefinitionArn, nameof(TaskDefinitionArn));
        EffectRequestIdentifiers.RequireSameAccount(ClusterArn, TaskArn, nameof(TaskArn), matchRegion: true);
        EffectRequestIdentifiers.RequireSameAccount(ClusterArn, TaskDefinitionArn, nameof(TaskDefinitionArn), matchRegion: true);
        string cluster = ClusterArn[(ClusterArn.IndexOf('/') + 1)..];
        string taskCluster = TaskArn.Split('/')[1];
        if (!StringComparer.Ordinal.Equals(cluster, taskCluster))
        {
            throw new ArgumentException("The task target must belong to the selected cluster.", nameof(TaskArn));
        }
        if (Incarnation is Guid incarnation && incarnation != FargateExecutorIncarnation.Compute(TaskArn, TaskDefinitionArn, AllocationId))
        {
            throw new ArgumentException("The task target must use its exact provider incarnation.", nameof(Incarnation));
        }
        return this;
    }
}

public sealed record GitHubSessionRetireEffectRequest(string ScopeUrl, long ScaleSetId, Guid SessionId) : EffectEnvelopeRequest
{
    [JsonIgnore]
    public override EffectTargetBinding Target =>
        new(ScopeUrl, ScaleSetId.ToString(CultureInfo.InvariantCulture) + "/" + SessionId.ToString("D"));

    internal override EffectEnvelopeRequest Normalize()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ScaleSetId);
        EffectRequestIdentifiers.Guid(SessionId, nameof(SessionId));
        EffectRequestIdentifiers.Match(ScopeUrl,
            @"\Ahttps://(?:github\.com|(?:[a-z0-9-]+\.)+ghe\.com)/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)?\z",
            nameof(ScopeUrl));
        if (ScopeUrl.Split('/').Skip(3).Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The GitHub scope must identify the exact target.", nameof(ScopeUrl));
        }
        return this;
    }
}

public sealed record GitHubScaleSetCreateEffectRequest(
    string ScopeUrl,
    string RunnerGroupName,
    long RunnerGroupId,
    string ScaleSetName,
    ImmutableArray<string> Labels) : EffectEnvelopeRequest
{
    [JsonIgnore]
    public override EffectTargetBinding Target => new(
        GitHubRoutingIdentity.Scope(ScopeUrl, nameof(ScopeUrl)),
        RunnerGroupId.ToString(CultureInfo.InvariantCulture) + "/" + GitHubRoutingIdentity.Name(ScaleSetName, nameof(ScaleSetName)));

    internal override EffectEnvelopeRequest Normalize()
    {
        _ = GitHubRoutingIdentity.Scope(ScopeUrl, nameof(ScopeUrl));
        ArgumentOutOfRangeException.ThrowIfNegative(RunnerGroupId);
        _ = GitHubRoutingIdentity.Name(RunnerGroupName, nameof(RunnerGroupName));
        string scaleSet = GitHubRoutingIdentity.Name(ScaleSetName, nameof(ScaleSetName));
        if (Labels.IsDefaultOrEmpty)
        {
            throw new ArgumentException("The scale-set request requires managed labels.", nameof(Labels));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string label in Labels)
        {
            if (!seen.Add(GitHubRoutingIdentity.Name(label, nameof(Labels))))
            {
                throw new ArgumentException("Managed scale-set labels must be distinct.", nameof(Labels));
            }
        }
        if (!seen.Contains(scaleSet))
        {
            throw new ArgumentException("The managed labels must include the scale-set name.", nameof(Labels));
        }

        return this;
    }
}

internal static class EffectRequestIdentifiers
{
    private const string RegionalArn = "arn:aws(?:-[a-z0-9-]+)?:";
    private const string RegionAndAccount = ":[a-z0-9-]+:[0-9]{12}:";
    private const string ResourceName = "[A-Za-z0-9_-]+";

    public const string SecurityGroupId = @"\Asg-(?:[a-f0-9]{8}|[a-f0-9]{17})\z";
    public const string SubnetId = @"\Asubnet-(?:[a-f0-9]{8}|[a-f0-9]{17})\z";
    public const string TemplateId = @"\Alt-(?:[a-f0-9]{8}|[a-f0-9]{17})\z";
    public const string RoleArn = @"\A" + RegionalArn + @"iam::[0-9]{12}:role/[A-Za-z0-9+=,.@_/-]+\z";
    public const string Ec2InstanceArn = @"\A" + RegionalArn + "ec2" + RegionAndAccount + @"instance/i-(?:[a-f0-9]{8}|[a-f0-9]{17})\z";
    public const string EcsClusterArn = @"\A" + RegionalArn + "ecs" + RegionAndAccount + "cluster/" + ResourceName + @"\z";
    public const string EcsTaskDefinitionArn = @"\A" + RegionalArn + "ecs" + RegionAndAccount + "task-definition/" + ResourceName + @":[1-9][0-9]*\z";
    public const string EcsTaskArn = @"\A" + RegionalArn + "ecs" + RegionAndAccount + "task/" + ResourceName + @"/[a-f0-9]{32}\z";

    public static void Guid(Guid value, string parameterName)
    {
        if (value == System.Guid.Empty)
        {
            throw new ArgumentException("An exact nonempty identity is required.", parameterName);
        }
    }

    public static void Match(string value, string pattern, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || !IsMatch(value, pattern))
        {
            throw new ArgumentException("An effect request identifier is invalid.", parameterName);
        }
    }

    public static bool IsMatch(string value, string pattern) =>
        Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    public static ImmutableArray<string> OrderedIds(ImmutableArray<string> values, string pattern, string parameterName)
    {
        if (values.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A nonempty set of exact resource identifiers is required.", parameterName);
        }

        var identities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            Match(value, pattern, parameterName);
            if (!identities.Add(value))
            {
                throw new ArgumentException("Effect request resource identifiers must be unique.", parameterName);
            }
        }
        return identities.ToImmutableArray();
    }

    public static void RequireSameAccount(string sourceArn, string targetArn, string parameterName, bool matchRegion)
    {
        var source = sourceArn.Split(':', 6);
        var target = targetArn.Split(':', 6);
        if (source[1] != target[1] || source[4] != target[4] || (matchRegion && source[3] != target[3]))
        {
            throw new ArgumentException("Effect request resources must share the target account and applicable Region.", parameterName);
        }
    }
}
