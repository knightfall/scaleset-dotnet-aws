using System.Collections.Specialized;
using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Amazon.EC2;
using Amazon.IdentityManagement;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Providers.Ec2;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

internal sealed class Ec2InspectionFixture : IDisposable
{
    internal const string Account = "111122223333";
    internal const string Region = "ap-southeast-2";
    internal const string Deployment = "111122223333:ap-southeast-2:engineering-dev";
    internal const string ProfileId = "linux-ec2-docker";
    internal const string TemplateId = "lt-0123456789abcdef0";
    internal const string ImageId = "ami-0123456789abcdef0";
    internal const string RootDevice = "/dev/xvda";
    internal const string Instance1 = "i-0123456789abcdef0";
    internal const string Instance2 = "i-0123456789abcdef1";
    internal const string Instance3 = "i-0123456789abcdef2";
    internal const string Instance4 = "i-0123456789abcdef3";
    internal const string Allocation = "12345678-0000-0000-0000-000000000001";
    internal const string OriginalLaunch = "2049-12-31T00:00:00Z";
    internal const string RoleName = "runner-linux-bootstrap";
    internal const string RoleArn = "arn:aws:iam::" + Account + ":role/" + RoleName;
    internal const string ProfileArn = "arn:aws:iam::" + Account + ":instance-profile/" + RoleName;
    internal const string RoleId = "AROAEXAMPLE0123456789";
    internal const string Vpc = "vpc-0123456789abcdef0";
    internal const string Group = "sg-0123456789abcdef1";
    internal const string Subnet1 = "subnet-0123456789abcdef2";
    internal const string Subnet2 = "subnet-0123456789abcdef3";
    internal static readonly DateTimeOffset Now = new(2050, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal const string Namespace = "http://ec2.amazonaws.com/doc/2016-11-15/";

    private readonly AwsWireTransport _wire;
    private readonly TimeProvider _clock;

    internal Ec2InspectionFixture(string ec2Region = Region, TimeProvider? clock = null)
    {
        _wire = new AwsWireTransport(Respond);
        _clock = clock ?? new FixtureClock();
        var ec2 = _wire.Configure(new AmazonEC2Config());
        ec2.AuthenticationRegion = ec2Region;
        Inspector = new Ec2ProfileInspector(AwsWireTransport.Credentials, ec2,
            _wire.Configure(new AmazonIdentityManagementServiceConfig()),
            _wire.Configure(new AmazonSecurityTokenServiceConfig()), _clock);
    }

    internal Ec2ProfileInspector Inspector { get; }

    internal Ec2EffectProvider CreateProvider(RuntimeConfiguration configuration)
    {
        var ec2 = _wire.Configure(new AmazonEC2Config());
        ec2.AuthenticationRegion = Region;
        ec2.AllowAutoRedirect = false;
        return new Ec2EffectProvider(configuration, new LifecycleProfile(RunnerOwnershipTags.DeploymentIdentity(configuration), ProfileId), AwsWireTransport.Credentials,
            ec2, _wire.Configure(new AmazonIdentityManagementServiceConfig()),
            _wire.Configure(new AmazonSecurityTokenServiceConfig()), _clock);
    }
    internal IReadOnlyList<AwsWireRequest> WireRequests => _wire.Requests;
    internal NameValueCollection[] Requests => _wire.Requests.Select(ParseQuery).ToArray();
    internal string IdentityResponse { get; set; } = CallerIdentity();
    internal HttpStatusCode IdentityStatus { get; set; } = HttpStatusCode.OK;
    internal string TemplateResponse { get; set; } = Template();
    internal string ImageResponse { get; set; } = Image();
    internal string InstanceTypeResponse { get; set; } = InstanceTypes();
    internal string ProfileResponse { get; set; } = InstanceProfile();
    internal string RoleResponse { get; set; } = Role();
    internal string SubnetResponse { get; set; } = Subnets();
    internal string GroupsResponse { get; set; } = Groups();
    internal Func<NameValueCollection, HttpResponseMessage> RouteTableResponse { get; set; } =
        static _ => AwsWireTransport.Xml(RouteTables());
    internal Func<NameValueCollection, HttpResponseMessage>? LaunchResponse { get; set; }
    internal Func<NameValueCollection, HttpResponseMessage>? TerminationResponse { get; set; }
    internal Func<NameValueCollection, HttpResponseMessage> InventoryResponse { get; set; } =
        static _ => AwsWireTransport.Xml(InventoryPage());

    internal static async Task<RuntimeConfiguration> ConfigurationAsync(
        CancellationToken cancellationToken, string selector = "$Latest", bool inspectAws = true, string? instanceType = null,
        bool active = false, string? subnetStrategy = null)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ec2-only-latest.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!;
        var spec = document["spec"]!;
        spec["dryRun"]!["inspectAws"] = inspectAws;
        if (active)
        {
            spec["mode"] = "active";
        }
        var provisioner = spec["profiles"]![0]!["provisioner"]!;
        if (instanceType is not null)
        {
            provisioner["instanceType"] = instanceType;
        }
        if (subnetStrategy is not null)
        {
            provisioner["subnetStrategy"] = subnetStrategy;
        }
        provisioner["launchTemplateVersion"] = selector.StartsWith('$')
            ? new JsonObject { ["type"] = "alias", ["name"] = selector }
            : new JsonObject { ["type"] = "pinned", ["version"] = selector };
        return RuntimeConfigurationLoader.Load(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    internal static string Template(long version = 12, bool encrypted = true, string tokens = "required", string imageId = ImageId,
        string protocol = "v1", bool byName = false, string extra = "", string rootDevice = RootDevice,
        string? endpoint = "enabled", string? instanceType = "m7i.large", string? ebsKey = null)
    {
        string profile = byName ? $"<name>{RoleName}</name>" : $"<arn>{ProfileArn}</arn>";
        string endpointElement = endpoint is null ? string.Empty : $"<httpEndpoint>{endpoint}</httpEndpoint>";
        string instanceTypeElement = instanceType is null ? string.Empty : $"<instanceType>{instanceType}</instanceType>";
        string key = ebsKey is null ? string.Empty : $"<kmsKeyId>{ebsKey}</kmsKeyId>";
        return FormattableString.Invariant($"""
            <DescribeLaunchTemplateVersionsResponse xmlns="{Namespace}">
              <requestId>template-1</requestId>
              <launchTemplateVersionSet><item>
                <launchTemplateId>{TemplateId}</launchTemplateId><versionNumber>{version}</versionNumber>
                <defaultVersion>true</defaultVersion>
                <launchTemplateData>
                  <imageId>{imageId}</imageId>{instanceTypeElement}
                  <iamInstanceProfile>{profile}</iamInstanceProfile>
                  <metadataOptions>{endpointElement}<httpTokens>{tokens}</httpTokens></metadataOptions>
                  <blockDeviceMappingSet><item><deviceName>{rootDevice}</deviceName>
                    <ebs><encrypted>{(encrypted ? "true" : "false")}</encrypted>{key}</ebs></item></blockDeviceMappingSet>
                  <tagSpecificationSet><item><resourceType>instance</resourceType><tagSet><item>
                    <key>scaleset-aws:bootstrap-protocol</key><value>{protocol}</value>
                  </item></tagSet></item></tagSpecificationSet>
                  {extra}
                </launchTemplateData>
              </item></launchTemplateVersionSet>
            </DescribeLaunchTemplateVersionsResponse>
            """);
    }

    internal static string Image(string architecture = "x86_64", string platform = "") => $"""
        <DescribeImagesResponse xmlns="{Namespace}">
          <requestId>images-1</requestId><imagesSet><item>
            <imageId>{ImageId}</imageId><architecture>{architecture}</architecture>
            <rootDeviceName>{RootDevice}</rootDeviceName><rootDeviceType>ebs</rootDeviceType>
            <imageState>available</imageState><platformDetails>Linux/UNIX</platformDetails>{platform}
          </item></imagesSet>
        </DescribeImagesResponse>
        """;

    internal static string InstanceTypes(string instanceType = "m7i.large", string architecture = "x86_64", string next = "") => $"""
        <DescribeInstanceTypesResponse xmlns="{Namespace}">
          <requestId>instance-types-1</requestId>{next}
          <instanceTypeSet><item><instanceType>{instanceType}</instanceType>
            <processorInfo><supportedArchitectures><item>{architecture}</item></supportedArchitectures></processorInfo>
          </item></instanceTypeSet>
        </DescribeInstanceTypesResponse>
        """;

    internal static string CallerIdentity(string account = Account, string partition = "aws", string? arnAccount = null) => $"""
        <GetCallerIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
          <GetCallerIdentityResult><Account>{account}</Account>
            <Arn>arn:{partition}:sts::{arnAccount ?? account}:assumed-role/fixture-controller/fake-session</Arn>
            <UserId>AROAEXAMPLE0123456789:fake-session</UserId></GetCallerIdentityResult>
          <ResponseMetadata><RequestId>caller-1</RequestId></ResponseMetadata>
        </GetCallerIdentityResponse>
        """;

    internal static string InstanceProfile(string arn = ProfileArn, string roleArn = RoleArn) => $"""
        <GetInstanceProfileResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/">
          <GetInstanceProfileResult><InstanceProfile>
            <InstanceProfileName>{RoleName}</InstanceProfileName><Arn>{arn}</Arn>
            <Roles><member><RoleName>{RoleName}</RoleName><RoleId>{RoleId}</RoleId><Arn>{roleArn}</Arn></member></Roles>
          </InstanceProfile></GetInstanceProfileResult>
          <ResponseMetadata><RequestId>iam-profile</RequestId></ResponseMetadata>
        </GetInstanceProfileResponse>
        """;

    internal static string Role(string roleId = RoleId) => $"""
        <GetRoleResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/">
          <GetRoleResult><Role>
            <RoleName>{RoleName}</RoleName><RoleId>{roleId}</RoleId><Arn>{RoleArn}</Arn>
          </Role></GetRoleResult><ResponseMetadata><RequestId>iam-role</RequestId></ResponseMetadata>
        </GetRoleResponse>
        """;

    internal static string Subnets(string firstVpc = Vpc, string firstPublic = "false", string firstZone = Region + "a",
        string secondAvailable = "0") => $"""
        <DescribeSubnetsResponse xmlns="{Namespace}">
          <requestId>subnets-1</requestId><subnetSet>
            <item><subnetId>{Subnet2}</subnetId><vpcId>{Vpc}</vpcId>
              <availabilityZone>{Region}b</availabilityZone><availableIpAddressCount>{secondAvailable}</availableIpAddressCount>
              <state>available</state><mapPublicIpOnLaunch>false</mapPublicIpOnLaunch></item>
            <item><subnetId>{Subnet1}</subnetId><vpcId>{firstVpc}</vpcId>
              <availabilityZone>{firstZone}</availabilityZone><availableIpAddressCount>7</availableIpAddressCount>
              <state>available</state><mapPublicIpOnLaunch>{firstPublic}</mapPublicIpOnLaunch></item>
          </subnetSet>
        </DescribeSubnetsResponse>
        """;

    internal static string Groups(string vpc = Vpc) => $"""
        <DescribeSecurityGroupsResponse xmlns="{Namespace}">
          <requestId>groups-1</requestId><securityGroupInfo><item>
            <groupId>{Group}</groupId><vpcId>{vpc}</vpcId>
          </item></securityGroupInfo>
        </DescribeSecurityGroupsResponse>
        """;

    internal static string RouteTables(string? tables = null, string next = "") => $"""
        <DescribeRouteTablesResponse xmlns="{Namespace}">
          <requestId>routes-1</requestId>{next}
          <routeTableSet>{tables ?? RouteTable("rtb-0123456789abcdef0", main: true) +
              RouteTable("rtb-0123456789abcdef1", subnet: Subnet2)}</routeTableSet>
        </DescribeRouteTablesResponse>
        """;

    internal static string RouteTable(string id, bool main = false, string? subnet = null, string extraRoutes = "",
        string? associationState = "associated")
    {
        string mainElement = main ? "true" : "false";
        string subnetElement = subnet is null ? string.Empty : $"<subnetId>{subnet}</subnetId>";
        string stateElement = associationState is null ? string.Empty : $"<associationState><state>{associationState}</state></associationState>";
        return $"""
            <item><routeTableId>{id}</routeTableId><vpcId>{Vpc}</vpcId><ownerId>{Account}</ownerId>
              <associationSet><item><routeTableId>{id}</routeTableId><main>{mainElement}</main>{subnetElement}
                <routeTableAssociationId>rtbassoc-{id[4..]}</routeTableAssociationId>{stateElement}</item></associationSet>
              <routeSet>
                <item><destinationCidrBlock>10.0.0.0/16</destinationCidrBlock><gatewayId>local</gatewayId><state>active</state></item>
                <item><destinationCidrBlock>0.0.0.0/0</destinationCidrBlock><natGatewayId>nat-0123456789abcdef0</natGatewayId><state>active</state></item>
                <item><destinationIpv6CidrBlock>::/0</destinationIpv6CidrBlock><egressOnlyInternetGatewayId>eigw-0123456789abcdef0</egressOnlyInternetGatewayId><state>active</state></item>
                {extraRoutes}
              </routeSet></item>
            """;
    }

    internal static string InventoryPage(string instances = "", string? next = null, string owner = Account)
    {
        string token = next is null ? string.Empty : $"<nextToken>{next}</nextToken>";
        string reservations = instances.Length == 0 ? string.Empty : $"""
            <item><ownerId>{owner}</ownerId><instancesSet>{instances}</instancesSet></item>
            """;
        return $"""
            <DescribeInstancesResponse xmlns="{Namespace}">
              <requestId>instances-1</requestId>{token}<reservationSet>{reservations}</reservationSet>
            </DescribeInstancesResponse>
            """;
    }

    internal static string Instance(string id = Instance1, string state = "running", int code = 16, string version = "12",
        string lastLaunch = OriginalLaunch, string? originalLaunch = OriginalLaunch, string profile = ProfileId, string allocation = Allocation,
        string? instanceProfileArn = null)
    {
        string network = originalLaunch is null ? string.Empty : $"""
            <networkInterfaceSet><item><attachment><deviceIndex>0</deviceIndex><attachTime>{originalLaunch}</attachTime></attachment></item></networkInterfaceSet>
            """;
        string credential = instanceProfileArn is null ? string.Empty : $"<iamInstanceProfile><arn>{instanceProfileArn}</arn></iamInstanceProfile>";
        return FormattableString.Invariant($"""
            <item><instanceId>{id}</instanceId><launchTime>{lastLaunch}</launchTime>
              <instanceState><code>{code}</code><name>{state}</name></instanceState>
              <tagSet>
                <item><key>scaleset-aws:controller</key><value>scaleset-dotnet-aws</value></item>
                <item><key>scaleset-aws:deployment</key><value>{Deployment}</value></item>
                <item><key>scaleset-aws:profile</key><value>{profile}</value></item>
                <item><key>scaleset-aws:allocation</key><value>{allocation}</value></item>
                <item><key>aws:ec2launchtemplate:id</key><value>{TemplateId}</value></item>
                <item><key>aws:ec2launchtemplate:version</key><value>{version}</value></item>
              </tagSet>{network}{credential}
            </item>
            """);
    }

    private HttpResponseMessage Respond(AwsWireRequest wire)
    {
        var query = ParseQuery(wire);
        return query["Action"] switch
        {
            "GetCallerIdentity" => AwsWireTransport.Xml(IdentityResponse, IdentityStatus),
            "DescribeLaunchTemplateVersions" => AwsWireTransport.Xml(TemplateResponse),
            "DescribeImages" => AwsWireTransport.Xml(ImageResponse),
            "DescribeInstanceTypes" => AwsWireTransport.Xml(InstanceTypeResponse),
            "GetInstanceProfile" => AwsWireTransport.Xml(ProfileResponse),
            "GetRole" => AwsWireTransport.Xml(RoleResponse),
            "DescribeSubnets" => AwsWireTransport.Xml(SubnetResponse),
            "DescribeSecurityGroups" => AwsWireTransport.Xml(GroupsResponse),
            "DescribeRouteTables" => RouteTableResponse(query),
            "DescribeInstances" => InventoryResponse(query),
            "RunInstances" when LaunchResponse is not null => LaunchResponse(query),
            "TerminateInstances" when TerminationResponse is not null => TerminationResponse(query),
            _ => throw new InvalidOperationException("The fixture only supports the EC2 inspection read surface."),
        };
    }

    private static NameValueCollection ParseQuery(AwsWireRequest request) =>
        HttpUtility.ParseQueryString(request.Body.Length != 0 ? request.Body : request.Uri.Query);

    public void Dispose()
    {
        Inspector.Dispose();
        _wire.Dispose();
    }

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
