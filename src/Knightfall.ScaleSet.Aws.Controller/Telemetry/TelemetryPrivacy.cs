using System.Collections.Frozen;
using System.Diagnostics;
using OpenTelemetry;
using Serilog.Events;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal sealed class TelemetryPrivacy(FrozenDictionary<string, string> profiles) : BaseProcessor<Activity>
{
    internal string? Profile(string? profile, string? provider) =>
        profile is not null && provider is not null && profiles.TryGetValue(profile, out string? configuredProvider) &&
        configuredProvider == provider ? profile : null;

    internal static bool IsApprovedSource(string? category)
    {
        if (category is null || category.Length > 192 ||
            category.Any(static value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '`')))
        {
            return false;
        }
        return TelemetryNames.Components.Any(component => category == component ||
                category.StartsWith(component + ".", StringComparison.Ordinal)) ||
            category is TelemetryNames.Client or TelemetryNames.Listener ||
            category.StartsWith("ScaleSet.Client.", StringComparison.Ordinal) ||
            category.StartsWith("ScaleSet.Listener.", StringComparison.Ordinal);
    }

    internal static string? Scalar(LogEvent log, string property) =>
        log.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: string text } ? text : null;

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // The Activity API exposes no way to remove individual events or
        // links. Drop a record whose unapproved data could otherwise escape.
        if (data.Events.Any() || data.Links.Any())
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }

        data.TraceStateString = null;
        data.SetStatus(data.Status);
        string? profile = Profile(data.GetTagItem("profile") as string, data.GetTagItem("provider") as string);
        foreach (var (key, value) in data.TagObjects.ToArray())
        {
            if (!AllowsTag(data.Source.Name, key, value, profile))
            {
                data.SetTag(key, key == "reason" && TelemetryNames.Components.Contains(data.Source.Name) ? "other" : null);
            }
        }
        if (data.Source.Name == TelemetryNames.Http)
        {
            data.DisplayName = data.GetTagItem("http.request.method") as string ?? "HTTP";
        }
    }

    private static bool AllowsTag(string source, string key, object? value, string? profile)
    {
        if (source == TelemetryNames.Http)
        {
            return key switch
            {
                "http.request.method" => value is string method && TelemetryNames.IsHttpMethod(method),
                "http.response.status_code" => value is int and (>= 100 and <= 599) or long and (>= 100 and <= 599),
                "network.protocol.version" => value is "1.0" or "1.1" or "2" or "3",
                "server.address" => value is "github.com" or "api.github.com",
                _ => false,
            };
        }
        if (key == "http.request.method" && source == TelemetryNames.Client)
        {
            return value is string method && TelemetryNames.IsHttpMethod(method);
        }
        return key switch
        {
            "profile" => TelemetryNames.Components.Contains(source) && profile is not null,
            "provider" => TelemetryNames.Components.Contains(source) && value is "ec2" or "fargate",
            "operation" => value is string operation && TelemetryNames.Operations.Contains(operation),
            "outcome" => value is string outcome && TelemetryNames.Outcomes.Contains(outcome),
            "reason" => value is string reason && TelemetryNames.Reasons.Contains(reason),
            "dependency" when source == TelemetryNames.Client => value is "github" or "actions",
            "status_class" when source == TelemetryNames.Client => value is "1xx" or "2xx" or "3xx" or "4xx" or "5xx" or "none",
            _ => false,
        };
    }

}
