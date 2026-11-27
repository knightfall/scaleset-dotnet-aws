using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class GitHubProtocolHost : IAsyncDisposable
{
    internal const string PublicApi = "api.github.com";
    internal const string EnterpriseApi = "api.example.ghe.com";
    internal const string ServiceHost = "service-fixture.actions.githubusercontent.com";
    internal const string QueueHost = "queue-fixture.actions.githubusercontent.com";

    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly string[] _hosts;
    private GitHubProtocolHost(WebApplication app, X509Certificate2 certificate, string[] hosts)
    {
        _app = app;
        _certificate = certificate;
        _hosts = hosts;
        var listening = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new Uri(listening);
        Port = endpoint.Port;
    }

    internal int Port { get; }

    internal string Fingerprint => _certificate.GetCertHashString(HashAlgorithmName.SHA256);

    internal static Task<GitHubProtocolHost> StartAsync(GitHubSimulator simulator, CancellationToken cancellationToken,
        Func<HttpContext, Task>? beforeRequest = null) => StartAsync([simulator], cancellationToken, beforeRequest);

    internal static async Task<GitHubProtocolHost> StartAsync(IReadOnlyList<GitHubSimulator> simulators, CancellationToken cancellationToken,
        Func<HttpContext, Task>? beforeRequest = null)
    {
        var hosts = simulators.SelectMany(simulator => simulator.Address.Hosts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var certificate = CreateCertificate(hosts);
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
            if (beforeRequest is not null)
            {
                app.Use(async (context, next) =>
                {
                    await beforeRequest(context).ConfigureAwait(false);
                    if (!context.RequestAborted.IsCancellationRequested)
                    {
                        await next(context).ConfigureAwait(false);
                    }
                });
            }
            foreach (var simulator in simulators)
            {
                simulator.Map(app);
            }
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return new GitHubProtocolHost(app, certificate, hosts);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            certificate.Dispose();
            throw;
        }
    }

    internal HttpClient CreateClient() => CreateClient(Port, Fingerprint, _hosts);

    internal static HttpClient CreateClient(int port, string fingerprint, IEnumerable<string>? allowedHosts = null)
    {
        var hosts = (allowedHosts ?? GitHubSimulatorAddress.Default.Hosts).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            ConnectCallback = (context, token) => ConnectAsync(context, port, hosts, token),
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

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, int port,
        HashSet<string> hosts, CancellationToken cancellationToken)
    {
        if (context.DnsEndPoint.Port != 443 || !hosts.Contains(context.DnsEndPoint.Host) ||
            context.InitialRequestMessage.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("The simulator transport accepts only its pinned HTTPS hosts.");
        }
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static X509Certificate2 CreateCertificate(IEnumerable<string> hosts)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Scale Set Test Simulator", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (string host in hosts)
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
