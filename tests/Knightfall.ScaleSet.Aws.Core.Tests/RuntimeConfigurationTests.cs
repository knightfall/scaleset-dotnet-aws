using System.Text;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class RuntimeConfigurationTests
{
    [Theory]
    [InlineData(ConfigurationFixtures.Ec2Latest, ControllerMode.DryRun, true, 1)]
    [InlineData(ConfigurationFixtures.Ec2Pinned, ControllerMode.DryRun, false, 1)]
    [InlineData(ConfigurationFixtures.Fargate, ControllerMode.Active, false, 1)]
    [InlineData(ConfigurationFixtures.Mixed, ControllerMode.Active, false, 4)]
    public void LoadsEachCanonicalRuntimeFixture(
        string name,
        ControllerMode expectedMode,
        bool expectedInspection,
        int expectedProfiles)
    {
        var json = ConfigurationFixtures.Runtime(name);
        var digest = ConfigurationFixtures.Sha256(json);

        var configuration = RuntimeConfigurationLoader.Load(json, digest);

        Assert.Equal(digest, configuration.Sha256);
        Assert.Equal("engineering-dev", configuration.EnvironmentName);
        Assert.Equal(new AwsEnvironment("111122223333", "ap-southeast-2"), configuration.Aws);
        Assert.Equal(expectedMode, configuration.Mode);
        Assert.Equal(expectedInspection, configuration.DryRun.InspectAws);
        Assert.Equal(expectedProfiles, configuration.Profiles.Length);
        Assert.Equal("controller-private", configuration.Controller.Network);
        Assert.Equal("control-plane", configuration.Controller.Cluster);
        Assert.Equal(RunnerArchitecture.Arm64, configuration.Controller.CpuArchitecture);
        Assert.Equal(512, configuration.Controller.Cpu);
        Assert.Equal(1024, configuration.Controller.MemoryMiB);
        Assert.Equal("subnet-0123456789abcdef0", configuration.Networks["controller-private"].Subnets[0].Id);
        Assert.Equal("ap-southeast-2a", configuration.Networks["controller-private"].Subnets[0].AvailabilityZone);
    }

    [Fact]
    public void PreservesSnapshotOptionsAndLatestLaunchTemplateAlias()
    {
        var configuration = RuntimeConfigurationLoader.Load(ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Latest));
        var demand = Assert.IsType<LocalSnapshotDemandConfiguration>(configuration.DryRun.Demand);
        Assert.Equal("/input/demand.json", demand.Path);
        Assert.Equal("engineering-dev-runner-lifecycle", Assert.IsType<CreateTrackingTableConfiguration>(configuration.TrackingTable).Name);
        var profile = Assert.Single(configuration.Profiles);
        var provisioner = Assert.IsType<Ec2ProvisionerConfiguration>(profile.Provisioner);
        Assert.Equal("lt-0123456789abcdef0", provisioner.LaunchTemplateId);
        Assert.Equal(LaunchTemplateAlias.Latest, Assert.IsType<AliasLaunchTemplateVersion>(provisioner.LaunchTemplateVersion).Name);
        Assert.Null(profile.GitHub.ScaleSetId);
    }

    [Fact]
    public void TranslatesPinnedVersionAndMaximumSignedScaleSetId()
    {
        var configuration = RuntimeConfigurationLoader.Load(ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Pinned));
        Assert.IsType<NoDryRunDemandConfiguration>(configuration.DryRun.Demand);
        Assert.False(configuration.DryRun.InspectAws);
        var profile = Assert.Single(configuration.Profiles);
        Assert.Equal(long.MaxValue, profile.GitHub.ScaleSetId);
        Assert.Equal(GitHubRegistration.Existing, profile.GitHub.Registration);
        var provisioner = Assert.IsType<Ec2ProvisionerConfiguration>(profile.Provisioner);
        Assert.Equal("lt-0123456789abcdef1", provisioner.LaunchTemplateId);
        Assert.Equal("3", Assert.IsType<PinnedLaunchTemplateVersion>(provisioner.LaunchTemplateVersion).Version);
    }

    [Fact]
    public void PreservesImportedTableAndEnterpriseCloudRepositoryScope()
    {
        var configuration = RuntimeConfigurationLoader.Load(ConfigurationFixtures.Runtime(ConfigurationFixtures.Fargate));
        var table = Assert.IsType<ImportTrackingTableConfiguration>(configuration.TrackingTable);
        Assert.Equal("existing-runner-lifecycle", table.Name);
        var connection = configuration.GitHub["engineering"];
        Assert.Equal("https://example.ghe.com/engineering/tools", connection.ScopeUrl);
        Assert.Equal(GitHubAuthType.GitHubApp, connection.AuthType);

        var profile = Assert.Single(configuration.Profiles);
        var runner = Assert.IsType<FargateProvisionerConfiguration>(profile.Provisioner);
        Assert.Equal("runner-shell", runner.Cluster);
        Assert.Equal("arn:aws:ecs:ap-southeast-2:111122223333:task-definition/linux-shell-runner:7",
            runner.TaskDefinitionArn);
        Assert.Equal("runner", runner.RunnerContainer);
        Assert.Equal("arn:aws:iam::111122223333:role/fargate-shell-execution", runner.ExecutionRoleArn);
    }

    [Fact]
    public void KeepsFourIndependentMixedProfileIdentitiesAndCapacitySettings()
    {
        var configuration = RuntimeConfigurationLoader.Load(ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed));

        Assert.Collection(configuration.Profiles,
            profile =>
            {
                AssertProfile(profile, "linux-ec2-docker", "Linux-EC2", ["linux-ec2-docker"], new(0, 12, 600));
                var runner = Assert.IsType<Ec2ProvisionerConfiguration>(profile.Provisioner);
                Assert.Equal("lt-0123456789abcdef0", runner.LaunchTemplateId);
                Assert.Equal(LaunchTemplateAlias.Latest, Assert.IsType<AliasLaunchTemplateVersion>(runner.LaunchTemplateVersion).Name);
            },
            profile =>
            {
                AssertProfile(profile, "linux-ec2-build", "Linux-EC2-Arm", ["linux-ec2-build", "arm64-compiled"], new(2, 16, 480));
                var runner = Assert.IsType<Ec2ProvisionerConfiguration>(profile.Provisioner);
                Assert.Equal("lt-0123456789abcdef1", runner.LaunchTemplateId);
                Assert.Equal("3", Assert.IsType<PinnedLaunchTemplateVersion>(runner.LaunchTemplateVersion).Version);
            },
            profile =>
            {
                AssertProfile(profile, "linux-shell", "No-Docker", ["linux-shell", "shell-x64"], new(1, 24, 420));
                var runner = Assert.IsType<FargateProvisionerConfiguration>(profile.Provisioner);
                Assert.Equal("runner-shell", runner.Cluster);
                Assert.Equal("arn:aws:ecs:ap-southeast-2:111122223333:task-definition/linux-shell-runner:7",
                    runner.TaskDefinitionArn);
            },
            profile =>
            {
                AssertProfile(profile, "linux-ci-arm", "Fargate-Arm-Batches", ["linux-ci-arm", "slim-arm"], new(3, 31, 540));
                var runner = Assert.IsType<FargateProvisionerConfiguration>(profile.Provisioner);
                Assert.Equal("runner-arm", runner.Cluster);
                Assert.Equal("arn:aws:ecs:ap-southeast-2:111122223333:task-definition/runner-slim-arm:19",
                    runner.TaskDefinitionArn);
            });
        Assert.Equal("arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-shell",
            Assert.IsType<ImportEcsClusterConfiguration>(configuration.EcsClusters["runner-shell"]).Arn);
        Assert.Equal("arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-arm",
            Assert.IsType<ImportEcsClusterConfiguration>(configuration.EcsClusters["runner-arm"]).Arn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopilotProfilesRequireADedicatedGroupWithinTheResolvedScope(bool copilotFirst)
    {
        var copilot = copilotFirst ? 0 : 1;
        var ordinary = 1 - copilot;
        ConfigurationEdit[] scopedProfiles =
        [
            ConfigurationEdit.Set("/spec/github/alias", new
            {
                scopeURL = "https://www.github.com/EXAMPLE-ENGINEERING/",
                auth = new
                {
                    type = "github-app",
                    secretArn = "arn:aws:secretsmanager:ap-southeast-2:111122223333:secret:github-app-alias-a1b2c3",
                },
            }),
            ConfigurationEdit.Set($"/spec/profiles/{copilot}/purpose", "copilot-cloud-agent"),
            ConfigurationEdit.Set($"/spec/profiles/{copilot}/platform/arch", "x64"),
            ConfigurationEdit.Set($"/spec/profiles/{copilot}/github/runnerGroup", "Copilot-Only"),
            ConfigurationEdit.Set($"/spec/profiles/{ordinary}/github/connection", "alias"),
        ];
        var valid = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Mixed, scopedProfiles);
        var configuration = RuntimeConfigurationLoader.Load(valid, ConfigurationFixtures.Sha256(valid));
        Assert.Equal(RunnerProfilePurpose.CopilotCloudAgent, configuration.Profiles[copilot].Purpose);
        Assert.Equal(RunnerProfilePurpose.Ec2General, configuration.Profiles[ordinary].Purpose);
        var copilotRouting = configuration.Profiles[copilot].GitHub;
        var ordinaryRouting = configuration.Profiles[ordinary].GitHub;
        Assert.Equal("Copilot-Only", copilotRouting.RunnerGroup);
        Assert.NotEqual(copilotRouting.ScaleSet, ordinaryRouting.ScaleSet);
        Assert.NotEqual(copilotRouting.Connection, ordinaryRouting.Connection);

        var invalid = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Mixed,
        [
            .. scopedProfiles,
            ConfigurationEdit.Set($"/spec/profiles/{copilot}/github/runnerGroup", ordinaryRouting.RunnerGroup.ToLowerInvariant()),
        ]);
        var error = AssertRejected(invalid, "semantic");
        Assert.Equal("/spec/profiles/1/github/runnerGroup", error.Path);
        Assert.Contains("dedicated runner group", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> RejectionNames()
    {
        var names = new TheoryData<string>();
        foreach (var rejection in ConfigurationFixtures.Rejections())
        {
            names.Add(rejection.Name);
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(RejectionNames))]
    public void RejectsEverySharedConfigurationFixture(string name)
    {
        var rejection = ConfigurationFixtures.Rejections().Single(item => item.Name == name);
        if (rejection.Fault == "yaml syntax")
        {
            Assert.NotNull(rejection.Yaml);
            AssertRejected(Encoding.UTF8.GetBytes(rejection.Yaml), "json");
            return;
        }

        Assert.Equal("configuration", rejection.Fault);
        Assert.NotNull(rejection.Edits);
        var json = ConfigurationFixtures.EditRuntime(rejection.Base ?? ConfigurationFixtures.Ec2Latest, rejection.Edits);
        var error = AssertRejected(json, rejection.Code);
        if (rejection.ErrorPath is not null)
        {
            Assert.Equal(rejection.ErrorPath, error.Path);
        }

        foreach (var secret in rejection.Redacted ?? [])
        {
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.Path, StringComparison.Ordinal);
        }

        if (rejection.Code == "ghes-unsupported")
        {
            Assert.Contains("scaleset.NET #20", error.Message, StringComparison.Ordinal);
        }

        if (rejection.Code == "custom-ca-unsupported")
        {
            Assert.Contains("scaleset.NET #21", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\"kind\": \"RunnerControllerRuntime\"", "\"kind\": \"RunnerControllerRuntime\", \"kind\": \"RunnerControllerRuntime\"")]
    [InlineData("\"maxRunners\": 12", "\"maxRunners\": 12, \"maxRunners\": 12")]
    [InlineData("\"maxRunners\": 12", "\"maxRunners\": 12, \"m\\u0061xRunners\": 12")]
    public void RejectsDuplicatePropertiesEvenWhenTheValuesAgree(string original, string duplicate)
    {
        AssertRejected(ReplaceText(original, duplicate), "json");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("/*comment*/{}")]
    [InlineData("{\"kind\":\"RunnerControllerRuntime\",}")]
    [InlineData("{\"\\uD800\":\"invalid key\"}")]
    public void RejectsMalformedJsonSyntax(string malformed)
    {
        AssertRejected(Encoding.UTF8.GetBytes(malformed), "json");
    }

    [Fact]
    public void RejectsInvalidUtf8InAJsonString()
    {
        var json = ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Latest);
        var location = json.AsSpan().IndexOf("engineering-dev"u8);
        Assert.True(location >= 0);
        json[location] = 0xFF;

        AssertRejected(json, "json");
    }

    [Fact]
    public void RejectsAnUnpairedEscapedSurrogateInAJsonString()
    {
        AssertRejected(ReplaceText("engineering-dev", "engineering-\\uD800"), "json");
    }

    [Fact]
    public void PreservesAnEscapedSupplementaryCharacterInARunnerGroupName()
    {
        var json = ReplaceText("Linux-EC2", "Linux-EC2-\\uD83D\\uDE00");
        var configuration = RuntimeConfigurationLoader.Load(json, ConfigurationFixtures.Sha256(json));

        Assert.Equal("Linux-EC2-\U0001F600", Assert.Single(configuration.Profiles).GitHub.RunnerGroup);
    }

    [Theory]
    [InlineData("\"mode\": \"dry-run\",", "", "schema")]
    [InlineData("\"mode\": \"dry-run\"", "\"MODE\": \"dry-run\"", "schema")]
    [InlineData("\"mode\": \"dry-run\"", "\"mode\": null", "schema")]
    [InlineData("\"maxRunners\": 12", "\"maxRunners\": 12.000000000000000001", "schema")]
    [InlineData("\"idleReserve\": 0", "\"idleReserve\": -1e-999", "schema")]
    public void RejectsRuntimeOnlySchemaFailures(string original, string invalid, string code)
    {
        AssertRejected(ReplaceText(original, invalid), code);
    }

    [Fact]
    public void HashesTheOriginalBytesEvenWhenOnlyInsignificantWhitespaceChanges()
    {
        var original = ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Latest);
        byte[] changed = [.. original, (byte)'\n'];
        var expected = ConfigurationFixtures.Sha256(original);

        var mismatch = Assert.Throws<RuntimeConfigurationException>(
            () => RuntimeConfigurationLoader.Load(changed, expected));

        Assert.Equal("digest", mismatch.Code);
        Assert.Empty(mismatch.Path);
        var verified = RuntimeConfigurationLoader.Load(changed, ConfigurationFixtures.Sha256(changed));
        Assert.Equal(ConfigurationFixtures.Sha256(changed), verified.Sha256);
        Assert.NotEqual(expected, verified.Sha256);
        Assert.Equal("engineering-dev", verified.EnvironmentName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void RejectsMalformedOrMismatchedExpectedDigest(string malformed)
    {
        var json = ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Latest);
        var error = Assert.Throws<RuntimeConfigurationException>(
            () => RuntimeConfigurationLoader.Load(json, malformed));

        Assert.Equal("digest", error.Code);
        Assert.Empty(error.Path);
    }

    private static RuntimeConfigurationException AssertRejected(byte[] json, string code)
    {
        var error = Assert.Throws<RuntimeConfigurationException>(
            () => RuntimeConfigurationLoader.Load(json, ConfigurationFixtures.Sha256(json)));
        Assert.Equal(code, error.Code);
        return error;
    }

    private static byte[] ReplaceText(string original, string replacement)
    {
        var source = Encoding.UTF8.GetString(ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Latest));
        Assert.Contains(original, source, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(source.Replace(original, replacement, StringComparison.Ordinal));
    }

    private static void AssertProfile(
        RunnerProfile profile,
        string id,
        string group,
        string[] labels,
        RunnerCapacityConfiguration capacity)
    {
        Assert.Equal(id, profile.Id);
        Assert.Equal(RunnerProfileState.Active, profile.State);
        Assert.Equal(capacity, profile.Capacity);
        Assert.Equal("engineering", profile.GitHub.Connection);
        Assert.Equal(group, profile.GitHub.RunnerGroup);
        Assert.Equal(id, profile.GitHub.ScaleSet);
        Assert.Equal(labels, profile.GitHub.Labels);
    }
}
