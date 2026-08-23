namespace SharpLink.Runtime;

/// <summary>Provides concurrent request-scoped routing for active RPC streams.</summary>
internal sealed class StreamManager
{
    private StripedLongMap<RequestDispatchers>? _dispatchersByRequestId;
    private readonly RuntimeConcurrencyOptions _concurrencyOptions;
    private readonly Lock _dispatchersInitializationGate = new();
    private readonly Action<long, ushort, int>? _acceptBytes;
    private readonly Action<long, ushort, int>? _bytesConsumed;
    private readonly Action<long, ushort>? _streamCompleted;
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
    {
        ArgumentNullException.ThrowIfNull(concurrencyOptions);
        _concurrencyOptions = concurrencyOptions.CloneValidated();
        _acceptBytes = acceptBytes;
        _bytesConsumed = bytesConsumed;
        _streamCompleted = streamCompleted;
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
        SharpLinkTelemetry.AddActiveStreams(1);
        Interlocked.Increment(ref _activeStreamCount);
        if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
            consumptionAware.SetBytesConsumedCallback(_bytesConsumed, requestId, streamId);
        if (!requestDispatchers.TryRegister(streamId, dispatcher))
        {
            if (dispatcher is IStreamConsumptionAwareDispatcher failedRegistration)
                failedRegistration.SetBytesConsumedCallback(null, 0, 0);
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

        if (requestDispatchers.TryRemove(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                    consumptionAware.SetBytesConsumedCallback(null, 0, 0);
                PublishReceiveTerminal(requestId, streamId, entry);
            }
            finally
            {
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
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

        if (requestDispatchers.TryCompleteRetainedRoute(streamId, exception, out var retainedEntry))
        {
            PublishReceiveTerminal(requestId, streamId, retainedEntry);
            return;
        }

        if (requestDispatchers.TryRemove(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                dispatcher.Complete(exception);
                PublishReceiveTerminal(requestId, streamId, entry);
            }
            finally
            {
                entry.Detach();
                RemoveEmptyRequest(requestId, requestDispatchers);
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

        if (requestDispatchers.TryRemove(streamId, out var entry))
        {
            var dispatcher = entry.Dispatcher;
            SharpLinkTelemetry.AddActiveStreams(-1);
            Interlocked.Decrement(ref _activeStreamCount);
            try
            {
                dispatcher.Complete(exception);
                PublishReceiveTerminal(requestId, streamId, entry);
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
        var dispatchersByRequestId = Volatile.Read(ref _dispatchersByRequestId);
        if (dispatchersByRequestId is null ||
            !dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) ||
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
            if (entry.Dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(null, 0, 0);
            PublishReceiveTerminal(requestId, streamId, entry);
        }
        finally
        {
            entry.Detach();
            RemoveEmptyRequest(requestId, requestDispatchers);
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
            !dispatchersByRequestId.TryRemove(requestId, out var requestDispatchers))
        {
            return;
        }

        List<Exception>? failures = null;
        var completed = requestDispatchers.CompleteAll(exception, ref failures);
        SharpLinkTelemetry.AddActiveStreams(-completed);
        Interlocked.Add(ref _activeStreamCount, -completed);
        ThrowCompletionFailures(failures);
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
        if (dispatchersByRequestId is null)
        {
            dispatch = default;
            return false;
        }

        if (dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) &&
            requestDispatchers.TryGetPreAdmission(streamId, out var preAdmission))
        {
            _acceptBytes?.Invoke(requestId, streamId, originalByteCount);
            dispatch = preAdmission.DispatchCompressedAsync(wirePayload, originalByteCount);
            return true;
        }
        if (dispatchersByRequestId.TryGetValue(requestId, out requestDispatchers) &&
            requestDispatchers.TryGetDiscarding(streamId, out var discarding))
        {
            _acceptBytes?.Invoke(requestId, streamId, originalByteCount);
            dispatch = discarding.DispatchAsync(wirePayload, originalByteCount);
            return true;
        }
        dispatch = default;
        return false;
    }

    private void CompleteTerminatedRegistration(
        long requestId,
        ushort streamId,
        RequestDispatchers requestDispatchers,
        Exception? exception)
    {
        if (!requestDispatchers.TryRemove(streamId, out var entry))
            return;

        SharpLinkTelemetry.AddActiveStreams(-1);
        Interlocked.Decrement(ref _activeStreamCount);
        try
        {
            entry.Dispatcher.Complete(exception);
            PublishReceiveTerminal(requestId, streamId, entry);
        }
        finally
        {
            entry.Detach();
            RemoveEmptyRequest(requestId, requestDispatchers);
        }
    }

    private void PublishReceiveTerminal(long requestId, ushort streamId, DispatcherEntry entry)
    {
        if (entry.TryPublishReceiveTerminal())
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

    private sealed class RequestDispatchers
    {
        private DispatcherEntry? _defaultDispatcher;
        private readonly Lock _gate = new();
        private readonly Dictionary<ushort, DispatcherEntry> _byStreamId = [];

        public bool TryRegister(ushort streamId, IStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var entry = new DispatcherEntry(dispatcher);
                return Interlocked.CompareExchange(ref _defaultDispatcher, entry, null) is null;
            }

            lock (_gate)
            {
                if (_byStreamId.ContainsKey(streamId))
                    return false;
                _byStreamId.Add(streamId, new DispatcherEntry(dispatcher));
                return true;
            }
        }

        public bool TryAttachPreAdmission(
            ushort streamId,
            IStreamDispatcher dispatcher,
            out bool alreadyCompleted)
        {
            alreadyCompleted = false;
            if (streamId == 0)
            {
                var entry = Volatile.Read(ref _defaultDispatcher);
                if (entry?.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                try
                {
                    if (!preAdmission.TryBeginAttach(dispatcher, out alreadyCompleted))
                        return false;
                    preAdmission.FinishAttach(dispatcher);
                    return true;
                }
                finally
                {
                    entry.Release();
                }
            }

            DispatcherEntry? acquiredEntry;
            PreAdmissionStreamDispatcher? acquiredPreAdmission;
            lock (_gate)
            {
                if (!_byStreamId.TryGetValue(streamId, out var entry) ||
                    entry.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                if (!preAdmission.TryBeginAttach(dispatcher, out alreadyCompleted))
                {
                    entry.Release();
                    return false;
                }
                acquiredEntry = entry;
                acquiredPreAdmission = preAdmission;
            }
            try
            {
                acquiredPreAdmission.FinishAttach(dispatcher);
                return true;
            }
            finally
            {
                acquiredEntry.Release();
            }
        }

        public bool TryAbandonInboundRoute(ushort streamId, out bool peerTerminalReceived)
        {
            peerTerminalReceived = false;
            if (streamId == 0)
            {
                var entry = Volatile.Read(ref _defaultDispatcher);
                if (entry?.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                try
                {
                    preAdmission.Abandon(out _);
                    peerTerminalReceived = entry.PeerTerminalReceived;
                    return true;
                }
                finally
                {
                    entry.Release();
                }
            }

            DispatcherEntry? acquiredEntry;
            PreAdmissionStreamDispatcher? acquiredPreAdmission;
            lock (_gate)
            {
                if (!_byStreamId.TryGetValue(streamId, out var entry) ||
                    entry.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    !entry.TryAcquire())
                {
                    return false;
                }
                acquiredEntry = entry;
                acquiredPreAdmission = preAdmission;
            }
            try
            {
                acquiredPreAdmission.Abandon(out _);
                peerTerminalReceived = acquiredEntry.PeerTerminalReceived;
                return true;
            }
            finally
            {
                acquiredEntry.Release();
            }
        }

        public bool TryMarkPeerTerminal(ushort streamId)
        {
            if (streamId == 0)
            {
                var entry = Volatile.Read(ref _defaultDispatcher);
                if (entry is null || !entry.TryAcquire())
                    return false;
                try
                {
                    entry.MarkPeerTerminalReceived();
                    return true;
                }
                finally
                {
                    entry.Release();
                }
            }

            DispatcherEntry? acquiredEntry;
            lock (_gate)
            {
                if (!_byStreamId.TryGetValue(streamId, out var entry) || !entry.TryAcquire())
                    return false;
                acquiredEntry = entry;
            }
            try
            {
                acquiredEntry.MarkPeerTerminalReceived();
                return true;
            }
            finally
            {
                acquiredEntry.Release();
            }
        }

        public bool TryCompleteRetainedRoute(
            ushort streamId,
            Exception? exception,
            out DispatcherEntry entry)
        {
            if (streamId == 0)
            {
                var found = Volatile.Read(ref _defaultDispatcher);
                if (found?.Dispatcher is PreAdmissionStreamDispatcher preAdmission &&
                    preAdmission.TryCompleteAndRetain(exception))
                {
                    entry = found;
                    return true;
                }
                entry = null!;
                return false;
            }

            lock (_gate)
            {
                if (_byStreamId.TryGetValue(streamId, out var found) &&
                    found.Dispatcher is PreAdmissionStreamDispatcher preAdmission &&
                    preAdmission.TryCompleteAndRetain(exception))
                {
                    entry = found;
                    return true;
                }
                entry = null!;
                return false;
            }
        }

        public bool TryGetPreAdmission(
            ushort streamId,
            out PreAdmissionStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var found = Volatile.Read(ref _defaultDispatcher)?.Dispatcher as
                    PreAdmissionStreamDispatcher;
                dispatcher = found!;
                return found is not null;
            }
            lock (_gate)
            {
                var found = _byStreamId.TryGetValue(streamId, out var entry)
                    ? entry.Dispatcher as PreAdmissionStreamDispatcher
                    : null;
                dispatcher = found!;
                return found is not null;
            }
        }

        public bool TryGetDiscarding(
            ushort streamId,
            out DiscardingStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var found = Volatile.Read(ref _defaultDispatcher)?.Dispatcher as
                    DiscardingStreamDispatcher;
                dispatcher = found!;
                return found is not null;
            }
            lock (_gate)
            {
                var found = _byStreamId.TryGetValue(streamId, out var entry)
                    ? entry.Dispatcher as DiscardingStreamDispatcher
                    : null;
                dispatcher = found!;
                return found is not null;
            }
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

        public int CompleteAll(Exception? exception, ref List<Exception>? failures)
        {
            var defaultDispatcher = Interlocked.Exchange(ref _defaultDispatcher, null);
            if (defaultDispatcher is not null)
            {
                defaultDispatcher.Close();
                CompleteEntry(defaultDispatcher, exception, ref failures);
            }
            var count = defaultDispatcher is null ? 0 : 1;

            DispatcherEntry[] entries;
            lock (_gate)
            {
                count += _byStreamId.Count;
                entries = [.. _byStreamId.Values];
                _byStreamId.Clear();
            }
            for (var index = 0; index < entries.Length; index++)
            {
                entries[index].Close();
                CompleteEntry(entries[index], exception, ref failures);
            }
            return count;
        }

        private static void CompleteEntry(
            DispatcherEntry entry,
            Exception? exception,
            ref List<Exception>? failures)
        {
            try
            {
                entry.Dispatcher.Complete(exception);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
                entry.Detach();
            }
            catch (Exception detachException)
            {
                (failures ??= []).Add(detachException);
            }
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
        private int _detached;
        private int _receiveTerminalPublished;
        private int _peerTerminalReceived;
        // Lazily shares the distinct drain/detach completions without growing common entries.
        private DispatcherEntryCompletions? _completions;

        internal DispatcherEntry(IStreamDispatcher dispatcher)
        {
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (dispatcher is IStreamDispatchLease lease)
                lease.BindDispatchState(this);
        }

        internal IStreamDispatcher Dispatcher { get; }

        public bool HasActiveDispatches => (Volatile.Read(ref _state) & CountMask) != 0;

        public bool IsDetached => Volatile.Read(ref _detached) != 0;

        internal bool PeerTerminalReceived => Volatile.Read(ref _peerTerminalReceived) != 0;

        internal void MarkPeerTerminalReceived()
            => Volatile.Write(ref _peerTerminalReceived, 1);

        internal bool TryPublishReceiveTerminal()
            => Interlocked.CompareExchange(ref _receiveTerminalPublished, 1, 0) == 0;

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
                Volatile.Read(ref _completions)?.SignalDispatchesDrained();
                if (IsDetached && Dispatcher is IStreamDispatchLease lease)
                    lease.OnDispatchesDrained();
            }
        }

        public ValueTask WaitForDispatchesDrainedAsync()
        {
            if (!HasActiveDispatches)
                return ValueTask.CompletedTask;

            var completions = GetOrCreateCompletions();
            if (!HasActiveDispatches)
            {
                completions.SignalDispatchesDrained();
                return ValueTask.CompletedTask;
            }

            return completions.WaitForDispatchesDrainedAsync();
        }

        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
        {
            if (IsDetached)
                return ValueTask.CompletedTask;

            var completions = GetOrCreateCompletions();
            if (IsDetached)
            {
                completions.SignalDetached();
                return ValueTask.CompletedTask;
            }

            return completions.WaitForDetachedAsync(cancellationToken);
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
            Volatile.Read(ref _completions)?.SignalDetached();
            if (!HasActiveDispatches && Dispatcher is IStreamDispatchLease lease)
                lease.OnDispatchesDrained();
        }

        private DispatcherEntryCompletions GetOrCreateCompletions()
        {
            var completions = Volatile.Read(ref _completions);
            if (completions is not null)
                return completions;

            var created = new DispatcherEntryCompletions();
            return Interlocked.CompareExchange(ref _completions, created, null) ?? created;
        }

        private sealed class DispatcherEntryCompletions
        {
            private int _dispatchesDrainedSignaled;
            private int _detachedSignaled;
            private TaskCompletionSource? _dispatchesDrainedCompletion;
            private TaskCompletionSource? _detachedCompletion;

            internal void SignalDispatchesDrained()
            {
                if (Interlocked.Exchange(ref _dispatchesDrainedSignaled, 1) == 0)
                    Volatile.Read(ref _dispatchesDrainedCompletion)?.TrySetResult();
            }

            internal void SignalDetached()
            {
                if (Interlocked.Exchange(ref _detachedSignaled, 1) == 0)
                    Volatile.Read(ref _detachedCompletion)?.TrySetResult();
            }

            internal ValueTask WaitForDispatchesDrainedAsync()
            {
                if (Volatile.Read(ref _dispatchesDrainedSignaled) != 0)
                    return ValueTask.CompletedTask;

                var completion = GetOrCreateCompletion(ref _dispatchesDrainedCompletion);
                if (Volatile.Read(ref _dispatchesDrainedSignaled) != 0)
                    completion.TrySetResult();
                return new ValueTask(completion.Task);
            }

            internal ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
            {
                if (Volatile.Read(ref _detachedSignaled) != 0)
                    return ValueTask.CompletedTask;

                var completion = GetOrCreateCompletion(ref _detachedCompletion);
                if (Volatile.Read(ref _detachedSignaled) != 0)
                    completion.TrySetResult();
                return cancellationToken.CanBeCanceled
                    ? new ValueTask(completion.Task.WaitAsync(cancellationToken))
                    : new ValueTask(completion.Task);
            }

            private static TaskCompletionSource GetOrCreateCompletion(
                ref TaskCompletionSource? completion)
            {
                var existing = Volatile.Read(ref completion);
                if (existing is not null)
                    return existing;

                var created = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return Interlocked.CompareExchange(ref completion, created, null) ?? created;
            }
        }
    }
}

internal sealed class DiscardingStreamDispatcher : IStreamConsumptionAwareDispatcher
{
    private Action<long, ushort, int>? _bytesConsumed;
    private long _requestId;
    private ushort _streamId;

    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        => DispatchAsync(payload, Math.Max(1, checked((int)payload.Length)));

    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
    {
        _ = payload;
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
}

internal static class StreamManagerTestHooks
{
    [ThreadStatic]
    private static Action? s_beforeRoutingMapInitialize;

    internal static Action? BeforeRoutingMapInitialize
    {
        get => s_beforeRoutingMapInitialize;
        set => s_beforeRoutingMapInitialize = value;
    }
}
