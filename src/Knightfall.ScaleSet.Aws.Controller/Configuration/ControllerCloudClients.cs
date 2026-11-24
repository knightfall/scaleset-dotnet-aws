using Amazon.DynamoDBv2;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SecretsManager;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Providers.Ec2;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

internal sealed class ControllerCloudClients(string region, AWSCredentials credentials, Uri? endpoint) : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    internal static async Task<ControllerCloudClients> ConnectAsync(AwsEnvironment aws, Uri? endpoint, CancellationToken cancellationToken)
    {
        var options = ControllerAwsClientConfiguration.Create<AmazonSecurityTokenServiceConfig>(aws.Region, endpoint);
        var credentials = await DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync(options).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        using var sts = new AmazonSecurityTokenServiceClient(credentials, options);
        var caller = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken).ConfigureAwait(false);
        if (caller.Account != aws.Account)
        {
            throw new InvalidOperationException("The AWS caller account does not match the verified runtime configuration.");
        }

        return new ControllerCloudClients(aws.Region, credentials, endpoint);
    }

    internal AmazonDynamoDBClient DynamoDb() => Own(new AmazonDynamoDBClient(credentials, Configure<AmazonDynamoDBConfig>()));

    internal AmazonECSClient Ecs() => Own(new AmazonECSClient(credentials, Configure<AmazonECSConfig>()));

    internal AmazonEC2Client Ec2Network() => Own(new AmazonEC2Client(credentials, Configure<AmazonEC2Config>()));

    internal AmazonIdentityManagementServiceClient Iam() =>
        Own(new AmazonIdentityManagementServiceClient(credentials, Configure<AmazonIdentityManagementServiceConfig>()));

    internal AmazonSecurityTokenServiceClient Sts() =>
        Own(new AmazonSecurityTokenServiceClient(credentials, Configure<AmazonSecurityTokenServiceConfig>()));

    internal AmazonSecretsManagerClient Secrets() => Own(new AmazonSecretsManagerClient(credentials, Configure<AmazonSecretsManagerConfig>()));

    internal AmazonSQSClient Queue() => Own(new AmazonSQSClient(credentials, Configure<AmazonSQSConfig>()));

    internal Ec2ProfileInspector Ec2Inventory(TimeProvider clock) => Own(new Ec2ProfileInspector(credentials, Configure<AmazonEC2Config>(),
        Configure<AmazonIdentityManagementServiceConfig>(), Configure<AmazonSecurityTokenServiceConfig>(), clock));

    internal Ec2EffectProvider Ec2Effects(RuntimeConfiguration configuration, LifecycleProfile profile, TimeProvider clock) =>
        Own(new Ec2EffectProvider(configuration, profile, credentials, Configure<AmazonEC2Config>(),
            Configure<AmazonIdentityManagementServiceConfig>(), Configure<AmazonSecurityTokenServiceConfig>(), clock));

    public void Dispose()
    {
        for (int index = _owned.Count - 1; index >= 0; index--)
        {
            _owned[index].Dispose();
        }
        _owned.Clear();
    }

    private T Configure<T>() where T : ClientConfig, new() => ControllerAwsClientConfiguration.Create<T>(region, endpoint);

    private T Own<T>(T client) where T : IDisposable
    {
        _owned.Add(client);
        return client;
    }
}
