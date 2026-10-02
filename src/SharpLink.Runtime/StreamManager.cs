namespace SharpLink.Runtime;

/// <summary>Provides concurrent request-scoped routing for active RPC streams.</summary>
internal sealed partial class StreamManager
{
    private StripedLongMap<RequestDispatchers>? _dispatchersByRequestId;
    private readonly RuntimeConcurrencyOptions _concurrencyOptions;
    private readonly Lock _dispatchersInitializationGate = new();
    private readonly Action<long, ushort, int>? _acceptBytes;
    private readonly Action<long, ushort, int>? _bytesConsumed;
    private readonly Func<long, ushort, StreamFlowController.ResolvedReceiveCreditLease>? _resolveReceiveCreditLease;
    private readonly ResolvedStreamBytesCallback? _acceptResolvedBytes;
    private readonly ResolvedStreamBytesCallback? _resolvedBytesConsumed;
    private readonly Action<long, ushort>? _streamCompleted;
    private readonly Action<StreamFlowController.ResolvedReceiveCreditLease>? _resolvedStreamCompleted;
    private readonly int _maxActiveStreams;
    private readonly Action<Exception>? _activeStreamCapacityExceeded;
    private long _droppedStreamFrames;
    private int _activeStreamCount;
    private Termination? _termination;

    /// <summary>Creates a stream manager with default concurrency settings.</summary>
    internal StreamManager() : this(new RuntimeConcurrencyOptions())
    {
    }

    /// <summary>Creates a stream manager with explicit concurrency settings.</summary>
    /// <param name="concurrencyOptions">The stripe and sizing policy for active stream lookup.</param>
    internal StreamManager(RuntimeConcurrencyOptions concurrencyOptions)
        : this(concurrencyOptions, null, null, null)
    {
    }

    internal StreamManager(
        RuntimeConcurrencyOptions concurrencyOptions,
        Action<long, ushort, int>? acceptBytes,
        Action<long, ushort, int>? bytesConsumed,
        Action<long, ushort>? streamCompleted)
        : this(
            concurrencyOptions,
            acceptBytes,
            bytesConsumed,
            streamCompleted,
            int.MaxValue,
            activeStreamCapacityExceeded: null)
    {
    }

    internal StreamManager(
        RuntimeConcurrencyOptions concurrencyOptions,
        Action<long, ushort, int>? acceptBytes,
        Action<long, ushort, int>? bytesConsumed,
        Action<long, ushort>? streamCompleted,
        int maxActiveStreams,
        Action<Exception>? activeStreamCapacityExceeded,
        Func<long, ushort, StreamFlowController.ResolvedReceiveCreditLease>? resolveReceiveCreditLease = null,
        ResolvedStreamBytesCallback? acceptResolvedBytes = null,
        ResolvedStreamBytesCallback? resolvedBytesConsumed = null,
        Action<StreamFlowController.ResolvedReceiveCreditLease>? resolvedStreamCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(concurrencyOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActiveStreams);
        _concurrencyOptions = concurrencyOptions.CloneValidated();
        _acceptBytes = acceptBytes;
        _bytesConsumed = bytesConsumed;
        _resolveReceiveCreditLease = resolveReceiveCreditLease;
        _acceptResolvedBytes = acceptResolvedBytes;
        _resolvedBytesConsumed = resolvedBytesConsumed;
        _streamCompleted = streamCompleted;
        _resolvedStreamCompleted = resolvedStreamCompleted;
        _maxActiveStreams = maxActiveStreams;
        _activeStreamCapacityExceeded = activeStreamCapacityExceeded;
    }

    /// <inheritdoc />
    internal void Register(long requestId, IStreamDispatcher dispatcher) => Register(requestId, 0, dispatcher);

    /// <inheritdoc />
    internal void Register(long requestId, ushort streamId, IStreamDispatcher dispatcher)
        => Register(requestId, streamId, dispatcher, ignoreExisting: false);

    private void Register(
        long requestId,
        ushort streamId,
        IStreamDispatcher dispatcher,
        bool ignoreExisting)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var termination = Volatile.Read(ref _termination);
        if (termination is not null)
        {
            dispatcher.Complete(termination.Exception);
            return;
        }

