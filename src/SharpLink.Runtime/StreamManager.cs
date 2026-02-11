namespace SharpLink.Runtime;

public class StreamManager : IStreamManager
{
    private readonly ConcurrentDictionary<StreamKey, IStreamDispatcher> _dispatchers = new();

    public void Register(long requestId, IStreamDispatcher dispatcher) => Register(requestId, 0, dispatcher);

    public void Register(long requestId, sbyte streamId, IStreamDispatcher dispatcher)
    {
        _dispatchers[new StreamKey(requestId, streamId)] = dispatcher;
    }

    public void Unregister(long requestId) => Unregister(requestId, 0);

    public void Unregister(long requestId, sbyte streamId)
    {
        _dispatchers.TryRemove(new StreamKey(requestId, streamId), out _);
    }

    public async ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload)
    {
        await DispatchChunkAsync(requestId, 0, payload);
    }

    public async ValueTask DispatchChunkAsync(long requestId, sbyte streamId, ReadOnlySequence<byte> payload)
    {
        if (_dispatchers.TryGetValue(new StreamKey(requestId, streamId), out var dispatcher))
        {
            await dispatcher.DispatchAsync(payload);
        }
    }

    public void CompleteStream(long requestId, bool isError, string? msg)
    {
        CompleteStream(requestId, 0, isError, msg);
    }

    public void CompleteStream(long requestId, sbyte streamId, bool isError, string? msg)
    {
        if (_dispatchers.TryRemove(new StreamKey(requestId, streamId), out var dispatcher))
        {
            dispatcher.Complete(isError, msg);
        }
    }

    public void CompleteAll(bool isError, string? msg)
    {
        foreach (var key in _dispatchers.Keys)
        {
            if (_dispatchers.TryRemove(key, out var dispatcher))
            {
                dispatcher.Complete(isError, msg);
            }
        }
    }

    private readonly record struct StreamKey(long RequestId, sbyte StreamId);
}
