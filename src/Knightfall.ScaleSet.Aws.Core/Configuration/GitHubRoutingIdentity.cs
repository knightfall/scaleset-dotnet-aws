using System.Text;

namespace Knightfall.ScaleSet.Aws.Core.Configuration;

public static class GitHubRoutingIdentity
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Scope(string scopeUrl) => Scope(scopeUrl, nameof(scopeUrl));

    public static string Name(string name) => Name(name, nameof(name));

    internal static string Scope(string scopeUrl, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeUrl, parameterName);
        try
        {
            _ = StrictUtf8.GetByteCount(scopeUrl);
            return RuntimeConfigurationSemantics.GitHubScopeIdentity(scopeUrl, parameterName);
        }
        catch (Exception error) when (error is RuntimeConfigurationException or EncoderFallbackException)
        {
            throw new ArgumentException("The request requires an exact supported GitHub scope.", parameterName);
        }
    }

    internal static string Name(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        try
        {
            _ = StrictUtf8.GetByteCount(name);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("A GitHub routing name must be valid Unicode.", parameterName);
        }
        if (name.Any(char.IsControl))
        {
            throw new ArgumentException("A GitHub routing name must not contain control characters.", parameterName);
        }

        return RuntimeConfigurationSemantics.Fold(name);
    }
}
