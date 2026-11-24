using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal static class TelemetryViews
{
    private static readonly FrozenSet<string> ProfileMetrics = new[]
    {
        "capacity.assigned", "capacity.active", "capacity.idle", "capacity.starting", "capacity.unknown",
        "capacity.deficit", "capacity.drift", "capacity.orphan_candidates", "inventory.age", "reconcile.age",
        "cleanup.oldest_age", "capacity.configured_max", "capacity.configured_idle_reserve", "effects.unfinished", "listener.cooldown.remaining", "profile.ready",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> OperationMetrics = new[]
    {
        "reconcile.duration", "listener.operation.duration", "provider.operation.duration", "handoff.duration",
        "startup.duration", "handoff.operations", "listener.operations", "provider.operations", "lease.losses", "dynamodb.conflicts",
        "effect.rebuild.pages", "ec2.az_failovers", "ec2.template_drifts", "event.age",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RuntimeMetrics = new[]
    {
        "dotnet.gc.collections", "dotnet.gc.heap.total_allocated", "dotnet.gc.last_collection.heap.size",
        "dotnet.gc.last_collection.heap.fragmentation.size", "dotnet.gc.last_collection.memory.committed_size",
        "dotnet.gc.pause.time", "dotnet.process.memory.working_set", "dotnet.thread_pool.thread.count",
        "dotnet.thread_pool.queue.length", "dotnet.thread_pool.work_item.count", "dotnet.monitor.lock_contentions",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static MetricStreamConfiguration Configure(Instrument instrument)
    {
        if (instrument.Meter.Name == TelemetryNames.Client)
        {
            return instrument.Name switch
            {
                "scaleset.client.requests" or "scaleset.client.request.duration" => View("dependency", "method", "status_class"),
                "scaleset.client.retries" => View("dependency", "method", "retry_kind"),
                _ => MetricStreamConfiguration.Drop,
            };
        }
        if (instrument.Meter.Name == TelemetryNames.Http)
        {
            return instrument.Name == "http.client.request.duration"
                ? View("http.request.method", "http.response.status_code", "network.protocol.version")
                : MetricStreamConfiguration.Drop;
        }
        if (instrument.Meter.Name == "System.Runtime")
        {
            return RuntimeMetrics.Contains(instrument.Name) ? View("gc.heap.generation") : MetricStreamConfiguration.Drop;
        }
        const string prefix = "scaleset.aws.";
        if (!TelemetryNames.Components.Contains(instrument.Meter.Name) || !instrument.Name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return MetricStreamConfiguration.Drop;
        }
        var suffix = instrument.Name[prefix.Length..];
        if (ProfileMetrics.Contains(suffix))
        {
            return View("profile", "provider");
        }
        if (OperationMetrics.Contains(suffix))
        {
            return View("profile", "provider", "operation", "outcome", "reason");
        }
        return instrument.Meter.Name == TelemetryNames.Controller && suffix == "telemetry.smoke"
            ? View() : MetricStreamConfiguration.Drop;
    }

    private static MetricStreamConfiguration View(params string[] tags) => new() { TagKeys = tags, CardinalityLimit = 2_000 };
}
