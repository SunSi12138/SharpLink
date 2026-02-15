using System.Collections.Generic;

namespace SharpLink.Runtime;

public class StreamManager : IStreamManager
{
    private readonly ConcurrentDictionary<long, RequestDispatchers> _dispatchersByRequestId = new();

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
        TryCleanupRequestDispatchers(requestId, requestDispatchers);
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

        TryCleanupRequestDispatchers(requestId, requestDispatchers);
    }

    public void CompleteAll(bool isError, string? msg)
    {
        foreach (var (requestId, _) in _dispatchersByRequestId)
        {
            if (_dispatchersByRequestId.TryRemove(requestId, out var requestDispatchers))
                requestDispatchers.CompleteAll(isError, msg);
        }
    }

    private void TryCleanupRequestDispatchers(long requestId, RequestDispatchers requestDispatchers)
    {
        if (!requestDispatchers.IsEmpty)
            return;

        ((ICollection<KeyValuePair<long, RequestDispatchers>>)_dispatchersByRequestId)
            .Remove(new KeyValuePair<long, RequestDispatchers>(requestId, requestDispatchers));
    }

    private sealed class RequestDispatchers
    {
        private IStreamDispatcher? _defaultDispatcher;
        private readonly ConcurrentDictionary<sbyte, IStreamDispatcher> _byStreamId = new();

        public bool IsEmpty => Volatile.Read(ref _defaultDispatcher) is null && _byStreamId.IsEmpty;

        public void Register(sbyte streamId, IStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                _defaultDispatcher = dispatcher;
                return;
            }

            _byStreamId[streamId] = dispatcher;
        }

        public void Unregister(sbyte streamId)
        {
            if (streamId == 0)
            {
                Interlocked.Exchange(ref _defaultDispatcher, null);
                return;
            }

            _byStreamId.TryRemove(streamId, out _);
        }

        public bool TryGet(sbyte streamId, out IStreamDispatcher dispatcher)
        {
            if (streamId != 0) return _byStreamId.TryGetValue(streamId, out dispatcher!);
            
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

            return _byStreamId.TryRemove(streamId, out dispatcher!);
        }

        public void CompleteAll(bool isError, string? msg)
        {
            var defaultDispatcher = Interlocked.Exchange(ref _defaultDispatcher, null);
            defaultDispatcher?.Complete(isError, msg);

            foreach (var streamId in _byStreamId.Keys)
            {
                if (_byStreamId.TryRemove(streamId, out var dispatcher))
                    dispatcher.Complete(isError, msg);
            }
        }
    }
}
