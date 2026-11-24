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
    TimeSpan MaximumAbsoluteSkew)
{
    public bool TryGetUpperBound(DateTimeOffset now, DateTimeOffset writeDeadline, TimeSpan reserve, out DateTimeOffset upperBound)
    {
        upperBound = default;
        TimeSpan remaining = writeDeadline - now;
        if (ObservedAt > now || ValidUntil <= now || MaximumAbsoluteSkew < TimeSpan.Zero || reserve < TimeSpan.Zero ||
            remaining <= reserve || MaximumAbsoluteSkew >= remaining - reserve)
        {
            return false;
        }

        upperBound = now + MaximumAbsoluteSkew;
        return ValidUntil > upperBound;
    }
}

/// <summary>Independently verified isolation from both external mutation surfaces for one exact task.</summary>
public sealed record VerifiedControllerIsolation(
    string TaskArn,
    string AwsEvidenceId,
    string GitHubEvidenceId,
    DateTimeOffset ObservedAt,
    DateTimeOffset ValidUntil);
