namespace SharpLink.Client;

internal enum PendingCallKind : byte
{
    Unary,
    Health,
    ClientStreaming,
    ServerStreaming,
    DuplexStreaming,
    OneWayClientStreaming
}

internal enum PendingCallCompletionReason : byte
{
    Response,
    RemoteError,
    RemoteStreamComplete,
    UserCancellation,
    DeadlineExceeded,
    ConsumerAbandoned,
    LocalStreamComplete,
    SendFailure,
    ConnectionClosed,
    GoAway
}

internal readonly record struct PendingCallCompletion(
    long RequestId,
    PendingCallKind Kind,
    PendingCallCompletionReason Reason,
    IStreamDispatcher? Dispatcher,
    Exception? Exception,
    bool RequestPublished);

internal interface IPendingCallOwner
{
    void OnPendingCallRegistered();
    void OnPendingCallCompleted(in PendingCallCompletion completion);
    void OnProducerCancellationCallbackFailed(Exception exception);

    void OnPendingCallCapacityIdle()
    {
    }
}

/// <summary>
/// Receives the single terminal outcome selected by the pending-call completion race.
/// This stays attached to the existing pending entry, rather than creating a second
/// request lookup for retry or endpoint-admission bookkeeping.
/// </summary>
internal interface IPendingCallCompletionObserver
{
    void OnResponseObserved();
    void OnPendingCallCompleted(in PendingCallCompletion completion);
}

/// <summary>
/// Stores all pending calls for one physical connection in a bounded power-of-two table.
/// </summary>
/// <remarks>
/// A connection starts with an inline slot. Overlap may publish a flat table without
/// moving the original call; once that call leaves, the flat table is the only route.
/// A slot contains the complete request ID. Completion removes
/// the slot with one compare/exchange,
/// so response, cancellation, deadline and disconnect races converge on one terminal path. The
/// operation object is returned to its type-specific pool only after its caller observes GetResult.
/// </remarks>
internal sealed partial class PendingRequestTable : IDisposable, IRequestEmissionFailureObserver
{
    private readonly int _indexMask;
    private readonly int _capacity;
    private readonly object _slotsInitializationGate = new();
    // The two references can only point to the same array. _slots is published
    // after the inline cell is empty and can no longer accept new calls.
    private PendingCall?[]? _slots;
    private PendingCall?[]? _transitionSlots;
    private PendingCall? _inlineSlot;
    private long _inlineRequestId;
    private readonly IRpcCodecProvider _codecProvider;
    private readonly IPendingCallOwner _owner;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _slotAvailable;
    private readonly PendingDeadlineScheduler _deadlineScheduler;
    private long _nextId;
    private int _activeSlots;
    private int _waiterCount;
    private int _slotAvailableDisposed;
    private int _disposed;

