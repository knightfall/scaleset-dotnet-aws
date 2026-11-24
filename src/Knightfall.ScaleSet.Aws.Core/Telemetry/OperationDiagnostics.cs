using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Knightfall.ScaleSet.Aws.Core.Telemetry;

/// <summary>Measures one bounded operation without emitting input or exception data.</summary>
public sealed class OperationDiagnostics : IDisposable
{
    private readonly Activity? _activity;
    private readonly Histogram<double>? _duration;
    private readonly Counter<long>? _counter;
    private readonly CancellationToken _cancellationToken;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private TagList _tags;
    private string _outcome = "unknown";
    private string _reason = "no_evidence";

    private OperationDiagnostics(Activity? activity, Histogram<double>? duration, Counter<long>? counter, TagList tags,
        CancellationToken cancellationToken)
    {
        _activity = activity;
        _duration = duration;
        _counter = counter;
        _cancellationToken = cancellationToken;
        _tags = tags;
        if (activity is { IsAllDataRequested: true })
        {
            foreach (var tag in tags)
            {
                activity.SetTag(tag.Key, tag.Value);
            }
        }
    }

    /// <summary>Starts diagnostics with code-defined names and an optional verified profile.</summary>
    public static OperationDiagnostics? Start(ActivitySource source, string spanSuffix, string operation,
        Histogram<double>? duration = null, Counter<long>? counter = null, TelemetryProfile? profile = null, string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var activity = source.HasListeners() ? source.StartActivity("scaleset.aws." + spanSuffix) : null;
        if (activity is null && duration?.Enabled != true && counter?.Enabled != true)
        {
            return null;
        }

        var tags = profile?.Tags() ?? default;
        if (profile is null && provider is not null)
        {
            tags.Add("provider", provider);
        }
        tags.Add("operation", operation);
        return new OperationDiagnostics(activity, duration, counter, tags, cancellationToken);
    }

    /// <summary>Sets the typed call site's final bounded decision.</summary>
    public void Complete(string outcome, string reason)
    {
        _outcome = outcome;
        _reason = reason;
    }

    /// <summary>Adds a profile once an initially unscoped observation has verified ownership.</summary>
    public void SetProfile(TelemetryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _tags.Remove(new KeyValuePair<string, object?>("provider", profile.Provider));
        _tags.Add("profile", profile.Id);
        _tags.Add("provider", profile.Provider);
        if (_activity is { IsAllDataRequested: true })
        {
            profile.SetTags(_activity);
        }
    }

    public void Dispose()
    {
        if (_reason == "no_evidence" && _cancellationToken.IsCancellationRequested)
        {
            Complete("cancelled", "cancellation");
        }

        _tags.Add("outcome", _outcome);
        _tags.Add("reason", _reason);

        if (_activity is { IsAllDataRequested: true } activity)
        {
            activity.SetTag("outcome", _outcome).SetTag("reason", _reason);
            if (_outcome == "error")
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }
        _activity?.Dispose();

        _duration?.Record(Stopwatch.GetElapsedTime(_startedAt).TotalSeconds, _tags);
        _counter?.Add(1, _tags);
    }
}
