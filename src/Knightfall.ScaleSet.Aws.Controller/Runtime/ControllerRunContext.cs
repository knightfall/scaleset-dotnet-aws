using Knightfall.ScaleSet.Aws.Controller.Health;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Runtime;

public sealed record ControllerEventQueues(string? Ec2, string? Ecs);

/// <summary>Verified startup inputs and the process-wide shutdown signals for the active runtime.</summary>
public sealed class ControllerRunContext
{
    private readonly Action<ControllerHealthSnapshot> _publishHealth;
    private readonly ControllerRunLifetime _lifetime;

    internal ControllerRunContext(
        RuntimeConfiguration configuration, long generation, TelemetryPipeline telemetry, ControllerEventQueues eventQueues,
        Uri? awsLoopbackEndpoint, Action<ControllerHealthSnapshot> publishHealth, ControllerRunLifetime lifetime)
    {
        Configuration = configuration;
        Generation = generation;
        Telemetry = telemetry;
        EventQueues = eventQueues;
        AwsLoopbackEndpoint = awsLoopbackEndpoint;
        _publishHealth = publishHealth;
        _lifetime = lifetime;
        Stopping = lifetime.Stopping;
        ShutdownDeadline = lifetime.ShutdownDeadline;
    }

    public RuntimeConfiguration Configuration { get; }
    public long Generation { get; }
    public TelemetryPipeline Telemetry { get; }
    public ControllerEventQueues EventQueues { get; }
    public Uri? AwsLoopbackEndpoint { get; }

    /// <summary>Signals that admissions and polling must stop immediately.</summary>
    public CancellationToken Stopping { get; }

    /// <summary>Bounds lifecycle cleanup before the process reserves time to stop health and telemetry.</summary>
    public CancellationToken ShutdownDeadline { get; }

    internal void RequestStop() => _lifetime.RequestStop();

    public void PublishHealth(ControllerHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _publishHealth(snapshot);
    }
}
