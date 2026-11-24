using System.Security.Cryptography;
using System.Text;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class EffectEnvelopeScaleSetCreateTests
{
    private static readonly LifecycleProfile Profile = new("test", "runners");
    private static readonly DateTimeOffset Now = new(2050, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Theory]
    [InlineData("https://github.com/Organization", "github.com/organization", 0L)]
    [InlineData("https://www.github.com/Organization/Repo/", "github.com/organization/repo", 1L)]
    [InlineData("https://example.ghe.com/Organization/Repo", "example.ghe.com/organization/repo", long.MaxValue)]
    [InlineData("https://example.ghe.com/enterprises/Account", "example.ghe.com/enterprises/account", 0L)]
    [InlineData("https://github.com/enterprises/Account", "github.com/enterprises/account", long.MaxValue)]
    public void PersistsOriginalWireInputsAndDerivesTheExactCanonicalRemoteTarget(
        string url, string canonicalScope, long runnerGroupId)
    {
        var request = CreateRequest() with { ScopeUrl = url, RunnerGroupId = runnerGroupId };
        var envelope = Envelope(request);

        var roundTrip = EffectEnvelope.Read(envelope.CanonicalJson, envelope.Sha256);
        var actual = Assert.IsType<GitHubScaleSetCreateEffectRequest>(roundTrip.Request);
        Assert.Equal(url, actual.ScopeUrl);
        Assert.Equal("Default Group", actual.RunnerGroupName);
        Assert.Equal(runnerGroupId, actual.RunnerGroupId);
        Assert.Equal("Linux-Runners", actual.ScaleSetName);
        Assert.Equal(["z-label", "LINUX-RUNNERS", "a-label"], actual.Labels);
        Assert.Equal(canonicalScope, roundTrip.Target.Scope);
        Assert.Equal(canonicalScope, GitHubRoutingIdentity.Scope(url));
        Assert.Equal(FormattableString.Invariant($"{runnerGroupId}/linux-runners"), roundTrip.Target.Incarnation);
        Assert.Null(roundTrip.ProviderReplayDeadline);
        Assert.Contains("\"operation\":\"github-scale-set-create\"", roundTrip.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalTargetFoldsScopeAndNameButDigestPreservesExactRequestValues()
    {
        var original = CreateRequest();
        var envelope = Envelope(original);

        var alias = Envelope(original with
        {
            ScopeUrl = "https://www.github.com/ORGANIZATION/REPO/",
            ScaleSetName = "linux-runners",
        });
        var differentGroup = Envelope(original with { RunnerGroupId = 1 });
        var differentGroupName = Envelope(original with { RunnerGroupName = "Renamed Group" });
        var differentScope = Envelope(original with { ScopeUrl = "https://github.com/organization/other" });
        var labelsReordered = Envelope(original with { Labels = ["a-label", "LINUX-RUNNERS", "z-label"] });

        Assert.Equal(envelope.Target, alias.Target);
        Assert.NotEqual(envelope.Sha256, alias.Sha256);
        Assert.NotEqual(envelope.Target, differentGroup.Target);
        Assert.Equal(envelope.Target, differentGroupName.Target);
        Assert.NotEqual(envelope.Sha256, differentGroupName.Sha256);
        Assert.NotEqual(envelope.Target, differentScope.Target);
        Assert.Equal(envelope.Target, labelsReordered.Target);
        Assert.NotEqual(envelope.Sha256, labelsReordered.Sha256);
    }

    [Theory]
    [InlineData("https://enterprise.example.test/org")]
    [InlineData("https://synthetic-token@github.com/org")]
    [InlineData("https://github.com/org?token=synthetic-token")]
    [InlineData("https://github.com/org/..")]
    [InlineData("https://github.com/org%2Frepo")]
    public void UnsupportedOrAmbiguousScopesAreRejectedWithoutEchoingCredentials(string scopeUrl)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Envelope(CreateRequest() with { ScopeUrl = scopeUrl }));

        Assert.Equal("ScopeUrl", exception.ParamName);
        Assert.DoesNotContain(scopeUrl, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNegativeGroupAndMissingOrMalformedManagedLabels()
    {
        var request = CreateRequest();
        Assert.Throws<ArgumentOutOfRangeException>(() => Envelope(request with { RunnerGroupId = -1 }));
        Assert.Throws<ArgumentException>(() => Envelope(request with { RunnerGroupName = "\n" }));
        Assert.Throws<ArgumentException>(() => Envelope(request with { ScaleSetName = " " }));

        var duplicateUnicodeLabels = request with { Labels = ["LINUX-RUNNERS", "Straße", "Straẞe"] };
        Assert.Throws<ArgumentException>(() => Envelope(request with { Labels = [] }));
        Assert.Throws<ArgumentException>(() => Envelope(request with { Labels = ["wrong-name"] }));
        Assert.Throws<ArgumentException>(() => Envelope(request with { Labels = ["linux-runners", "bad\nlabel"] }));
        Assert.Throws<ArgumentException>(() => Envelope(request with { Labels = ["linux-runners", "\uD800"] }));
        Assert.Throws<ArgumentException>(() => Envelope(duplicateUnicodeLabels));
        Assert.Equal(GitHubRoutingIdentity.Name("Straße"), GitHubRoutingIdentity.Name("Straẞe"));
    }

    [Fact]
    public void ScaleSetCreationNeverGainsReplayPermissionAfterAnUncertainSend()
    {
        var request = CreateRequest();
        Assert.Throws<ArgumentException>(
            () => EffectEnvelope.Create(Profile, "create", 1, request, Now, Now.AddMinutes(1)));

        var first = Guid.NewGuid();
        var uncertain = EffectJournalEntry.Plan(Envelope(request))
            .Begin(first, 1, Now, Now.AddMinutes(1), Now.AddMinutes(2))
            .MarkUncertain(first);

        Assert.Throws<InvalidOperationException>(() =>
            uncertain.Begin(Guid.NewGuid(), 2, Now.AddSeconds(1), Now.AddMinutes(1), Now.AddMinutes(2)));
    }

    [Fact]
    public void DurableReadRejectsInjectedAuthenticationFields()
    {
        var envelope = Envelope(CreateRequest());
        const string secret = "fixture-only-secret";
        string altered = envelope.CanonicalJson.Replace("\"runnerGroupId\":0",
            "\"runnerGroupId\":0,\"token\":\"" + secret + "\"", StringComparison.Ordinal);
        Assert.NotEqual(envelope.CanonicalJson, altered);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(altered)));

        var exception = Assert.Throws<InvalidDataException>(() => EffectEnvelope.Read(altered, hash));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    private static GitHubScaleSetCreateEffectRequest CreateRequest() =>
        new("https://github.com/organization/repo", "Default Group", 0, "Linux-Runners", ["z-label", "LINUX-RUNNERS", "a-label"]);

    private static EffectEnvelope Envelope(GitHubScaleSetCreateEffectRequest request) =>
        EffectEnvelope.Create(Profile, "create", 1, request, Now, null);
}
