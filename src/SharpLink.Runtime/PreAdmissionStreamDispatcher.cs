namespace SharpLink.Runtime;

/// <summary>
/// Owns one stable inbound client-stream mailbox from deferred buffering through typed consumption.
/// The mailbox identity never changes: retention and typed-consumer state change in place while
/// <see cref="StreamManager"/> keeps the same request/stream route until peer terminal/call release.
/// </summary>
internal sealed partial class PreAdmissionStreamDispatcher(
    SharpLinkBufferWriterPool buffers,
    Func<int, bool> reserveBytes,
    Action<int> releaseBytes,
    Action capacityExceeded,
    Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null,
    bool retainUntilLocalCompletion = false,
    int maxRetainedBytes = int.MaxValue)
    : IStreamConsumptionAwareDispatcher, IStreamDispatchLease, IStreamDispatchState
{
    private const int MaxBufferedElements = 4096;
    private static Action<long, ushort, bool>? s_bufferedItemObserverForTests;

    private readonly Lock _gate = new();
    private readonly Queue<BufferedItem> _items = [];
    private RetentionPolicy _retentionPolicy = CreateRetentionPolicy(
        reserveBytes,
        releaseBytes,
        capacityExceeded,
        maxRetainedBytes);
    private Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? _decodeCompressed = decodeCompressed;
    private IStreamDispatcher? _dispatcher;
    private IStreamDispatchLease? _childLease;
    private Action<long, ushort, int>? _bytesConsumed;
    private long _requestId;
    private ushort _streamId;
    private Exception? _completion;
    private bool _completed;
    private bool _completionForwarded;
    private bool _discarding;
    private bool _localAbandonRequested;
    private bool _retainUntilLocalCompletion = retainUntilLocalCompletion;
    private bool _attachmentInProgress;
    private int _configurationVersion;
    private int _replayedDuringAttach;
    private int _retainedBytes;
    private int _activeChildDispatches;
    private bool _childClosed;
    private bool _childDetachRequested;
    private bool _disposeChildOnDetach;
    private bool _childDetached;
    private bool _childDetachFinalizing;
    private TaskCompletionSource? _childDispatchesDrained;
    private TaskCompletionSource? _childDetachedCompletion;

    internal static Action<long, ushort, bool>? BufferedItemObserverForTests
    {
        get => Volatile.Read(ref s_bufferedItemObserverForTests);
        set => Volatile.Write(ref s_bufferedItemObserverForTests, value);
    }

    internal int RetainedBytesForTests
    {
        get
        {
            lock (_gate)
                return _retainedBytes;
        }
    }

    internal bool TryCompleteAndRetain(Exception? exception)
    {
        IStreamDispatcher? attached = null;
        lock (_gate)
        {
            if (_localAbandonRequested ||
                (!_retainUntilLocalCompletion && (_dispatcher is not null || _attachmentInProgress)))
            {
                return false;
            }
            if (_completed)
                return true;

            _completed = true;
            _completion = exception;
            if (!_attachmentInProgress)
                _ = TryAcquireCompletionDispatchLocked(out attached);
        }

        CompleteAttachedDispatcher(attached, exception);
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
            IStreamDispatcher? attached = null;
            bool completed;
            bool discarding;
            lock (_gate)
            {
                policy = _retentionPolicy;
                completed = _completed;
                discarding = _discarding;
                if (!completed && !discarding)
                    _ = TryAcquireLiveChildDispatchLocked(out attached);
            }

            if (attached is not null)
                return DispatchAttachedAcquired(attached, payload, encodedByteCount);
            if (completed || discarding)
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
                    completed = _completed;
                    discarding = _discarding;
                    attached = null;
                    if (!retry && !completed && !discarding)
                        _ = TryAcquireLiveChildDispatchLocked(out attached);
                }
                if (retry)
                    continue;
                if (attached is not null)
                    return DispatchAttachedAcquired(attached, payload, encodedByteCount);

                NotifyBytesConsumed(encodedByteCount);
                if (!completed && !discarding)
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
            var capacityExceeded = false;
            attached = null;
            lock (_gate)
            {
                retryPolicy = !ReferenceEquals(policy, _retentionPolicy);
                completed = _completed;
                discarding = _discarding;
                if (!retryPolicy && !completed && !discarding)
                {
                    if (!TryAcquireLiveChildDispatchLocked(out attached))
                    {
                        if (_items.Count + _replayedDuringAttach >= MaxBufferedElements ||
                            !CanRetainLocked(policy, retainedBytes))
                        {
                            capacityExceeded = true;
                            _completed = true;
                            _completion = _items.Count + _replayedDuringAttach >= MaxBufferedElements
                                ? CreateElementCapacityException()
                                : CreateRetentionCapacityException(policy.MaxRetainedBytes);
                        }
                        else
                        {
                            _items.Enqueue(new BufferedItem(
                                owner,
                                retainedBytes,
                                encodedByteCount,
                                policy.ReleaseBytes));
                            _retainedBytes = checked(_retainedBytes + retainedBytes);
                            buffered = true;
                        }
                    }
                }
            }

            if (retryPolicy)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                continue;
            }
            if (buffered)
            {
                Volatile.Read(ref s_bufferedItemObserverForTests)?.Invoke(_requestId, _streamId, false);
                return ValueTask.CompletedTask;
            }

            buffers.Return(owner);
            policy.ReleaseBytes(retainedBytes);
            if (attached is not null)
                return DispatchAttachedAcquired(attached, payload, encodedByteCount);

            NotifyBytesConsumed(encodedByteCount);
            if (capacityExceeded)
                policy.CapacityExceeded();
            return ValueTask.CompletedTask;
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
            IStreamDispatcher? attached = null;
            bool completed;
            bool discarding;
            lock (_gate)
            {
                policy = _retentionPolicy;
                decoder = _decodeCompressed;
                completed = _completed;
                discarding = _discarding;
                if (!completed && !discarding)
                    _ = TryAcquireLiveChildDispatchLocked(out attached);
            }

            if (attached is not null)
            {
                decoder = decoder ?? throw new InvalidOperationException(
                    "The inbound stream mailbox has no compressed-frame decoder.");
                return DecodeAndDispatchAcquired(attached, wirePayload, originalByteCount, decoder);
            }
            if (completed || discarding)
            {
                NotifyBytesConsumed(originalByteCount);
                return ValueTask.CompletedTask;
            }
            decoder = decoder ?? throw new InvalidOperationException(
                "The inbound stream mailbox has no compressed-frame decoder.");

            if (!policy.ReserveBytes(retainedBytes))
            {
                var retry = false;
                lock (_gate)
                {
                    retry = !ReferenceEquals(policy, _retentionPolicy);
                    decoder = _decodeCompressed;
                    completed = _completed;
                    discarding = _discarding;
                    attached = null;
                    if (!retry && !completed && !discarding)
                        _ = TryAcquireLiveChildDispatchLocked(out attached);
                }
                if (retry)
                    continue;
                if (attached is not null)
                {
                    decoder = decoder ?? throw new InvalidOperationException(
                        "The inbound stream mailbox has no compressed-frame decoder.");
                    return DecodeAndDispatchAcquired(attached, wirePayload, originalByteCount, decoder);
                }

                NotifyBytesConsumed(originalByteCount);
                if (!completed && !discarding)
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
            var capacityExceeded = false;
            attached = null;
            lock (_gate)
            {
                retryPolicy = !ReferenceEquals(policy, _retentionPolicy);
                decoder = _decodeCompressed;
                completed = _completed;
                discarding = _discarding;
                if (!retryPolicy && !completed && !discarding)
                {
                    if (!TryAcquireLiveChildDispatchLocked(out attached))
                    {
                        if (_items.Count + _replayedDuringAttach >= MaxBufferedElements ||
                            !CanRetainLocked(policy, retainedBytes))
                        {
                            capacityExceeded = true;
                            _completed = true;
                            _completion = _items.Count + _replayedDuringAttach >= MaxBufferedElements
                                ? CreateElementCapacityException()
                                : CreateRetentionCapacityException(policy.MaxRetainedBytes);
                        }
                        else
                        {
                            _items.Enqueue(new BufferedItem(
                                owner,
                                retainedBytes,
                                originalByteCount,
                                policy.ReleaseBytes,
                                IsCompressed: true));
                            _retainedBytes = checked(_retainedBytes + retainedBytes);
                            buffered = true;
                        }
                    }
                }
            }

            if (retryPolicy)
            {
                buffers.Return(owner);
                policy.ReleaseBytes(retainedBytes);
                continue;
            }
            if (buffered)
            {
                Volatile.Read(ref s_bufferedItemObserverForTests)?.Invoke(_requestId, _streamId, true);
                return ValueTask.CompletedTask;
            }

            if (attached is not null)
            {
                try
                {
                    decoder = decoder ?? throw new InvalidOperationException(
                        "The inbound stream mailbox has no compressed-frame decoder.");
                    var dispatch = DecodeAndDispatchAcquired(
                        attached,
                        new ReadOnlySequence<byte>(owner.WrittenMemory),
                        originalByteCount,
                        decoder);
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
                catch
                {
                    buffers.Return(owner);
                    policy.ReleaseBytes(retainedBytes);
                    throw;
                }
            }

            buffers.Return(owner);
            policy.ReleaseBytes(retainedBytes);
            NotifyBytesConsumed(originalByteCount);
            if (capacityExceeded)
                policy.CapacityExceeded();
            return ValueTask.CompletedTask;
        }
    }

    internal void Abandon(out bool alreadyCompleted)
    {
        BufferedItem[] discarded;
        TaskCompletionSource? dispatchesDrained = null;
        lock (_gate)
        {
            alreadyCompleted = _completed;
            if (_localAbandonRequested)
                return;

            _localAbandonRequested = true;
            _discarding = true;
            _childClosed = true;
            if (_dispatcher is not null || _attachmentInProgress)
            {
                _childDetachRequested = true;
                _disposeChildOnDetach = true;
            }
            discarded = TakeBufferedItemsLocked();
            if (_activeChildDispatches == 0)
                dispatchesDrained = TakeDispatchesDrainedCompletionLocked();
        }

        DiscardBufferedItems(discarded);
        dispatchesDrained?.TrySetResult();
        TryFinalizeChildDetach();
    }

    internal bool TryBeginAttach(IStreamDispatcher dispatcher, out bool alreadyCompleted)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher is PreAdmissionStreamDispatcher reconfiguration)
        {
            ReconfigureFrom(reconfiguration);
            alreadyCompleted = false;
            return true;
        }

        lock (_gate)
        {
            if (_localAbandonRequested || _dispatcher is not null || _attachmentInProgress)
            {
                alreadyCompleted = false;
                return false;
            }

            _dispatcher = dispatcher;
            _childLease = dispatcher as IStreamDispatchLease;
            _attachmentInProgress = true;
            _childClosed = false;
            _childDetached = false;
            _childDetachRequested = false;
            _disposeChildOnDetach = false;
            _childDetachFinalizing = false;
            _completionForwarded = false;
            _replayedDuringAttach = 0;
            alreadyCompleted = _completed && !_retainUntilLocalCompletion;
            return true;
        }
    }

    internal void FinishAttach(IStreamDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher is PreAdmissionStreamDispatcher)
            return;

        lock (_gate)
        {
            if (!ReferenceEquals(_dispatcher, dispatcher) || !_attachmentInProgress)
                throw new InvalidOperationException("The generated stream dispatcher was not claimed for attachment.");
        }

        try
        {
            ConfigureAttachedDispatcher(dispatcher);
        }
        catch (Exception exception)
        {
            FailAttachment(dispatcher, exception);
            throw;
        }

        var replay = ReplayBufferedItemsAsync(dispatcher);
        if (replay.IsCompleted)
            replay.GetAwaiter().GetResult();
        else
            _ = ObserveReplayFailureAsync(replay);
    }

    public void Complete(bool isError, string? errorMessage)
        => Complete(isError
            ? new SharpLinkException(
                SharpLinkErrorCode.RemoteError,
                string.IsNullOrWhiteSpace(errorMessage) ? "Remote Error" : errorMessage)
            : null);

    public void Complete(Exception? exception)
    {
        IStreamDispatcher? attached = null;
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            _completion = exception;
            if (!_attachmentInProgress)
                _ = TryAcquireCompletionDispatchLocked(out attached);
        }

        CompleteAttachedDispatcher(attached, exception);
    }

    public void SetBytesConsumedCallback(
        Action<long, ushort, int>? callback,
        long requestId,
        ushort streamId)
    {
        IStreamConsumptionAwareDispatcher? consumptionAware = null;
        var childLeaseAcquired = false;
        lock (_gate)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
            _configurationVersion++;
            if (!_attachmentInProgress && !_childClosed && !_childDetached &&
                _dispatcher is IStreamConsumptionAwareDispatcher child &&
                TryAcquireChildDispatchLocked(out _))
            {
                consumptionAware = child;
                childLeaseAcquired = true;
            }
        }

        if (consumptionAware is null)
            return;
        try
        {
            consumptionAware.SetBytesConsumedCallback(callback, requestId, streamId);
        }
        finally
        {
            if (childLeaseAcquired)
                ReleaseChildDispatch();
        }
    }

    ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
        ReadOnlySequence<byte> payload,
        int encodedByteCount)
        => DispatchAsync(payload, encodedByteCount);

    void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
        => ArgumentNullException.ThrowIfNull(state);

    void IStreamDispatchLease.OnDispatchesDrained()
    {
        BufferedItem[] discarded = [];
        lock (_gate)
        {
            if (_dispatcher is not null || _attachmentInProgress)
                _childDetachRequested = true;
            if (!_attachmentInProgress)
            {
                _discarding = true;
                discarded = TakeBufferedItemsLocked();
            }
        }

        DiscardBufferedItems(discarded);
        TryFinalizeChildDetach();
    }

    bool IStreamDispatchState.HasActiveDispatches
    {
        get
        {
            lock (_gate)
                return _activeChildDispatches != 0;
        }
    }

    bool IStreamDispatchState.IsDetached
    {
        get
        {
            lock (_gate)
                return _childDetached;
        }
    }

    ValueTask IStreamDispatchState.WaitForDispatchesDrainedAsync()
    {
        lock (_gate)
        {
            if (_activeChildDispatches == 0)
                return ValueTask.CompletedTask;
            _childDispatchesDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_childDispatchesDrained.Task);
        }
    }

    ValueTask IStreamDispatchState.WaitForDetachedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_gate)
        {
            if (_childDetached)
                return ValueTask.CompletedTask;
            _childDetachedCompletion ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            task = _childDetachedCompletion.Task;
        }
        return cancellationToken.CanBeCanceled
            ? new ValueTask(task.WaitAsync(cancellationToken))
            : new ValueTask(task);
    }

    void IStreamDispatchState.Close()
    {
        BufferedItem[] discarded;
        TaskCompletionSource? dispatchesDrained = null;
        lock (_gate)
        {
            if (_childClosed)
                return;
            _childClosed = true;
            _discarding = true;
            discarded = TakeBufferedItemsLocked();
            if (_activeChildDispatches == 0)
                dispatchesDrained = TakeDispatchesDrainedCompletionLocked();
        }

        DiscardBufferedItems(discarded);
        dispatchesDrained?.TrySetResult();
        TryFinalizeChildDetach();
    }

    private void ReconfigureFrom(PreAdmissionStreamDispatcher replacement)
    {
        RetentionPolicy replacementPolicy;
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? replacementDecoder;
        bool replacementRetainUntilLocalCompletion;
        lock (replacement._gate)
        {
            replacementPolicy = replacement._retentionPolicy;
            replacementDecoder = replacement._decodeCompressed;
            replacementRetainUntilLocalCompletion = replacement._retainUntilLocalCompletion;
        }

        BufferedItem[] rejected = [];
        lock (_gate)
        {
            _retentionPolicy = replacementPolicy;
            _decodeCompressed = replacementDecoder;
            _retainUntilLocalCompletion |= replacementRetainUntilLocalCompletion;
            _configurationVersion++;
            if (!_completed && _retainedBytes > replacementPolicy.MaxRetainedBytes)
            {
                _completed = true;
                _completion = CreateRetentionReconfigurationCapacityException(
                    replacementPolicy.MaxRetainedBytes);
                rejected = TakeBufferedItemsLocked();
            }
        }

        DiscardBufferedItems(rejected);
    }

    private async Task ReplayBufferedItemsAsync(IStreamDispatcher dispatcher)
    {
        try
        {
            while (true)
            {
                BufferedItem item;
                Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decoder;
                IStreamDispatcher? completionDispatcher = null;
                Exception? completion = null;
                bool stop;
                lock (_gate)
                {
                    if (_localAbandonRequested || _discarding && _childClosed ||
                        !ReferenceEquals(_dispatcher, dispatcher))
                    {
                        _attachmentInProgress = false;
                        _replayedDuringAttach = 0;
                        item = default;
                        decoder = null;
                        stop = true;
                    }
                    else if (_items.TryDequeue(out item))
                    {
                        _replayedDuringAttach++;
                        if (!TryAcquireReplayChildDispatchLocked(dispatcher))
                            throw new InvalidOperationException(
                                "The inbound stream mailbox lost its typed child during replay.");
                        decoder = _decodeCompressed;
                        stop = false;
                    }
                    else
                    {
                        _attachmentInProgress = false;
                        _replayedDuringAttach = 0;
                        item = default;
                        decoder = null;
                        stop = true;
                        if (_completed && TryAcquireCompletionDispatchLocked(out completionDispatcher))
                            completion = _completion;
                    }
                }

                if (completionDispatcher is not null)
                    CompleteAttachedDispatcher(completionDispatcher, completion);
                if (stop)
                    return;

                try
                {
                    var bufferedPayload = new ReadOnlySequence<byte>(item.Owner.WrittenMemory);
                    var dispatch = item.IsCompressed
                        ? DecodeAndDispatchAcquired(
                            dispatcher,
                            bufferedPayload,
                            item.EncodedByteCount,
                            decoder ?? throw new InvalidOperationException(
                                "The inbound stream mailbox has no compressed-frame decoder."))
                        : DispatchAttachedAcquired(
                            dispatcher,
                            bufferedPayload,
                            item.EncodedByteCount);
                    await dispatch.ConfigureAwait(false);
                }
                finally
                {
                    ReleaseBufferedItem(item, notifyBytesConsumed: false);
                }
            }
        }
        catch (Exception exception)
        {
            FailAttachment(dispatcher, exception);
            throw;
        }
        finally
        {
            FinalizeAttachmentEnd();
        }
    }

    private static async Task ObserveReplayFailureAsync(Task replay)
    {
        try { await replay.ConfigureAwait(false); }
        catch { }
    }

    private void FailAttachment(IStreamDispatcher dispatcher, Exception exception)
    {
        BufferedItem[] discarded;
        IStreamDispatcher? completionDispatcher = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_dispatcher, dispatcher))
                return;

            _attachmentInProgress = false;
            _replayedDuringAttach = 0;
            _discarding = true;
            if (!_completed)
            {
                _completed = true;
                _completion = exception;
            }
            if (!_completionForwarded && !_childClosed)
            {
                _completionForwarded = true;
                _activeChildDispatches++;
                completionDispatcher = dispatcher;
            }
            _childClosed = true;
            _childDetachRequested = true;
            discarded = TakeBufferedItemsLocked();
        }

        DiscardBufferedItems(discarded);
        if (completionDispatcher is not null)
        {
            try { CompleteAttachedDispatcher(completionDispatcher, exception); }
            catch { }
        }
        TryFinalizeChildDetach();
    }

    private void FinalizeAttachmentEnd()
    {
        BufferedItem[] discarded = [];
        lock (_gate)
        {
            if (_attachmentInProgress)
                return;
            if (_childDetachRequested)
            {
                _discarding = true;
                discarded = TakeBufferedItemsLocked();
            }
        }

        DiscardBufferedItems(discarded);
        TryFinalizeChildDetach();
    }

    private void ConfigureAttachedDispatcher(IStreamDispatcher dispatcher)
    {
        var dispatchStateBound = false;
        while (true)
        {
            Action<long, ushort, int>? bytesConsumed;
            long requestId;
            ushort streamId;
            int version;
            lock (_gate)
            {
                if (_localAbandonRequested || !ReferenceEquals(_dispatcher, dispatcher) ||
                    !_attachmentInProgress)
                {
                    return;
                }
                bytesConsumed = _bytesConsumed;
                requestId = _requestId;
                streamId = _streamId;
                version = _configurationVersion;
            }

            if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(bytesConsumed, requestId, streamId);
            if (!dispatchStateBound && dispatcher is IStreamDispatchLease dispatchLease)
            {
                dispatchLease.BindDispatchState(this);
                dispatchStateBound = true;
            }

            lock (_gate)
            {
                if (_localAbandonRequested || !ReferenceEquals(_dispatcher, dispatcher) ||
                    !_attachmentInProgress || version == _configurationVersion)
                {
                    return;
                }
            }
        }
    }

    private bool TryAcquireLiveChildDispatchLocked(out IStreamDispatcher? dispatcher)
    {
        dispatcher = null;
        if (_attachmentInProgress || _discarding || _childClosed || _childDetachRequested ||
            _childDetached || _childDetachFinalizing)
        {
            return false;
        }
        return TryAcquireChildDispatchLocked(out dispatcher);
    }

    private bool TryAcquireReplayChildDispatchLocked(IStreamDispatcher dispatcher)
    {
        if (_localAbandonRequested || _childClosed || _childDetached ||
            !ReferenceEquals(_dispatcher, dispatcher))
        {
            return false;
        }
        _activeChildDispatches++;
        return true;
    }

    private bool TryAcquireCompletionDispatchLocked(out IStreamDispatcher? dispatcher)
    {
        dispatcher = null;
        if (_completionForwarded || _childClosed || _childDetached || _childDetachFinalizing ||
            _dispatcher is null)
        {
            return false;
        }

        _completionForwarded = true;
        _activeChildDispatches++;
        dispatcher = _dispatcher;
        return true;
    }

    private bool TryAcquireChildDispatchLocked(out IStreamDispatcher? dispatcher)
    {
        dispatcher = _dispatcher;
        if (dispatcher is null || _childClosed || _childDetached || _childDetachFinalizing)
        {
            dispatcher = null;
            return false;
        }
        _activeChildDispatches++;
        return true;
    }

    private ValueTask DispatchAttachedAcquired(
        IStreamDispatcher dispatcher,
        ReadOnlySequence<byte> payload,
        int encodedByteCount)
    {
        try
        {
            var dispatch = dispatcher is IStreamDispatchLease leased
                ? leased.DispatchAcquiredAsync(payload, encodedByteCount)
                : dispatcher is IStreamConsumptionAwareDispatcher consumptionAware
                    ? consumptionAware.DispatchAsync(payload, encodedByteCount)
                    : dispatcher.DispatchAsync(payload);
            if (dispatch.IsCompletedSuccessfully)
            {
                ReleaseChildDispatch();
                return ValueTask.CompletedTask;
            }
            return AwaitChildDispatchAsync(dispatch);
        }
        catch
        {
            ReleaseChildDispatch();
            throw;
        }
    }

    private async ValueTask AwaitChildDispatchAsync(ValueTask dispatch)
    {
        try { await dispatch.ConfigureAwait(false); }
        finally { ReleaseChildDispatch(); }
    }

    private ValueTask DecodeAndDispatchAcquired(
        IStreamDispatcher dispatcher,
        ReadOnlySequence<byte> payload,
        int encodedByteCount,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload> decoder)
    {
        PreAdmissionDecodedPayload decoded;
        try
        {
            decoded = decoder(payload);
        }
        catch
        {
            ReleaseChildDispatch();
            throw;
        }

        try
        {
            var dispatch = DispatchAttachedAcquired(dispatcher, decoded.Payload, encodedByteCount);
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
        try { await dispatch.ConfigureAwait(false); }
        finally { decoded.Dispose(); }
    }

    private async ValueTask AwaitRetainedCompressedDispatchAsync(
        ValueTask dispatch,
        IRpcByteBufferWriter owner,
        int retainedBytes,
        Action<int> releaseRetainedBytes)
    {
        try { await dispatch.ConfigureAwait(false); }
        finally
        {
            buffers.Return(owner);
            releaseRetainedBytes(retainedBytes);
        }
    }

    private void CompleteAttachedDispatcher(IStreamDispatcher? dispatcher, Exception? exception)
    {
        if (dispatcher is null)
            return;
        try { dispatcher.Complete(exception); }
        finally { ReleaseChildDispatch(); }
    }

    private bool CanRetainLocked(RetentionPolicy policy, int retainedBytes)
        => retainedBytes <= policy.MaxRetainedBytes - _retainedBytes;

    private BufferedItem[] TakeBufferedItemsLocked()
    {
        if (_items.Count == 0)
            return [];
        var items = _items.ToArray();
        _items.Clear();
        return items;
    }

    private TaskCompletionSource? TakeDispatchesDrainedCompletionLocked()
    {
        var completion = _childDispatchesDrained;
        _childDispatchesDrained = null;
        return completion;
    }

    private void ReleaseBufferedItem(BufferedItem item, bool notifyBytesConsumed)
    {
        buffers.Return(item.Owner);
        lock (_gate)
        {
            _retainedBytes -= item.RetainedBytes;
            if (_retainedBytes < 0)
            {
                _retainedBytes += item.RetainedBytes;
                throw new InvalidOperationException("Inbound stream mailbox retained-byte count underflowed.");
            }
        }
        item.ReleaseBytes(item.RetainedBytes);
        if (notifyBytesConsumed)
            NotifyBytesConsumed(item.EncodedByteCount);
    }

    private void DiscardBufferedItems(IEnumerable<BufferedItem> items)
    {
        foreach (var item in items)
            ReleaseBufferedItem(item, notifyBytesConsumed: true);
    }

    private static RetentionPolicy CreateRetentionPolicy(
        Func<int, bool> reserveBytes,
        Action<int> releaseBytes,
        Action capacityExceeded,
        int maxRetainedBytes)
    {
        ArgumentNullException.ThrowIfNull(reserveBytes);
        ArgumentNullException.ThrowIfNull(releaseBytes);
        ArgumentNullException.ThrowIfNull(capacityExceeded);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedBytes);
        return new RetentionPolicy(reserveBytes, releaseBytes, capacityExceeded, maxRetainedBytes);
    }

    private static SharpLinkException CreateElementCapacityException()
        => new(
            SharpLinkErrorCode.ResourceExhausted,
            $"Stream receive buffer exceeded {MaxBufferedElements} elements.");

    private static SharpLinkException CreateRetentionCapacityException(int maxRetainedBytes)
        => new(
            SharpLinkErrorCode.ResourceExhausted,
            $"Deferred stream retention exceeded the stable {maxRetainedBytes}-byte mailbox budget.");

    private static SharpLinkException CreateRetentionReconfigurationCapacityException(int maxRetainedBytes)
        => new(
            SharpLinkErrorCode.ResourceExhausted,
            $"Existing deferred stream retention exceeds the active {maxRetainedBytes}-byte mailbox budget.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyBytesConsumed(int encodedByteCount)
        => _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);

    private sealed record RetentionPolicy(
        Func<int, bool> ReserveBytes,
        Action<int> ReleaseBytes,
        Action CapacityExceeded,
        int MaxRetainedBytes);

    private readonly record struct BufferedItem(
        IRpcByteBufferWriter Owner,
        int RetainedBytes,
        int EncodedByteCount,
        Action<int> ReleaseBytes,
        bool IsCompressed = false);

    private readonly record struct ChildDetachWork(
        IStreamDispatcher Dispatcher,
        IStreamDispatchLease? Lease,
        bool DisposeChild);
}

internal readonly record struct PreAdmissionDecodedPayload(
    ReadOnlySequence<byte> Payload,
    IRpcByteBufferWriter Owner,
    SharpLinkBufferWriterPool Pool) : IDisposable
{
    public void Dispose() => Pool.Return(Owner);
}