        var requestDispatchers = GetOrCreateDispatchersByRequestId().GetOrAdd(
            requestId,
            static _ => new RequestDispatchers());
        if (dispatcher is not DiscardingStreamDispatcher &&
            requestDispatchers.TryAttachPreAdmission(streamId, dispatcher, out var alreadyCompleted))
        {
            if (alreadyCompleted)
                Unregister(requestId, streamId);
            return;
        }

        var activeStreamCount = Interlocked.Increment(ref _activeStreamCount);
        if (activeStreamCount > _maxActiveStreams)
        {
            Interlocked.Decrement(ref _activeStreamCount);
            var exception = new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Active inbound stream routes exceeded the per-connection limit of {_maxActiveStreams}.");
            try
            {
                dispatcher.Complete(exception);
            }
            finally
            {
                RemoveEmptyRequest(requestId, requestDispatchers);
                _activeStreamCapacityExceeded?.Invoke(exception);
            }
            if (ignoreExisting)
                return;
            throw exception;
        }

        SharpLinkTelemetry.AddActiveStreams(1);
        var receiveCreditLease = default(StreamFlowController.ResolvedReceiveCreditLease);
        try
        {
            if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
            {
                consumptionAware.SetBytesConsumedCallback(_bytesConsumed, requestId, streamId);
                if (_resolveReceiveCreditLease is not null && _resolvedBytesConsumed is not null)
                {
                    receiveCreditLease = _resolveReceiveCreditLease(requestId, streamId);
                    if (receiveCreditLease.IsResolved)
                    {
                        _ = consumptionAware.TrySetResolvedBytesConsumedCallback(
                            _resolvedBytesConsumed,
                            in receiveCreditLease);
                    }
                }
            }
        }
        catch
        {
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            RemoveEmptyRequest(requestId, requestDispatchers);
            throw;
        }

        if (!requestDispatchers.TryRegister(streamId, dispatcher, receiveCreditLease))
        {
            ClearBytesConsumedCallback(dispatcher);
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            RemoveEmptyRequest(requestId, requestDispatchers);
            if (ignoreExisting)
                return;
            throw new InvalidOperationException("The stream is already registered.");
        }

