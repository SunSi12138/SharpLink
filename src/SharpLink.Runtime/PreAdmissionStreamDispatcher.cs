namespace SharpLink.Runtime;

/// <summary>
/// Temporarily owns client-stream frames before the generated typed dispatcher is registered.
/// Admission-queued calls use the admission byte budget; intercepted active calls promote that
/// reservation to active-call retention. During typed attachment, live frames remain on this
/// non-blocking ordered queue while one shared handoff count preserves the 4096-element bound.
/// </summary>
internal sealed class PreAdmissionStreamDispatcher(
    SharpLinkBufferWriterPool buffers,
    Func<int, bool> reserveBytes,
    Action<int> releaseBytes,
    Action capacityExceeded,
    Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null)
    : IStreamConsumptionAwareDispatcher, IStreamDispatchLease
{
    private const int MaxBufferedElements = 4096;
    private static readonly Action<int> NoopReleaseBytes = static _ => { };
    private static Action<long, ushort, bool>? s_bufferedItemObserverForTests;

    private readonly Lock _gate = new();
    private readonly Queue<BufferedItem> _items = [];
    private RetentionPolicy _retentionPolicy = new(reserveBytes, releaseBytes, capacityExceeded);
    private Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? _decodeCompressed = decodeCompressed;
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
    private int _replayedDuringAttach;
    private bool _drainRequested;
    private bool _drainForwarded;

    internal static Action<long, ushort, bool>? BufferedItemObserverForTests
    {
        get => Volatile.Read(ref s_bufferedItemObserverForTests);
        set => Volatile.Write(ref s_bufferedItemObserverForTests, value);
    }

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
        var retainedBytes = Math.Max(1, checked((int)payload.Length));

        while (true)
        {
            RetentionPolicy policy;
            IStreamDispatcher? attached;
            bool completed;
            lock (_gate)
            {
                policy = _retentionPolicy;
                attached = _dispatcher;
                completed = _completed;
            }
            if (attached is not null)
                return DispatchAttached(attached, payload, encodedByteCount);
            if (completed)
            {
                _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
                return ValueTask.CompletedTask;
            }

            if (!policy.ReserveBytes(retainedBytes))
            {
                var retry = false;
                lock (_gate)
                {
                    retry = !ReferenceEquals(policy, _retentionPolicy);
                    attached = _dispatcher;
                    completed = _completed;
                }
                if (retry)
                    continue;
                if (attached is not null)
                    return DispatchAttached(attached, payload, encodedByteCount);

                _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
                if (!completed)
                    policy.CapacityExceeded();
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
                policy.ReleaseBytes(retainedBytes);
                throw;
            }

            var retryPolicy = false;
            var buffered = false;
            var elementCapacityExceeded = false;
            lock (_gate)
            {
                retryPolicy = !ReferenceEquals(policy, _retentionPolicy);
                if (!retryPolicy && _dispatcher is null && !_completed)
                {
                    if (_items.Count + _replayedDuringAttach >= MaxBufferedElements)
                    {
                        elementCapacityExceeded = true;
                        _completed = true;
                        _completion = CreateElementCapacityException();
                    }
                    else
                    {
                        _items.Enqueue(new BufferedItem(
                            owner,
                            retainedBytes,
                            encodedByteCount,
                            policy.ReleaseBytes));
                        buffered = true;
                    }
                }
                attached = _dispatcher;
            }

            if (retryPolicy)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                continue;
            }

            if (buffered)
            {
                Volatile.Read(ref s_bufferedItemObserverForTests)?.Invoke(
                    _requestId,
                    _streamId,
                    false);
                return ValueTask.CompletedTask;
            }

            buffers.Return(owner);
            policy.ReleaseBytes(retainedBytes);
            if (elementCapacityExceeded)
            {
                _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
                policy.CapacityExceeded();
                return ValueTask.CompletedTask;
            }
            if (attached is null)
            {
                _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
                return ValueTask.CompletedTask;
            }
            return DispatchAttached(attached, payload, encodedByteCount);
        }
    }

    internal ValueTask DispatchCompressedAsync(
        ReadOnlySequence<byte> wirePayload,
        int originalByteCount)
    {
        var retainedBytes = checked((int)wirePayload.Length);

        while (true)
        {
            RetentionPolicy policy;
            Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decoder;
            IStreamDispatcher? attached;
            bool completed;
            lock (_gate)
            {
                policy = _retentionPolicy;
                decoder = _decodeCompressed;
                attached = _dispatcher;
                completed = _completed;
            }
            decoder = decoder ?? throw new InvalidOperationException(
                "The pre-admission stream has no compressed-frame decoder.");
            if (attached is not null)
            {
                return attached is DiscardingStreamDispatcher
                    ? DispatchAttached(attached, wirePayload, originalByteCount)
                    : DecodeAndDispatch(attached, wirePayload, originalByteCount, decoder);
            }
            if (completed)
            {
                _bytesConsumed?.Invoke(_requestId, _streamId, originalByteCount);
                return ValueTask.CompletedTask;
            }

            if (!policy.ReserveBytes(retainedBytes))
            {
                var retry = false;
                lock (_gate)
                {
                    retry = !ReferenceEquals(policy, _retentionPolicy);
                    decoder = _decodeCompressed;
                    attached = _dispatcher;
                    completed = _completed;
                }
                if (retry)
                    continue;
                if (attached is not null)
                {
                    decoder = decoder ?? throw new InvalidOperationException(
                        "The pre-admission stream has no compressed-frame decoder.");
                    return attached is DiscardingStreamDispatcher
                        ? DispatchAttached(attached, wirePayload, originalByteCount)
                        : DecodeAndDispatch(attached, wirePayload, originalByteCount, decoder);
                }

                _bytesConsumed?.Invoke(_requestId, _streamId, originalByteCount);
                if (!completed)
                    policy.CapacityExceeded();
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
                policy.ReleaseBytes(retainedBytes);
                throw;
            }

            var retryPolicy = false;
            var buffered = false;
            var elementCapacityExceeded = false;
            lock (_gate)
            {
                retryPolicy = !ReferenceEquals(policy, _retentionPolicy);
                if (!retryPolicy && _dispatcher is null && !_completed)
                {
                    if (_items.Count + _replayedDuringAttach >= MaxBufferedElements)
                    {
                        elementCapacityExceeded = true;
                        _completed = true;
                        _completion = CreateElementCapacityException();
                    }
                    else
                    {
                        _items.Enqueue(new BufferedItem(
                            owner,
                            retainedBytes,
                            originalByteCount,
                            policy.ReleaseBytes,
                            IsCompressed: true));
                        buffered = true;
                    }
                }
                attached = _dispatcher;
                decoder = _decodeCompressed;
            }

            if (retryPolicy)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                continue;
            }

            if (buffered)
            {
                Volatile.Read(ref s_bufferedItemObserverForTests)?.Invoke(
                    _requestId,
                    _streamId,
                    true);
                return ValueTask.CompletedTask;
            }

            if (elementCapacityExceeded)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                _bytesConsumed?.Invoke(_requestId, _streamId, originalByteCount);
                policy.CapacityExceeded();
                return ValueTask.CompletedTask;
            }

            ValueTask dispatch;
            try
            {
                if (attached is null)
                {
                    _bytesConsumed?.Invoke(_requestId, _streamId, originalByteCount);
                    buffers.Return(owner);
                    policy.ReleaseBytes(retainedBytes);
                    return ValueTask.CompletedTask;
                }
                decoder = decoder ?? throw new InvalidOperationException(
                    "The pre-admission stream has no compressed-frame decoder.");
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
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                throw;
            }
            if (dispatch.IsCompletedSuccessfully)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                return ValueTask.CompletedTask;
            }
            return AwaitRetainedCompressedDispatchAsync(
                dispatch,
                owner,
                retainedBytes,
                policy.ReleaseBytes);
        }
    }

    internal bool TryBeginAttach(IStreamDispatcher dispatcher, out bool alreadyCompleted)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher is PreAdmissionStreamDispatcher promotion)
        {
            PromoteFrom(promotion);
            alreadyCompleted = false;
            return true;
        }
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
            _replayedDuringAttach = 0;
            alreadyCompleted = _completed;
            return true;
        }
    }

    internal void FinishAttach(IStreamDispatcher dispatcher)
    {
        if (dispatcher is PreAdmissionStreamDispatcher)
            return;

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

    private void PromoteFrom(PreAdmissionStreamDispatcher replacement)
    {
        while (true)
        {
            RetentionPolicy currentPolicy;
            RetentionPolicy replacementPolicy;
            BufferedItem[] buffered;
            lock (_gate)
            {
                currentPolicy = _retentionPolicy;
                replacementPolicy = replacement._retentionPolicy;
                buffered = [.. _items];
            }

            if (currentPolicy == replacementPolicy)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(currentPolicy, _retentionPolicy))
                        continue;
                    _decodeCompressed = replacement._decodeCompressed;
                }
                return;
            }

            var reservedCount = 0;
            for (; reservedCount < buffered.Length; reservedCount++)
            {
                if (!replacementPolicy.ReserveBytes(buffered[reservedCount].RetainedBytes))
                    break;
            }
            var reservationSucceeded = reservedCount == buffered.Length;
            if (!reservationSucceeded)
            {
                for (var index = 0; index < reservedCount; index++)
                    replacementPolicy.ReleaseBytes(buffered[index].RetainedBytes);
            }

            var retry = false;
            BufferedItem[] rejected = [];
            lock (_gate)
            {
                if (!ReferenceEquals(currentPolicy, _retentionPolicy) ||
                    _items.Count != buffered.Length)
                {
                    retry = true;
                }
                else if (!reservationSucceeded)
                {
                    _retentionPolicy = replacementPolicy;
                    _decodeCompressed = replacement._decodeCompressed;
                    rejected = [.. _items];
                    _items.Clear();
                    _completed = true;
                    _completion = CreateRetentionPromotionCapacityException();
                }
                else
                {
                    _retentionPolicy = replacementPolicy;
                    _decodeCompressed = replacement._decodeCompressed;
                    if (buffered.Length != 0)
                    {
                        _items.Clear();
                        for (var index = 0; index < buffered.Length; index++)
                        {
                            _items.Enqueue(buffered[index] with
                            {
                                ReleaseBytes = replacementPolicy.ReleaseBytes
                            });
                        }
                    }
                }
            }

            if (retry)
            {
                if (reservationSucceeded)
                {
                    for (var index = 0; index < buffered.Length; index++)
                        replacementPolicy.ReleaseBytes(buffered[index].RetainedBytes);
                }
                continue;
            }

            if (!reservationSucceeded)
            {
                // Promotion runs while StreamManager holds its registration lock. Marking this
                // wrapper terminal directly avoids re-entering StreamManager through the active
                // policy's capacity callback while still ensuring typed attachment observes
                // ResourceExhausted. Existing queued owners/accounting are released immediately.
                ReleaseBufferedItems(rejected);
                return;
            }

            // The active policy now owns the retained-byte accounting. Settle the old admission
            // reservation only after that ownership transfer is published, so there is never a
            // window where the same pooled owners are charged to neither budget.
            for (var index = 0; index < buffered.Length; index++)
                buffered[index].ReleaseBytes(buffered[index].RetainedBytes);
            return;
        }
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
                        _replayedDuringAttach = 0;
                        completed = _completed;
                        if (!completed)
                            barrier.TrySetResult();
                        else
                            break;
                        return;
                    }
                    _replayedDuringAttach++;
                }
                try
                {
                    var bufferedPayload = new ReadOnlySequence<byte>(item.Owner.WrittenMemory);
                    var dispatch = item.IsCompressed && dispatcher is not DiscardingStreamDispatcher
                        ? DecodeAndDispatch(
                            dispatcher,
                            bufferedPayload,
                            item.EncodedByteCount,
                            _decodeCompressed ?? throw new InvalidOperationException(
                                "The pre-admission stream has no compressed-frame decoder."))
                        : DispatchAttached(dispatcher, bufferedPayload, item.EncodedByteCount);
                    await dispatch.ConfigureAwait(false);
                }
                finally
                {
                    buffers.Return(item.Owner);
                    item.ReleaseBytes(item.RetainedBytes);
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
            _replayedDuringAttach = 0;
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
        int retainedBytes,
        Action<int> releaseRetainedBytes)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            buffers.Return(owner);
            releaseRetainedBytes(retainedBytes);
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

    private static SharpLinkException CreateElementCapacityException()
        => new(
            SharpLinkErrorCode.ResourceExhausted,
            $"Stream receive buffer exceeded {MaxBufferedElements} elements.");

    private static SharpLinkException CreateRetentionPromotionCapacityException()
        => new(
            SharpLinkErrorCode.ResourceExhausted,
            "Deferred stream retention exceeded the active byte budget during admission promotion.");

    private void ReleaseBufferedItems(IEnumerable<BufferedItem> items)
    {
        foreach (var item in items)
        {
            buffers.Return(item.Owner);
            item.ReleaseBytes(item.RetainedBytes);
            _bytesConsumed?.Invoke(_requestId, _streamId, item.EncodedByteCount);
        }
    }

    private sealed record RetentionPolicy(
        Func<int, bool> ReserveBytes,
        Action<int> ReleaseBytes,
        Action CapacityExceeded);

    private readonly record struct BufferedItem(
        IRpcByteBufferWriter Owner,
        int RetainedBytes,
        int EncodedByteCount,
        Action<int> ReleaseBytes,
        bool IsCompressed = false);
}

internal readonly record struct PreAdmissionDecodedPayload(
    ReadOnlySequence<byte> Payload,
    IRpcByteBufferWriter Owner,
    SharpLinkBufferWriterPool Pool) : IDisposable
{
    public void Dispose() => Pool.Return(Owner);
}
