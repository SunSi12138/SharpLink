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

        if (requestDispatchers.TryRemove(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                    consumptionAware.SetBytesConsumedCallback(null, 0, 0);
                _streamCompleted?.Invoke(requestId, streamId);
            }
            finally
            {
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
            }
        }
    }

    public ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload)
        => DispatchChunkAsync(requestId, 0, payload);

    public ValueTask DispatchChunkAsync(long requestId, ushort streamId, ReadOnlySequence<byte> payload)
    {
        if (_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) &&
            requestDispatchers.TryAcquire(streamId, out var entry))
        {
            try
            {
                var dispatcher = entry.Dispatcher;
                var encodedByteCount = Math.Max(1, checked((int)payload.Length));
                if (_acceptBytes is not null && dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                {
                    _acceptBytes(requestId, streamId, encodedByteCount);
                    return CompleteDispatch(
                        entry,
                        dispatcher is IStreamDispatchLease leased
                            ? leased.DispatchAcquiredAsync(payload, encodedByteCount)
                            : consumptionAware.DispatchAsync(payload, encodedByteCount));
                }

                return CompleteDispatch(
                    entry,
                    dispatcher is IStreamDispatchLease dispatchLease
                        ? dispatchLease.DispatchAcquiredAsync(payload, encodedByteCount)
                        : dispatcher.DispatchAsync(payload));
            }
            catch
            {
                entry.Release();
                throw;
            }
        }

        Interlocked.Increment(ref _droppedStreamFrames);
        return ValueTask.CompletedTask;
    }

    private static ValueTask CompleteDispatch(DispatcherEntry entry, ValueTask dispatch)
    {
        if (dispatch.IsCompletedSuccessfully)
        {
            entry.Release();
            return ValueTask.CompletedTask;
        }
        return AwaitDispatchAsync(entry, dispatch);
    }

    private static async ValueTask AwaitDispatchAsync(DispatcherEntry entry, ValueTask dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            entry.Release();
        }
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

        if (requestDispatchers.TryRemove(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                dispatcher.Complete(exception);
                _streamCompleted?.Invoke(requestId, streamId);
            }
            finally
            {
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
            }
        }
    }

    /// <summary>
    /// Closes a locally terminated receive stream and waits for dispatches that acquired the
    /// entry before it was closed. The final receive-credit flush therefore precedes a caller's
    /// Cancel frame without blocking the normal no-dispatch completion path.
    /// </summary>
    internal ValueTask CompleteStreamAfterDispatchesAsync(
        long requestId,
        ushort streamId,
        Exception? exception)
    {
        if (!_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) ||
            !requestDispatchers.TryRemove(streamId, out var entry))
        {
            return ValueTask.CompletedTask;
        }

        SharpLinkTelemetry.AddActiveStreams(-1);
        Interlocked.Decrement(ref _activeStreamCount);
        try
        {
            entry.Dispatcher.Complete(exception);
        }
        catch
        {
            entry.Detach();
            RemoveEmptyRequest(requestId, requestDispatchers);
            throw;
        }
        if (!entry.HasActiveDispatches)
        {
            FinalizeLocallyTerminatedStream(requestId, streamId, requestDispatchers, entry);
            return ValueTask.CompletedTask;
        }

        return AwaitDispatchesAndFinalizeAsync(
            requestId,
            streamId,
            requestDispatchers,
            entry);
    }

    private async ValueTask AwaitDispatchesAndFinalizeAsync(
        long requestId,
        ushort streamId,
        RequestDispatchers requestDispatchers,
        DispatcherEntry entry)
    {
        await entry.WaitForDispatchesAsync().ConfigureAwait(false);
        FinalizeLocallyTerminatedStream(requestId, streamId, requestDispatchers, entry);
    }

    private void FinalizeLocallyTerminatedStream(
        long requestId,
        ushort streamId,
        RequestDispatchers requestDispatchers,
        DispatcherEntry entry)
    {
        try
        {
            if (entry.Dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(null, 0, 0);
            _streamCompleted?.Invoke(requestId, streamId);
        }
        finally
        {
            entry.Detach();
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
        private DispatcherEntry? _defaultDispatcher;
        private readonly Lock _gate = new();
        private readonly Dictionary<ushort, DispatcherEntry> _byStreamId = [];

        public void Register(ushort streamId, IStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var entry = new DispatcherEntry(dispatcher);
                if (Interlocked.CompareExchange(ref _defaultDispatcher, entry, null) is not null)
                    throw new InvalidOperationException("The default stream is already registered.");
                return;
            }

            lock (_gate)
                _byStreamId.Add(streamId, new DispatcherEntry(dispatcher));
        }

        public bool TryAcquire(ushort streamId, out DispatcherEntry entry)
        {
            if (streamId != 0)
            {
                lock (_gate)
                {
                    if (_byStreamId.TryGetValue(streamId, out entry!) && entry.TryAcquire())
                        return true;
                    entry = null!;
                    return false;
                }
            }

            var defaultEntry = Volatile.Read(ref _defaultDispatcher);
            if (defaultEntry is not null && defaultEntry.TryAcquire())
            {
                entry = defaultEntry;
                return true;
            }

            entry = null!;
            return false;
        }

        public bool TryRemove(ushort streamId, out DispatcherEntry entry)
        {
            if (streamId == 0)
            {
                var removed = Interlocked.Exchange(ref _defaultDispatcher, null);
                if (removed is null)
                {
                    entry = default!;
                    return false;
                }

                removed.Close();
                entry = removed;
                return true;
            }

            lock (_gate)
            {
                if (_byStreamId.Remove(streamId, out var removed))
                {
                    removed.Close();
                    entry = removed;
                    return true;
                }
                entry = default!;
                return false;
            }
        }

        public int CompleteAll(Exception? exception)
        {
            var defaultDispatcher = Interlocked.Exchange(ref _defaultDispatcher, null);
            if (defaultDispatcher is not null)
            {
                defaultDispatcher.Close();
                try
                {
                    defaultDispatcher.Dispatcher.Complete(exception);
                }
                finally
                {
                    defaultDispatcher.Detach();
                }
            }
            var count = defaultDispatcher is null ? 0 : 1;

            lock (_gate)
            {
                count += _byStreamId.Count;
                foreach (var entry in _byStreamId.Values)
                {
                    entry.Close();
                    try
                    {
                        entry.Dispatcher.Complete(exception);
                    }
                    finally
                    {
                        entry.Detach();
                    }
                }
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

    private sealed class DispatcherEntry : IStreamDispatchState
    {
        private const int ClosedMask = int.MinValue;
        private const int CountMask = int.MaxValue;
        private int _state;
        private TaskCompletionSource? _dispatchesDrained;

        internal DispatcherEntry(IStreamDispatcher dispatcher)
        {
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (dispatcher is IStreamDispatchLease lease)
                lease.BindDispatchState(this);
        }

        internal IStreamDispatcher Dispatcher { get; }

        public bool HasActiveDispatches => (Volatile.Read(ref _state) & CountMask) != 0;

        public bool IsDetached => Volatile.Read(ref _detached) != 0;

        private int _detached;

        internal bool TryAcquire()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if ((state & ClosedMask) != 0 || (state & CountMask) == CountMask)
                    return false;
                if (Interlocked.CompareExchange(ref _state, state + 1, state) != state)
                    continue;

                return true;
            }
        }

        internal void Release()
        {
            var state = Interlocked.Decrement(ref _state);
            if ((state & CountMask) == CountMask)
                throw new InvalidOperationException("Stream dispatcher lease underflowed.");
            if ((state & ClosedMask) != 0 && (state & CountMask) == 0)
            {
                Volatile.Read(ref _dispatchesDrained)?.TrySetResult();
                if (IsDetached && Dispatcher is IStreamDispatchLease lease)
                    lease.OnDispatchesDrained();
            }
        }

        internal ValueTask WaitForDispatchesAsync()
        {
            if (!HasActiveDispatches)
                return ValueTask.CompletedTask;

            var completion = Volatile.Read(ref _dispatchesDrained);
            if (completion is null)
            {
                var created = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                completion = Interlocked.CompareExchange(
                    ref _dispatchesDrained,
                    created,
                    null) ?? created;
            }

            if (!HasActiveDispatches)
                completion.TrySetResult();
            return new ValueTask(completion.Task);
        }

        public void Close()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if ((state & ClosedMask) != 0)
                    return;
                if (Interlocked.CompareExchange(ref _state, state | ClosedMask, state) == state)
                    return;
            }
        }

        internal void Detach()
        {
            Close();
            if (Interlocked.Exchange(ref _detached, 1) != 0)
                return;
            if (!HasActiveDispatches && Dispatcher is IStreamDispatchLease lease)
                lease.OnDispatchesDrained();
        }
    }
}
