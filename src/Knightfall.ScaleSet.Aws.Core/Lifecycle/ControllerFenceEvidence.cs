namespace Knightfall.ScaleSet.Aws.Core.Lifecycle;

public enum ControllerTaskFenceStatus
{
    Unknown = 0,
    Stopped = 1,
    Running = 2,
    Missing = 3,
}

public interface IControllerTaskFenceObserver
{
    Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken);
}

public sealed record ControllerClockEvidence(
    DateTimeOffset ObservedAt,
    DateTimeOffset ValidUntil,
    TimeSpan MaximumAbsoluteSkew);

/// <summary>Independently verified isolation from both external mutation surfaces for one exact task.</summary>
public sealed record VerifiedControllerIsolation(
    string TaskArn,
    string AwsEvidenceId,
    string GitHubEvidenceId,
    DateTimeOffset ObservedAt,
    DateTimeOffset ValidUntil);
