using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed record SessionChange(
    LifecycleWriteOutcome Outcome, SessionRecord? Session = null, SessionSendAuthorization? Permission = null);

public sealed class SessionSendAuthorization
{
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly DateTimeOffset _upperUtcNow;
    private int _consumed;

    internal SessionSendAuthorization(SessionRecord session, TimeProvider clock, long started, DateTimeOffset upperUtcNow)
    {
        Session = session;
        _clock = clock;
        _started = started;
        _upperUtcNow = upperUtcNow;
    }

    public SessionRecord Session { get; }

    internal bool WasConsumed => Volatile.Read(ref _consumed) != 0;

    public bool TryConsume(out TimeSpan remaining)
    {
        var estimate = _upperUtcNow + _clock.GetElapsedTime(_started);
        var local = _clock.GetUtcNow();
        remaining = Session.AttemptDeadline - (estimate > local ? estimate : local);
        return remaining > TimeSpan.Zero && Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }
}
