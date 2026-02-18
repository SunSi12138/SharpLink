namespace SharpLink.Runtime;

public class StreamManager : IStreamManager
{
    private readonly StripedLongMap<RequestDispatchers> _dispatchersByRequestId = new();

    public void Register(long requestId, IStreamDispatcher dispatcher) => Register(requestId, 0, dispatcher);

    public void Register(long requestId, sbyte streamId, IStreamDispatcher dispatcher)
    {
        var requestDispatchers = _dispatchersByRequestId.GetOrAdd(requestId, static _ => new RequestDispatchers());
        requestDispatchers.Register(streamId, dispatcher);
    }

    public void Unregister(long requestId) => Unregister(requestId, 0);

    public void Unregister(long requestId, sbyte streamId)
    {
        if (!_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
            return;

        requestDispatchers.Unregister(streamId);
    }

    public ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload)
        => DispatchChunkAsync(requestId, 0, payload);

    public ValueTask DispatchChunkAsync(long requestId, sbyte streamId, ReadOnlySequence<byte> payload)
    {
        if (_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) &&
            requestDispatchers.TryGet(streamId, out var dispatcher))
        {
            return dispatcher.DispatchAsync(payload);
        }

        return ValueTask.CompletedTask;
    }

    public void CompleteStream(long requestId, bool isError, string? msg)
    {
        CompleteStream(requestId, 0, isError, msg);
    }

    public void CompleteStream(long requestId, sbyte streamId, bool isError, string? msg)
    {
        if (!_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
            return;

        if (requestDispatchers.TryRemove(streamId, out var dispatcher))
            dispatcher.Complete(isError, msg);
    }

    public void CompleteAll(bool isError, string? msg)
    {
        foreach (var requestDispatchers in _dispatchersByRequestId.DrainValues())
        {
            requestDispatchers.CompleteAll(isError, msg);
        }
    }

    private sealed class RequestDispatchers
    {
        private IStreamDispatcher? _defaultDispatcher;
        private readonly Lock _gate = new();
        private readonly Dictionary<sbyte, IStreamDispatcher> _byStreamId = [];

        public void Register(sbyte streamId, IStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                _defaultDispatcher = dispatcher;
                return;
            }

            lock (_gate)
                _byStreamId[streamId] = dispatcher;
        }

        public void Unregister(sbyte streamId)
        {
            if (streamId == 0)
            {
                Interlocked.Exchange(ref _defaultDispatcher, null);
                return;
            }

            lock (_gate)
                _byStreamId.Remove(streamId);
        }

        public bool TryGet(sbyte streamId, out IStreamDispatcher dispatcher)
        {
            if (streamId != 0)
            {
                lock (_gate)
                    return _byStreamId.TryGetValue(streamId, out dispatcher!);
            }
            
            var defaultDispatcher = Volatile.Read(ref _defaultDispatcher);
            
            if (defaultDispatcher is not null)
            {
                dispatcher = defaultDispatcher;
                return true;
            }

            dispatcher = null!;
            return false;

        }

        public bool TryRemove(sbyte streamId, out IStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var removed = Interlocked.Exchange(ref _defaultDispatcher, null);
                if (removed is null)
                {
                    dispatcher = default!;
                    return false;
                }

                dispatcher = removed;
                return true;
            }

            lock (_gate)
            {
                if (_byStreamId.TryGetValue(streamId, out dispatcher!))
                    return _byStreamId.Remove(streamId);
                return false;
            }
        }

        public void CompleteAll(bool isError, string? msg)
        {
            var defaultDispatcher = Interlocked.Exchange(ref _defaultDispatcher, null);
            defaultDispatcher?.Complete(isError, msg);

            lock (_gate)
            {
                foreach (var dispatcher in _byStreamId.Values)
                    dispatcher.Complete(isError, msg);
                _byStreamId.Clear();
            }
        }
    }
}
