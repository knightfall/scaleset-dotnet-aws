using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

internal static partial class BootstrapLog
{
    internal static async Task<int> ReportUsageAsync(TextWriter errors, TimeProvider clock)
    {
        await using var logger = BootstrapLogQueue.Start(new BootstrapConsoleLogger(TextWriter.Null, errors), clock);
        Write(logger, Usage);
        return 2;
    }

    internal static void Write(ILogger? logger, Action<ILogger> emit)
    {
        if (logger is null)
        {
            return;
        }

        try
        {
            emit(logger);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostics cannot replace the runner outcome or prevent status publication.
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Bootstrap status timed out.")]
    internal static partial void StatusTimedOut(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Bootstrap status was unavailable.")]
    internal static partial void StatusUnavailable(ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Bootstrap status was not confirmed.")]
    internal static partial void StatusUnconfirmed(ILogger logger);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "One-job runner connected.")]
    internal static partial void RunnerConnected(ILogger logger);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "One-job runner exited.")]
    internal static partial void RunnerExited(ILogger logger);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "{outcome}")]
    internal static partial void Outcome(ILogger logger, RunnerBootstrapOutcome outcome);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Error, Message = "EC2 bootstrap stopped.")]
    internal static partial void Ec2Stopped(ILogger logger);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Error,
        Message = "EC2 supervisor STATUS requires a current boot proof of job process teardown.")]
    internal static partial void Ec2TeardownUnqualified(ILogger logger);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Error,
        Message = "EC2 runner cleanup was incomplete. Retain the instance for operator recovery.")]
    internal static partial void Ec2CleanupIncomplete(ILogger logger);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Error, Message = "EC2 bootstrap failed.")]
    internal static partial void Ec2Failed(ILogger logger);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Error, Message = "Fargate bootstrap must run as PID 1.")]
    internal static partial void FargateRequiresPidOne(ILogger logger);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Error, Message = "Fargate bootstrap stopped.")]
    internal static partial void FargateStopped(ILogger logger);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Error, Message = "Fargate bootstrap failed.")]
    internal static partial void FargateFailed(ILogger logger);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Error,
        Message = "Run RunnerBootstrap on Linux with ec2 or fargate and an absolute settings path.")]
    private static partial void Usage(ILogger logger);
}
