namespace SharpLink.Runtime;

/// <summary>Provides concurrent request-scoped routing for active RPC streams.</summary>
public class StreamManager : IStreamManager
{
    private readonly StripedLongMap<RequestDispatchers> _dispatchersByRequestId;
    private readonly Action<long, ushort, int>? _acceptBytes;
    private readonly Action<long, ushort, int>? _bytesConsumed;
    private readonly Action<long, ushort>? _streamCompleted;
    private long _droppedStreamFrames;
    private int _activeStreamCount;
    private Termination? _termination;

    /// <summary>Creates a stream manager with default concurrency settings.</summary>
    public StreamManager() : this(new RuntimeConcurrencyOptions())
    {
    }

    /// <summary>Creates a stream manager with explicit concurrency settings.</summary>
    /// <param name="concurrencyOptions">The stripe and sizing policy for active stream lookup.</param>
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

    /// <inheritdoc />
    public void Register(long requestId, IStreamDispatcher dispatcher) => Register(requestId, 0, dispatcher);

    /// <inheritdoc />
    public void Register(long requestId, ushort streamId, IStreamDispatcher dispatcher)
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

        var requestDispatchers = _dispatchersByRequestId.GetOrAdd(
            requestId,
            static _ => new RequestDispatchers());
        if (requestDispatchers.TryAttachPreAdmission(streamId, dispatcher, out var alreadyCompleted))
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
    public void Unregister(long requestId) => Unregister(requestId, 0);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload)
        => DispatchChunkAsync(requestId, 0, payload);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void CompleteStream(long requestId, bool isError, string? msg)
    {
        CompleteStream(requestId, 0, CreateCompletionException(isError, msg));
    }

    /// <inheritdoc />
    public void CompleteStream(long requestId, ushort streamId, bool isError, string? msg)
    {
        CompleteStream(requestId, streamId, CreateCompletionException(isError, msg));
    }

    /// <inheritdoc />
    public void CompleteAll(bool isError, string? msg)
    {
        CompleteAll(CreateCompletionException(isError, msg));
    }

    /// <inheritdoc />
    public void CompleteStream(long requestId, Exception? exception)
    {
        CompleteStream(requestId, 0, exception);
    }

    /// <inheritdoc />
    public void CompleteStream(long requestId, ushort streamId, Exception? exception)
    {
        if (!_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers))
            return;

        if (requestDispatchers.TryCompletePreAdmission(streamId, exception))
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

    /// <inheritdoc />
    public void CompleteAll(Exception? exception)
    {
        var termination = new Termination(exception);
        if (Interlocked.CompareExchange(ref _termination, termination, null) is not null)
            return;

        List<Exception>? failures = null;
        var completed = 0;
        foreach (var requestDispatchers in _dispatchersByRequestId.DrainValues())
            completed += requestDispatchers.CompleteAll(exception, ref failures);
        SharpLinkTelemetry.AddActiveStreams(-completed);
        Interlocked.Add(ref _activeStreamCount, -completed);
        ThrowCompletionFailures(failures);
    }

    internal void CompleteRequestStreams(long requestId, Exception? exception)
    {
        if (!_dispatchersByRequestId.TryRemove(requestId, out var requestDispatchers))
            return;

        List<Exception>? failures = null;
        var completed = requestDispatchers.CompleteAll(exception, ref failures);
        SharpLinkTelemetry.AddActiveStreams(-completed);
        Interlocked.Add(ref _activeStreamCount, -completed);
        ThrowCompletionFailures(failures);
    }

    /// <summary>
    /// Removes every receive stream owned by one request and waits for StreamData dispatches that
    /// acquired their entries before removal. Callers may release request-owned Codec/module state
    /// only after this barrier completes.
    /// </summary>
    internal ValueTask CompleteRequestStreamsAfterDispatchesAsync(long requestId, Exception? exception)
    {
        if (!_dispatchersByRequestId.TryRemove(requestId, out var requestDispatchers))
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
            await entries[index].Entry.WaitForDispatchesAsync().ConfigureAwait(false);

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
        long requestId,
        int streamCount,
        SharpLinkBufferWriterPool buffers,
        Func<int, bool> reserveBytes,
        Action<int> releaseBytes,
        Action capacityExceeded,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null)
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
                    decodeCompressed));
        }
    }

    internal void DrainRejectedRequestStreams(long requestId, int streamCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(streamCount);
        for (var index = 1; index <= streamCount; index++)
        {
            var streamId = checked((ushort)index);
            Register(
                requestId,
                streamId,
                new DiscardingStreamDispatcher(),
                ignoreExisting: true);
        }
    }

    internal bool TryDispatchPreAdmissionCompressed(
        long requestId,
        ushort streamId,
        ReadOnlySequence<byte> wirePayload,
        int originalByteCount,
        out ValueTask dispatch)
    {
        if (_dispatchersByRequestId.TryGetValue(requestId, out var requestDispatchers) &&
            requestDispatchers.TryGetPreAdmission(streamId, out var preAdmission))
        {
            _acceptBytes?.Invoke(requestId, streamId, originalByteCount);
            dispatch = preAdmission.DispatchCompressedAsync(wirePayload, originalByteCount);
            return true;
        }
        if (_dispatchersByRequestId.TryGetValue(requestId, out requestDispatchers) &&
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
            _streamCompleted?.Invoke(requestId, streamId);
        }
        finally
        {
            entry.Detach();
            RemoveEmptyRequest(requestId, requestDispatchers);
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

    private sealed class Termination(Exception? exception)
    {
        internal Exception? Exception { get; } = exception;
    }

    private readonly record struct RequestDrainEntry(ushort StreamId, DispatcherEntry Entry);

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

        public bool TryCompletePreAdmission(ushort streamId, Exception? exception)
        {
            if (streamId == 0)
            {
                if (Volatile.Read(ref _defaultDispatcher)?.Dispatcher is not
                    PreAdmissionStreamDispatcher preAdmission || preAdmission.IsAttached)
                {
                    return false;
                }
                preAdmission.Complete(exception);
                return true;
            }

            lock (_gate)
            {
                if (!_byStreamId.TryGetValue(streamId, out var entry) ||
                    entry.Dispatcher is not PreAdmissionStreamDispatcher preAdmission ||
                    preAdmission.IsAttached)
                {
                    return false;
                }
                preAdmission.Complete(exception);
                return true;
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
                foreach (var pair in _byStreamId)
                {
                    pair.Value.Close();
                    entries.Add(new RequestDrainEntry(pair.Key, pair.Value));
                }
                _byStreamId.Clear();
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
