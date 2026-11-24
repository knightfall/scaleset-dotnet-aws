using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Core.Telemetry;

/// <summary>Diagnostic identity derived from a configured runner profile.</summary>
public sealed record TelemetryProfile
{
    public const int MaximumIdLength = 256;

    private TelemetryProfile(string id, string provider)
    {
        Id = id;
        Provider = provider;
    }

    public string Id { get; }
    public string Provider { get; }

    public static TelemetryProfile? From(RunnerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrEmpty(profile.Id) || !char.IsAsciiLetterOrDigit(profile.Id[0]) ||
            profile.Id.Any(static value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '-')))
        {
            return null;
        }

        return profile.Provisioner.Type switch
        {
            RunnerProvisionerType.Ec2 => new TelemetryProfile(BoundId(profile.Id), "ec2"),
            RunnerProvisionerType.Fargate => new TelemetryProfile(BoundId(profile.Id), "fargate"),
            _ => null,
        };
    }

    public void SetTags(Activity? activity)
    {
        activity?.SetTag("profile", Id).SetTag("provider", Provider);
    }

    public TagList Tags() => new() { { "profile", Id }, { "provider", Provider } };

    private static string BoundId(string id)
    {
        if (id.Length <= MaximumIdLength)
        {
            return id;
        }

        // ':' is excluded by the profile schema, so compact IDs cannot alias ordinary names.
        const int digestLength = 64;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(id)));
        return string.Concat(id.AsSpan(0, MaximumIdLength - digestLength - 1), ":", hash);
    }
}

/// <summary>Passes configured profile identity to diagnostics in awaited adapters.</summary>
public static class TelemetryProfileScope
{
    private static readonly AsyncLocal<TelemetryProfile?> Profile = new();

    public static TelemetryProfile? Current => Profile.Value;

    public static IDisposable Enter(TelemetryProfile? profile)
    {
        var scope = new ProfileContext(Profile.Value);
        Profile.Value = profile;
        return scope;
    }

    private sealed class ProfileContext(TelemetryProfile? previous) : IDisposable
    {
        public void Dispose() => Profile.Value = previous;
    }
}
