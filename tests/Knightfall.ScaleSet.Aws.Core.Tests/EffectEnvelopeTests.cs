using System.Security.Cryptography;
using System.Text;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class EffectEnvelopeTests
{
    private static readonly DateTimeOffset Now = new(2050, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly LifecycleProfile Profile = new("test", "runners");
    private static readonly Guid Allocation = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Token = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid FirstAttempt = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid SecondAttempt = Guid.Parse("10000000-0000-0000-0000-000000000004");

    [Fact]
    public void CanonicalPayloadIsStableAndRetainsExactOriginalInputsAfterConfigurationChanges()
    {
        var original = Launch();
        var reordered = original with { SubnetIds = [.. original.SubnetIds.Reverse()] };
        var envelope = Fargate(original);
        var equivalent = Fargate(reordered);

        Assert.Equal(envelope.CanonicalJson, equivalent.CanonicalJson);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.CanonicalJson))), envelope.Sha256);

        var changed = Fargate(original with { TaskDefinitionArn = TaskDefinition(19) }, generation: 2);
        var generationOnly = Fargate(original, generation: 2);
        Assert.False(envelope.Matches(changed));
        Assert.False(envelope.Matches(generationOnly));
        Assert.NotEqual(envelope.Sha256, generationOnly.Sha256);

        var saved = Persisted(EffectJournalEntry.Plan(envelope));
        var readback = EffectJournalEntry.Read(Profile, saved);
        Assert.True(envelope.Matches(readback.Envelope));
        Assert.Equal(1, readback.Envelope.Generation);
        var request = Assert.IsType<FargateLaunchEffectRequest>(readback.Envelope.Request);
        Assert.Equal(TaskDefinition(17), request.TaskDefinitionArn);
        Assert.Equal(Token, request.ClientToken);
        Assert.Equal(1, request.Count);
        Assert.Equal("FARGATE", request.LaunchType);
        Assert.Equal("DISABLED", request.AssignPublicIp);
        Assert.False(request.EnableExecuteCommand);
        Assert.Contains("\"enableExecuteCommand\":false", readback.Envelope.CanonicalJson, StringComparison.Ordinal);
        Assert.Equal("ss-10000000000000000000000000000001", request.StartedBy);
        Assert.Equal(["subnet-0123456789abcdef0", "subnet-0123456789abcdef1"], request.SubnetIds);
    }

    [Fact]
    public void RejectsTamperedOrNoncanonicalEnvelopeAndAdditionalPayloadFields()
    {
        var envelope = Fargate(Launch());
        string replacedRequest = envelope.CanonicalJson.Replace("runner:17", "runner:19", StringComparison.Ordinal);
        Assert.NotEqual(envelope.CanonicalJson, replacedRequest);
        Assert.Throws<InvalidDataException>(() => EffectEnvelope.Read(replacedRequest, envelope.Sha256));

        string padded = envelope.CanonicalJson + " ";
        Assert.Throws<InvalidDataException>(() => EffectEnvelope.Read(padded, Hash(padded)));

        const string sensitive = "fixture-only-jit-material";
        string unexpected = envelope.CanonicalJson.Replace("\"runnerContainer\":\"runner\"",
            "\"runnerContainer\":\"runner\",\"jitConfig\":\"" + sensitive + "\"", StringComparison.Ordinal);
        Assert.NotEqual(envelope.CanonicalJson, unexpected);
        var error = Assert.Throws<InvalidDataException>(() => EffectEnvelope.Read(unexpected, Hash(unexpected)));
        Assert.DoesNotContain(sensitive, error.ToString(), StringComparison.Ordinal);

        var stored = Persisted(EffectJournalEntry.Plan(envelope));
        Assert.Throws<InvalidDataException>(() => EffectJournalEntry.Read(new LifecycleProfile("test", "other"), stored));
        Assert.Throws<InvalidDataException>(() => EffectJournalEntry.Read(Profile, stored with { IntentId = "different-intent" }));
    }

    [Fact]
    public void TargetPinsExactTaskScopeAndIncarnationAndRejectsAForeignCluster()
    {
        var request = new FargateStopEffectRequest(Allocation, Launch().ClusterArn,
            "arn:aws:ecs:us-east-1:123456789012:task/runners/0123456789abcdef0123456789abcdef", TaskDefinition(17));
        var envelope = EffectEnvelope.Create(Profile, "stop-1", 1, request, Now, null);
        var sameTaskDifferentIncarnation = EffectEnvelope.Create(Profile, "stop-1", 1,
            request with { TaskDefinitionArn = TaskDefinition(18) }, Now, null);
        Assert.Equal(request.TaskArn, envelope.Target.Scope);
        Assert.NotEqual(envelope.Target, sameTaskDifferentIncarnation.Target);
        Assert.NotEqual(envelope.Sha256, sameTaskDifferentIncarnation.Sha256);
        Assert.Throws<ArgumentException>(() => EffectEnvelope.Create(Profile, "stop-1", 1,
            request with { TaskArn = request.TaskArn.Replace("/runners/", "/foreign/", StringComparison.Ordinal) }, Now, null));

        var running = EffectJournalEntry.Plan(envelope).Begin(FirstAttempt, 1, Now, Now.AddMinutes(1), Now.AddMinutes(5));
        var wrongTarget = Evidence(envelope, EffectResolutionKind.Applied, Now.AddSeconds(3)) with
        {
            Target = sameTaskDifferentIncarnation.Target,
        };

        Assert.Throws<InvalidOperationException>(() => running.Resolve(wrongTarget));
        Assert.Equal(EffectState.Settled, running.Resolve(Evidence(envelope, EffectResolutionKind.Applied, Now.AddSeconds(3))).State);
    }

    [Fact]
    public void Ec2LaunchKeepsZonalTokenAndNumericTemplateVersionInTheCanonicalRequest()
    {
        var request = new Ec2LaunchEffectRequest(Allocation, "us-east-1", "subnet-0123456789abcdef0",
            "us-east-1a", "lt-0123456789abcdef0", 12, "c7g.xlarge", ["sg-0123456789abcdef0"],
            "arn:aws:iam::123456789012:instance-profile/bootstrap", Token);
        var envelope = EffectEnvelope.Create(Profile, "ec2-start", 1, request, Now, Now.AddMinutes(3));
        var changedSubnet = EffectEnvelope.Create(Profile, "ec2-start", 1,
            request with { SubnetId = "subnet-0123456789abcdef1", AvailabilityZone = "us-east-1b" }, Now, Now.AddMinutes(3));

        var replay = EffectEnvelope.Read(envelope.CanonicalJson, envelope.Sha256);
        var original = Assert.IsType<Ec2LaunchEffectRequest>(replay.Request);
        Assert.Equal(12L, original.LaunchTemplateVersion);
        Assert.Equal(Token, original.ClientToken);
        Assert.Equal(1, original.MinCount);
        Assert.Equal(1, original.MaxCount);
        Assert.Contains("\"maxCount\":1", replay.CanonicalJson, StringComparison.Ordinal);
        Assert.Equal("us-east-1/us-east-1a/subnet-0123456789abcdef0", replay.Target.Scope);
        Assert.NotEqual(replay.Target, changedSubnet.Target);
        Assert.NotEqual(replay.Sha256, changedSubnet.Sha256);
        Assert.Throws<ArgumentException>(() => EffectEnvelope.Create(Profile, "ec2-start", 1,
            request with { AvailabilityZone = "us-west-2a" }, Now, null));
    }

    [Fact]
    public void DeadlineRenewalIsClampedByLeaseAndByOriginalReplayCutoff()
    {
        var envelope = Fargate(Launch());
        var first = EffectJournalEntry.Plan(envelope)
            .Begin(FirstAttempt, 1, Now, Now.AddMinutes(4), Now.AddMinutes(3));
        Assert.Equal(Now.AddMinutes(3), first.Attempt?.Deadline);

        var renewed = first.RenewAttemptDeadline(FirstAttempt, 1, Now.AddMinutes(1), Now.AddHours(4), Now.AddHours(3));
        DateTimeOffset cutoff = Now.AddMinutes(50);
        Assert.Equal(cutoff, renewed.Attempt?.Deadline);
        Assert.Equal(cutoff, renewed.LatestAuthorizedDeadline);
        Assert.Equal(cutoff, renewed.Envelope.ProviderReplayDeadline);
        Assert.True(renewed.Envelope.Matches(envelope));
        Assert.Throws<InvalidOperationException>(() =>
            renewed.RenewAttemptDeadline(FirstAttempt, 1, cutoff, cutoff.AddHours(1), cutoff.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            renewed.RenewAttemptDeadline(FirstAttempt, 2, Now.AddMinutes(2), cutoff.AddHours(1), cutoff.AddHours(1)));
    }

    [Fact]
    public void ReplayAtCutoffIsDeniedButPriorAuthorizationUsesOnlyTheRemainingTime()
    {
        var envelope = Fargate(Launch());
        DateTimeOffset cutoff = Now.AddMinutes(50);
        var uncertain = EffectJournalEntry.Plan(envelope)
            .Begin(FirstAttempt, 1, Now, Now.AddMinutes(1), Now.AddMinutes(2))
            .MarkUncertain(FirstAttempt);

        var resumed = EffectJournalEntry.Read(Profile, Persisted(uncertain));
        Assert.Throws<InvalidOperationException>(() =>
            resumed.Begin(SecondAttempt, 2, cutoff, cutoff.AddMinutes(2), cutoff.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            resumed.Begin(SecondAttempt, 2, cutoff.AddMinutes(1), cutoff.AddMinutes(2), cutoff.AddHours(1)));

        var replay = resumed.Begin(SecondAttempt, 2, cutoff.AddTicks(-1), cutoff.AddMinutes(2), cutoff.AddHours(1));
        Assert.Equal(cutoff, replay.Attempt?.Deadline);
        Assert.Equal(2, replay.Attempt?.LeaseEpoch);
        Assert.True(replay.EarlierSendUncertain);
        Assert.Equal(envelope.Sha256, replay.Envelope.Sha256);
    }

    [Fact]
    public void NonIdempotentRetirementNeverReplaysAnUncertainSend()
    {
        var request = new GitHubSessionRetireEffectRequest("https://github.com/organization/repository", long.MaxValue, Allocation);
        Assert.Throws<ArgumentException>(() => EffectEnvelope.Create(Profile, "retire", 1, request, Now, Now.AddMinutes(10)));
        var envelope = EffectEnvelope.Create(Profile, "retire", 1, request, Now, null);
        var planned = EffectJournalEntry.Plan(envelope);
        var operation = planned.Begin(FirstAttempt, 1, Now, Now.AddMinutes(1), Now.AddMinutes(2));
        var uncertain = operation.MarkUncertain(FirstAttempt);

        Assert.Equal(EffectState.Uncertain, EffectJournalEntry.Read(Profile, Persisted(uncertain)).State);
        Assert.Throws<InvalidOperationException>(() =>
            uncertain.Begin(SecondAttempt, 2, Now.AddSeconds(30), Now.AddMinutes(1), Now.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() =>
            uncertain.Resolve(Evidence(envelope, EffectResolutionKind.CanceledBeforeSend, Now.AddSeconds(30))));
        Assert.Equal(EffectResolutionKind.CanceledBeforeSend,
            planned.Resolve(Evidence(envelope, EffectResolutionKind.CanceledBeforeSend, Now)).Resolution?.Kind);
    }

    [Fact]
    public void DefinitiveRejectionMustMatchAttemptRequestAndTargetAndCannotEraseAnEarlierUnknownSend()
    {
        var envelope = Fargate(Launch());
        var first = EffectJournalEntry.Plan(envelope)
            .Begin(FirstAttempt, 1, Now, Now.AddMinutes(10), Now.AddHours(1));
        var rejection = Evidence(envelope, EffectResolutionKind.Rejected, Now.AddMinutes(1), FirstAttempt);

        Assert.Throws<InvalidOperationException>(() => first.Resolve(rejection with { AttemptId = SecondAttempt }));
        Assert.Throws<InvalidOperationException>(() => first.Resolve(rejection with { EnvelopeSha256 = Fargate(Launch(), 2).Sha256 }));
        Assert.Throws<InvalidOperationException>(() => first.Resolve(rejection with { Target = new("foreign", envelope.Target.Incarnation) }));
        Assert.Equal(EffectResolutionKind.Rejected, first.Resolve(rejection).Resolution?.Kind);

        var replay = first.MarkUncertain(FirstAttempt).Begin(
            SecondAttempt, 2, Now.AddMinutes(1), Now.AddMinutes(4), Now.AddMinutes(5));
        Assert.Throws<InvalidOperationException>(() => replay.Resolve(rejection with { AttemptId = SecondAttempt }));
        Assert.Equal(EffectState.Executing, replay.State);
    }

    [Fact]
    public void ConfirmedAbsenceRequiresEveryPriorAuthorizedAttemptWindowToHaveClosed()
    {
        var envelope = Fargate(Launch());
        var replay = EffectJournalEntry.Plan(envelope)
            .Begin(FirstAttempt, 1, Now, Now.AddMinutes(10), Now.AddMinutes(12))
            .MarkUncertain(FirstAttempt)
            .Begin(SecondAttempt, 2, Now.AddMinutes(1), Now.AddMinutes(4), Now.AddMinutes(5))
            .MarkUncertain(SecondAttempt);

        var absence = Evidence(envelope, EffectResolutionKind.Absent, Now.AddMinutes(11)) with
        {
            EarliestObservedAt = Now.AddMinutes(10),
        };
        Assert.Throws<InvalidOperationException>(() => replay.Resolve(absence with { EarliestObservedAt = null }));
        Assert.Throws<InvalidOperationException>(() => replay.Resolve(absence with { EarliestObservedAt = Now.AddMinutes(12) }));
        Assert.Throws<InvalidOperationException>(() =>
            replay.Resolve(absence with { EarliestObservedAt = Now.AddMinutes(10).AddTicks(-1) }));
        var settled = replay.Resolve(absence);
        var reloaded = EffectJournalEntry.Read(Profile, Persisted(settled));
        Assert.Equal(EffectState.Settled, reloaded.State);
        Assert.Equal(Now.AddMinutes(10), reloaded.Resolution?.EarliestObservedAt);
        Assert.Equal(EffectResolutionKind.Absent, settled.Resolution?.Kind);
        Assert.Equal(Now.AddMinutes(10), settled.LatestAuthorizedDeadline);
    }

    private static EffectEnvelope Fargate(FargateLaunchEffectRequest request, long generation = 1) =>
        EffectEnvelope.Create(Profile, "launch-1", generation, request, Now, Now.AddMinutes(50));

    private static FargateLaunchEffectRequest Launch() => new(
        Allocation, "arn:aws:ecs:us-east-1:123456789012:cluster/runners", TaskDefinition(17),
        ["subnet-0123456789abcdef1", "subnet-0123456789abcdef0"], ["sg-0123456789abcdef0"],
        "arn:aws:iam::123456789012:role/bootstrap", "arn:aws:iam::123456789012:role/execution", "runner", Token);

    private static string TaskDefinition(int revision) =>
        FormattableString.Invariant($"arn:aws:ecs:us-east-1:123456789012:task-definition/runner:{revision}");

    private static EffectEvidence Evidence(EffectEnvelope envelope, EffectResolutionKind kind, DateTimeOffset at, Guid? attempt = null) =>
        new(kind, envelope.Sha256, envelope.Target, at, attempt);

    private static StoredEffect Persisted(EffectJournalEntry entry) =>
        new(entry.Envelope.IntentId, 1, Guid.NewGuid(), entry.State, entry.WritePayload(), null);

    private static string Hash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
