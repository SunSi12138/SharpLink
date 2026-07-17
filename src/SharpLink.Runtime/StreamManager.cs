namespace SharpLink.Runtime;

public class StreamManager : IStreamManager
{
    private readonly StripedLongMap<RequestDispatchers> _dispatchersByRequestId;
    private readonly Action<long, ushort, int>? _acceptBytes;
    private readonly Action<long, ushort, int>? _bytesConsumed;
    private readonly Action<long, ushort>? _streamCompleted;
    private long _droppedStreamFrames;
    private int _activeStreamCount;

    public StreamManager() : this(new RuntimeConcurrencyOptions())
    {
    }

    public StreamManager(RuntimeConcurrencyOptions concurrencyOptions)
        : this(concurrencyOptions, null, null, null)
    {
    }

    internal StreamManager(
        RuntimeConcurrencyOptions concurrencyOptions,
        Action<long, ushort, int>? acceptBytes,
        Action<long, ushort, int>? bytesConsumed,
        Action<long, ushort>? streamCompleted)
    {
        _dispatchersByRequestId = new StripedLongMap<RequestDispatchers>(concurrencyOptions);
        _acceptBytes = acceptBytes;
        _bytesConsumed = bytesConsumed;
        _streamCompleted = streamCompleted;
    }

    public void Register(long requestId, IStreamDispatcher dispatcher) => Register(requestId, 0, dispatcher);

    public void Register(long requestId, ushort streamId, IStreamDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var requestDispatchers = _dispatchersByRequestId.GetOrAdd(requestId, static _ => new RequestDispatchers());
        requestDispatchers.Register(streamId, dispatcher);
        SharpLinkTelemetry.AddActiveStreams(1);
        Interlocked.Increment(ref _activeStreamCount);
        if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
            consumptionAware.SetBytesConsumedCallback(_bytesConsumed, requestId, streamId);
    }

    public void Unregister(long requestId) => Unregister(requestId, 0);

    public void Unregister(long requestId, ushort streamId)
    {
        if (!_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
            return;

        if (requestDispatchers.TryRemove(streamId, out var dispatcher))
        {
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(null, 0, 0);
            _streamCompleted?.Invoke(requestId, streamId);
            RemoveEmptyRequest(requestId, requestDispatchers);
        }
    }

    public ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload)
        => DispatchChunkAsync(requestId, 0, payload);

    public ValueTask DispatchChunkAsync(long requestId, ushort streamId, ReadOnlySequence<byte> payload)
    {
        if (_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) &&
            requestDispatchers.TryGet(streamId, out var dispatcher))
        {
            if (_acceptBytes is not null && dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
            {
                var encodedByteCount = Math.Max(1, checked((int)payload.Length));
                _acceptBytes(requestId, streamId, encodedByteCount);
                return consumptionAware.DispatchAsync(payload, encodedByteCount);
            }
            return dispatcher.DispatchAsync(payload);
        }

        Interlocked.Increment(ref _droppedStreamFrames);
        return ValueTask.CompletedTask;
    }

    public void CompleteStream(long requestId, bool isError, string? msg)
    {
        CompleteStream(requestId, 0, CreateCompletionException(isError, msg));
    }

    public void CompleteStream(long requestId, ushort streamId, bool isError, string? msg)
    {
        CompleteStream(requestId, streamId, CreateCompletionException(isError, msg));
    }

    public void CompleteAll(bool isError, string? msg)
    {
        CompleteAll(CreateCompletionException(isError, msg));
    }

    public void CompleteStream(long requestId, Exception? exception)
    {
        CompleteStream(requestId, 0, exception);
    }

    public void CompleteStream(long requestId, ushort streamId, Exception? exception)
    {
        if (!_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
            return;

        if (requestDispatchers.TryRemove(streamId, out var dispatcher))
        {
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            dispatcher.Complete(exception);
            _streamCompleted?.Invoke(requestId, streamId);
            RemoveEmptyRequest(requestId, requestDispatchers);
        }
    }

    public void CompleteAll(Exception? exception)
    {
        foreach (var requestDispatchers in _dispatchersByRequestId.DrainValues())
        {
            var completed = requestDispatchers.CompleteAll(exception);
            SharpLinkTelemetry.AddActiveStreams(-completed);
            Interlocked.Add(ref _activeStreamCount, -completed);
        }
    }

    internal long DroppedStreamFrames => Volatile.Read(ref _droppedStreamFrames);
    internal int ActiveStreamCount => Volatile.Read(ref _activeStreamCount);

    private void RemoveEmptyRequest(long requestId, RequestDispatchers requestDispatchers)
    {
        if (requestDispatchers.IsEmpty)
            _dispatchersByRequestId.TryRemove(requestId, requestDispatchers);
    }

    private static Exception? CreateCompletionException(bool isError, string? msg)
    {
        if (!isError)
            return null;

        var message = string.IsNullOrWhiteSpace(msg) ? "Remote Error" : msg;
        return new SharpLinkException(SharpLinkErrorCode.RemoteError, message);
    }

    private sealed class RequestDispatchers
    {
        private IStreamDispatcher? _defaultDispatcher;
        private readonly Lock _gate = new();
        private readonly Dictionary<ushort, IStreamDispatcher> _byStreamId = [];

        public void Register(ushort streamId, IStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                if (Interlocked.CompareExchange(ref _defaultDispatcher, dispatcher, null) is not null)
                    throw new InvalidOperationException("The default stream is already registered.");
                return;
            }

            lock (_gate)
                _byStreamId.Add(streamId, dispatcher);
        }

        public void Unregister(ushort streamId)
        {
            if (streamId == 0)
            {
                Interlocked.Exchange(ref _defaultDispatcher, null);
                return;
            }

            lock (_gate)
                _byStreamId.Remove(streamId);
        }

        public bool TryGet(ushort streamId, out IStreamDispatcher dispatcher)
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

        public bool TryRemove(ushort streamId, out IStreamDispatcher dispatcher)
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

        public int CompleteAll(Exception? exception)
        {
            var defaultDispatcher = Interlocked.Exchange(ref _defaultDispatcher, null);
            defaultDispatcher?.Complete(exception);
            var count = defaultDispatcher is null ? 0 : 1;

            lock (_gate)
            {
                count += _byStreamId.Count;
                foreach (var dispatcher in _byStreamId.Values)
                    dispatcher.Complete(exception);
                _byStreamId.Clear();
            }
            return count;
        }

        public bool IsEmpty
        {
            get
            {
                if (Volatile.Read(ref _defaultDispatcher) is not null)
                    return false;
                lock (_gate)
                    return _defaultDispatcher is null && _byStreamId.Count == 0;
            }
        }
    }
}
