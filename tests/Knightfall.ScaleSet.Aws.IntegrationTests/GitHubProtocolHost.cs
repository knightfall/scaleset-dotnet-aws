using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class GitHubProtocolHost : IAsyncDisposable
{
    internal const string PublicApi = "api.github.com";
    internal const string EnterpriseApi = "api.example.ghe.com";
    internal const string ServiceHost = "actions.example.test";
    internal const string QueueHost = "queue.example.test";
    private static readonly string[] Hosts = [PublicApi, EnterpriseApi, ServiceHost, QueueHost];

    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly IPAddress _address;
    private readonly int _port;

    private GitHubProtocolHost(WebApplication app, X509Certificate2 certificate)
    {
        _app = app;
        _certificate = certificate;
        var listening = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new Uri(listening);
        _address = IPAddress.Parse(endpoint.Host);
        _port = endpoint.Port;
    }

    internal static async Task<GitHubProtocolHost> StartAsync(GitHubSimulator simulator, CancellationToken cancellationToken)
    {
        var certificate = CreateCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Listen(IPAddress.Loopback, 0, listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseHttps(certificate);
            });
        });
        var app = builder.Build();
        try
        {
            simulator.Map(app);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return new GitHubProtocolHost(app, certificate);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            certificate.Dispose();
            throw;
        }
    }

    internal HttpClient CreateClient()
    {
        string fingerprint = _certificate.GetCertHashString(HashAlgorithmName.SHA256);
        return new HttpClient(new RequireHttp2(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    cert is not null && cert.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint &&
                    errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors,
            },
            ConnectCallback = ConnectAsync,
        }))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
        _certificate.Dispose();
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (context.DnsEndPoint.Port != 443 || !Hosts.Contains(context.DnsEndPoint.Host, StringComparer.OrdinalIgnoreCase) ||
            context.InitialRequestMessage.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("The simulator transport accepts only its pinned HTTPS hosts.");
        }
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_address, _port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Scale Set Test Simulator", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (string host in Hosts)
        {
            names.AddDnsName(host);
        }
        request.CertificateExtensions.Add(names.Build());
        var now = TimeProvider.System.GetUtcNow();
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    }

    private sealed class RequireHttp2(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
