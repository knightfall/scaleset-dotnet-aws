using Amazon.Runtime;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

internal sealed class BootstrapHttpFactory : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    });

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
}
