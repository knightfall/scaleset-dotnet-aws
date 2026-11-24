using Amazon;
using Amazon.Runtime;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

/// <summary>Builds region-pinned single-attempt AWS clients for controller operations.</summary>
public static class ControllerAwsClientConfiguration
{
    public static T Create<T>(string region, Uri? loopbackEndpoint = null) where T : ClientConfig, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        var configuration = new T
        {
            IgnoreConfiguredEndpointUrls = true,
            AllowAutoRedirect = false,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
            DisableClockSkewCorrection = true,
        };
        if (loopbackEndpoint is null)
        {
            configuration.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        else
        {
            if (!IsLoopbackEndpoint(loopbackEndpoint))
            {
                throw new ArgumentException("The local AWS endpoint must be a loopback HTTP(S) address.", nameof(loopbackEndpoint));
            }
            configuration.ServiceURL = loopbackEndpoint.AbsoluteUri;
            configuration.AuthenticationRegion = region;
        }
        return configuration;
    }

    public static Uri? ReadLoopbackEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var endpoint) && IsLoopbackEndpoint(endpoint)
            ? endpoint
            : throw new ArgumentException("The local AWS endpoint must be a loopback HTTP(S) address.", nameof(value));
    }

    private static bool IsLoopbackEndpoint(Uri endpoint) => endpoint.IsAbsoluteUri && endpoint.IsLoopback &&
        endpoint.Scheme is "http" or "https" && endpoint.AbsolutePath == "/" &&
        endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0;
}
