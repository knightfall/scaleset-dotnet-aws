using System.Net;
using Knightfall.ScaleSet.Aws.Controller.Health;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static class HealthcheckCommand
{
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        LoopbackHealthAddress address;
        try
        {
            address = args switch
            {
                ["healthcheck"] => LoopbackHealthAddress.Parse(null),
                ["healthcheck", "--health-addr", string value] => LoopbackHealthAddress.Parse(value),
                _ => throw new ArgumentException("Use healthcheck [--health-addr <loopback-ip:port>].", nameof(args)),
            };
        }
        catch (ArgumentException error)
        {
            await stderr.WriteLineAsync(error.Message.AsMemory(), cancellationToken);
            return 2;
        }

        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
        }) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync(address.ProbeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                await stdout.WriteLineAsync("healthy".AsMemory(), cancellationToken);
                return 0;
            }
        }
        catch (HttpRequestException)
        {
            // Endpoint errors produce the same bounded probe result.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        await stderr.WriteLineAsync("The controller health probe failed.".AsMemory(), cancellationToken);
        return 1;
    }
}
