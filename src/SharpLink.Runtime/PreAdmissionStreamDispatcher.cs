namespace SharpLink.Runtime;

/// <summary>
/// Owns an inbound client-stream route from deferred buffering through typed attachment.
/// Admission-queued calls use the admission byte budget; intercepted active calls promote that
/// reservation to active-call retention. The route remains stable after typed attachment so a
/// OneWay invocation can abandon its consumer without dropping peer frames before terminal.
/// </summary>
internal sealed class PreAdmissionStreamDispatcher(
    SharpLinkBufferWriterPool buffers,
    Func<int, bool> reserveBytes,
    Action<int> releaseBytes,
    Action capacityExceeded,
    Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null,
    bool retainUntilLocalCompletion = false)
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
    private InboundStreamChildDispatchState? _dispatcherState;
    private InboundStreamChildDispatchState? _attachingDispatchState;
    private Action<long, ushort, int>? _bytesConsumed;
    private long _requestId;
    private ushort _streamId;
    private Exception? _completion;
    private bool _completed;
    private bool _abandoned;
    private bool _retainUntilLocalCompletion = retainUntilLocalCompletion;
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

    /// <summary>
    /// Records peer terminal while deciding whether this stable route still has local ownership.
    /// Ordinary deferred routes retain only before typed attachment. OneWay routes also retain
    /// after attachment until local invocation completion can abandon/dispose the typed child.
    /// </summary>
    internal bool TryCompleteAndRetain(Exception? exception)
    {
        IStreamDispatcher? attached;
        InboundStreamChildDispatchState? childState;
        var childLeaseAcquired = false;
        lock (_gate)
        {
            if (_abandoned ||
                (!_retainUntilLocalCompletion &&
                 (_dispatcher is not null || _attachingDispatcher is not null)))
            {
                return false;
            }
            if (_completed)
                return true;

            _completed = true;
            _completion = exception;
            attached = _dispatcher;
            childState = _dispatcherState;
            if (attached is not null && childState is not null)
                childLeaseAcquired = childState.TryAcquire();
        }

        CompleteAttachedDispatcher(attached, childState, childLeaseAcquired, exception);
        return true;
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
            InboundStreamChildDispatchState? attachedState;
            bool completed;
            bool abandoned;
            lock (_gate)
            {
                policy = _retentionPolicy;
                attached = _dispatcher;
                attachedState = _dispatcherState;
                completed = _completed;
                abandoned = _abandoned;
            }
            if (abandoned)
            {
                NotifyBytesConsumed(encodedByteCount);
                return ValueTask.CompletedTask;
            }
            if (attached is not null)
                return DispatchAttached(attached, attachedState, payload, encodedByteCount);
            if (completed)
            {
                NotifyBytesConsumed(encodedByteCount);
                return ValueTask.CompletedTask;
            }

            if (!policy.ReserveBytes(retainedBytes))
            {
                var retry = false;
                lock (_gate)
                {
                    retry = !ReferenceEquals(policy, _retentionPolicy);
                    attached = _dispatcher;
                    attachedState = _dispatcherState;
                    completed = _completed;
                    abandoned = _abandoned;
                }
                if (retry)
                    continue;
                if (abandoned)
                {
                    NotifyBytesConsumed(encodedByteCount);
                    return ValueTask.CompletedTask;
                }
                if (attached is not null)
                    return DispatchAttached(attached, attachedState, payload, encodedByteCount);

                NotifyBytesConsumed(encodedByteCount);
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
                abandoned = _abandoned;
                if (!retryPolicy && !abandoned && _dispatcher is null && !_completed)
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
                attachedState = _dispatcherState;
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
            if (abandoned)
            {
                NotifyBytesConsumed(encodedByteCount);
                return ValueTask.CompletedTask;
            }
            if (elementCapacityExceeded)
            {
                NotifyBytesConsumed(encodedByteCount);
                policy.CapacityExceeded();
                return ValueTask.CompletedTask;
            }
            if (attached is null)
            {
                NotifyBytesConsumed(encodedByteCount);
                return ValueTask.CompletedTask;
            }
            return DispatchAttached(attached, attachedState, payload, encodedByteCount);
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
            InboundStreamChildDispatchState? attachedState;
            bool completed;
            bool abandoned;
            lock (_gate)
            {
                policy = _retentionPolicy;
                decoder = _decodeCompressed;
                attached = _dispatcher;
                attachedState = _dispatcherState;
                completed = _completed;
                abandoned = _abandoned;
            }
            if (abandoned)
            {
                NotifyBytesConsumed(originalByteCount);
                return ValueTask.CompletedTask;
            }
            decoder = decoder ?? throw new InvalidOperationException(
                "The inbound stream route has no compressed-frame decoder.");
            if (attached is not null)
                return DecodeAndDispatch(attached, attachedState, wirePayload, originalByteCount, decoder);
            if (completed)
            {
                NotifyBytesConsumed(originalByteCount);
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
                    attachedState = _dispatcherState;
                    completed = _completed;
                    abandoned = _abandoned;
                }
                if (retry)
                    continue;
                if (abandoned)
                {
                    NotifyBytesConsumed(originalByteCount);
                    return ValueTask.CompletedTask;
                }
                if (attached is not null)
                {
                    decoder = decoder ?? throw new InvalidOperationException(
                        "The inbound stream route has no compressed-frame decoder.");
                    return DecodeAndDispatch(
                        attached,
                        attachedState,
                        wirePayload,
                        originalByteCount,
                        decoder);
                }

                NotifyBytesConsumed(originalByteCount);
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
                abandoned = _abandoned;
                if (!retryPolicy && !abandoned && _dispatcher is null && !_completed)
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
                attachedState = _dispatcherState;
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

            if (abandoned)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                NotifyBytesConsumed(originalByteCount);
                return ValueTask.CompletedTask;
            }

            if (elementCapacityExceeded)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                NotifyBytesConsumed(originalByteCount);
                policy.CapacityExceeded();
                return ValueTask.CompletedTask;
            }

            ValueTask dispatch;
            try
            {
                if (attached is null)
                {
                    NotifyBytesConsumed(originalByteCount);
                    buffers.Return(owner);
                    policy.ReleaseBytes(retainedBytes);
                    return ValueTask.CompletedTask;
                }
                decoder = decoder ?? throw new InvalidOperationException(
                    "The inbound stream route has no compressed-frame decoder.");
                dispatch = DecodeAndDispatch(
                    attached,
                    attachedState,
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

    /// <summary>
    /// Atomically transitions this receive route to discard mode. Existing deferred owners are
    /// released immediately. A fully attached typed child is detached and disposed; an attaching
    /// child is handed back to its attachment owner so no pooled instance can be returned while
    /// configuration or replay still holds a reference.
    /// </summary>
    internal void Abandon(out bool alreadyCompleted)
    {
        BufferedItem[] bufferedItems;
        IStreamDispatcher? attached = null;
        InboundStreamChildDispatchState? childState = null;
        TaskCompletionSource? barrier = null;
        lock (_gate)
        {
            alreadyCompleted = _completed;
            if (_abandoned)
                return;

            _abandoned = true;
            bufferedItems = [.. _items];
            _items.Clear();

            if (_attachingDispatcher is null)
            {
                attached = _dispatcher;
                childState = _dispatcherState;
                _dispatcher = null;
                _dispatcherState = null;
                barrier = _attachmentBarrier;
                _attachmentBarrier = null;
                _replayedDuringAttach = 0;
            }
        }

        ReleaseBufferedItems(bufferedItems);
        childState?.Detach();
        barrier?.TrySetResult();
        if (attached is not null)
            BeginAbandonedDispatcherDisposal(attached);
        TryForwardDrain();
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
            if (_abandoned || _dispatcher is not null || _attachingDispatcher is not null)
            {
                alreadyCompleted = false;
                return false;
            }
            _attachingDispatcher = dispatcher;
            _attachingDispatchState = new InboundStreamChildDispatchState(
                dispatcher as IStreamDispatchLease);
            _attachmentBarrier = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _replayedDuringAttach = 0;
            alreadyCompleted = _completed && !_retainUntilLocalCompletion;
            return true;
        }
    }

    internal void FinishAttach(IStreamDispatcher dispatcher)
    {
        if (dispatcher is PreAdmissionStreamDispatcher)
            return;

        TaskCompletionSource barrier;
        bool abandoned;
        lock (_gate)
        {
            if (!ReferenceEquals(_attachingDispatcher, dispatcher) || _attachmentBarrier is null)
                throw new InvalidOperationException("The generated stream dispatcher was not claimed for attachment.");
            barrier = _attachmentBarrier;
            abandoned = _abandoned;
        }

        if (abandoned)
        {
            FinishAbandonedAttachment(dispatcher, barrier);
            return;
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
        InboundStreamChildDispatchState? childState;
        var childLeaseAcquired = false;
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            _completion = exception;
            attached = _abandoned ? null : _dispatcher;
            childState = _abandoned ? null : _dispatcherState;
            if (attached is not null && childState is not null)
                childLeaseAcquired = childState.TryAcquire();
        }

        CompleteAttachedDispatcher(attached, childState, childLeaseAcquired, exception);
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
        => ArgumentNullException.ThrowIfNull(state);

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
                    _retainUntilLocalCompletion |= replacement._retainUntilLocalCompletion;
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
                    _retainUntilLocalCompletion |= replacement._retainUntilLocalCompletion;
                    rejected = [.. _items];
                    _items.Clear();
                    _completed = true;
                    _completion = CreateRetentionPromotionCapacityException();
                }
                else
                {
                    _retentionPolicy = replacementPolicy;
                    _decodeCompressed = replacement._decodeCompressed;
                    _retainUntilLocalCompletion |= replacement._retainUntilLocalCompletion;
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
                ReleaseBufferedItems(rejected);
                return;
            }

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
        InboundStreamChildDispatchState? completionState = null;
        var completionLeaseAcquired = false;
        try
        {
            while (true)
            {
                BufferedItem item;
                InboundStreamChildDispatchState? childState;
                bool completed;
                bool abandoned;
                bool ownsAbandonedChild;
                lock (_gate)
                {
                    abandoned = _abandoned;
                    ownsAbandonedChild = abandoned &&
                        ReferenceEquals(_attachingDispatcher, dispatcher);
                    if (abandoned)
                    {
                        childState = ownsAbandonedChild ? _attachingDispatchState : null;
                        if (ownsAbandonedChild)
                        {
                            _attachingDispatcher = null;
                            _attachingDispatchState = null;
                            _replayedDuringAttach = 0;
                            if (ReferenceEquals(_attachmentBarrier, barrier))
                                _attachmentBarrier = null;
                        }
                        item = default;
                        completed = false;
                    }
                    else if (!_items.TryDequeue(out item))
                    {
                        if (!ReferenceEquals(_attachingDispatcher, dispatcher))
                            throw new InvalidOperationException(
                                "The generated stream dispatcher lost its attachment claim during replay.");
                        childState = _attachingDispatchState;
                        _attachingDispatcher = null;
                        _attachingDispatchState = null;
                        _dispatcher = dispatcher;
                        _dispatcherState = childState;
                        _replayedDuringAttach = 0;
                        completed = _completed;
                        if (!completed)
                        {
                            barrier.TrySetResult();
                            return;
                        }
                        completionState = childState;
                        completionLeaseAcquired = childState?.TryAcquire() == true;
                    }
                    else
                    {
                        childState = _attachingDispatchState;
                        _replayedDuringAttach++;
                        completed = false;
                    }
                }

                if (abandoned)
                {
                    if (ownsAbandonedChild)
                    {
                        childState?.Detach();
                        barrier.TrySetResult();
                        BeginAbandonedDispatcherDisposal(dispatcher);
                    }
                    else
                    {
                        barrier.TrySetResult();
                    }
                    return;
                }

                if (completed)
                    break;

                try
                {
                    var bufferedPayload = new ReadOnlySequence<byte>(item.Owner.WrittenMemory);
                    var dispatch = item.IsCompressed
                        ? DecodeAndDispatch(
                            dispatcher,
                            childState,
                            bufferedPayload,
                            item.EncodedByteCount,
                            _decodeCompressed ?? throw new InvalidOperationException(
                                "The inbound stream route has no compressed-frame decoder."))
                        : DispatchAttached(
                            dispatcher,
                            childState,
                            bufferedPayload,
                            item.EncodedByteCount);
                    await dispatch.ConfigureAwait(false);
                }
                finally
                {
                    buffers.Return(item.Owner);
                    item.ReleaseBytes(item.RetainedBytes);
                }
            }

            completionStarted = true;
            var completionLeaseOwnedByHelper = completionLeaseAcquired;
            completionLeaseAcquired = false;
            CompleteAttachedDispatcher(
                dispatcher,
                completionState,
                completionLeaseOwnedByHelper,
                _completion);
            barrier.TrySetResult();
        }
        catch (Exception exception)
        {
            if (completionLeaseAcquired)
            {
                completionState!.Release();
                completionLeaseAcquired = false;
            }
            FailAttachment(
                dispatcher,
                barrier,
                exception,
                completeDispatcher: !completionStarted);
            throw;
        }
        finally
        {
            if (completionLeaseAcquired)
                completionState!.Release();
            TryForwardDrain();
        }
    }

    private void FinishAbandonedAttachment(
        IStreamDispatcher dispatcher,
        TaskCompletionSource barrier)
    {
        InboundStreamChildDispatchState? childState;
        lock (_gate)
        {
            if (!ReferenceEquals(_attachingDispatcher, dispatcher))
            {
                barrier.TrySetResult();
                return;
            }
            childState = _attachingDispatchState;
            _attachingDispatcher = null;
            _attachingDispatchState = null;
            _replayedDuringAttach = 0;
            if (ReferenceEquals(_attachmentBarrier, barrier))
                _attachmentBarrier = null;
        }

        childState?.Detach();
        barrier.TrySetResult();
        BeginAbandonedDispatcherDisposal(dispatcher);
        TryForwardDrain();
    }

    private void FailAttachment(
        IStreamDispatcher dispatcher,
        TaskCompletionSource barrier,
        Exception exception,
        bool completeDispatcher = true)
    {
        BufferedItem[] remaining;
        InboundStreamChildDispatchState? childState;
        bool abandoned;
        lock (_gate)
        {
            remaining = [.. _items];
            _items.Clear();
            _replayedDuringAttach = 0;
            abandoned = _abandoned;
            childState = ReferenceEquals(_attachingDispatcher, dispatcher)
                ? _attachingDispatchState
                : ReferenceEquals(_dispatcher, dispatcher)
                    ? _dispatcherState
                    : null;
            if (ReferenceEquals(_attachingDispatcher, dispatcher))
            {
                _attachingDispatcher = null;
                _attachingDispatchState = null;
            }
            if (ReferenceEquals(_dispatcher, dispatcher))
            {
                _dispatcher = null;
                _dispatcherState = null;
            }
            if (ReferenceEquals(_attachmentBarrier, barrier))
                _attachmentBarrier = null;
        }

        ReleaseBufferedItems(remaining);
        childState?.Detach();
        if (abandoned)
        {
            barrier.TrySetResult();
            BeginAbandonedDispatcherDisposal(dispatcher);
            return;
        }
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

    private static void CompleteAttachedDispatcher(
        IStreamDispatcher? dispatcher,
        InboundStreamChildDispatchState? childState,
        bool childLeaseAcquired,
        Exception? exception)
    {
        if (dispatcher is null)
            return;
        if (childState is not null && !childLeaseAcquired)
            return;

        try
        {
            dispatcher.Complete(exception);
        }
        finally
        {
            if (childLeaseAcquired)
                childState!.Release();
        }
    }

    private static void BeginAbandonedDispatcherDisposal(IStreamDispatcher dispatcher)
    {
        if (dispatcher is not IAsyncDisposable asyncDisposable)
        {
            try
            {
                dispatcher.Complete(new OperationCanceledException(
                    "The inbound stream consumer completed before peer terminal."));
            }
            catch
            {
            }
            return;
        }

        try
        {
            var disposal = asyncDisposable.DisposeAsync();
            if (disposal.IsCompletedSuccessfully)
            {
                disposal.GetAwaiter().GetResult();
                return;
            }
            _ = ObserveAbandonedDispatcherDisposalAsync(disposal);
        }
        catch
        {
        }
    }

    private static async Task ObserveAbandonedDispatcherDisposalAsync(ValueTask disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void TryForwardDrain()
    {
        InboundStreamChildDispatchState? childState = null;
        BufferedItem[] bufferedItems = [];
        lock (_gate)
        {
            if (!_drainRequested || _drainForwarded ||
                _attachmentBarrier?.Task.IsCompleted == false)
            {
                return;
            }
            _drainForwarded = true;
            childState = _dispatcherState ?? _attachingDispatchState;
            if (childState is null)
            {
                bufferedItems = [.. _items];
                _items.Clear();
                _replayedDuringAttach = 0;
            }
        }

        childState?.Detach();
        ReleaseBufferedItems(bufferedItems);
    }

    private void ConfigureAttachingDispatcher(IStreamDispatcher dispatcher)
    {
        var dispatchStateBound = false;
        while (true)
        {
            Action<long, ushort, int>? bytesConsumed;
            long requestId;
            ushort streamId;
            InboundStreamChildDispatchState? childState;
            int version;
            lock (_gate)
            {
                if (_abandoned || !ReferenceEquals(_attachingDispatcher, dispatcher))
                    return;
                bytesConsumed = _bytesConsumed;
                requestId = _requestId;
                streamId = _streamId;
                childState = _attachingDispatchState;
                version = _configurationVersion;
            }

            if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(bytesConsumed, requestId, streamId);
            if (!dispatchStateBound && dispatcher is IStreamDispatchLease dispatchLease && childState is not null)
            {
                dispatchLease.BindDispatchState(childState);
                dispatchStateBound = true;
            }

            lock (_gate)
            {
                if (_abandoned || !ReferenceEquals(_attachingDispatcher, dispatcher) ||
                    version == _configurationVersion)
                {
                    return;
                }
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

    private ValueTask DispatchAttached(
        IStreamDispatcher dispatcher,
        InboundStreamChildDispatchState? childState,
        ReadOnlySequence<byte> payload,
        int encodedByteCount)
    {
        if (childState is not null && !childState.TryAcquire())
        {
            NotifyBytesConsumed(encodedByteCount);
            return ValueTask.CompletedTask;
        }

        try
        {
            var dispatch = dispatcher is IStreamDispatchLease lease
                ? lease.DispatchAcquiredAsync(payload, encodedByteCount)
                : dispatcher is IStreamConsumptionAwareDispatcher consumptionAware
                    ? consumptionAware.DispatchAsync(payload, encodedByteCount)
                    : dispatcher.DispatchAsync(payload);
            if (childState is null)
                return dispatch;
            if (dispatch.IsCompletedSuccessfully)
            {
                childState.Release();
                return ValueTask.CompletedTask;
            }
            return AwaitChildDispatchAsync(dispatch, childState);
        }
        catch
        {
            childState?.Release();
            throw;
        }
    }

    private static async ValueTask AwaitChildDispatchAsync(
        ValueTask dispatch,
        InboundStreamChildDispatchState childState)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            childState.Release();
        }
    }

    private ValueTask DecodeAndDispatch(
        IStreamDispatcher dispatcher,
        InboundStreamChildDispatchState? childState,
        ReadOnlySequence<byte> payload,
        int encodedByteCount,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload> decoder)
    {
        if (childState is not null && childState.IsClosed)
        {
            NotifyBytesConsumed(encodedByteCount);
            return ValueTask.CompletedTask;
        }

        var decoded = decoder(payload);
        try
        {
            var dispatch = DispatchAttached(
                dispatcher,
                childState,
                decoded.Payload,
                encodedByteCount);
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
            NotifyBytesConsumed(item.EncodedByteCount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyBytesConsumed(int encodedByteCount)
        => _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);

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
