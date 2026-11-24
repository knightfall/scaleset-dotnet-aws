using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.ListenerConsumer;

public sealed class ConsumerCheckpointStore : IScaleSetListenerCheckpointStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ScaleSetListenerSessionKey, ScaleSetListenerCheckpoint> _records = [];
    private readonly List<ScaleSetListenerCheckpoint> _committed = [];
    private readonly CancellationToken _owner;

    public ConsumerCheckpointStore(ScaleSetListenerCheckpoint initial, CancellationToken owner)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _owner = owner;
        _records.Add(initial.SessionKey, initial);
    }

    public IReadOnlyList<ScaleSetListenerCheckpoint> Committed
    {
        get
        {
            lock (_gate)
            {
                return _committed.ToArray();
            }
        }
    }

    public Task<ScaleSetListenerCheckpoint?> ReadAsync(
        ScaleSetListenerSessionKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_records.GetValueOrDefault(key));
        }
    }

    public Task<ScaleSetListenerCheckpointWriteResult> WriteAsync(
        ScaleSetListenerSessionKey key,
        long? expectedRevision,
        ScaleSetListenerCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_owner.IsCancellationRequested ||
                key != checkpoint.SessionKey ||
                _records.GetValueOrDefault(key)?.Revision != expectedRevision)
            {
                return Task.FromResult(ScaleSetListenerCheckpointWriteResult.Conflict);
            }

            _records[key] = checkpoint;
            _committed.Add(checkpoint);
            return Task.FromResult(ScaleSetListenerCheckpointWriteResult.Committed);
        }
    }
}