        termination = Volatile.Read(ref _termination);
        if (termination is not null)
        {
            CompleteTerminatedRegistration(
                requestId,
                streamId,
                requestDispatchers,
                termination.Exception);
        }
    }

    /// <inheritdoc />
    internal void Unregister(long requestId) => Unregister(requestId, 0);

    /// <inheritdoc />
    internal void Unregister(long requestId, ushort streamId)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
        {
            return;
        }

        if (requestDispatchers.TryBeginRetirement(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                // Keep the closed route key visible until its own generation has published
                // receive terminal. A racing same-key registration may not resolve the
                // still-live state and then replace this route before retirement.
                if (dispatcher is not PreAdmissionStreamDispatcher)
                    ClearBytesConsumedCallback(dispatcher);
            }
            finally
            {
                try
                {
                    PublishReceiveTerminal(requestId, streamId, entry);
                }
                finally
                {
                    _ = requestDispatchers.FinishRetirement(streamId, entry);
                    entry.Detach();
                    RemoveEmptyRequest(requestId, requestDispatchers);
                }
            }
        }
    }

    /// <inheritdoc />
    internal ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload)
        => DispatchChunkAsync(requestId, 0, payload);

    /// <inheritdoc />
    internal ValueTask DispatchChunkAsync(long requestId, ushort streamId, ReadOnlySequence<byte> payload)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is not null &&
            dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) &&
            requestDispatchers.TryAcquire(streamId, out var entry))
        {
            try
            {
                ThrowIfPeerTerminal(entry);
                var dispatcher = entry.Dispatcher;
                var encodedByteCount = Math.Max(1, checked((int)payload.Length));
                if ((_acceptBytes is not null ||
                        _acceptResolvedBytes is not null && entry.ReceiveCreditLease.IsResolved) &&
                    dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                {
                    if (_acceptResolvedBytes is not null && entry.ReceiveCreditLease.IsResolved)
                    {
                        var receiveCreditLease = entry.ReceiveCreditLease;
                        _acceptResolvedBytes(in receiveCreditLease, encodedByteCount);
                    }
                    else
                        _acceptBytes?.Invoke(requestId, streamId, encodedByteCount);
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

    private static void ThrowIfPeerTerminal(DispatcherEntry entry)
    {
        if (entry.PeerTerminalReceived)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "StreamData was received after the peer completed the stream.");
        }
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

    /// <inheritdoc />
    internal void CompleteStream(long requestId, bool isError, string? msg)
    {
        CompleteStream(requestId, 0, CreateCompletionException(isError, msg));
    }

    /// <inheritdoc />
    internal void CompleteStream(long requestId, ushort streamId, bool isError, string? msg)
    {
        CompleteStream(requestId, streamId, CreateCompletionException(isError, msg));
    }

    /// <inheritdoc />
    internal void CompleteAll(bool isError, string? msg)
    {
        CompleteAll(CreateCompletionException(isError, msg));
    }

    /// <inheritdoc />
    internal void CompleteStream(long requestId, Exception? exception)
    {
        CompleteStream(requestId, 0, exception);
    }

    /// <inheritdoc />
    internal void CompleteStream(long requestId, ushort streamId, Exception? exception)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
        {
            return;
        }

        if (requestDispatchers.TryCompleteRetainedRoute(streamId, exception, out _))
            return;

        if (requestDispatchers.TryBeginRetirement(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                dispatcher.Complete(exception);
            }
            finally
            {
                try
                {
                    PublishReceiveTerminal(requestId, streamId, entry);
                }
                finally
                {
                    _ = requestDispatchers.FinishRetirement(streamId, entry);
                    entry.Detach();
                    RemoveEmptyRequest(requestId, requestDispatchers);
                }
            }
        }
    }

    /// <summary>
    /// Records an actual peer StreamComplete independently from local completion/error state.
    /// A retained OneWay route may stay registered after this point so local abandonment can
    /// dispose its typed child, while receive-flow terminal state is published immediately.
    /// </summary>
    internal void CompletePeerStream(long requestId, ushort streamId, Exception? exception)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) ||
            !requestDispatchers.TryMarkPeerTerminal(streamId))
        {
            return;
        }

        if (requestDispatchers.TryCompleteRetainedRoute(streamId, exception, out var retainedEntry))
        {
            PublishReceiveTerminal(requestId, streamId, retainedEntry);
            return;
        }

        if (requestDispatchers.TryBeginRetirement(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                dispatcher.Complete(exception);
            }
            finally
            {
                try
                {
                    PublishReceiveTerminal(requestId, streamId, entry);
                }
                finally
                {
                    _ = requestDispatchers.FinishRetirement(streamId, entry);
                    entry.Detach();
                    RemoveEmptyRequest(requestId, requestDispatchers);
                }
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
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) ||
            !requestDispatchers.TryBeginRetirement(streamId, out var entry))
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
            try
            {
                PublishReceiveTerminal(requestId, streamId, entry);
            }
            finally
            {
                _ = requestDispatchers.FinishRetirement(streamId, entry);
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
            }
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
        await entry.WaitForDispatchesDrainedAsync().ConfigureAwait(false);
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
            ClearBytesConsumedCallback(entry.Dispatcher);
        }
        finally
        {
            try
            {
                PublishReceiveTerminal(requestId, streamId, entry);
            }
            finally
            {
                _ = requestDispatchers.FinishRetirement(streamId, entry);
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
            }
        }
    }

    /// <inheritdoc />
    internal void CompleteAll(Exception? exception)
    {
        var termination = new Termination(exception);
        if (Interlocked.CompareExchange(ref _termination, termination, null) is not null)
            return;

        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null)
            return;

        List<Exception>? failures = null;
        var completed = 0;
        foreach (var requestDispatchers in dispatchersByRequestId.DrainValues())
            completed += requestDispatchers.CompleteAll(exception, ref failures);
        SharpLinkTelemetry.AddActiveStreams(-completed);
        Interlocked.Add(ref _activeStreamCount, -completed);
        ThrowCompletionFailures(failures);
    }

    internal void CompleteRequestStreams(long requestId, Exception? exception)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
        {
            return;
        }

        var entries = requestDispatchers.BeginDrain();
        if (entries.Length == 0)
        {
            RemoveEmptyRequest(requestId, requestDispatchers);
            return;
        }

        SharpLinkTelemetry.AddActiveStreams(-entries.Length);
        Interlocked.Add(ref _activeStreamCount, -entries.Length);
        List<Exception>? failures = null;
        FinalizeRequestEntries(requestId, entries, requestDispatchers, exception, ref failures);
        RemoveEmptyRequest(requestId, requestDispatchers);
        ThrowCompletionFailures(failures);
    }

    /// <summary>
    /// Removes every receive stream owned by one request and waits for StreamData dispatches that
    /// acquired their entries before removal. Callers may release request-owned Codec/module state
    /// only after this barrier completes.
    /// </summary>
    internal ValueTask CompleteRequestStreamsAfterDispatchesAsync(long requestId, Exception? exception)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
            return ValueTask.CompletedTask;

        var entries = requestDispatchers.BeginDrain();
        if (entries.Length == 0)
        {
            RemoveEmptyRequest(requestId, requestDispatchers);
            return ValueTask.CompletedTask;
        }

        SharpLinkTelemetry.AddActiveStreams(-entries.Length);
        Interlocked.Add(ref _activeStreamCount, -entries.Length);
        List<Exception>? failures = null;
        for (var index = 0; index < entries.Length; index++)
        {
            try
            {
                entries[index].Entry.Dispatcher.Complete(exception);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
        }

        if (entries.All(static item => !item.Entry.HasActiveDispatches))
        {
            FinalizeRequestDrain(requestId, requestDispatchers, entries, ref failures);
            ThrowCompletionFailures(failures);
            return ValueTask.CompletedTask;
        }

        return AwaitRequestDispatchesAndFinalizeAsync(requestId, requestDispatchers, entries, failures);
    }

    private async ValueTask AwaitRequestDispatchesAndFinalizeAsync(
        long requestId,
        RequestDispatchers requestDispatchers,
        RequestDrainEntry[] entries,
        List<Exception>? failures)
    {
        for (var index = 0; index < entries.Length; index++)
            await entries[index].Entry.WaitForDispatchesDrainedAsync().ConfigureAwait(false);

        FinalizeRequestDrain(requestId, requestDispatchers, entries, ref failures);
        ThrowCompletionFailures(failures);
    }

    private void FinalizeRequestDrain(
        long requestId,
        RequestDispatchers requestDispatchers,
        RequestDrainEntry[] entries,
        ref List<Exception>? failures)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var item = entries[index];
            try
            {
                ClearBytesConsumedCallback(item.Entry.Dispatcher);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
                PublishReceiveTerminal(requestId, item.StreamId, item.Entry);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
                _ = requestDispatchers.FinishRetirement(item.StreamId, item.Entry);
                item.Entry.Detach();
            }
            catch (Exception detachException)
            {
                (failures ??= []).Add(detachException);
            }
        }
        RemoveEmptyRequest(requestId, requestDispatchers);
    }

    private void FinalizeRequestEntries(
        long requestId,
        RequestDrainEntry[] entries,
        RequestDispatchers requestDispatchers,
        Exception? exception,
        ref List<Exception>? failures)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var item = entries[index];
            try
            {
                item.Entry.Dispatcher.Complete(exception);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
                ClearBytesConsumedCallback(item.Entry.Dispatcher);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
                PublishReceiveTerminal(requestId, item.StreamId, item.Entry);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
                _ = requestDispatchers.FinishRetirement(item.StreamId, item.Entry);
                item.Entry.Detach();
            }
            catch (Exception detachException)
            {
                (failures ??= []).Add(detachException);
            }
        }
    }

    private static void ThrowCompletionFailures(List<Exception>? failures)
    {
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    internal void ReservePreAdmissionStreams(
        long requestId,
        int streamCount,
        SharpLinkBufferWriterPool buffers,
        Func<int, bool> reserveBytes,
        Action<int> releaseBytes,
        Action capacityExceeded,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null,
        bool retainUntilLocalCompletion = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamCount);
        for (var index = 1; index <= streamCount; index++)
        {
            Register(
                requestId,
                checked((ushort)index),
                new PreAdmissionStreamDispatcher(
                    buffers,
                    reserveBytes,
                    releaseBytes,
                    capacityExceeded,
                    decodeCompressed,
                    retainUntilLocalCompletion));
        }
    }

    /// <summary>
    /// Transitions already-installed inbound routes to discard mode without creating a route when
    /// no stable route exists. This is the local-completion path for OneWay calls.
    /// </summary>
    internal void AbandonExistingRequestStreams(long requestId, int streamCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(streamCount);
        for (var index = 1; index <= streamCount; index++)
            _ = TryAbandonExistingStream(requestId, checked((ushort)index));
    }

    internal void DrainRejectedRequestStreams(long requestId, int streamCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(streamCount);
        for (var index = 1; index <= streamCount; index++)
        {
            var streamId = checked((ushort)index);
            if (TryAbandonExistingStream(requestId, streamId))
                continue;
            Register(
                requestId,
                streamId,
                new DiscardingStreamDispatcher(),
                ignoreExisting: true);
        }
    }

    private bool TryAbandonExistingStream(long requestId, ushort streamId)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) ||
            !requestDispatchers.TryAbandonInboundRoute(streamId, out var peerTerminalReceived))
        {
            return false;
        }

        if (peerTerminalReceived)
            Unregister(requestId, streamId);
        return true;
    }

    internal bool TryDispatchPreAdmissionCompressed(
        long requestId,
        ushort streamId,
        ReadOnlySequence<byte> wirePayload,
        int originalByteCount,
        out ValueTask dispatch)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) ||
            !requestDispatchers.TryAcquire(streamId, out var entry))
        {
            dispatch = default;
            return false;
        }

        try
        {
            ThrowIfPeerTerminal(entry);
            if (entry.Dispatcher is PreAdmissionStreamDispatcher preAdmission)
            {
                if (_acceptResolvedBytes is not null && entry.ReceiveCreditLease.IsResolved)
                {
                    var receiveCreditLease = entry.ReceiveCreditLease;
                    _acceptResolvedBytes(in receiveCreditLease, originalByteCount);
                }
                else
                    _acceptBytes?.Invoke(requestId, streamId, originalByteCount);
                dispatch = CompleteDispatch(
                    entry,
                    preAdmission.DispatchCompressedAsync(wirePayload, originalByteCount));
                return true;
            }
            if (entry.Dispatcher is DiscardingStreamDispatcher discarding)
            {
                if (_acceptResolvedBytes is not null && entry.ReceiveCreditLease.IsResolved)
                {
                    var receiveCreditLease = entry.ReceiveCreditLease;
                    _acceptResolvedBytes(in receiveCreditLease, originalByteCount);
                }
                else
                    _acceptBytes?.Invoke(requestId, streamId, originalByteCount);
                dispatch = CompleteDispatch(
                    entry,
                    discarding.DispatchAsync(wirePayload, originalByteCount));
                return true;
            }

            entry.Release();
            dispatch = default;
            return false;
        }
        catch
        {
            entry.Release();
            throw;
        }
    }

    private void CompleteTerminatedRegistration(
        long requestId,
        ushort streamId,
        RequestDispatchers requestDispatchers,
        Exception? exception)
    {
        if (!requestDispatchers.TryBeginRetirement(streamId, out var entry))
            return;

        SharpLinkTelemetry.AddActiveStreams(-1);
        Interlocked.Decrement(ref _activeStreamCount);
        try
        {
            entry.Dispatcher.Complete(exception);
        }
        finally
        {
            try
            {
                PublishReceiveTerminal(requestId, streamId, entry);
            }
            finally
            {
                _ = requestDispatchers.FinishRetirement(streamId, entry);
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
            }
        }
    }

    private void PublishReceiveTerminal(long requestId, ushort streamId, DispatcherEntry entry)
    {
        if (!entry.TryPublishReceiveTerminal())
            return;

        StreamManagerTestHooks.BeforeReceiveTerminalPublish?.Invoke(requestId, streamId);
        if (_resolvedStreamCompleted is not null && entry.ReceiveCreditLease.IsResolved)
            _resolvedStreamCompleted(entry.ReceiveCreditLease);
        else
            _streamCompleted?.Invoke(requestId, streamId);
    }

    private StripedLongMap<RequestDispatchers> GetOrCreateDispatchersByRequestId()
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is not null)
            return dispatchersByRequestId;

        lock (_dispatchersInitializationGate)
        {
            dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
            if (dispatchersByRequestId is not null)
                return dispatchersByRequestId;

            StreamManagerTestHooks.BeforeRoutingMapInitialize?.Invoke();
            dispatchersByRequestId = new StripedLongMap<RequestDispatchers>(_concurrencyOptions);
            Volatile.Write(ref _dispatchersByRequestId, dispatchersByRequestId);
            return dispatchersByRequestId;
        }
    }

    internal long DroppedStreamFrames => Volatile.Read(ref _droppedStreamFrames);
    internal int ActiveStreamCount => Volatile.Read(ref _activeStreamCount);
    internal bool IsTerminated => Volatile.Read(ref _termination) is not null;
    internal bool HasMaterializedRoutingState => Volatile.Read(ref _dispatchersByRequestId) is not null;

    /// <summary>
    /// Validates business-stream accounting at a lifecycle or test boundary. Dispatcher-entry
    /// dispatch leases have a separate encoded state machine and are intentionally not folded
    /// into this count.
    /// </summary>
    internal void AssertAccountingInvariant()
    {
        if (ActiveStreamCount < 0)
            throw new InvalidOperationException("Stream manager active stream count became negative.");
    }

    private static void ClearBytesConsumedCallback(IStreamDispatcher dispatcher)
    {
        if (dispatcher is not IStreamConsumptionAwareDispatcher consumptionAware)
            return;

        var emptyLease = default(StreamFlowController.ResolvedReceiveCreditLease);
        _ = consumptionAware.TrySetResolvedBytesConsumedCallback(null, in emptyLease);
        consumptionAware.SetBytesConsumedCallback(null, 0, 0);
    }

    private void RemoveEmptyRequest(long requestId, RequestDispatchers requestDispatchers)
    {
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (requestDispatchers.IsEmpty && dispatchersByRequestId is not null)
            dispatchersByRequestId.TryRemove(requestId, requestDispatchers);
    }

    private static Exception? CreateCompletionException(bool isError, string? msg)
    {
        if (!isError)
            return null;

        var message = string.IsNullOrWhiteSpace(msg) ? "Remote Error" : msg;
        return new SharpLinkException(SharpLinkErrorCode.RemoteError, message);
    }

    private sealed class Termination(Exception? exception)
    {
        internal Exception? Exception { get; } = exception;
    }


}

