using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class RuntimeCompositionTests
{
    private static readonly string[] ActiveCommon =
    [
        "ownership reader",
        "lifecycle writes",
        "GitHub credentials",
        "GitHub sessions",
        "event consumers",
    ];

    public static TheoryData<string, string[], string[]> ActiveScenarios() => new()
    {
        { ConfigurationFixtures.Ec2Latest, ["linux-ec2-docker"], [] },
        { ConfigurationFixtures.Fargate, [], ["linux-shell"] },
        { ConfigurationFixtures.Mixed, ["linux-ec2-docker", "linux-ec2-build"], ["linux-shell", "linux-ci-arm"] },
    };

    [Theory]
    [MemberData(nameof(ActiveScenarios))]
    public void ActiveModeConstructsOnlySelectedProvisioners(string fixture, string[] ec2, string[] fargate)
    {
        var json = ConfigurationFixtures.EditRuntime(fixture, ConfigurationEdit.Set("/spec/mode", "active"));
        var spies = new RegistrationSpies();

        var composition = Compose(json, spies);

        Assert.Equal(ControllerMode.Active, composition.Configuration.Mode);
        AssertActive(spies, ec2, fargate);
        AssertOutputServices(composition, spies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisabledBackendNeverConstructsItsProvisioner(bool disableEc2)
    {
        var first = disableEc2 ? 0 : 2;
        var second = first + 1;
        var json = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Mixed,
            ConfigurationEdit.Set($"/spec/profiles/{first}/state", "disabled"),
            ConfigurationEdit.Set($"/spec/profiles/{second}/state", "disabled"));
        var spies = new RegistrationSpies();

        var composition = Compose(json, spies);

        string[] expectedEc2 = disableEc2 ? [] : ["linux-ec2-docker", "linux-ec2-build"];
        string[] expectedFargate = disableEc2 ? ["linux-shell", "linux-ci-arm"] : [];
        AssertActive(spies, expectedEc2, expectedFargate);
        Assert.Equal(RunnerProfileState.Disabled, composition.Configuration.Profiles[first].State);
        Assert.Equal(RunnerProfileState.Disabled, composition.Configuration.Profiles[second].State);
        AssertOutputServices(composition, spies);
    }

    [Fact]
    public void DrainingProfilesRemainSelectedAndDisabledPeersAreExcluded()
    {
        var json = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Mixed,
            ConfigurationEdit.Set("/spec/profiles/0/state", "disabled"),
            ConfigurationEdit.Set("/spec/profiles/1/state", "draining"),
            ConfigurationEdit.Set("/spec/profiles/2/state", "draining"),
            ConfigurationEdit.Set("/spec/profiles/3/state", "disabled"));
        var spies = new RegistrationSpies();

        var composition = Compose(json, spies);

        AssertActive(spies, ["linux-ec2-build"], ["linux-shell"]);
        Assert.Equal(RunnerProfileState.Draining, Assert.Single(spies.For("EC2 provisioner").Profiles).State);
        Assert.Equal(RunnerProfileState.Draining, Assert.Single(spies.For("Fargate provisioner").Profiles).State);
        AssertOutputServices(composition, spies);
    }

    [Fact]
    public void DryRunWithInspectionDisabledConstructsNoServicesFromEitherBranch()
    {
        var spies = new RegistrationSpies();
        var json = ConfigurationFixtures.Runtime(ConfigurationFixtures.Ec2Pinned);

        var composition = Compose(json, spies);

        Assert.Equal(ControllerMode.DryRun, composition.Configuration.Mode);
        Assert.False(composition.Configuration.DryRun.InspectAws);
        Assert.Empty(spies.Constructed);
        AssertOutputServices(composition, spies);
    }

    [Theory]
    [MemberData(nameof(ActiveScenarios))]
    public void DryRunInspectionConstructsOnlySelectedReadOnlyInventory(string fixture, string[] ec2, string[] fargate)
    {
        var json = ConfigurationFixtures.EditRuntime(fixture,
            ConfigurationEdit.Set("/spec/mode", "dry-run"),
            ConfigurationEdit.Set("/spec/dryRun/inspectAws", true));
        var spies = new RegistrationSpies();

        var composition = Compose(json, spies);

        Assert.Equal(ControllerMode.DryRun, composition.Configuration.Mode);
        Assert.True(composition.Configuration.DryRun.InspectAws);
        AssertOnlyKinds(spies, [], "EC2 inventory", ec2, "Fargate inventory", fargate);
        AssertOutputServices(composition, spies);
    }

    [Fact]
    public void DryRunInspectionSkipsDisabledBackendAndIncludesDrainingProfiles()
    {
        var json = ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Mixed,
            ConfigurationEdit.Set("/spec/mode", "dry-run"),
            ConfigurationEdit.Set("/spec/dryRun/inspectAws", true),
            ConfigurationEdit.Set("/spec/profiles/0/state", "disabled"),
            ConfigurationEdit.Set("/spec/profiles/1/state", "disabled"),
            ConfigurationEdit.Set("/spec/profiles/2/state", "draining"));
        var spies = new RegistrationSpies();

        var composition = Compose(json, spies);

        AssertOnlyKinds(spies, [], "EC2 inventory", [], "Fargate inventory", ["linux-shell", "linux-ci-arm"]);
        var draining = spies.For("Fargate inventory").Profiles[0];
        Assert.Equal(RunnerProfileState.Draining, draining.State);
        AssertOutputServices(composition, spies);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("json")]
    [InlineData("schema")]
    [InlineData("semantic")]
    public void ValidationFailureAlwaysPrecedesAnyServiceConstruction(string fault)
    {
        var json = fault switch
        {
            "json" => "{\"spec\":"u8.ToArray(),
            "schema" => ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Mixed, new ConfigurationEdit("/spec/mode", default)),
            "semantic" => ConfigurationFixtures.EditRuntime(ConfigurationFixtures.Mixed,
                ConfigurationEdit.Set("/spec/profiles/3/provisioner/cluster", "unknown-cluster")),
            _ => ConfigurationFixtures.Runtime(ConfigurationFixtures.Mixed),
        };
        var digest = fault == "digest" ? new string('0', 64) : ConfigurationFixtures.Sha256(json);
        var spies = new RegistrationSpies();

        var error = Assert.Throws<RuntimeConfigurationException>(
            () => RuntimeServiceComposition.Compose(json, spies.Active, spies.DryRun, digest));

        Assert.Equal(fault, error.Code);
        if (fault == "semantic")
        {
            Assert.Equal("/spec/profiles/3/provisioner/cluster", error.Path);
        }

        Assert.Empty(spies.Constructed);
    }

    private static RuntimeComposition Compose(byte[] json, RegistrationSpies spies) =>
        RuntimeServiceComposition.Compose(json, spies.Active, spies.DryRun, ConfigurationFixtures.Sha256(json));

    private static void AssertActive(RegistrationSpies spies, string[] ec2, string[] fargate)
    {
        AssertOnlyKinds(spies, ActiveCommon, "EC2 provisioner", ec2, "Fargate provisioner", fargate);
        var ownership = spies.For("ownership reader");
        Assert.Equal("control-plane", ownership.Configuration.Controller.Cluster);
        Assert.Empty(ownership.Profiles);
    }

    private static void AssertOnlyKinds(
        RegistrationSpies spies, string[] common,
        string ec2Kind, string[] ec2, string fargateKind, string[] fargate)
    {
        var expected = new List<string>(common);
        if (ec2.Length > 0)
        {
            expected.Add(ec2Kind);
            Assert.Equal(ec2, spies.For(ec2Kind).Profiles.Select(profile => profile.Id));
        }

        if (fargate.Length > 0)
        {
            expected.Add(fargateKind);
            Assert.Equal(fargate, spies.For(fargateKind).Profiles.Select(profile => profile.Id));
        }

        Assert.Equal(expected.Order(StringComparer.Ordinal),
            spies.Constructed.Select(marker => marker.Kind).Order(StringComparer.Ordinal));
    }

    private static void AssertOutputServices(RuntimeComposition composition, RegistrationSpies spies)
    {
        using var provider = composition.Services.BuildServiceProvider();
        Assert.Same(composition.Configuration, provider.GetRequiredService<RuntimeConfiguration>());
        Assert.Equal(spies.Constructed, provider.GetServices<Construction>());
        Assert.All(spies.Constructed, constructed => Assert.Same(composition.Configuration, constructed.Configuration));
    }

    private sealed record Construction(string Kind, RuntimeConfiguration Configuration, ImmutableArray<RunnerProfile> Profiles);

    private sealed class RegistrationSpies
    {
        private readonly List<Construction> _constructed = [];

        public RegistrationSpies()
        {
            Active = new(
                Common("ownership reader"),
                Common("lifecycle writes"),
                Common("GitHub credentials"),
                Common("GitHub sessions"),
                Common("event consumers"),
                Backend("EC2 provisioner"),
                Backend("Fargate provisioner"));
            DryRun = new(Backend("EC2 inventory"), Backend("Fargate inventory"));
        }

        public ActiveRuntimeRegistrations Active { get; }
        public DryRunRuntimeRegistrations DryRun { get; }
        public IReadOnlyList<Construction> Constructed => _constructed;

        public Construction For(string kind) => Assert.Single(_constructed, construction => construction.Kind == kind);

        private Action<IServiceCollection, RuntimeConfiguration> Common(string kind) =>
            (services, configuration) => Construct(services, new Construction(kind, configuration, []));

        private ProvisionerRegistration Backend(string kind) =>
            (services, configuration, profiles) => Construct(services, new Construction(kind, configuration, profiles));

        private void Construct(IServiceCollection services, Construction construction)
        {
            _constructed.Add(construction);
            services.AddSingleton(construction);
        }
    }
}
