using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Runtime;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

/// <summary>Stops a restore drill request before sending if it names anything except the isolated target table.</summary>
internal sealed class RestoredTableDynamoWire(Uri endpoint, string table) : HttpClientFactory
{
    internal static AmazonDynamoDBClient CreateClient(MiniStackIamClients clients, string table)
    {
        var options = clients.Configure<AmazonDynamoDBConfig>();
        options.HttpClientFactory = new RestoredTableDynamoWire(clients.Endpoint, table);
        return new AmazonDynamoDBClient(clients.Credentials, options);
    }

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new TargetOnly(endpoint, table));

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;

    private sealed class TargetOnly(Uri endpoint, string table) : DelegatingHandler(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { Scheme: "http", Host: "127.0.0.1", UserInfo.Length: 0 } uri ||
                uri.Port != endpoint.Port || request.Method != HttpMethod.Post || request.Content is null)
            {
                throw new InvalidOperationException("The restore drill only permits the native loopback DynamoDB endpoint.");
            }
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("TransactItems", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    RequireTarget(item.EnumerateObject().Single().Value);
                }
            }
            else
            {
                RequireTarget(root);
            }
            return await base.SendAsync(request, cancellationToken);
        }

        private void RequireTarget(JsonElement request)
        {
            if (!request.TryGetProperty("TableName", out var name) || name.GetString() != table)
            {
                throw new InvalidOperationException("The restore drill tried to read or write outside the restored table.");
            }
        }
    }
}