internal sealed class DiscardingStreamDispatcher : IStreamConsumptionAwareDispatcher
{
    private Action<long, ushort, int>? _bytesConsumed;
    private ResolvedStreamBytesCallback? _resolvedBytesConsumed;
    private StreamFlowController.ResolvedReceiveCreditLease _receiveCreditLease;
    private long _requestId;
    private ushort _streamId;

    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        => DispatchAsync(payload, Math.Max(1, checked((int)payload.Length)));

    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
    {
        _ = payload;
        if (_resolvedBytesConsumed is { } resolved)
            resolved(in _receiveCreditLease, encodedByteCount);
        else
            _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
        return ValueTask.CompletedTask;
    }

    public void Complete(bool isError, string? errorMessage)
    {
        _ = isError;
        _ = errorMessage;
    }

    public void Complete(Exception? exception) => _ = exception;

    public void SetBytesConsumedCallback(
        Action<long, ushort, int>? callback,
        long requestId,
        ushort streamId)
    {
        _bytesConsumed = callback;
        _requestId = requestId;
        _streamId = streamId;
    }

    public bool TrySetResolvedBytesConsumedCallback(
        ResolvedStreamBytesCallback? callback,
        in StreamFlowController.ResolvedReceiveCreditLease lease)
    {
        _resolvedBytesConsumed = callback;
        _receiveCreditLease = lease;
        return true;
    }
}

internal static class StreamManagerTestHooks
{
    [ThreadStatic]
    private static Action? s_beforeRoutingMapInitialize;
    [ThreadStatic]
    private static Action<long, ushort>? s_beforeReceiveTerminalPublish;

    internal static Action<long, ushort>? BeforeReceiveTerminalPublish
    {
        get => s_beforeReceiveTerminalPublish;
        set => s_beforeReceiveTerminalPublish = value;
    }

    internal static Action? BeforeRoutingMapInitialize
    {
        get => s_beforeRoutingMapInitialize;
        set => s_beforeRoutingMapInitialize = value;
    }
}
