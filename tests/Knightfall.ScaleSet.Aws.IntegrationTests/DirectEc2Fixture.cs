using System.Text.Json.Nodes;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class DirectEc2Fixture(
    Uri endpoint, AWSCredentials credentials, string account, string region, string deployment, string roleArn,
    string instanceProfileArn, string vpc, string subnetA, string subnetB, string securityGroup, string templateId) : IDisposable
{
    internal const string LinuxImage = "ami-0abcdef1234567890";
    internal static string RoleName() => "t23-ec2-" + Guid.NewGuid().ToString("N");
    internal const string ProfileId = "linux-ec2-build";

    internal string FirstSubnet => subnetA;

    internal string SecondSubnet => subnetB;

    internal string TemplateId => templateId;

    internal DirectEc2AwsWire Wire { get; } = new(endpoint, subnetA);

    internal static async Task<DirectEc2Fixture> CreateAsync(Uri endpoint, AWSCredentials credentials, AmazonEC2Client ec2,
        AmazonIdentityManagementServiceClient iam, string account, string region, string roleArn, CancellationToken cancellationToken)
    {
        var vpc = await ec2.CreateVpcAsync(new CreateVpcRequest { CidrBlock = "10.57.0.0/16" }, cancellationToken);
        string vpcId = vpc.Vpc.VpcId;
        var a = await ec2.CreateSubnetAsync(new CreateSubnetRequest
        {
            VpcId = vpcId, CidrBlock = "10.57.1.0/24", AvailabilityZone = region + "a",
        }, cancellationToken);
        var b = await ec2.CreateSubnetAsync(new CreateSubnetRequest
        {
            VpcId = vpcId, CidrBlock = "10.57.2.0/24", AvailabilityZone = region + "b",
        }, cancellationToken);
        string unique = "t23-" + Guid.NewGuid().ToString("N");
        var group = await ec2.CreateSecurityGroupAsync(new CreateSecurityGroupRequest
        {
            VpcId = vpcId, GroupName = unique, Description = "Synthetic private EC2 runner fixture",
        }, cancellationToken);
        var profile = await iam.CreateInstanceProfileAsync(new CreateInstanceProfileRequest
        {
            InstanceProfileName = unique,
        }, cancellationToken);
        await iam.AddRoleToInstanceProfileAsync(new AddRoleToInstanceProfileRequest
        {
            InstanceProfileName = unique, RoleName = roleArn[(roleArn.LastIndexOf('/') + 1)..],
        }, cancellationToken);
        string profileArn = profile.InstanceProfile.Arn;
        var created = await ec2.CreateLaunchTemplateAsync(new CreateLaunchTemplateRequest
        {
            LaunchTemplateName = unique,
            LaunchTemplateData = TemplateData(profileArn, requireImdsV2: true),
        }, cancellationToken);
        Assert.Equal(1, created.LaunchTemplate.DefaultVersionNumber);
        Assert.Equal(1, created.LaunchTemplate.LatestVersionNumber);
        return new DirectEc2Fixture(endpoint, credentials, account, region, unique, roleArn, profileArn, vpcId,
            a.Subnet.SubnetId, b.Subnet.SubnetId, group.GroupId, created.LaunchTemplate.LaunchTemplateId);
    }

    internal async Task<long> AddRevisionAsync(AmazonEC2Client ec2, bool requireImdsV2, CancellationToken cancellationToken)
    {
        var version = await ec2.CreateLaunchTemplateVersionAsync(new CreateLaunchTemplateVersionRequest
        {
            LaunchTemplateId = templateId, SourceVersion = "1",
            LaunchTemplateData = TemplateData(instanceProfileArn, requireImdsV2),
        }, cancellationToken);
        return version.LaunchTemplateVersion.VersionNumber!.Value;
    }

    internal async Task<RuntimeConfiguration> ConfigurationAsync(string table, string selector, CancellationToken cancellationToken,
        string? patSecretArn = null)
    {
        string document = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "ec2-only-pinned.json"), cancellationToken);
        var tree = JsonNode.Parse(document)!;
        tree["metadata"]!["name"] = deployment;
        var spec = tree["spec"]!;
        spec["aws"] = new JsonObject { ["account"] = account, ["region"] = region };
        spec["mode"] = "active";
        spec["tracking"]!["table"] = new JsonObject { ["type"] = "import", ["name"] = table };
        if (patSecretArn is not null)
        {
            var connection = spec["github"]!["engineering"]!;
            connection["scopeURL"] = "https://github.com/enterprises/engineering";
            connection["auth"] = new JsonObject { ["type"] = "enterprise-pat", ["secretArn"] = patSecretArn };
        }
        spec["networks"]!["runner-private"] = new JsonObject
        {
            ["vpcId"] = vpc,
            ["subnets"] = new JsonArray(
                new JsonObject { ["id"] = subnetA, ["az"] = region + "a" },
                new JsonObject { ["id"] = subnetB, ["az"] = region + "b" }),
            ["securityGroupIds"] = new JsonArray(securityGroup),
        };
        var profile = spec["profiles"]![0]!;
        profile["platform"]!["arch"] = "x64";
        profile["github"]!["runnerGroup"] = "Linux-EC2-Arm";
        profile["github"]!["scaleSetId"] = FormattableString.Invariant($"{GitHubSimulator.SetId}");
        var provisioner = profile["provisioner"]!;
        provisioner["launchTemplateId"] = templateId;
        provisioner["instanceProfileArn"] = instanceProfileArn;
        provisioner["bootstrapRoleArn"] = roleArn;
        provisioner["launchTemplateVersion"] = selector.StartsWith('$') ?
            new JsonObject { ["type"] = "alias", ["name"] = selector } :
            new JsonObject { ["type"] = "pinned", ["version"] = selector };
        return RuntimeConfigurationLoader.Load(System.Text.Encoding.UTF8.GetBytes(tree.ToJsonString()));
    }

    internal Ec2ProfileInspector Inspector(TimeProvider clock) => new(credentials, Ec2Config(), IamConfig(), StsConfig(), clock);

    internal Ec2EffectProvider Provider(RuntimeConfiguration configuration, LifecycleProfile profile, TimeProvider clock) =>
        new(configuration, profile, credentials, Ec2Config(), IamConfig(), StsConfig(), clock);

    internal AmazonSecurityTokenServiceConfig StsConfig() => new()
    {
        ServiceURL = endpoint.AbsoluteUri, AuthenticationRegion = region, MaxErrorRetry = 0,
        MaxStaleConnectionRetries = 0, DisableClockSkewCorrection = true, AllowAutoRedirect = false,
    };

    public void Dispose() => Wire.Dispose();

    private AmazonIdentityManagementServiceConfig IamConfig() => new()
    {
        ServiceURL = endpoint.AbsoluteUri, AuthenticationRegion = region, MaxErrorRetry = 0,
        MaxStaleConnectionRetries = 0, DisableClockSkewCorrection = true, AllowAutoRedirect = false,
    };

    private AmazonEC2Config Ec2Config() => new()
    {
        ServiceURL = endpoint.AbsoluteUri, AuthenticationRegion = region, HttpClientFactory = Wire.ClientFactory,
        MaxErrorRetry = 0, MaxStaleConnectionRetries = 0, DisableClockSkewCorrection = true, AllowAutoRedirect = false,
    };

    private static RequestLaunchTemplateData TemplateData(string profileArn, bool requireImdsV2) => new()
    {
        ImageId = LinuxImage,
        InstanceType = InstanceType.T3Micro,
        IamInstanceProfile = new LaunchTemplateIamInstanceProfileSpecificationRequest { Arn = profileArn },
        MetadataOptions = new LaunchTemplateInstanceMetadataOptionsRequest
        {
            HttpEndpoint = LaunchTemplateInstanceMetadataEndpointState.Enabled,
            HttpTokens = requireImdsV2 ? LaunchTemplateHttpTokensState.Required : LaunchTemplateHttpTokensState.Optional,
        },
        BlockDeviceMappings = [new LaunchTemplateBlockDeviceMappingRequest
        {
            DeviceName = "/dev/xvda", Ebs = new LaunchTemplateEbsBlockDeviceRequest { Encrypted = true, VolumeSize = 8 },
        }],
        TagSpecifications = [new LaunchTemplateTagSpecificationRequest
        {
            ResourceType = ResourceType.Instance,
            Tags = [new Amazon.EC2.Model.Tag { Key = RunnerOwnershipTags.BootstrapProtocol, Value = "v1" }],
        }],
    };
}
