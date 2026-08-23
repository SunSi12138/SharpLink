namespace SharpLink.Runtime;

/// <summary>
/// Temporarily owns client-stream frames while the request is waiting for server admission.
/// It remains the StreamManager entry after the generated dispatcher attaches so ordering and
/// completion are preserved without a second request-level stream registry.
/// </summary>
internal sealed class PreAdmissionStreamDispatcher(
    SharpLinkBufferWriterPool buffers,
    Func<int, IDisposable?> reserveBytes,
    Action capacityExceeded,
    Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null)
    : IStreamConsumptionAwareDispatcher, IStreamDispatchLease
{
    internal PreAdmissionStreamDispatcher(
        SharpLinkBufferWriterPool buffers,
        Func<int, bool> reserveBytes,
        Action<int> releaseBytes,
        Action capacityExceeded,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null)
        : this(
            buffers,
            retainedBytes => reserveBytes(retainedBytes)
                ? new CallbackByteLease(releaseBytes, retainedBytes)
                : null,
            capacityExceeded,
            decodeCompressed)
    {
    }

    private readonly Lock _gate = new();
    private readonly Queue<BufferedItem> _items = [];
    private IStreamDispatcher? _dispatcher;
    private IStreamDispatcher? _attachingDispatcher;
    private Action<long, ushort, int>? _bytesConsumed;
    private long _requestId;
    private ushort _streamId;
    private Exception? _completion;
    private bool _completed;
    private IStreamDispatchState? _dispatchState;
    private IStreamDispatchLease? _failedDispatchLease;
    private TaskCompletionSource? _attachmentBarrier;
    private int _configurationVersion;
    private bool _drainRequested;
    private bool _drainForwarded;

    internal bool IsAttached
    {
        get
        {
            lock (_gate)
                return _dispatcher is not null || _attachingDispatcher is not null;
        }
    }

    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        => DispatchAsync(payload, Math.Max(1, checked((int)payload.Length)));

    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
    {
        IStreamDispatcher? attached;
        lock (_gate)
            attached = _dispatcher;
        if (attached is not null)
            return DispatchAttached(attached, payload, encodedByteCount);

        var retainedBytes = Math.Max(1, checked((int)payload.Length));
        var byteLease = reserveBytes(retainedBytes);
        if (byteLease is null)
        {
            _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
            capacityExceeded();
            return ValueTask.CompletedTask;
        }

        IRpcByteBufferWriter owner;
        try
        {
            owner = buffers.Rent(retainedBytes);
            foreach (var segment in payload)
                owner.Write(segment.Span);
        }
        catch
        {
            byteLease.Dispose();
            throw;
        }

        lock (_gate)
        {
            if (_dispatcher is null && !_completed)
            {
                _items.Enqueue(new BufferedItem(owner, byteLease, encodedByteCount));
                return ValueTask.CompletedTask;
            }
            attached = _dispatcher;
        }

        ReleaseRetainedBuffer(owner, byteLease);
        if (attached is null)
        {
            _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
            return ValueTask.CompletedTask;
        }
        return DispatchAttached(attached, payload, encodedByteCount);
    }

    internal ValueTask DispatchCompressedAsync(
        ReadOnlySequence<byte> wirePayload,
        int originalByteCount)
    {
        var decoder = decodeCompressed ?? throw new InvalidOperationException(
            "The pre-admission stream has no compressed-frame decoder.");
        IStreamDispatcher? attached;
        lock (_gate)
            attached = _dispatcher;
        if (attached is not null)
        {
            return attached is DiscardingStreamDispatcher
                ? DispatchAttached(attached, wirePayload, originalByteCount)
                : DecodeAndDispatch(attached, wirePayload, originalByteCount, decoder);
        }

        var retainedBytes = Math.Max(1, checked((int)wirePayload.Length));
        var byteLease = reserveBytes(retainedBytes);
        if (byteLease is null)
        {
            _bytesConsumed?.Invoke(_requestId, _streamId, originalByteCount);
            capacityExceeded();
            return ValueTask.CompletedTask;
        }

        IRpcByteBufferWriter owner;
        try
        {
            owner = buffers.Rent(retainedBytes);
            foreach (var segment in wirePayload)
                owner.Write(segment.Span);
        }
        catch
        {
            byteLease.Dispose();
            throw;
        }

        lock (_gate)
        {
            if (_dispatcher is null && !_completed)
            {
                _items.Enqueue(new BufferedItem(
                    owner,
                    byteLease,
                    originalByteCount,
                    IsCompressed: true));
                return ValueTask.CompletedTask;
            }
            attached = _dispatcher;
        }

        ValueTask dispatch;
        try
        {
            if (attached is null)
            {
                _bytesConsumed?.Invoke(_requestId, _streamId, originalByteCount);
                ReleaseRetainedBuffer(owner, byteLease);
                return ValueTask.CompletedTask;
            }
            dispatch = attached is DiscardingStreamDispatcher
                ? DispatchAttached(
                    attached,
                    new ReadOnlySequence<byte>(owner.WrittenMemory),
                    originalByteCount)
                : DecodeAndDispatch(
                    attached,
                    new ReadOnlySequence<byte>(owner.WrittenMemory),
                    originalByteCount,
                    decoder);
        }
        catch
        {
            ReleaseRetainedBuffer(owner, byteLease);
            throw;
        }
        if (dispatch.IsCompletedSuccessfully)
        {
            ReleaseRetainedBuffer(owner, byteLease);
            return ValueTask.CompletedTask;
        }
        return AwaitRetainedCompressedDispatchAsync(
            dispatch, owner, byteLease);
    }

    internal bool TryBeginAttach(IStreamDispatcher dispatcher, out bool alreadyCompleted)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        lock (_gate)
        {
            if (_dispatcher is not null || _attachingDispatcher is not null)
            {
                alreadyCompleted = false;
                return false;
            }
            _attachingDispatcher = dispatcher;
            _attachmentBarrier = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            alreadyCompleted = _completed;
            return true;
        }
    }

    internal void FinishAttach(IStreamDispatcher dispatcher)
    {
        TaskCompletionSource barrier;
        lock (_gate)
        {
            if (!ReferenceEquals(_attachingDispatcher, dispatcher) || _attachmentBarrier is null)
                throw new InvalidOperationException("The generated stream dispatcher was not claimed for attachment.");
            barrier = _attachmentBarrier;
        }

        try
        {
            ConfigureAttachingDispatcher(dispatcher);
        }
        catch (Exception exception)
        {
            FailAttachment(dispatcher, barrier, exception);
            throw;
        }

        var replay = ReplayBufferedItemsAsync(dispatcher, barrier);
        if (replay.IsCompleted)
            replay.GetAwaiter().GetResult();
        else
            _ = replay.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public void Complete(bool isError, string? errorMessage)
        => Complete(isError
            ? new SharpLinkException(
                SharpLinkErrorCode.RemoteError,
                string.IsNullOrWhiteSpace(errorMessage) ? "Remote Error" : errorMessage)
            : null);

    public void Complete(Exception? exception)
    {
        IStreamDispatcher? attached;
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            _completion = exception;
            attached = _dispatcher;
        }
        attached?.Complete(exception);
    }

    public void SetBytesConsumedCallback(
        Action<long, ushort, int>? callback,
        long requestId,
        ushort streamId)
    {
        IStreamConsumptionAwareDispatcher? consumptionAware;
        lock (_gate)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
            _configurationVersion++;
            consumptionAware = (_dispatcher ?? _attachingDispatcher) as
                IStreamConsumptionAwareDispatcher;
        }
        consumptionAware?.SetBytesConsumedCallback(callback, requestId, streamId);
    }

    ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
        ReadOnlySequence<byte> payload,
        int encodedByteCount)
        => DispatchAsync(payload, encodedByteCount);

    void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
    {
        IStreamDispatchLease? dispatchLease;
        lock (_gate)
        {
            _dispatchState = state;
            _configurationVersion++;
            dispatchLease = (_dispatcher ?? _attachingDispatcher) as IStreamDispatchLease;
        }
        dispatchLease?.BindDispatchState(state);
    }

    void IStreamDispatchLease.OnDispatchesDrained()
    {
        lock (_gate)
            _drainRequested = true;
        TryForwardDrain();
    }

    private async Task ReplayBufferedItemsAsync(
        IStreamDispatcher dispatcher,
        TaskCompletionSource barrier)
    {
        var completionStarted = false;
        try
        {
            while (true)
            {
                BufferedItem item;
                bool completed;
                lock (_gate)
                {
                    if (!_items.TryDequeue(out item))
                    {
                        if (!ReferenceEquals(_attachingDispatcher, dispatcher))
                            throw new InvalidOperationException(
                                "The generated stream dispatcher lost its attachment claim during replay.");
                        _attachingDispatcher = null;
                        _dispatcher = dispatcher;
                        completed = _completed;
                        if (!completed)
                            barrier.TrySetResult();
                        else
                            break;
                        return;
                    }
                }
                try
                {
                    var bufferedPayload = new ReadOnlySequence<byte>(item.Owner.WrittenMemory);
                    var dispatch = item.IsCompressed && dispatcher is not DiscardingStreamDispatcher
                        ? DecodeAndDispatch(
                            dispatcher,
                            bufferedPayload,
                            item.EncodedByteCount,
                            decodeCompressed ?? throw new InvalidOperationException(
                                "The pre-admission stream has no compressed-frame decoder."))
                        : DispatchAttached(dispatcher, bufferedPayload, item.EncodedByteCount);
                    await dispatch.ConfigureAwait(false);
                }
                finally
                {
                    ReleaseRetainedBuffer(item.Owner, item.ByteLease);
                }
            }

            completionStarted = true;
            dispatcher.Complete(_completion);
            barrier.TrySetResult();
        }
        catch (Exception exception)
        {
            FailAttachment(
                dispatcher,
                barrier,
                exception,
                completeDispatcher: !completionStarted);
            throw;
        }
        finally
        {
            TryForwardDrain();
        }
    }

    private void FailAttachment(
        IStreamDispatcher dispatcher,
        TaskCompletionSource barrier,
        Exception exception,
        bool completeDispatcher = true)
    {
        BufferedItem[] remaining;
        lock (_gate)
        {
            remaining = [.. _items];
            _items.Clear();
            _failedDispatchLease = dispatcher as IStreamDispatchLease;
            if (ReferenceEquals(_attachingDispatcher, dispatcher))
                _attachingDispatcher = null;
            if (ReferenceEquals(_dispatcher, dispatcher))
                _dispatcher = null;
        }
        ReleaseBufferedItems(remaining);
        if (!completeDispatcher)
        {
            barrier.TrySetException(exception);
            return;
        }
        try
        {
            dispatcher.Complete(exception);
        }
        finally
        {
            barrier.TrySetException(exception);
        }
    }

    private void TryForwardDrain()
    {
        IStreamDispatchLease? dispatchLease = null;
        BufferedItem[] bufferedItems = [];
        lock (_gate)
        {
            if (!_drainRequested || _drainForwarded ||
                _attachmentBarrier?.Task.IsCompleted == false)
            {
                return;
            }
            _drainForwarded = true;
            dispatchLease = _failedDispatchLease ??
                (_dispatcher ?? _attachingDispatcher) as IStreamDispatchLease;
            _failedDispatchLease = null;
            if (dispatchLease is null)
            {
                bufferedItems = [.. _items];
                _items.Clear();
            }
        }
        if (dispatchLease is not null)
            dispatchLease.OnDispatchesDrained();
        else
            ReleaseBufferedItems(bufferedItems);
    }

    private void ConfigureAttachingDispatcher(IStreamDispatcher dispatcher)
    {
        while (true)
        {
            Action<long, ushort, int>? bytesConsumed;
            long requestId;
            ushort streamId;
            IStreamDispatchState? dispatchState;
            int version;
            lock (_gate)
            {
                if (!ReferenceEquals(_attachingDispatcher, dispatcher))
                    return;
                bytesConsumed = _bytesConsumed;
                requestId = _requestId;
                streamId = _streamId;
                dispatchState = _dispatchState;
                version = _configurationVersion;
            }

            if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(bytesConsumed, requestId, streamId);
            if (dispatcher is IStreamDispatchLease dispatchLease && dispatchState is not null)
                dispatchLease.BindDispatchState(dispatchState);

            lock (_gate)
            {
                if (version == _configurationVersion)
                    return;
            }
        }
    }

    private async ValueTask AwaitRetainedCompressedDispatchAsync(
        ValueTask dispatch,
        IRpcByteBufferWriter owner,
        IDisposable byteLease)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            ReleaseRetainedBuffer(owner, byteLease);
        }
    }

    private static ValueTask DispatchAttached(
        IStreamDispatcher dispatcher,
        ReadOnlySequence<byte> payload,
        int encodedByteCount)
        => dispatcher is IStreamConsumptionAwareDispatcher consumptionAware
            ? consumptionAware.DispatchAsync(payload, encodedByteCount)
            : dispatcher.DispatchAsync(payload);

    private static ValueTask DecodeAndDispatch(
        IStreamDispatcher dispatcher,
        ReadOnlySequence<byte> payload,
        int encodedByteCount,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload> decoder)
    {
        var decoded = decoder(payload);
        try
        {
            var dispatch = DispatchAttached(dispatcher, decoded.Payload, encodedByteCount);
            if (dispatch.IsCompletedSuccessfully)
            {
                decoded.Dispose();
                return ValueTask.CompletedTask;
            }
            return AwaitDecodedDispatchAsync(dispatch, decoded);
        }
        catch
        {
            decoded.Dispose();
            throw;
        }
    }

    private static async ValueTask AwaitDecodedDispatchAsync(
        ValueTask dispatch,
        PreAdmissionDecodedPayload decoded)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            decoded.Dispose();
        }
    }

    private void ReleaseBufferedItems(IEnumerable<BufferedItem> items)
    {
        foreach (var item in items)
        {
            ReleaseRetainedBuffer(item.Owner, item.ByteLease);
            _bytesConsumed?.Invoke(_requestId, _streamId, item.EncodedByteCount);
        }
    }

    private void ReleaseRetainedBuffer(IRpcByteBufferWriter owner, IDisposable byteLease)
    {
        try
        {
            buffers.Return(owner);
        }
        finally
        {
            byteLease.Dispose();
        }
    }

    private readonly record struct BufferedItem(
        IRpcByteBufferWriter Owner,
        IDisposable ByteLease,
        int EncodedByteCount,
        bool IsCompressed = false);

    private sealed class CallbackByteLease(Action<int> releaseBytes, int retainedBytes) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            releaseBytes(retainedBytes);
        }
    }
}

internal readonly record struct PreAdmissionDecodedPayload(
    ReadOnlySequence<byte> Payload,
    IRpcByteBufferWriter Owner,
    SharpLinkBufferWriterPool Pool) : IDisposable
{
    public void Dispose() => Pool.Return(Owner);
}