    public PendingRequestTable(
        int capacity,
        IRpcCodecProvider codecProvider,
        IPendingCallOwner owner,
        TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (!System.Numerics.BitOperations.IsPow2(capacity))
            throw new ArgumentException("Pending request capacity must be a power of two.", nameof(capacity));
        if (capacity > SharpLinkProtocolOptions.MaximumPendingRequestsPerConnection)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                $"Pending request capacity cannot exceed {SharpLinkProtocolOptions.MaximumPendingRequestsPerConnection}.");
        }

        ArgumentNullException.ThrowIfNull(codecProvider);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _capacity = capacity;
        _indexMask = capacity - 1;
        _codecProvider = codecProvider;
        _owner = owner;
        _timeProvider = timeProvider;
        _slotAvailable = new SemaphoreSlim(0, capacity);
        _deadlineScheduler = new PendingDeadlineScheduler(
            _timeProvider,
            ScanExpiredDeadlines);
    }

    public int Capacity => _capacity;

    internal int ActiveCount => Volatile.Read(ref _activeSlots);

    public RpcRequestOperation<T> Rent<T>(out long id)
        => Rent(_codecProvider.GetCodec<T>(), out id);

    public RpcRequestOperation<T> Rent<T>(IRpcCodec<T> responseCodec, out long id)
        => Rent(
            responseCodec,
            PendingCallKind.Unary,
            default,
            CancellationToken.None,
            out id);

    public RpcRequestOperation<T> Rent<T>(
        IRpcCodec<T> responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IPendingCallCompletionObserver? completionObserver = null,
        bool hasResponsePayload = true,
        bool responseNullable = false,
        SharpLinkClient? logicalOwner = null)
    {
        // A supplied logical count belongs to this registration attempt, including
        // a rejection before a pending slot exists.
        try
        {
            ArgumentNullException.ThrowIfNull(responseCodec);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }
        catch
        {
            logicalOwner?.ReleaseDirectLogicalInvocation();
            throw;
        }
        if (TryRent(
                responseCodec, kind, deadline, cancellationToken, hasResponsePayload, responseNullable,
                completionObserver, out id, out var operation, logicalOwner))
            return operation;

        throw CreateResourceExhaustedException();
    }

    public ValueTask<PendingRequestLease<T>> RentAsync<T>(
        bool waitForSlot,
        RpcDeadline deadline,
        CancellationToken cancellationToken)
        => RentAsync(
            _codecProvider.GetCodec<T>(),
            PendingCallKind.Unary,
            deadline,
            waitForSlot,
            cancellationToken);

    public ValueTask<PendingRequestLease<T>> RentAsync<T>(
        IRpcCodec<T> responseCodec,
        bool waitForSlot,
        RpcDeadline deadline,
        CancellationToken cancellationToken)
        => RentAsync(
            responseCodec,
            PendingCallKind.Unary,
            deadline,
            waitForSlot,
            cancellationToken);

    public async ValueTask<PendingRequestLease<T>> RentAsync<T>(
        IRpcCodec<T> responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        bool waitForSlot,
        CancellationToken cancellationToken,
        IPendingCallCompletionObserver? completionObserver = null,
        bool hasResponsePayload = true,
        bool responseNullable = false)
    {
        ArgumentNullException.ThrowIfNull(responseCodec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRent(
                responseCodec, kind, deadline, cancellationToken, hasResponsePayload, responseNullable,
                completionObserver, out var id, out var operation))
            return new PendingRequestLease<T>(id, operation);
        if (!waitForSlot)
            throw CreateResourceExhaustedException();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            Interlocked.Increment(ref _waiterCount);
            try
            {
                // Close the race where disposal starts after the pre-increment check but before
                // this waiter begins using the semaphore.
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                if (TryRent(
                        responseCodec, kind, deadline, cancellationToken, hasResponsePayload, responseNullable,
                        completionObserver, out id, out operation))
                    return new PendingRequestLease<T>(id, operation);

                if (!deadline.HasValue)
                {
                    await _slotAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (!await SharpLinkTimer.WaitAsync(
                            _slotAvailable,
                            deadline,
                            _timeProvider,
                            cancellationToken).ConfigureAwait(false))
                    {
                        throw CreateDeadlineExceededException();
                    }
                }
            }
            finally
            {
                var remainingWaiters = Interlocked.Decrement(ref _waiterCount);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    if (remainingWaiters == 0)
                        DisposeSlotAvailable();
                    else
                        SignalSlotAvailable();
                }
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (TryRent(
                    responseCodec, kind, deadline, cancellationToken, hasResponsePayload, responseNullable,
                    completionObserver, out id, out operation))
                return new PendingRequestLease<T>(id, operation);
        }
    }

    public long RegisterStream(
        PendingCallKind kind,
        IStreamDispatcher dispatcher,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        IPendingCallCompletionObserver? completionObserver = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
            throw new ArgumentOutOfRangeException(nameof(kind));

        if (TryRegister(
                kind,
                operation: null,
                dispatcher,
                deadline,
                cancellationToken,
                out var id,
                completionObserver))
        {
            return id;
        }

        throw CreateResourceExhaustedException();
    }

    public PendingRequestLease<RpcEmptyRequest> RegisterOneWayClientStream(
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        IPendingCallCompletionObserver? completionObserver = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var operation = RpcOperationPool<RpcEmptyRequest>.Rent();
        var published = false;
        if (TryRegister(
                PendingCallKind.OneWayClientStreaming,
                operation,
                dispatcher: null,
                deadline,
                cancellationToken,
                out var id,
                RpcEmptyRequestCodec.Instance,
                completionObserver,
                hasResponsePayload: false,
                responseNullable: false,
                ref published))
        {
            return new PendingRequestLease<RpcEmptyRequest>(id, operation);
        }

        operation.ReturnError();
        throw CreateResourceExhaustedException();
    }

    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        ref var slot = ref ReadSlot(id);
        while (true)
        {
            var current = Volatile.Read(ref slot);
            if (current is null || current.Id != id)
                return false;

            bool deadlineExpired;
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slot), current) || current.Id != id)
                    continue;

                deadlineExpired = current.Deadline.IsExpired(_timeProvider);
                // Response acknowledges server/duplex streams; StreamComplete owns their end.
                if (!deadlineExpired &&
                    current.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
                {
                    current.CompletionObserver?.OnResponseObserved();
                    return true;
                }

                var exchanged = Interlocked.CompareExchange(ref slot, null, current);
                if (!ReferenceEquals(exchanged, current))
                    continue;
                current.WaitUntilRegistered();
            }

            var reason = deadlineExpired
                ? PendingCallCompletionReason.DeadlineExceeded
                : PendingCallCompletionReason.Response;
            CompleteTakenCall(current, reason, exception: null, ref payload);
            return true;
        }
    }

    public bool DispatchError(long id, Exception exception)
        => TryComplete(id, PendingCallCompletionReason.RemoteError, exception);

    void IRequestEmissionFailureObserver.OnRequestEmissionFailure(long requestId, Exception exception)
    {
        var deadlineExceeded = exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded };
        CompleteTransportFailure(
            requestId,
            deadlineExceeded ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.SendFailure,
            deadlineExceeded ? null : exception);
    }

    public bool TryComplete(
        long id,
        PendingCallCompletionReason reason,
        Exception? exception = null)
    {
        if (!TryTakeMatchingCall(id, out var call, out var deadlineExpired))
            return false;

        if (deadlineExpired && reason != PendingCallCompletionReason.DeadlineExceeded)
        {
            reason = PendingCallCompletionReason.DeadlineExceeded;
            exception = null;
        }

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(call!, reason, exception, ref emptyPayload);
        return true;
    }

    public bool TryAcceptStreamData(long id)
    {
        ref var slot = ref ReadSlot(id);
        var current = Volatile.Read(ref slot);
        if (current is null || current.Id != id ||
            current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
        {
            return false;
        }

        PendingCall? expiredCall = null;
        lock (current.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slot), current) || current.Id != id ||
                current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
            {
                return false;
            }

            if (!current.Deadline.IsExpired(_timeProvider))
                return true;

            var exchanged = Interlocked.CompareExchange(ref slot, null, current);
            if (!ReferenceEquals(exchanged, current))
                return false;
            current.WaitUntilRegistered();
            expiredCall = current;
        }

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(
            expiredCall!, PendingCallCompletionReason.DeadlineExceeded, exception: null, ref emptyPayload);
        return false;
    }

    /// <summary>
    /// Validates producer progress against the authoritative request slot without acquiring the
    /// pending-call completion gate for every stream item. The deadline belongs to the same
    /// pending generation and was captured under that gate when stream production started.
    /// Expiration falls back to the pending table's single terminal authority.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAcceptProducerProgress(long id, RpcDeadline deadline)
    {
        if (!Contains(id))
            return false;
        if (!deadline.IsExpired(_timeProvider))
            return true;

        TryComplete(id, PendingCallCompletionReason.DeadlineExceeded);
        return false;
    }

    public bool Contains(long id)
    {
        var call = Volatile.Read(ref ReadSlot(id));
        return call is not null && call.Id == id;
    }

    public CancellationToken GetProducerCancellationToken(long id)
    {
        var call = Volatile.Read(ref ReadSlot(id));
        if (call is null || call.Id != id)
            return new CancellationToken(canceled: true);
        return call.ProducerCancellationToken;
    }

    public bool TryGetProducerDeadline(long id, out RpcDeadline deadline)
    {
        ref var slot = ref ReadSlot(id);
        var current = Volatile.Read(ref slot);
        if (current is null || current.Id != id ||
            current.Kind is not (PendingCallKind.OneWayClientStreaming or
                                 PendingCallKind.ClientStreaming or
                                 PendingCallKind.DuplexStreaming))
        {
            deadline = default;
            return false;
        }

        lock (current.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slot), current) || current.Id != id ||
                current.Kind is not (PendingCallKind.OneWayClientStreaming or
                                     PendingCallKind.ClientStreaming or
                                     PendingCallKind.DuplexStreaming))
            {
                deadline = default;
                return false;
            }

            deadline = current.Deadline;
            return true;
        }
    }

    public long AllocateRequestId()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return NextRequestId();
    }

    public void FailAllPendingRequests(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<Exception>? failures = null;
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
        {
            FailPendingSlot(ref _inlineSlot, exception, ref failures);
            slots = Volatile.Read(ref _transitionSlots);
        }
        if (slots is not null)
        {
            for (var index = 0; index < slots.Length; index++)
                FailPendingSlot(ref slots[index], exception, ref failures);
        }
        ThrowTerminalCleanupFailures(failures);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FailPendingSlot(ref PendingCall? slot, Exception exception, ref List<Exception>? failures)
    {
        if (TryTakeTransportFailure(ref slot, expectedId: null, out var call, out var deadlineExpired, ref failures))
        {
            CompleteTransportFailureCall(
                call!,
                deadlineExpired
                    ? PendingCallCompletionReason.DeadlineExceeded
                    : PendingCallCompletionReason.ConnectionClosed,
                deadlineExpired ? null : exception,
                ref failures);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        List<Exception>? failures = null;
        try
        {
            try { _deadlineScheduler.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            try
            {
                FailAllPendingRequests(new SharpLinkException(
                    SharpLinkErrorCode.ConnectionClosed,
                    "Pending request table is disposed."));
            }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        finally
        {
            if (Volatile.Read(ref _waiterCount) == 0)
                DisposeSlotAvailable();
            else
                SignalSlotAvailable();
        }
        ThrowTerminalCleanupFailures(failures);
    }

    private bool TryRent<T>(
        IRpcCodec<T> responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        bool hasResponsePayload,
        bool responseNullable,
        IPendingCallCompletionObserver? completionObserver,
        out long id,
        out RpcRequestOperation<T> operation,
        SharpLinkClient? logicalOwner = null)
    {
        var published = false;
        RpcRequestOperation<T>? rented = null;
        try
        {
            rented = RpcOperationPool<T>.Rent();
            rented.SetLogicalOwner(logicalOwner);
            if (TryRegister(kind, rented, dispatcher: null, deadline, cancellationToken, out id,
                    responseCodec, completionObserver, hasResponsePayload, responseNullable, ref published))
            {
                operation = rented;
                return true;
            }
            operation = null!;
            return false;
        }
        finally
        {
            // Publication transfers logical completion to the same winner as the result.
            if (!published)
            {
                rented?.ReturnError();
                logicalOwner?.ReleaseDirectLogicalInvocation();
            }
        }
    }

    private bool TryRegister<T>(
        PendingCallKind kind,
        RpcRequestOperation<T> operation,
        IStreamDispatcher? dispatcher,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IRpcCodec<T> responseCodec,
        IPendingCallCompletionObserver? completionObserver,
        bool hasResponsePayload,
        bool responseNullable,
        ref bool published)
    {
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        try
        {
            var slots = Volatile.Read(ref _slots);
            var reservedIndex = -1;
            if (slots is null)
            {
                if (Volatile.Read(ref _transitionSlots) is null && Volatile.Read(ref _inlineSlot) is null)
                {
                    id = NextRequestId();
                    operation.Initialize(id, responseCodec, hasResponsePayload, responseNullable);
                    var call = PendingCall.Rent(this, id, kind, operation, dispatcher, deadline,
                        cancellationToken, completionObserver);
                    if (TryInstallInline(call, id))
                    {
                        published = true;
                        OnRegistered(call);
                        CompleteRegistrationIfDisposed(call);
                        return true;
                    }
                    call.ReturnUnused();
                }
                slots = PrepareFlatSlots(out reservedIndex);
            }

            while (true)
            {
                for (var attempt = 0; attempt < slots.Length; attempt++)
                {
                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (Volatile.Read(ref slots[index]) is not null ||
                        index == reservedIndex && Volatile.Read(ref _inlineSlot) is not null)
                        continue;

                    operation.Initialize(id, responseCodec, hasResponsePayload, responseNullable);
                    var call = PendingCall.Rent(
                        this,
                        id,
                        kind,
                        operation,
                        dispatcher,
                        deadline,
                        cancellationToken,
                        completionObserver);
                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)
                    {
                        published = true;
                        OnRegistered(call);
                        CompleteRegistrationIfDisposed(call);
                        return true;
                    }

                    call.ReturnUnused();
                }

                // A capacity reservation guarantees that some physical slot is free. Concurrent
                // registrars can consume the request IDs that map to that slot, so retry another
                // bounded round instead of reporting false resource exhaustion.
                Thread.Yield();
            }
        }
        catch
        {
            if (!published)
                ReleaseCapacity();
            throw;
        }
    }

    private bool TryRegister(
        PendingCallKind kind,
        IRpcOperation? operation,
        IStreamDispatcher? dispatcher,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IPendingCallCompletionObserver? completionObserver = null)
    {
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        var published = false;
        try
        {
            var slots = Volatile.Read(ref _slots);
            var reservedIndex = -1;
            if (slots is null)
            {
                if (Volatile.Read(ref _transitionSlots) is null && Volatile.Read(ref _inlineSlot) is null)
                {
                    id = NextRequestId();
                    var call = PendingCall.Rent(this, id, kind, operation, dispatcher, deadline,
                        cancellationToken, completionObserver);
                    if (TryInstallInline(call, id))
                    {
                        published = true;
                        OnRegistered(call);
                        CompleteRegistrationIfDisposed(call);
                        return true;
                    }
                    call.ReturnUnused();
                }
                slots = PrepareFlatSlots(out reservedIndex);
            }

            while (true)
            {
                for (var attempt = 0; attempt < slots.Length; attempt++)
                {
                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (Volatile.Read(ref slots[index]) is not null ||
                        index == reservedIndex && Volatile.Read(ref _inlineSlot) is not null)
                        continue;

                    var call = PendingCall.Rent(
                        this,
                        id,
                        kind,
                        operation,
                        dispatcher,
                        deadline,
                        cancellationToken,
                        completionObserver);
                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)
                    {
                        published = true;
                        OnRegistered(call);
                        CompleteRegistrationIfDisposed(call);
                        return true;
                    }

                    call.ReturnUnused();
                }

                // A capacity reservation guarantees that some physical slot is free. Concurrent
                // registrars can consume the request IDs that map to that slot, so retry another
                // bounded round instead of reporting false resource exhaustion.
                Thread.Yield();
            }
        }
        catch
        {
            if (!published)
                ReleaseCapacity();
            throw;
        }
    }

    private bool TryAcquireCapacity()
    {
        var active = Interlocked.Increment(ref _activeSlots);
        if (active <= _capacity)
            return true;

        RefundRejectedCapacity();
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefundRejectedCapacity()
    {
        var remaining = Interlocked.Decrement(ref _activeSlots);
        if (remaining < 0)
            throw new InvalidOperationException("Pending request capacity accounting underflowed.");

        if (remaining < _capacity && Volatile.Read(ref _waiterCount) != 0)
            SignalSlotAvailable();

        if (remaining == 0)
            _owner.OnPendingCallCapacityIdle();
    }

    private void OnRegistered(PendingCall call)
    {
        SharpLinkTelemetry.AddPendingRequests(1);
        _owner.OnPendingCallRegistered();
        call.MarkRegistered();
        if (call.Deadline.HasValue)
            _deadlineScheduler.Observe(call.Deadline);
        if (call.CancellationToken.IsCancellationRequested)
            TryComplete(call.Id, PendingCallCompletionReason.UserCancellation);
    }

    private void CompleteRegistrationIfDisposed(PendingCall call)
    {
        if (Volatile.Read(ref _disposed) != 0)
            TryComplete(call.Id, PendingCallCompletionReason.ConnectionClosed);
    }

    private bool TryTakeMatchingCall(
        long id,
        out PendingCall? call,
        out bool deadlineExpired)
    {
        ref var slot = ref ReadSlot(id);
        while (true)
        {
            var current = Volatile.Read(ref slot);
            if (current is null || current.Id != id)
            {
                call = null;
                deadlineExpired = false;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slot), current) || current.Id != id)
                    continue;

                // Sample the authoritative monotonic boundary while owning the same
                // completion gate that claims/removes the terminal slot. A response that
                // wins before the boundary remains a response even if completion work is
                // descheduled until after the boundary; a response claiming after it loses.
                deadlineExpired = current.Deadline.IsExpired(_timeProvider);

                var exchanged = Interlocked.CompareExchange(ref slot, null, current);
                if (!ReferenceEquals(exchanged, current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }

    private void CompleteTakenCall(
        PendingCall call,
        PendingCallCompletionReason reason,
        Exception? exception,
        ref ReadOnlySequence<byte> payload)
    {
        try
        {
            call.DisposeCancellationRegistration();
            var producerCancellationFailure = call.CancelProducer(reason);
            if (producerCancellationFailure is not null)
            {
                try
                {
                    _owner.OnProducerCancellationCallbackFailed(producerCancellationFailure);
                }
                catch
                {
                    // Diagnostics must never interrupt the terminal pending-call transition.
                }
            }
            var isResponse = reason is PendingCallCompletionReason.Response or PendingCallCompletionReason.LocalStreamComplete;
            if (isResponse && call.Operation is { } responseOperation)
            {
                exception = responseOperation.TryDeserializeResponse(ref payload);
                if (exception is not null)
                    reason = PendingCallCompletionReason.RemoteError;
            }
            exception ??= CreateCompletionException(call, reason);

            var completion = new PendingCallCompletion(
                call.Id,
                call.Kind,
                reason,
                call.Dispatcher,
                exception,
                call.RequestPublished);
            // Decode response payloads before reporting the terminal admission outcome so malformed
            // endpoint responses are not published as successful attempts.
            call.CompletionObserver?.OnPendingCallCompleted(in completion);

            if (call.Operation is { } operation)
            {
                if (isResponse)
                    operation.CompleteResponse(exception);
                else
                    operation.SetError(exception ?? new SharpLinkException(
                        SharpLinkErrorCode.Internal,
                        "A pending request completed without a result."));
            }

            _owner.OnPendingCallCompleted(in completion);
            call.ReturnCompleted();
        }
        catch
        {
            ReleaseSlot();
            throw;
        }

        ReleaseSlot();
    }

    private static Exception? CreateCompletionException(
        PendingCall call,
        PendingCallCompletionReason reason)
        => reason switch
        {
            PendingCallCompletionReason.UserCancellation => call.CancellationToken.CanBeCanceled
                ? new OperationCanceledException(call.CancellationToken)
                : new OperationCanceledException(),
            PendingCallCompletionReason.DeadlineExceeded => new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."),
            PendingCallCompletionReason.ConsumerAbandoned => new OperationCanceledException(
                "The response stream consumer stopped before remote completion."),
            PendingCallCompletionReason.ConnectionClosed => new SharpLinkException(
                SharpLinkErrorCode.ConnectionClosed,
                "The owning RPC connection closed."),
            _ => null
        };

    private void ReleaseSlot()
    {
        SharpLinkTelemetry.AddPendingRequests(-1);
        ReleaseCapacity();
    }

    private void ReleaseCapacity()
    {
        var remaining = Interlocked.Decrement(ref _activeSlots);
        if (remaining < 0)
            throw new InvalidOperationException("Pending request capacity accounting underflowed.");

        if (Volatile.Read(ref _waiterCount) != 0)
            SignalSlotAvailable();

        if (remaining == 0)
            _owner.OnPendingCallCapacityIdle();
    }

    private void SignalSlotAvailable()
    {
        if (Volatile.Read(ref _slotAvailableDisposed) != 0)
            return;

        try
        {
            _slotAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private void DisposeSlotAvailable()
    {
        if (Interlocked.Exchange(ref _slotAvailableDisposed, 1) != 0)
            return;

        _slotAvailable.Dispose();
    }

    private long NextRequestId()
    {
        var id = Interlocked.Increment(ref _nextId);
        return id != 0 ? id : Interlocked.Increment(ref _nextId);
    }

    private bool TryTakeExpiredCall(
        ref PendingCall? slot,
        PendingCall expected,
        long expectedId,
        out PendingCall? call)
    {
        lock (expected.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slot), expected) ||
                expected.Id != expectedId ||
                !expected.Deadline.HasValue ||
                !expected.Deadline.IsExpired(_timeProvider))
            {
                call = null;
                return false;
            }

            if (!ReferenceEquals(Interlocked.CompareExchange(ref slot, null, expected), expected))
            {
                call = null;
                return false;
            }

            expected.WaitUntilRegistered();
            call = expected;
            return true;
        }
    }

    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var slots = Volatile.Read(ref _slots);
        if (slots is null)
        {
            ScanSlotDeadline(ref _inlineSlot);
            slots = Volatile.Read(ref _transitionSlots);
        }
        if (slots is null)
            return;

        for (var index = 0; index < slots.Length; index++)
            ScanSlotDeadline(ref slots[index]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ScanSlotDeadline(ref PendingCall? slot)
    {
        var call = Volatile.Read(ref slot);
        if (call is null)
            return;

        // The authoritative deadline, full ID and removal are still checked
        // together, even if dense storage was published after this read.
        var expectedId = call.Id;
        var deadline = call.Deadline;
        if (!deadline.HasValue)
            return;
        if (!deadline.IsExpired(_timeProvider))
        {
            _deadlineScheduler.Observe(deadline);
            return;
        }
        if (!TryTakeExpiredCall(ref slot, call, expectedId, out var expiredCall))
            return;

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(
            expiredCall!, PendingCallCompletionReason.DeadlineExceeded, exception: null, ref emptyPayload);
    }

    private static SharpLinkException CreateResourceExhaustedException()
        => SharpLinkResourceExhaustion.Create(
            SharpLinkResourceExhaustion.PendingRequestCapacity,
            "Pending request capacity is exhausted (pending_request_capacity).");

    private static SharpLinkException CreateDeadlineExceededException()
        => new(SharpLinkErrorCode.DeadlineExceeded, "Timed out waiting for pending request capacity.");

    private static class RpcOperationPool<T>
    {
        private const int MaxRetainedOperations = 4096;
        private static readonly ConcurrentQueue<RpcRequestOperation<T>> Queue = new();
        private static int _retainedCount;

        public static RpcRequestOperation<T> Rent()
        {
            if (Queue.TryDequeue(out var operation))
            {
                Interlocked.Decrement(ref _retainedCount);
                return operation;
            }

            return new RpcRequestOperation<T>(Return);
        }

        private static void Return(RpcRequestOperation<T> operation)
        {
            while (true)
            {
                var current = Volatile.Read(ref _retainedCount);
                if (current >= MaxRetainedOperations)
                    return;
                if (Interlocked.CompareExchange(ref _retainedCount, current + 1, current) == current)
                    break;
            }

            Queue.Enqueue(operation);
        }
    }

    private sealed class PendingCall
    {
        private const int MaxRetained = 4096;
        private static readonly ConcurrentQueue<PendingCall> Pool = new();
        private static int s_retainedCount;

        private PendingRequestTable? _table;
        private CancellationTokenRegistration _cancellationRegistration;
        private IPendingCallCompletionObserver? _completionObserver;
        private int _registered;
        private bool _requestPublished;

        public object CompletionGate { get; } = new();

        public long Id { get; private set; }
        public PendingCallKind Kind { get; private set; }
        public IRpcOperation? Operation { get; private set; }
        public IStreamDispatcher? Dispatcher { get; private set; }
        public RpcDeadline Deadline { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public CancellationToken ProducerCancellationToken
            => NStudyAdmission?.Token ?? CancellationToken.None;
        public PendingInputProducer? NStudyAdmission { get; private set; }
        public IPendingCallCompletionObserver? CompletionObserver => _completionObserver;

        /// <summary>
        /// Whether the owning Request frame reached the session send queue while this call was
        /// still live.
        /// </summary>
        /// <remarks>
        /// Written once by the publisher while it holds <see cref="CompletionGate"/> and only read
        /// after the call left the slot, so the gate orders the flag against every terminal
        /// transition. A terminal transition that observes this as false must not emit a cancel:
        /// the peer would discard a cancel for a request it never received.
        /// </remarks>
        public bool RequestPublished => _requestPublished;

        public void MarkRequestPublished()
        {
            _requestPublished = true;
            NStudyAdmission?.MarkRequestPublished();
        }

        public static PendingCall Rent(
            PendingRequestTable table,
            long id,
            PendingCallKind kind,
            IRpcOperation? operation,
            IStreamDispatcher? dispatcher,
            RpcDeadline deadline,
            CancellationToken cancellationToken,
            IPendingCallCompletionObserver? completionObserver)
        {
            if (!Pool.TryDequeue(out var call))
                call = new PendingCall();
            else
                Interlocked.Decrement(ref s_retainedCount);

            call._table = table;
            Volatile.Write(ref call._registered, 0);
            call._requestPublished = false;
            call.Id = id;
            call.Kind = kind;
            call.Operation = operation;
            call.Dispatcher = dispatcher;
            call.Deadline = deadline;
            call.CancellationToken = cancellationToken;
            call._completionObserver = completionObserver;
            call.NStudyAdmission = kind switch
            {
                PendingCallKind.ClientStreaming or PendingCallKind.DuplexStreaming or
                    PendingCallKind.OneWayClientStreaming => new(),
                _ => null
            };
            call._cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.UnsafeRegister(static state =>
                {
                    var pending = (PendingCall)state!;
                    pending._table?.TryComplete(
                        pending.Id,
                        PendingCallCompletionReason.UserCancellation);
                }, call)
                : default;
            return call;
        }

        public void DisposeCancellationRegistration() => _cancellationRegistration.Dispose();

        public void MarkRegistered() => Volatile.Write(ref _registered, 1);

        public void WaitUntilRegistered()
        {
            var spinner = new SpinWait();
            while (Volatile.Read(ref _registered) == 0)
                spinner.SpinOnce();
        }

        public Exception? CancelProducer(PendingCallCompletionReason reason)
        {
            var admission = NStudyAdmission;
            NStudyAdmission = null;
            return admission?.Terminate(reason);
        }

        public void ReturnUnused()
        {
            _cancellationRegistration.Dispose();
            NStudyAdmission?.Release();
            ReturnCore();
        }

        public void ReturnCompleted() => ReturnCore();

        private void ReturnCore()
        {
            _table = null;
            Id = 0;
            Kind = default;
            Operation = null;
            Dispatcher = null;
            Deadline = default;
            CancellationToken = CancellationToken.None;
            _completionObserver = null;
            NStudyAdmission = null;
            _cancellationRegistration = default;
            Volatile.Write(ref _registered, 0);

            while (true)
            {
                var retained = Volatile.Read(ref s_retainedCount);
                if (retained >= MaxRetained)
                    return;
                if (Interlocked.CompareExchange(ref s_retainedCount, retained + 1, retained) == retained)
                    break;
            }
            Pool.Enqueue(this);
        }
    }
}

internal readonly record struct PendingRequestLease<T>(long Id, RpcRequestOperation<T> Operation);
