using System.Net;
using System.Net.Sockets;

namespace Knightfall.ScaleSet.Aws.Controller.Health;

public sealed class LoopbackHealthAddress
{
    public const string Default = "127.0.0.1:8080";

    internal LoopbackHealthAddress(IPAddress address, int port)
    {
        Address = address;
        Port = port;
    }

    internal IPAddress Address { get; }
    internal int Port { get; }
    internal Uri ProbeUri => new UriBuilder(Uri.UriSchemeHttp, Address.ToString(), Port, "/healthz").Uri;

    public static LoopbackHealthAddress Parse(string? value)
    {
        if (!IPEndPoint.TryParse(value ?? Default, out var endpoint) || endpoint.Port == 0 ||
            !IPAddress.IsLoopback(endpoint.Address) ||
            (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.Address.ScopeId != 0))
        {
            throw new ArgumentException("Use a numeric loopback address with a port for --health-addr.", nameof(value));
        }
        return new LoopbackHealthAddress(endpoint.Address, endpoint.Port);
    }
}
