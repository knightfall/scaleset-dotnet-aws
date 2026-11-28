using System.Diagnostics;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal sealed partial class ControllerRuntimeLog(ILogger logger, TimeProvider clock)
{
    private static readonly ActivitySource Source = new(TelemetryNames.Controller);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<(string Operation, string Reason, TelemetryProfile? Profile), long> _lastReported = [];

    internal static Activity? StartAttempt(string operation) =>
        Source.StartActivity($"scaleset.aws.controller.{operation}")?.SetTag("operation", operation);

    internal void Write(LogLevel level, string operation, string outcome, string reason,
        TelemetryProfile? profile = null, bool throttle = false)
    {
        // These keys come from fixed call sites and configured profiles.
        if (throttle)
        {
            lock (_lastReported)
            {
                var key = (operation, reason, profile);
                long now = clock.GetTimestamp();
                if (_lastReported.TryGetValue(key, out long previous) && clock.GetElapsedTime(previous, now) < RepeatInterval)
                {
                    return;
                }
                _lastReported[key] = now;
            }
        }

        try
        {
            using var activity = Activity.Current is null ? StartAttempt(operation) : null;
            activity?.SetTag("outcome", outcome).SetTag("reason", reason);
            profile?.SetTags(activity);
            using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
            {
                new("reason", reason),
                new("profile", profile?.Id),
                new("provider", profile?.Provider),
            });
            LogOutcome(logger, level, operation, outcome);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostics must not affect the control-plane result.
        }
    }

    [LoggerMessage(EventId = 903, Message = TelemetryNames.EventTemplate)]
    private static partial void LogOutcome(ILogger logger, LogLevel level, string operation, string outcome);
}
