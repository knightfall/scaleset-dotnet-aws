using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Knightfall.ScaleSet.Aws.Core.Configuration;

internal static class RuntimeConfigurationTranslation
{
    internal static RuntimeConfiguration Translate(JsonElement root, string sha256)
    {
        string name = Text(root.GetProperty("metadata"), "name");
        JsonElement spec = root.GetProperty("spec");
        JsonElement aws = spec.GetProperty("aws");
        var environment = new AwsEnvironment(Text(aws, "account"), Text(aws, "region"));
        var mode = Text(spec, "mode") switch
        {
            "active" => ControllerMode.Active,
            "dry-run" => ControllerMode.DryRun,
            _ => throw UnknownVariant(),
        };

        return new RuntimeConfiguration(
            name,
            sha256,
            environment,
            mode,
            DryRun(spec.GetProperty("dryRun")),
            Controller(spec.GetProperty("controller")),
            TrackingTable(spec.GetProperty("tracking").GetProperty("table"), name),
            NamedMap(spec.GetProperty("networks"), Network),
            NamedMap(spec.GetProperty("ecsClusters"), Cluster),
            NamedMap(spec.GetProperty("github"), GitHubConnection),
            spec.GetProperty("profiles").EnumerateArray().Select(Profile).ToImmutableArray());
    }

    private static DryRunConfiguration DryRun(JsonElement value)
    {
        JsonElement demand = value.GetProperty("demand");
        DryRunDemandConfiguration source = Text(demand, "type") switch
        {
            "none" => new NoDryRunDemandConfiguration(),
            "snapshot" when demand.TryGetProperty("path", out var path) => new LocalSnapshotDemandConfiguration(path.GetString()!),
            "snapshot" => new S3SnapshotDemandConfiguration(Text(demand, "s3Uri"), Text(demand, "versionId"), Text(demand, "sha256")),
            "replay" => new LocalReplayDemandConfiguration(Text(demand, "path")),
            _ => throw UnknownVariant(),
        };
        return new DryRunConfiguration(source, value.GetProperty("inspectAws").GetBoolean());
    }

    private static ControllerHostingConfiguration Controller(JsonElement value)
    {
        const string path = "/spec/controller/hosting";
        JsonElement hosting = value.GetProperty("hosting");
        return new ControllerHostingConfiguration(
            Text(value, "image"),
            Text(hosting, "network"),
            Text(hosting, "cluster"),
            Architecture(Text(hosting, "cpuArchitecture")),
            Integer(hosting, "cpu", path),
            Integer(hosting, "memoryMiB", path));
    }

    private static TrackingTableConfiguration TrackingTable(JsonElement table, string environmentName) =>
        Text(table, "type") switch
        {
            "create" => new CreateTrackingTableConfiguration(environmentName + "-runner-lifecycle"),
            "import" => new ImportTrackingTableConfiguration(Text(table, "name")),
            _ => throw UnknownVariant(),
        };

    private static NetworkConfiguration Network(JsonElement value) =>
        new(
            Text(value, "vpcId"),
            value.GetProperty("subnets").EnumerateArray()
                .Select(subnet => new SubnetConfiguration(Text(subnet, "id"), Text(subnet, "az")))
                .ToImmutableArray(),
            Strings(value.GetProperty("securityGroupIds")));

    private static EcsClusterConfiguration Cluster(JsonElement value) =>
        Text(value, "type") switch
        {
            "create" => new CreateEcsClusterConfiguration(Text(value, "name")),
            "import" => new ImportEcsClusterConfiguration(Text(value, "arn")),
            _ => throw UnknownVariant(),
        };

    private static GitHubConnectionConfiguration GitHubConnection(JsonElement value)
    {
        JsonElement auth = value.GetProperty("auth");
        var type = Text(auth, "type") switch
        {
            "github-app" => GitHubAuthType.GitHubApp,
            "enterprise-pat" => GitHubAuthType.EnterprisePat,
            _ => throw UnknownVariant(),
        };
        return new GitHubConnectionConfiguration(Text(value, "scopeURL"), type, Text(auth, "secretArn"));
    }

