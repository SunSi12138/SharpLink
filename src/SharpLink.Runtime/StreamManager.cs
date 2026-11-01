namespace SharpLink.Runtime;

/// <summary>Provides concurrent request-scoped routing for active RPC streams.</summary>
internal sealed partial class StreamManager
{
    private StripedLongMap<RequestDispatchers>? _dispatchersByRequestId;
    private readonly RuntimeConcurrencyOptions _concurrencyOptions;
    private readonly Lock _dispatchersInitializationGate = new();
    private readonly Action<long, ushort, int>? _acceptBytes;
    private readonly Action<long, ushort, int>? _bytesConsumed;
    private readonly Action<long, ushort>? _streamCompleted;
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
        Action<Exception>? activeStreamCapacityExceeded)
    {
        ArgumentNullException.ThrowIfNull(concurrencyOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActiveStreams);
        _concurrencyOptions = concurrencyOptions.CloneValidated();
        _acceptBytes = acceptBytes;
        _bytesConsumed = bytesConsumed;
        _streamCompleted = streamCompleted;
        _maxActiveStreams = maxActiveStreams;
        _activeStreamCapacityExceeded = activeStreamCapacityExceeded;
    }

    /// <inheritdoc />
    internal void Register(long requestId, ushort streamId, IStreamDispatcher dispatcher)
        => Register(requestId, streamId, dispatcher, ignoreExisting: false);

    private bool Register(
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
            return false;
        }

        var requestDispatchers = GetOrCreateDispatchersByRequestId().GetOrAdd(
            requestId,
            static _ => new RequestDispatchers());
        if (dispatcher is not DiscardingStreamDispatcher &&
            requestDispatchers.TryAttachPreAdmission(streamId, dispatcher, out var alreadyCompleted))
        {
            if (alreadyCompleted && dispatcher is not PreAdmissionStreamDispatcher)
                Unregister(requestId, streamId);
            return !alreadyCompleted && Volatile.Read(ref _termination) is null;
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
                return false;
            throw exception;
        }

        var installed = false;
        try
        {
            SharpLinkTelemetry.AddActiveStreams(1);
            if (dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(_bytesConsumed, requestId, streamId);
            installed = requestDispatchers.TryRegister(streamId, dispatcher);
            if (!installed)
                throw new InvalidOperationException("The stream is already registered.");
        }
        catch (Exception failure)
        {
            // TryRegister has not published on this path. Scalar accounting and each
            // independent callback are unwound even when telemetry/cleanup throws.
            Interlocked.Decrement(ref _activeStreamCount);
            try { SharpLinkTelemetry.AddActiveStreams(-1); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
            if (dispatcher is IStreamConsumptionAwareDispatcher failedRegistration)
            {
                try { failedRegistration.SetBytesConsumedCallback(null, 0, 0); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
            }
            try { dispatcher.Complete(failure); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
            RemoveEmptyRequest(requestId, requestDispatchers);
            if (ignoreExisting && failure is InvalidOperationException) return false;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        termination = Volatile.Read(ref _termination);
        if (termination is not null)
        {
            RemoveExactRegistration(requestId, streamId, dispatcher);
            return false;
        }
        return true;
    }

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
            FinalizeRemovedEntry(requestId, streamId, requestDispatchers, entry,
                completeDispatcher: false, exception: null, clearConsumptionCallback: true);
    }

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

        if (requestDispatchers.TryRemove(streamId, out var entry))
            FinalizeRemovedEntry(requestId, streamId, requestDispatchers, entry,
                completeDispatcher: true, exception, clearConsumptionCallback: false);
    }

    /// <summary>
    /// Records an actual peer StreamComplete independently from local completion/error state.
    /// A retained OneWay route may stay registered after this point so local abandonment can
    /// dispose its typed child, while receive-flow terminal state is published immediately.
    /// </summary>
    internal void CompletePeerStream(long requestId, ushort streamId, Exception? exception)
    {
        var map = Volatile.Read(ref _dispatchersByRequestId);
        if (map is null || !map.TryGetValue(requestId, out var routes) ||
            !routes.TryAcquire(streamId, out var entry)) return;
        Exception? failure = null;
        var retained = false;
        var completionFailed = false;
        entry.MarkPeerTerminalReceived();
        try
        {
            // Exact captured mailbox, outside the route-map gate. A throwing typed Complete
            // does not suppress receive-terminal publication or lose its indexed cleanup owner.
            if (entry.Dispatcher is PreAdmissionStreamDispatcher mailbox)
                retained = mailbox.TryCompleteAndRetain(exception);
        }
        catch (Exception error) { failure = error; completionFailed = true; }
        if (!retained && !completionFailed && routes.TryRemoveExact(streamId, entry.Dispatcher, out var removed))
        {
            try { FinalizeRemovedEntry(requestId, streamId, routes, removed,
                completeDispatcher: true, exception, clearConsumptionCallback: false); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        try { PublishReceiveTerminal(requestId, streamId, entry); } // Idempotent if removal already published it.
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        try { entry.Release(); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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

        var ownsDetachOutcome = entry.TryClaimJoinedDetach();
        SharpLinkTelemetry.AddActiveStreams(-1);
        Interlocked.Decrement(ref _activeStreamCount);
        Exception? failure = ownsDetachOutcome ? null
            : new InvalidOperationException("Removed stream entry already has a detach owner.");
        try { entry.Dispatcher.Complete(exception); }
        catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }

        // Removal closes acquisition. Even a throwing terminal callback cannot surrender
        // this exact entry while a previously acquired dispatch still owns it.
        if (entry.HasActiveDispatches)
            return AwaitDispatchesAndFinalizeAsync(
                requestId, streamId, requestDispatchers, entry, failure, ownsDetachOutcome);

        return FinalizeLocallyTerminatedStream(
            requestId, streamId, requestDispatchers, entry, failure, ownsDetachOutcome);
    }

    private async ValueTask AwaitDispatchesAndFinalizeAsync(
        long requestId,
        ushort streamId,
        RequestDispatchers requestDispatchers,
        DispatcherEntry entry,
        Exception? failure,
        bool ownsDetachOutcome)
    {
        try { await entry.WaitForDispatchesDrainedAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
        await FinalizeLocallyTerminatedStream(
            requestId, streamId, requestDispatchers, entry, failure, ownsDetachOutcome).ConfigureAwait(false);
    }

    private ValueTask FinalizeLocallyTerminatedStream(
        long requestId,
        ushort streamId,
        RequestDispatchers requestDispatchers,
        DispatcherEntry entry,
        Exception? failure,
        bool ownsDetachOutcome)
    {
        try
        {
            if (entry.Dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                consumptionAware.SetBytesConsumedCallback(null, 0, 0);
        }
        catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
        try { PublishReceiveTerminal(requestId, streamId, entry); }
        catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
        try { entry.Detach(); }
        catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
        try { RemoveEmptyRequest(requestId, requestDispatchers); }
        catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
        var callback = entry.WaitForDetachCallbackAsync();
        if (!callback.IsCompletedSuccessfully)
            return AwaitDetachCallbackAsync(callback, failure, ownsDetachOutcome);
        if (ownsDetachOutcome) failure = PhysicalTaskFailureException.CombineRaw(failure, callback.Result);
        return failure is null ? ValueTask.CompletedTask
            : ValueTask.FromException(PhysicalTaskFailureException.From(failure));
    }

    private static async ValueTask AwaitDetachCallbackAsync(
        ValueTask<Exception?> callback, Exception? failure, bool ownsDetachOutcome)
    {
        var callbackFailure = await callback.ConfigureAwait(false);
        if (ownsDetachOutcome) failure = PhysicalTaskFailureException.CombineRaw(failure, callbackFailure);
        if (failure is not null) throw PhysicalTaskFailureException.From(failure);
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
        Interlocked.Add(ref _activeStreamCount, -completed);
        try { SharpLinkTelemetry.AddActiveStreams(-completed); }
        catch (Exception cleanup) { (failures ??= []).Add(cleanup); }
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
        Interlocked.Add(ref _activeStreamCount, -completed);
        try { SharpLinkTelemetry.AddActiveStreams(-completed); }
        catch (Exception cleanup) { (failures ??= []).Add(cleanup); }
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
            !dispatchersByRequestId.TryRemove(requestId, out var requestDispatchers))
            return ValueTask.CompletedTask;

        var entries = requestDispatchers.TakeAllForDrain();
        if (entries.Length == 0)
            return ValueTask.CompletedTask;

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
            FinalizeRequestDrain(requestId, entries, ref failures);
            ThrowCompletionFailures(failures);
            return ValueTask.CompletedTask;
        }

        return AwaitRequestDispatchesAndFinalizeAsync(requestId, entries, failures);
    }

    private async ValueTask AwaitRequestDispatchesAndFinalizeAsync(
        long requestId,
        RequestDrainEntry[] entries,
        List<Exception>? failures)
    {
        for (var index = 0; index < entries.Length; index++)
            await entries[index].Entry.WaitForDispatchesDrainedAsync().ConfigureAwait(false);

        FinalizeRequestDrain(requestId, entries, ref failures);
        ThrowCompletionFailures(failures);
    }

    private void FinalizeRequestDrain(
        long requestId,
        RequestDrainEntry[] entries,
        ref List<Exception>? failures)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var item = entries[index];
            try
            {
                if (item.Entry.Dispatcher is IStreamConsumptionAwareDispatcher consumptionAware)
                    consumptionAware.SetBytesConsumedCallback(null, 0, 0);
                _streamCompleted?.Invoke(requestId, item.StreamId);
            }
            catch (Exception completionException)
            {
                (failures ??= []).Add(completionException);
            }
            try
            {
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
        long requestId, int streamCount, SharpLinkBufferWriterPool buffers,
        Func<int, bool> reserveBytes, Action<int> releaseBytes, Action capacityExceeded,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null,
        bool retainUntilLocalCompletion = false)
    {
        if (!TryReservePreAdmissionStreams(requestId, streamCount, buffers, reserveBytes,
                releaseBytes, capacityExceeded, decodeCompressed, retainUntilLocalCompletion))
            throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                "Inbound stream route reservation lost its terminal boundary.");
    }

    // Whole declared-domain success, never inferred from a void registration loop. All
    // callbacks run before the call terminal/ACK gate. The temporary reference array costs
    // at most 127 slots and proves exact rollback; it is not a new persistent owner/quota.
    internal bool TryReservePreAdmissionStreams(
        long requestId, int streamCount, SharpLinkBufferWriterPool buffers,
        Func<int, bool> reserveBytes, Action<int> releaseBytes, Action capacityExceeded,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null,
        bool retainUntilLocalCompletion = false, int maxRetainedBytes = int.MaxValue,
        Action<ushort>? streamCapacityExceeded = null)
    {
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (streamCount is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(streamCount));
        var attempted = new PreAdmissionStreamDispatcher?[streamCount];
        Exception? failure = null;
        var succeeded = false;
        try
        {
            for (var index = 0; index < streamCount; index++)
            {
                var streamId = checked((ushort)(index + 1));
                Action onCapacity = streamCapacityExceeded is null ? capacityExceeded : () => streamCapacityExceeded(streamId);
                var dispatcher = new PreAdmissionStreamDispatcher(buffers, reserveBytes,
                    releaseBytes, onCapacity, decodeCompressed, retainUntilLocalCompletion, maxRetainedBytes);
                attempted[index] = dispatcher;
                if (!Register(requestId, checked((ushort)(index + 1)), dispatcher, ignoreExisting: false))
                    return false;
            }
            succeeded = Volatile.Read(ref _termination) is null;
            return succeeded;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (!succeeded)
            {
                // A reconfigured old mailbox is NOT a newly installed owner; reference
                // comparison prevents this rollback removing it or any replacement.
                // The caller closes the exact Session on failure to retire reused routes.
                for (var index = 0; index < attempted.Length; index++)
                {
                    if (attempted[index] is not { } dispatcher) continue;
                    try { RemoveExactRegistration(requestId, checked((ushort)(index + 1)), dispatcher); }
                    catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
                }
            }
            Array.Clear(attempted);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return false;
    }

    private void RemoveExactRegistration(long requestId, ushort streamId, IStreamDispatcher expected)
    {
        var map = Volatile.Read(ref _dispatchersByRequestId);
        if (map is null || !map.TryGetValue(requestId, out var routes) ||
            !routes.TryRemoveExact(streamId, expected, out var entry)) return;
        FinalizeRemovedEntry(requestId, streamId, routes, entry, completeDispatcher: true,
            new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                "Declared inbound stream reservation did not commit."), clearConsumptionCallback: false);
    }

    private void FinalizeRemovedEntry(long requestId, ushort streamId, RequestDispatchers routes,
        DispatcherEntry entry, bool completeDispatcher, Exception? exception, bool clearConsumptionCallback)
    {
        // Removed from lookup already: every cleanup obligation must run independently or
        // Session teardown can no longer find this exact owner. Scalar refund precedes metrics.
        Exception? failure = null;
        Interlocked.Decrement(ref _activeStreamCount);
        try { SharpLinkTelemetry.AddActiveStreams(-1); }
        catch (Exception cleanup) { failure = cleanup; }
        if (clearConsumptionCallback && entry.Dispatcher is IStreamConsumptionAwareDispatcher aware &&
            entry.Dispatcher is not PreAdmissionStreamDispatcher)
        {
            try { aware.SetBytesConsumedCallback(null, 0, 0); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        if (completeDispatcher)
        {
            try { entry.Dispatcher.Complete(exception); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        try { PublishReceiveTerminal(requestId, streamId, entry); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        try { entry.Detach(); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        try { RemoveEmptyRequest(requestId, routes); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
                _acceptBytes?.Invoke(requestId, streamId, originalByteCount);
                dispatch = CompleteDispatch(
                    entry,
                    preAdmission.DispatchCompressedAsync(wirePayload, originalByteCount));
                return true;
            }
            if (entry.Dispatcher is DiscardingStreamDispatcher discarding)
            {
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

    private sealed class Termination(Exception? exception)
    {
        internal Exception? Exception { get; } = exception;
    }

    private readonly record struct RequestDrainEntry(ushort StreamId, DispatcherEntry Entry);

    private sealed partial class RequestDispatchers
    {
        private DispatcherEntry? _defaultDispatcher;
        private readonly Lock _gate = new();
        // Stream 0 already uses the inline slot. Allocate the named dictionary
        // only when the first nonzero route is actually registered.
        // Named-route operations remain protected by the existing registry gate.
        private Dictionary<ushort, DispatcherEntry>? _byStreamId;

        public bool TryRegister(ushort streamId, IStreamDispatcher dispatcher)
        {
            if (streamId == 0)
            {
                var entry = new DispatcherEntry(dispatcher);
                return Interlocked.CompareExchange(ref _defaultDispatcher, entry, null) is null;
            }

            lock (_gate)
            {
                if (_byStreamId?.ContainsKey(streamId) == true)
                    return false;
                (_byStreamId ??= []).Add(streamId, new DispatcherEntry(dispatcher));
                return true;
            }
        }

        public bool TryAttachPreAdmission(
            ushort streamId,
            IStreamDispatcher dispatcher,
            out bool alreadyCompleted)
        {
            alreadyCompleted = false;
            if (dispatcher is PreAdmissionStreamDispatcher replacement)
            {
                if (!TryAcquire(streamId, out var entry)) return false;
                try
                {
                    if (entry.Dispatcher is not PreAdmissionStreamDispatcher existing) return false;
                    existing.ReconfigureForAdmission(replacement);
                    alreadyCompleted = !existing.IsAvailableForAdmission;
                    return true;
                }
                finally { entry.Release(); }
            }
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
                if (_byStreamId is null || !_byStreamId.TryGetValue(streamId, out var entry) ||
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
                if (_byStreamId is null || !_byStreamId.TryGetValue(streamId, out var entry) ||
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
                if (_byStreamId is null || !_byStreamId.TryGetValue(streamId, out var entry) || !entry.TryAcquire())
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

        public bool TryCompleteRetainedRoute(ushort streamId, Exception? exception, out DispatcherEntry entry)
        {
            entry = null!;
            if (!TryAcquire(streamId, out var acquired)) return false;
            var retained = false;
            Exception? failure = null;
            try
            {
                retained = acquired.Dispatcher is PreAdmissionStreamDispatcher mailbox &&
                    mailbox.TryCompleteAndRetain(exception);
                if (retained) entry = acquired;
            }
            catch (Exception error) { failure = error; }
            try { acquired.Release(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return retained;
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
                var found = _byStreamId is not null && _byStreamId.TryGetValue(streamId, out var entry)
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
                var found = _byStreamId is not null && _byStreamId.TryGetValue(streamId, out var entry)
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
                    if (_byStreamId is not null && _byStreamId.TryGetValue(streamId, out entry!) && entry.TryAcquire())
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

        public bool TryRemoveExact(ushort streamId, IStreamDispatcher expected, out DispatcherEntry entry)
        {
            if (streamId == 0)
            {
                var found = Volatile.Read(ref _defaultDispatcher);
                if (found is not null && ReferenceEquals(found.Dispatcher, expected) &&
                    ReferenceEquals(Interlocked.CompareExchange(ref _defaultDispatcher, null, found), found))
                {
                    found.Close();
                    entry = found;
                    return true;
                }
                entry = null!;
                return false;
            }
            lock (_gate)
            {
                if (_byStreamId is not null && _byStreamId.TryGetValue(streamId, out var found) &&
                    ReferenceEquals(found.Dispatcher, expected))
                {
                    _byStreamId.Remove(streamId);
                    found.Close();
                    entry = found;
                    return true;
                }
                entry = null!;
                return false;
            }
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
                if (_byStreamId is not null && _byStreamId.Remove(streamId, out var removed))
                {
                    removed.Close();
                    entry = removed;
                    return true;
                }
                entry = default!;
                return false;
            }
        }

        public RequestDrainEntry[] TakeAllForDrain()
        {
            var entries = new List<RequestDrainEntry>();
            var defaultDispatcher = Interlocked.Exchange(ref _defaultDispatcher, null);
            if (defaultDispatcher is not null)
            {
                defaultDispatcher.Close();
                entries.Add(new RequestDrainEntry(0, defaultDispatcher));
            }

            lock (_gate)
            {
                if (_byStreamId is not null)
                {
                    foreach (var pair in _byStreamId)
                    {
                        pair.Value.Close();
                        entries.Add(new RequestDrainEntry(pair.Key, pair.Value));
                    }
                    _byStreamId.Clear();
                }
            }
            return [.. entries];
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
                count += _byStreamId?.Count ?? 0;
                entries = _byStreamId is null ? [] : [.. _byStreamId.Values];
                _byStreamId?.Clear();
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
                    return _defaultDispatcher is null && (_byStreamId is null || _byStreamId.Count == 0);
            }
        }
    }

    private sealed class DispatcherEntry : IStreamDispatchState
    {
        private const int ClosedMask = int.MinValue;
        private const int CountMask = int.MaxValue;
        private int _state;
        // Existing field also records callback claim/exit and an explicit local-drain owner.
        private const int Detached = 1;
        private const int DetachCallbackClaimed = 2;
        private const int DetachCallbackExited = 4;
        private const int JoinedDetachOwner = 8;
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

        public bool IsDetached => (Volatile.Read(ref _detached) & Detached) != 0;

        internal bool IsClosed => (Volatile.Read(ref _state) & ClosedMask) != 0;

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
                TryNotifyDetachedDispatchesDrained();
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

        // Only the exact removed local-drain path establishes this before any detach callback.
        // The returned drain operation, not the last dispatch's Release, owns that outcome.
        internal bool TryClaimJoinedDetach()
            => Interlocked.CompareExchange(ref _detached, JoinedDetachOwner, 0) == 0;

        internal void Detach()
        {
            Close();
            if ((Interlocked.Or(ref _detached, Detached) & Detached) == 0)
                Volatile.Read(ref _completions)?.SignalDetached();
            TryNotifyDetachedDispatchesDrained();
        }

        private void TryNotifyDetachedDispatchesDrained()
        {
            if (HasActiveDispatches) return;
            while (true)
            {
                var state = Volatile.Read(ref _detached);
                if ((state & Detached) == 0 || (state & DetachCallbackClaimed) != 0) return;
                if (Interlocked.CompareExchange(ref _detached, state | DetachCallbackClaimed, state) != state)
                    continue;
                break;
            }

            Exception? failure = null;
            try
            {
                if (Dispatcher is IStreamDispatchLease lease) lease.OnDispatchesDrained();
            }
            catch (Exception error) { failure = error; }
            // User callback has exited before either result or exit publication. Its raw outcome
            // is a successful TCS result, never an unobserved fault or OCE conversion.
            if (failure is not null) GetOrCreateCompletions().CompleteDetachCallback(failure);
            Interlocked.Or(ref _detached, DetachCallbackExited);
            Volatile.Read(ref _completions)?.CompleteDetachCallback(null);
            if (failure is not null && (Volatile.Read(ref _detached) & JoinedDetachOwner) == 0)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        internal ValueTask<Exception?> WaitForDetachCallbackAsync()
        {
            // Read exit before the outcome root: a failure publishes that root before exit.
            var exited = (Volatile.Read(ref _detached) & DetachCallbackExited) != 0;
            var completions = Volatile.Read(ref _completions);
            if (exited)
                return completions is null ? new ValueTask<Exception?>((Exception?)null)
                    : completions.ReadCompletedDetachCallback();
            completions ??= GetOrCreateCompletions();
            var result = completions.WaitForDetachCallbackAsync();
            if ((Volatile.Read(ref _detached) & DetachCallbackExited) != 0)
                completions.CompleteDetachCallback(null); // A recorded failure already won.
            return result;
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
            // Cold only: allocated for a real callback-exit waiter or retained callback failure.
            private TaskCompletionSource<Exception?>? _detachCallbackCompletion;

            internal void CompleteDetachCallback(Exception? failure)
            {
                if (failure is not null) GetOrCreateDetachCallbackCompletion().TrySetResult(failure);
                else Volatile.Read(ref _detachCallbackCompletion)?.TrySetResult(null);
            }

            internal ValueTask<Exception?> ReadCompletedDetachCallback()
            {
                var completion = Volatile.Read(ref _detachCallbackCompletion);
                if (completion is null) return new ValueTask<Exception?>((Exception?)null);
                completion.TrySetResult(null); // Failure is stored before the exited bit.
                return new ValueTask<Exception?>(completion.Task);
            }

            internal ValueTask<Exception?> WaitForDetachCallbackAsync()
                => new(GetOrCreateDetachCallbackCompletion().Task);

            private TaskCompletionSource<Exception?> GetOrCreateDetachCallbackCompletion()
            {
                var existing = Volatile.Read(ref _detachCallbackCompletion);
                if (existing is not null) return existing;
                var created = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                return Interlocked.CompareExchange(ref _detachCallbackCompletion, created, null) ?? created;
            }

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
