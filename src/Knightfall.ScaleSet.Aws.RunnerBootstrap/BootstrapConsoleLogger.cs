using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

internal sealed class BootstrapConsoleLogger(TextWriter output, TextWriter errors) : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            (logLevel >= LogLevel.Warning ? errors : output).WriteLine(formatter(state, null));
        }
    }
}
