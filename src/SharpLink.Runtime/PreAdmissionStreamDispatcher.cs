namespace SharpLink.Runtime;

/// <summary>
/// Temporarily owns client-stream frames while the request is waiting for server admission.
/// It remains the StreamManager entry after the generated dispatcher attaches so ordering and
/// completion are preserved without a second request-level stream registry.
/// </summary>
internal sealed class PreAdmissionStreamDispatcher(
    SharpLinkBufferWriterPool buffers,
    Func<int, bool> reserveBytes,
    Action<int> releaseBytes,
    Action capacityExceeded,
    Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null)
    : IStreamConsumptionAwareDispatcher, IStreamDispatchLease
{
    private readonly Lock _gate = new();
    private readonly Queue<BufferedItem> _items = [];
    private IStreamDispatcher? _dispatcher;
    private Action<long, ushort, int>? _bytesConsumed;
    private long _requestId;
    private ushort _streamId;
    private Exception? _completion;
    private bool _completed;
    private IStreamDispatchState? _dispatchState;

    internal bool IsAttached
    {
        get
        {
            lock (_gate)
                return _dispatcher is not null;
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

        var retainedBytes = checked((int)payload.Length);
        if (retainedBytes == 0)
            retainedBytes = 1;
        if (!reserveBytes(retainedBytes))
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
            releaseBytes(retainedBytes);
            throw;
        }

        lock (_gate)
        {
            if (_dispatcher is null && !_completed)
            {
                _items.Enqueue(new BufferedItem(owner, retainedBytes, encodedByteCount));
                return ValueTask.CompletedTask;
            }
            attached = _dispatcher;
        }

        buffers.Return(owner);
        releaseBytes(retainedBytes);
        return attached is null
            ? ValueTask.CompletedTask
            : DispatchAttached(attached, payload, encodedByteCount);
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
            return DecodeAndDispatch(attached, wirePayload, originalByteCount, decoder);

        var retainedBytes = checked((int)wirePayload.Length);
        if (!reserveBytes(retainedBytes))
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
            releaseBytes(retainedBytes);
            throw;
        }

        lock (_gate)
        {
            if (_dispatcher is null && !_completed)
            {
                _items.Enqueue(new BufferedItem(
                    owner,
                    retainedBytes,
                    originalByteCount,
                    IsCompressed: true));
                return ValueTask.CompletedTask;
            }
            attached = _dispatcher;
        }

        try
        {
            return attached is null
                ? ValueTask.CompletedTask
                : DecodeAndDispatch(
                    attached,
                    new ReadOnlySequence<byte>(owner.WrittenMemory),
                    originalByteCount,
                    decoder);
        }
        finally
        {
            buffers.Return(owner);
            releaseBytes(retainedBytes);
        }
    }

    internal bool Attach(IStreamDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        lock (_gate)
        {
            if (_dispatcher is not null)
                throw new InvalidOperationException("The generated stream dispatcher is already attached.");
            _dispatcher = dispatcher;
            if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(_bytesConsumed, _requestId, _streamId);
            if (dispatcher is IStreamDispatchLease dispatchLease && _dispatchState is not null)
                dispatchLease.BindDispatchState(_dispatchState);

            while (_items.TryDequeue(out var item))
            {
                try
                {
                    var bufferedPayload = new ReadOnlySequence<byte>(item.Owner.WrittenMemory);
                    var dispatch = item.IsCompressed
                        ? DecodeAndDispatch(
                            dispatcher,
                            bufferedPayload,
                            item.EncodedByteCount,
                            decodeCompressed ?? throw new InvalidOperationException(
                                "The pre-admission stream has no compressed-frame decoder."))
                        : DispatchAttached(dispatcher, bufferedPayload, item.EncodedByteCount);
                    if (!dispatch.IsCompletedSuccessfully)
                        dispatch.AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    buffers.Return(item.Owner);
                    releaseBytes(item.RetainedBytes);
                }
            }
            if (_completed)
                dispatcher.Complete(_completion);
            return _completed;
        }
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
        lock (_gate)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
            if (_dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(callback, requestId, streamId);
        }
    }

    ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
        ReadOnlySequence<byte> payload,
        int encodedByteCount)
        => DispatchAsync(payload, encodedByteCount);

    void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
    {
        lock (_gate)
        {
            _dispatchState = state;
            if (_dispatcher is IStreamDispatchLease dispatchLease)
                dispatchLease.BindDispatchState(state);
        }
    }

    void IStreamDispatchLease.OnDispatchesDrained()
    {
        lock (_gate)
        {
            if (_dispatcher is IStreamDispatchLease dispatchLease)
                dispatchLease.OnDispatchesDrained();
            else
                ReleaseBufferedItems();
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

    private void ReleaseBufferedItems()
    {
        while (_items.TryDequeue(out var item))
        {
            buffers.Return(item.Owner);
            releaseBytes(item.RetainedBytes);
            _bytesConsumed?.Invoke(_requestId, _streamId, item.EncodedByteCount);
        }
    }

    private readonly record struct BufferedItem(
        IRpcByteBufferWriter Owner,
        int RetainedBytes,
        int EncodedByteCount,
        bool IsCompressed = false);
}

internal readonly record struct PreAdmissionDecodedPayload(
    ReadOnlySequence<byte> Payload,
    IRpcByteBufferWriter Owner,
    SharpLinkBufferWriterPool Pool) : IDisposable
{
    public void Dispose() => Pool.Return(Owner);
}
