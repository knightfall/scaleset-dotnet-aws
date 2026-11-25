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
        var tags = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in data.TagObjects.ToArray())
        {
            // SetTag removes one occurrence. Drain the snapshot before writing approved values back.
            data.SetTag(key, null);
            if (!tags.TryAdd(key, value))
            {
                tags[key] = null; // Repeated keys have no authoritative value.
            }
        }

        string source = data.Source.Name;
        string? profile = Profile(tags.GetValueOrDefault("profile") as string, tags.GetValueOrDefault("provider") as string);
        foreach (var (key, value) in tags)
        {
            if (AllowsTag(source, key, value, profile))
            {
                data.SetTag(key, value);
            }
            else if (key == "reason" && TelemetryNames.Components.Contains(source))
            {
                data.SetTag(key, "other");
            }
        }
        data.DisplayName = source switch
        {
            TelemetryNames.Http => data.GetTagItem("http.request.method") as string ?? "HTTP",
            TelemetryNames.Client => "scaleset.client.request",
            TelemetryNames.Listener when data.DisplayName == "scaleset.listener.step" => "scaleset.listener.step",
            TelemetryNames.Listener => "scaleset.listener.operation",
            _ => TelemetryNames.IsApplicationSpan(data.DisplayName) ? data.DisplayName : "scaleset.aws.operation",
        };
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
        if (source == TelemetryNames.Client)
        {
            return key switch
            {
                "http.request.method" => value is string method && TelemetryNames.IsHttpMethod(method),
                "url.scheme" => value is "http" or "https",
                "scaleset.dependency" => value is "github" or "actions" or "queue" or "other",
                _ => false,
            };
        }
        if (source == TelemetryNames.Listener)
        {
            return key switch
            {
                "scaleset.listener.operation" => value is "ReadCheckpoint" or "PersistReceived" or "Poll" or "HandleDelivery" or
                    "PersistProcessed" or "PersistAcknowledging" or "Acknowledge" or "PersistSettled",
                "scaleset.listener.outcome" => value is "completed" or "deferred" or "canceled" or "failed",
                _ => false,
            };
        }
        return key switch
        {
            "profile" or "provider" => TelemetryNames.Components.Contains(source) && profile is not null,
            "operation" => value is string operation && TelemetryNames.Operations.Contains(operation),
            "outcome" => value is string outcome && TelemetryNames.Outcomes.Contains(outcome),
            "reason" => value is string reason && TelemetryNames.Reasons.Contains(reason),
            _ => false,
        };
    }

}