    private static RunnerProfile Profile(JsonElement value, int index)
    {
        JsonElement platform = value.GetProperty("platform");
        JsonElement capacity = value.GetProperty("capacity");
        var state = Text(value, "state") switch
        {
            "active" => RunnerProfileState.Active,
            "draining" => RunnerProfileState.Draining,
            "disabled" => RunnerProfileState.Disabled,
            _ => throw UnknownVariant(),
        };
        var purpose = Text(value, "purpose") switch
        {
            "copilot-cloud-agent" => RunnerProfilePurpose.CopilotCloudAgent,
            "fargate-compatible" => RunnerProfilePurpose.FargateCompatible,
            "ec2-general" => RunnerProfilePurpose.Ec2General,
            _ => throw UnknownVariant(),
        };
        var os = Text(platform, "os") switch
        {
            "linux" => RunnerOperatingSystem.Linux,
            "windows" => RunnerOperatingSystem.Windows,
            _ => throw UnknownVariant(),
        };
        string capacityPath = $"/spec/profiles/{index}/capacity";
        return new RunnerProfile(
            Text(value, "id"),
            state,
            purpose,
            new RunnerPlatformConfiguration(os, Architecture(Text(platform, "arch"))),
            new RunnerCapacityConfiguration(
                Integer(capacity, "idleReserve", capacityPath),
                Integer(capacity, "maxRunners", capacityPath),
                Integer(capacity, "startupTimeoutSeconds", capacityPath)),
            Routing(value.GetProperty("github")),
            Provisioner(value.GetProperty("provisioner")));
    }

    private static RunnerGitHubConfiguration Routing(JsonElement value)
    {
        var registration = Text(value, "registration") switch
        {
            "existing" => GitHubRegistration.Existing,
            "create-if-missing" => GitHubRegistration.CreateIfMissing,
            _ => throw UnknownVariant(),
        };
        return new RunnerGitHubConfiguration(
            Text(value, "connection"),
            Text(value, "runnerGroup"),
            Text(value, "scaleSet"),
            value.TryGetProperty("scaleSetId", out var id) ? long.Parse(id.GetString()!, CultureInfo.InvariantCulture) : null,
            Strings(value.GetProperty("labels")),
            registration);
    }

    private static RunnerProvisionerConfiguration Provisioner(JsonElement value)
    {
        string network = Text(value, "network");
        string role = Text(value, "bootstrapRoleArn");
        string protocol = Text(value, "bootstrapProtocol");
        return Text(value, "type") switch
        {
            "ec2" => new Ec2ProvisionerConfiguration(
                network,
                role,
                protocol,
                Text(value, "launchTemplateId"),
                LaunchTemplateVersion(value.GetProperty("launchTemplateVersion")),
                Text(value, "instanceProfileArn"),
                OptionalText(value, "instanceType"),
                OptionalText(value, "tenancy") switch
                {
                    null => null,
                    "default" => Ec2Tenancy.Default,
                    "dedicated" => Ec2Tenancy.Dedicated,
                    _ => throw UnknownVariant(),
                },
                OptionalText(value, "subnetStrategy") switch
                {
                    null => null,
                    "round-robin" => Ec2SubnetStrategy.RoundRobin,
                    "failure-weighted" => Ec2SubnetStrategy.FailureWeighted,
                    _ => throw UnknownVariant(),
                })
            {
                StatusQualification = value.TryGetProperty("statusQualification", out var qualification)
                    ? new Ec2StatusQualification(Text(qualification, "imageId"), Text(qualification, "roleId"), Text(qualification, "evidenceSha256"))
                    : null,
            },
            "fargate" => new FargateProvisionerConfiguration(
                network,
                role,
                protocol,
                Text(value, "cluster"),
                Text(value, "taskDefinitionArn"),
                Text(value, "runnerContainer"),
                Text(value, "executionRoleArn")),
            _ => throw UnknownVariant(),
        };
    }

    private static LaunchTemplateVersionConfiguration LaunchTemplateVersion(JsonElement value) =>
        Text(value, "type") switch
        {
            "pinned" => new PinnedLaunchTemplateVersion(Text(value, "version")),
            "alias" => new AliasLaunchTemplateVersion(Text(value, "name") switch
            {
                "$Latest" => LaunchTemplateAlias.Latest,
                "$Default" => LaunchTemplateAlias.Default,
                _ => throw UnknownVariant(),
            }),
            _ => throw UnknownVariant(),
        };

    private static RunnerArchitecture Architecture(string value) =>
        value switch
        {
            "x64" => RunnerArchitecture.X64,
            "arm64" => RunnerArchitecture.Arm64,
            _ => throw UnknownVariant(),
        };

    private static FrozenDictionary<string, T> NamedMap<T>(JsonElement value, Func<JsonElement, T> translate) =>
        value.EnumerateObject().ToFrozenDictionary(property => property.Name, property => translate(property.Value), StringComparer.Ordinal);

    private static ImmutableArray<string> Strings(JsonElement value) =>
        value.EnumerateArray().Select(item => item.GetString()!).ToImmutableArray();

    private static string Text(JsonElement value, string property) =>
        value.GetProperty(property).GetString()!;

    private static string? OptionalText(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) ? item.GetString() : null;

    private static int Integer(JsonElement value, string property, string parentPath)
    {
        if (!value.GetProperty(property).TryGetInt32(out int number))
        {
            throw new RuntimeConfigurationException("schema", parentPath + "/" + property, "Use a signed 32-bit JSON integer.");
        }
        return number;
    }

    private static InvalidOperationException UnknownVariant() => new("The runtime schema allowed an unsupported value.");
}
