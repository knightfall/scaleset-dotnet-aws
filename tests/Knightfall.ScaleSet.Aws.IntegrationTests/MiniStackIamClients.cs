using Amazon.DynamoDBv2;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecurityToken;
using Amazon.SQS;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class MiniStackIamClients : IDisposable
{
    private readonly ExactEndpointFactory _transport;

    internal MiniStackIamClients(Uri endpoint, AWSCredentials credentials)
    {
        Endpoint = endpoint;
        Credentials = credentials;
        _transport = new ExactEndpointFactory(endpoint);
        DynamoDb = new AmazonDynamoDBClient(credentials, Configure<AmazonDynamoDBConfig>());
        Ec2 = new AmazonEC2Client(credentials, Configure<AmazonEC2Config>());
        Ecs = new AmazonECSClient(credentials, Configure<AmazonECSConfig>());
        Iam = new AmazonIdentityManagementServiceClient(credentials, Configure<AmazonIdentityManagementServiceConfig>());
        Secrets = new AmazonSecretsManagerClient(credentials, Configure<AmazonSecretsManagerConfig>());
        Sts = new AmazonSecurityTokenServiceClient(credentials, Configure<AmazonSecurityTokenServiceConfig>());
        Sqs = new AmazonSQSClient(credentials, Configure<AmazonSQSConfig>());
    }

    internal Uri Endpoint { get; }

    internal AWSCredentials Credentials { get; }

    internal AmazonDynamoDBClient DynamoDb { get; }

    internal AmazonEC2Client Ec2 { get; }

    internal AmazonECSClient Ecs { get; }

    internal AmazonIdentityManagementServiceClient Iam { get; }

    internal AmazonSecretsManagerClient Secrets { get; }

    internal AmazonSecurityTokenServiceClient Sts { get; }

    internal AmazonSQSClient Sqs { get; }

    internal T Configure<T>() where T : ClientConfig, new() => new()
    {
        ServiceURL = Endpoint.AbsoluteUri,
        AuthenticationRegion = MiniStackIamFixture.Region,
        Timeout = TimeSpan.FromSeconds(15),
        MaxErrorRetry = 0,
        MaxStaleConnectionRetries = 0,
        DisableClockSkewCorrection = true,
        AllowAutoRedirect = false,
        HttpClientFactory = _transport,
    };

    public void Dispose()
    {
        Sqs.Dispose();
        Sts.Dispose();
        Secrets.Dispose();
        Iam.Dispose();
        Ecs.Dispose();
        Ec2.Dispose();
        DynamoDb.Dispose();
    }

    private sealed class ExactEndpointFactory(Uri endpoint) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new ExactEndpointHandler(endpoint));

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }

    private sealed class ExactEndpointHandler(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { Scheme: "http", Host: "127.0.0.1" } uri || uri.Port != endpoint.Port || uri.UserInfo.Length != 0)
            {
                throw new InvalidOperationException("The MiniStack fixture only permits its exact loopback endpoint.");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
