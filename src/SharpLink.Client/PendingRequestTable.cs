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
    void OnPendingCallCompleted(in PendingCallCompletion completion);
    void OnPendingCallCompleted(in PendingCallCompletion completion,
        ref StreamSinkPublicationDisposition disposition, long dispatcherLease, ClientStreamPublicationGroup? group = null)
        => OnPendingCallCompleted(in completion);
    void OnProducerCancellationCallbackFailed(Exception exception);

    // Called at most once after the permanent admission seal. Must not tear down admitted tails.
    void OnRequestIdsExhausted()
    {
    }

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
/// A slot contains the complete request ID. Completion removes the slot with one compare/exchange,
/// so response, cancellation, deadline and disconnect races converge on one terminal path. The
/// operation object is returned to its type-specific pool only after its caller observes GetResult.
/// </remarks>
internal sealed partial class PendingRequestTable : IDisposable, IRequestEmissionFailureObserver
{
    private readonly int _indexMask;
    private readonly int _capacity;
    private readonly object _slotsInitializationGate = new();
    private PendingCall?[]? _slots;
    private readonly IRpcCodecProvider _codecProvider;
    private readonly IPendingCallOwner _owner;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _slotAvailable;
    private readonly PendingDeadlineScheduler _deadlineScheduler;
    private long _nextId;
    private int _requestIdsExhausted;
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

    // Existing finite capacity ownership, including logically removed producers whose
    // physical group has not finished. Count remains the live logical slot census.
    internal int ActiveCount => Volatile.Read(ref _activeSlots);

    internal bool RequestIdsExhausted => Volatile.Read(ref _requestIdsExhausted) != 0;

    // _nextId is the allocated high-water, including IDs skipped because a ring slot was busy.
    // In-flight Increment racers may enter the negative sentinel after the last positive ID;
    // they can never publish it. Once sealed, the public high-water remains permanently MaxValue.
    internal long HighestAllocatedRequestId
    {
        get
        {
            if (RequestIdsExhausted)
                return long.MaxValue;
            var value = Volatile.Read(ref _nextId);
            return value >= 0 ? value : long.MaxValue;
        }
    }

    internal bool SlotsMaterialized => Volatile.Read(ref _slots) is not null;

    public int Count
    {
        get
        {
            var slots = Volatile.Read(ref _slots);
            if (slots is null)
                return 0;

            var count = 0;
            for (var index = 0; index < slots.Length; index++)
                if (Volatile.Read(ref slots[index]) is not null)
                    count++;
            return count;
        }
    }

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
        ClientStreamPublicationGroup? producerGroup = null)
    {
        ArgumentNullException.ThrowIfNull(responseCodec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRent(
                responseCodec, kind, deadline, cancellationToken, hasResponsePayload, responseNullable,
                completionObserver, out id, out var operation, producerGroup))
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
                if (RequestIdsExhausted && remainingWaiters != 0)
                    SignalSlotAvailable();
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
        IPendingCallCompletionObserver? completionObserver = null,
        ClientStreamPublicationGroup? producerGroup = null)
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
                completionObserver, producerGroup))
        {
            return id;
        }

        throw CreateResourceExhaustedException();
    }

    public PendingRequestLease<RpcEmptyRequest> RegisterOneWayClientStream(
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        IPendingCallCompletionObserver? completionObserver = null,
        ClientStreamPublicationGroup? producerGroup = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRent(
                RpcEmptyRequestCodec.Instance,
                PendingCallKind.OneWayClientStreaming,
                deadline,
                cancellationToken,
                hasResponsePayload: false,
                responseNullable: false,
                completionObserver,
                out var id,
                out var operation, producerGroup))
            return new PendingRequestLease<RpcEmptyRequest>(id, operation);
        throw CreateResourceExhaustedException();
    }

    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref slots[index]);
        if (current is not null && current.Id == id &&
            current.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            // A successful Response is only the server's acknowledgement; StreamComplete owns
            // the terminal transition for server and duplex streams. Deadline equality is checked
            // under the same completion gate so a late acknowledgement cannot beat the monotonic
            // boundary merely because the timer callback has not run yet.
            PendingCall? expiredCall = null;
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||
                    current.Id != id ||
                    current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
                {
                    return false;
                }

                if (current.Deadline.IsExpired(_timeProvider))
                {
                    var removed = TryRemoveTerminalCall(slots, index, current);
                    if (!removed)
                        return false;
                    current.WaitUntilRegistered();
                    current.StreamGroup?.ResolveHeadUnderCompletionGate(accepted: false);
                    expiredCall = current;
                }
                else
                {
                    current.CompletionObserver?.OnResponseObserved();
                    return true;
                }
            }

            var emptyPayload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(
                expiredCall!, PendingCallCompletionReason.DeadlineExceeded, exception: null, ref emptyPayload);
            return true;
        }

        if (!TryTakeMatchingCall(id, out var call, out var deadlineExpired))
            return false;

        var reason = deadlineExpired
            ? PendingCallCompletionReason.DeadlineExceeded
            : PendingCallCompletionReason.Response;
        CompleteTakenCall(call!, reason, exception: null, ref payload);
        return true;
    }

    public bool DispatchError(long id, Exception exception)
        => TryComplete(id, PendingCallCompletionReason.RemoteError, exception);

    void IRequestEmissionFailureObserver.OnRequestEmissionFailure(long requestId, Exception exception)
    {
        var deadlineExceeded = exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded };
        TryComplete(
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
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref slots[index]);
        if (current is null || current.Id != id ||
            current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
        {
            return false;
        }

        PendingCall? expiredCall = null;
        lock (current.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id ||
                current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
            {
                return false;
            }

            if (!current.Deadline.IsExpired(_timeProvider))
                return true;

            var removed = TryRemoveTerminalCall(slots, index, current);
            if (!removed)
                return false;
            current.WaitUntilRegistered();
            current.StreamGroup?.ResolveHeadUnderCompletionGate(accepted: false);
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
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);
        return call is not null && call.Id == id;
    }

    internal bool TryAcquireProducerLifetime(long id, out ProducerLifetimeLease lifetime,
        out CancellationToken originalToken, ClientStreamPublicationGroup? group = null)
    {
        lifetime = default;
        originalToken = default;
        var slots = Volatile.Read(ref _slots);
        if (slots is null) return false;
        var index = (int)(id & _indexMask);
        var call = Volatile.Read(ref slots[index]);
        if (call is null || call.Id != id) return false;
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), call) || call.Id != id ||
                !call.TryAttachProducer(out originalToken)) return false;
            call.StreamGroup = group;
            lifetime = new ProducerLifetimeLease(this, call, id);
            return true;
        }
    }

    // A copied lease can neither release twice nor affect a later pooled incarnation.
    // Only the original group owns Dispose, after actual producers and its frame fence.
    internal readonly struct ProducerLifetimeLease : IDisposable
    {
        private readonly PendingRequestTable? _table;
        private readonly PendingCall? _call;
        private readonly long _id;
        internal ProducerLifetimeLease(PendingRequestTable table, PendingCall call, long id)
        { _table = table; _call = call; _id = id; }
        public void Dispose() => _call?.ReleaseProducerLifetime(_table!, _id);
    }

    public CancellationToken GetProducerCancellationToken(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return new CancellationToken(canceled: true);

        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);
        if (call is null || call.Id != id)
            return new CancellationToken(canceled: true);
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[(int)(id & _indexMask)]), call) || call.Id != id)
                return new CancellationToken(canceled: true);
            return call.ProducerCancellationToken;
        }
    }

    public bool TryGetProducerDeadline(long id, out RpcDeadline deadline)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
        {
            deadline = default;
            return false;
        }

        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref slots[index]);
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
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id ||
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
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return;

        List<Exception>? failures = null;
        for (var index = 0; index < slots.Length; index++)
        {
            if (!TryTakeCallAtIndex(index, out var call, out var deadlineExpired)) continue;
            var payload = ReadOnlySequence<byte>.Empty;
            try
            {
                CompleteTakenCall(call!, deadlineExpired ? PendingCallCompletionReason.DeadlineExceeded :
                    PendingCallCompletionReason.ConnectionClosed, deadlineExpired ? null : exception, ref payload);
            }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
        }
        ThrowTerminalFailures(failures);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        List<Exception>? failures = null;
        try { _deadlineScheduler.Dispose(); }
        catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
        try
        {
            FailAllPendingRequests(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                "Pending request table is disposed."));
        }
        catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
        try
        {
            if (Volatile.Read(ref _waiterCount) == 0) DisposeSlotAvailable();
            else SignalSlotAvailable();
        }
        catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
        ThrowTerminalFailures(failures);
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
        ClientStreamPublicationGroup? producerGroup = null)
    {
        ThrowIfRequestIdsExhausted();
        operation = RpcOperationPool<T>.Rent();
        var registrationPublished = false;
        try
        {
            if (TryRegister(
                    kind,
                    operation,
                    dispatcher: null,
                    deadline,
                    cancellationToken,
                    out id,
                    responseCodec,
                    completionObserver,
                    hasResponsePayload,
                    responseNullable,
                    out registrationPublished, producerGroup))
                return true;
        }
        catch
        {
            // Counter exhaustion and callback failures before publication cannot strand the
            // operation borrowed above. A published entry retains its existing terminal owner.
            if (!registrationPublished)
            {
                operation.ReturnError();
                operation = null!;
            }
            throw;
        }

        operation.ReturnError();
        operation = null!;
        return false;
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
        out bool published,
        ClientStreamPublicationGroup? producerGroup = null)
    {
        published = false;
        producerGroup?.BeginPendingRegistration(kind);
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        try
        {
            var slots = GetOrCreateSlots();
            while (true)
            {
                for (var attempt = 0; attempt < slots.Length; attempt++)
                {
                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (Volatile.Read(ref slots[index]) is not null)
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
                    if (producerGroup is null
                        ? Interlocked.CompareExchange(ref slots[index], call, null) is null
                        : TryPublishProducingRegistration(slots, index, call, producerGroup, ref published))
                    {
                        published = true;
                        if (producerGroup is null)
                        {
                            OnRegistered(call);
                            CompleteRegistrationIfDisposed(call);
                        }
                        else
                        {
                            // These are registrar originals, not pooled call reads after callbacks.
                            try { OnProducingRegistered(id, deadline, cancellationToken); }
                            catch (Exception failure)
                            {
                                try { CompleteProducingSetupFailure(producerGroup, failure); }
                                catch (Exception cleanup) { failure = ClientStreamPublicationGroup.AddPhysical(failure, cleanup)!; }
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                            }
                        }
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
        IPendingCallCompletionObserver? completionObserver = null,
        ClientStreamPublicationGroup? producerGroup = null)
    {
        producerGroup?.BeginPendingRegistration(kind);
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        var published = false;
        try
        {
            var slots = GetOrCreateSlots();
            while (true)
            {
                for (var attempt = 0; attempt < slots.Length; attempt++)
                {
                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (Volatile.Read(ref slots[index]) is not null)
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
                    if (producerGroup is null
                        ? Interlocked.CompareExchange(ref slots[index], call, null) is null
                        : TryPublishProducingRegistration(slots, index, call, producerGroup, ref published))
                    {
                        published = true;
                        if (producerGroup is null)
                        {
                            OnRegistered(call);
                            CompleteRegistrationIfDisposed(call);
                        }
                        else
                        {
                            // These are registrar originals, not pooled call reads after callbacks.
                            try { OnProducingRegistered(id, deadline, cancellationToken); }
                            catch (Exception failure)
                            {
                                try { CompleteProducingSetupFailure(producerGroup, failure); }
                                catch (Exception cleanup) { failure = ClientStreamPublicationGroup.AddPhysical(failure, cleanup)!; }
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                            }
                        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateSlots()
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return slots;

        lock (_slotsInitializationGate)
        {
            slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                slots = new PendingCall?[_capacity];
                Volatile.Write(ref _slots, slots);
            }

            return slots;
        }
    }

    private bool TryAcquireCapacity()
    {
        ThrowIfRequestIdsExhausted();
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

    private bool TryPublishProducingRegistration(PendingCall?[] slots, int index, PendingCall call,
        ClientStreamPublicationGroup group, ref bool published)
    {
        // Exact scalar registration identity and producer lease commit before any remover can
        // enter CompletionGate. No user callback or transport runs in this transaction.
        var id = call.Id;
        lock (call.CompletionGate)
        {
            if (Interlocked.CompareExchange(ref slots[index], call, null) is not null) return false;
            published = true;
            try { group.AdoptRegisteredCall(this, call, id); }
            finally { call.MarkRegistered(); }
            return true;
        }
    }

    // Producing setup/head failure must not resample the same broken application clock.
    // Exact group/slot identity is the authority; all terminal obligations stay outside gates.
    internal void CompleteProducingSetupFailure(ClientStreamPublicationGroup group, Exception error,
        PendingCallCompletionReason? reason = null)
    {
        if (!group.IsAdopted) return;
        var id = group.RequestId;
        var slots = Volatile.Read(ref _slots);
        if (slots is null) return;
        var index = (int)(id & _indexMask);
        var call = Volatile.Read(ref slots[index]);
        if (call is null || call.Id != id || !ReferenceEquals(call.StreamGroup, group)) return;
        var terminal = TakeCallAfterClockFailure(slots, index, call, id);
        if (terminal is null) return;
        var empty = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(terminal, reason ?? (error is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
            ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.SendFailure), error, ref empty);
    }

    private void OnProducingRegistered(long id, RpcDeadline deadline, CancellationToken token)
    {
        SharpLinkTelemetry.AddPendingRequests(1);
        if (deadline.HasValue) _deadlineScheduler.Observe(deadline);
        if (token.IsCancellationRequested) TryComplete(id, PendingCallCompletionReason.UserCancellation);
        if (Volatile.Read(ref _disposed) != 0) TryComplete(id, PendingCallCompletionReason.ConnectionClosed);
    }

    private void OnRegistered(PendingCall call)
    {
        SharpLinkTelemetry.AddPendingRequests(1);
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
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
        {
            call = null;
            deadlineExpired = false;
            return false;
        }

        var index = (int)(id & _indexMask);
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null || current.Id != id)
            {
                call = null;
                deadlineExpired = false;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id)
                    continue;

                // Sample the authoritative monotonic boundary while owning the same
                // completion gate that claims/removes the terminal slot. A response that
                // wins before the boundary remains a response even if completion work is
                // descheduled until after the boundary; a response claiming after it loses.
                deadlineExpired = current.Deadline.IsExpired(_timeProvider);

                var removed = TryRemoveTerminalCall(slots, index, current);
                if (!removed)
                    continue;

                current.WaitUntilRegistered();
                current.StreamGroup?.ResolveHeadUnderCompletionGate(accepted: false);
                call = current;
                return true;
            }
        }
    }

    private bool TryTakeCallAtIndex(
        int index,
        out PendingCall? call,
        out bool deadlineExpired)
    {
        var slots = Volatile.Read(ref _slots)!;
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null)
            {
                call = null;
                deadlineExpired = false;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current))
                    continue;

                deadlineExpired = current.Deadline.IsExpired(_timeProvider);
                if (!TryRemoveTerminalCall(slots, index, current))
                    continue;

                current.WaitUntilRegistered();
                current.StreamGroup?.ResolveHeadUnderCompletionGate(accepted: false);
                call = current;
                return true;
            }
        }
    }

    // Every slot-removal caller owns CompletionGate. The attached group's existing gate
    // covers provisional receipt selection and the same removal CAS as one transaction.
    private static bool TryRemoveTerminalCall(PendingCall?[] slots, int index, PendingCall expected)
        => expected.StreamGroup is { } group
            ? group.TrySelectTerminal(slots, index, expected)
            : ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, expected), expected);

    private void CompleteTakenCall(PendingCall call, PendingCallCompletionReason reason,
        Exception? exception, ref ReadOnlySequence<byte> payload)
    {
        // Capture ALL receipt identities before SetError/CompleteResponse can let application
        // continuations consume/reset a pooled operation, or terminal processing returns call.
        var group = call.StreamGroup;
        var id = call.Id;
        var kind = call.Kind;
        var operation = call.Operation;
        var dispatcher = call.Dispatcher;
        var observer = call.CompletionObserver;
        var requestPublished = call.RequestPublished;
        var producing = call.IsProducing;
        var dispatcherLease = group?.ResponseDispatcherLease ?? 0;
        var receiptSink = dispatcher as IStreamSinkPublicationReceipt;
        var retainedLease = 0L;
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        List<Exception>? failures = null;
        try
        {
            if (kind == PendingCallKind.DuplexStreaming && receiptSink is not null && dispatcherLease != 0)
            {
                try { if (!receiptSink.TryRetainSinkPublication(dispatcherLease, out retainedLease)) dispatcherLease = 0; }
                catch (Exception failure) { dispatcherLease = 0; AddTerminalFailure(ref failures, failure); }
            }
            try { call.AcceptanceReady?.TrySetResult(false); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            try { group?.OnPendingTerminal(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            try { call.DisposeCancellationRegistration(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            var producerCancellationFailure = call.CancelProducer(reason);
            if (producerCancellationFailure is not null)
            {
                try { _owner.OnProducerCancellationCallbackFailed(producerCancellationFailure); }
                catch (Exception failure)
                {
                    AddTerminalFailure(ref failures, producerCancellationFailure);
                    AddTerminalFailure(ref failures, failure);
                }
            }
            var isResponse = reason is PendingCallCompletionReason.Response or PendingCallCompletionReason.LocalStreamComplete;
            if (isResponse && operation is not null)
            {
                try { exception = operation.TryDeserializeResponse(ref payload); }
                catch (Exception failure) { exception = failure; }
                if (exception is not null) reason = PendingCallCompletionReason.RemoteError;
            }
            exception ??= CreateCompletionException(call, reason);
            var completion = new PendingCallCompletion(id, kind, reason, dispatcher, exception, requestPublished);
            try { observer?.OnPendingCallCompleted(in completion); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            try
            {
                if (operation is not null)
                {
                    if (isResponse) operation.CompleteResponse(exception);
                    else operation.SetError(exception ?? new SharpLinkException(SharpLinkErrorCode.Internal,
                        "A pending request completed without a result."));
                    disposition = StreamSinkPublicationDisposition.Published;
                }
            }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            try { _owner.OnPendingCallCompleted(in completion, ref disposition, dispatcherLease, group); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            try { SharpLinkTelemetry.AddPendingRequests(-1); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            if (!producing)
            {
                try { ReleaseCapacity(); }
                catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            }
        }
        finally
        {
            if (retainedLease != 0)
            {
                try { receiptSink!.ReleaseSinkPublication(retainedLease); }
                catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            }
            // The original registration retention is distinct from the temporary receipt
            // retention above. This unique terminal owner has finished every sink access and
            // initiated exact route cleanup. It never waits for consumer/pool/response drain.
            if (kind == PendingCallKind.DuplexStreaming && group is not null && receiptSink is not null)
            {
                try
                {
                    var originalLease = group.FinishPendingSinkUseForRetention();
                    if (originalLease != 0) receiptSink.ReleaseSinkPublication(originalLease);
                }
                catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            }
            try { call.CompleteTerminalProcessing(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            // Do not read call/operation/dispatcher after the previous stage. Receipt and batch
            // handoff use captured originals only; Accepted and Published are separate facts.
            if (group is not null)
            {
                Exception? batch = null;
                Exception? handoffFailure = null;
                try { batch = failures is null ? null : failures.Count == 1 ? failures[0] :
                    new AggregateException("Independent pending terminal stages failed.", failures); }
                catch (Exception failure) { handoffFailure = failure; }
                var accepted = false;
                try { group.FinalizeLogicalReceipt(reason, exception, disposition, batch, ref accepted); }
                catch (Exception failure) { handoffFailure = ClientStreamPublicationGroup.AddPhysical(handoffFailure, failure); }
                if (accepted) failures = null; // The physical group now exclusively owns this raw batch.
                if (handoffFailure is not null) AddTerminalFailure(ref failures, handoffFailure);
            }
        }
        ThrowTerminalFailures(failures);
    }

    private static void AddTerminalFailure(ref List<Exception>? failures, Exception failure)
        => (failures ??= []).Add(failure);

    private static void ThrowTerminalFailures(List<Exception>? failures)
    {
        if (failures is null) return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Pending terminal cleanup failed after all obligations were attempted.", failures);
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

    private void ReleaseCapacity()
    {
        var remaining = Interlocked.Decrement(ref _activeSlots);
        if (remaining < 0)
            throw new InvalidOperationException("Pending request capacity accounting underflowed.");
        List<Exception>? failures = null;
        if (Volatile.Read(ref _waiterCount) != 0)
        {
            try { SignalSlotAvailable(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
        }
        if (remaining == 0)
        {
            try { _owner.OnPendingCallCapacityIdle(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
        }
        ThrowTerminalFailures(failures);
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
        ThrowIfRequestIdsExhausted();
        // Preserve one ordinary increment on the steady path. The permanent latch, not the
        // counter's signed value after a racing increment, forbids any later allocation/reuse.
        var id = Interlocked.Increment(ref _nextId);
        if (id == long.MaxValue || id <= 0)
            SealRequestIdAllocation();
        if (id <= 0)
            throw new RequestIdsExhaustedException();
        return id;
    }

    private void ThrowIfRequestIdsExhausted()
    {
        if (RequestIdsExhausted)
            throw new RequestIdsExhaustedException();
    }

    private void SealRequestIdAllocation()
    {
        if (Interlocked.Exchange(ref _requestIdsExhausted, 1) != 0)
            return;
        try
        {
            _owner.OnRequestIdsExhausted();
        }
        finally
        {
            // Waiters fail locally instead of waiting for an unrelated long-lived stream.
            // Each awakened waiter relays the signal in its existing finally path.
            if (Volatile.Read(ref _waiterCount) != 0)
                SignalSlotAvailable();
        }
    }

    private sealed class RequestIdsExhaustedException : SharpLinkException
    {
        internal RequestIdsExhaustedException()
            : base(SharpLinkErrorCode.Unavailable, SharpLinkErrorDetails.Unavailable.SessionBindingDraining,
                "The physical session has exhausted its request ID domain and is draining.")
        {
        }
    }

    private bool TryTakeExpiredCallAtIndex(
        PendingCall?[] slots,
        int index,
        PendingCall expected,
        long expectedId,
        out PendingCall? call)
    {
        lock (expected.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), expected) ||
                expected.Id != expectedId ||
                !expected.Deadline.HasValue ||
                !expected.Deadline.IsExpired(_timeProvider))
            {
                call = null;
                return false;
            }

            if (!TryRemoveTerminalCall(slots, index, expected))
            {
                call = null;
                return false;
            }

            expected.WaitUntilRegistered();
            expected.StreamGroup?.ResolveHeadUnderCompletionGate(accepted: false);
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
            return;

        for (var index = 0; index < slots.Length; index++)
        {
            var call = Volatile.Read(ref slots[index]);
            if (call is null)
                continue;

            // This first sample is only a cheap candidate filter. The request identity,
            // authoritative deadline check and slot removal are revalidated together below.
            var expectedId = call.Id;
            var deadline = call.Deadline;
            if (!deadline.HasValue)
                continue;
            if (deadline.IsExpired(_timeProvider))
            {
                if (!TryTakeExpiredCallAtIndex(slots, index, call, expectedId, out var expiredCall))
                    continue;

                var emptyPayload = ReadOnlySequence<byte>.Empty;
                CompleteTakenCall(
                    expiredCall!, PendingCallCompletionReason.DeadlineExceeded, exception: null, ref emptyPayload);
            }
            else
            {
                _deadlineScheduler.Observe(deadline);
            }
        }
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

    internal sealed class PendingCall
    {
        private const int MaxRetained = 4096;
        private static readonly ConcurrentQueue<PendingCall> Pool = new();
        private static int s_retainedCount;

        private PendingRequestTable? _table;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationTokenSource? _producerCancellation;
        private IPendingCallCompletionObserver? _completionObserver;
        private int _registered;
        private bool _requestPublished;
        private bool _producerLifetimeAttached;
        private bool _producerLifetimeEverAttached;
        private bool _terminalProcessingComplete;
        private bool _finalCleanupClaimed;

        internal ClientStreamPublicationGroup? StreamGroup;
        internal TaskCompletionSource<bool>? AcceptanceReady;
        internal bool PeerAccepted;
        internal bool ProducerStarted;

        public object CompletionGate { get; } = new();

        public long Id { get; private set; }
        public PendingCallKind Kind { get; private set; }
        internal bool IsProducing => Kind is PendingCallKind.ClientStreaming or
            PendingCallKind.DuplexStreaming or PendingCallKind.OneWayClientStreaming;
        public IRpcOperation? Operation { get; private set; }
        public IStreamDispatcher? Dispatcher { get; private set; }
        public RpcDeadline Deadline { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public CancellationToken ProducerCancellationToken
            => _producerCancellation?.Token ?? CancellationToken.None;
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

        public void MarkRequestPublished() => _requestPublished = true;

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
            call.StreamGroup = null;
            call.PeerAccepted = false;
            call.ProducerStarted = false;
            call.AcceptanceReady = kind is PendingCallKind.ClientStreaming or PendingCallKind.DuplexStreaming
                ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            call._producerLifetimeAttached = false;
            call._producerLifetimeEverAttached = false;
            call._terminalProcessingComplete = false;
            call._finalCleanupClaimed = false;
            call.Id = id;
            call.Kind = kind;
            call.Operation = operation;
            call.Dispatcher = dispatcher;
            call.Deadline = deadline;
            call.CancellationToken = cancellationToken;
            call._completionObserver = completionObserver;
            call._producerCancellation = kind is
                PendingCallKind.ClientStreaming or
                PendingCallKind.DuplexStreaming or
                PendingCallKind.OneWayClientStreaming
                ? new CancellationTokenSource()
                : null;
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

        // Caller owns CompletionGate and has checked exact slot/full-ID publication.
        internal bool TryAttachProducer(out CancellationToken token)
        {
            token = default;
            if (_producerLifetimeEverAttached || _terminalProcessingComplete || _finalCleanupClaimed ||
                _producerCancellation is null) return false;
            token = _producerCancellation.Token;
            _producerLifetimeEverAttached = true;
            _producerLifetimeAttached = true;
            return true;
        }

        public Exception? CancelProducer(PendingCallCompletionReason reason)
        {
            var source = _producerCancellation;
            if (source is null || reason == PendingCallCompletionReason.LocalStreamComplete) return null;
            try { source.Cancel(); return null; }
            catch (Exception failure) { return failure; }
            // Disposal belongs to the joint logical/physical final claimant, not Cancel.
        }

        internal void ReleaseProducerLifetime(PendingRequestTable table, long id)
        {
            CancellationTokenSource? source;
            lock (CompletionGate)
            {
                if (!_producerLifetimeAttached || Id != id || !ReferenceEquals(_table, table)) return;
                _producerLifetimeAttached = false;
                if (!TryClaimFinalCleanup(out source)) return;
            }
            FinishFinalCleanup(source);
        }

        internal void CompleteTerminalProcessing()
        {
            // Nonproducing calls cannot acquire this attachment; preserve their direct
            // terminal pool return without an added ordinary-unary gate transition.
            if (!IsProducing) { ReturnCore(); return; }
            CancellationTokenSource? source;
            lock (CompletionGate)
            {
                _terminalProcessingComplete = true;
                // These roots have completed their final actual use, even if the original
                // producer group must retain this PendingCall and its CTS much longer.
                Operation = null;
                Dispatcher = null;
                _completionObserver = null;
                _cancellationRegistration = default;
                CancellationToken = CancellationToken.None;
                if (!TryClaimFinalCleanup(out source)) return;
            }
            FinishFinalCleanup(source);
        }

        private bool TryClaimFinalCleanup(out CancellationTokenSource? source)
        {
            source = null;
            if (!_terminalProcessingComplete || _producerLifetimeAttached || _finalCleanupClaimed) return false;
            _finalCleanupClaimed = true;
            source = _producerCancellation;
            _producerCancellation = null;
            return true;
        }

        private void FinishFinalCleanup(CancellationTokenSource? source)
        {
            // The final claimant owns the ORIGINAL registrar capacity unit. No new
            // counter or reservation is acquired for the producer, task set or fence.
            var table = _table!;
            List<Exception>? failures = null;
            try { source?.Dispose(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            try { table.ReleaseCapacity(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            try { ReturnCore(); }
            catch (Exception failure) { AddTerminalFailure(ref failures, failure); }
            ThrowTerminalFailures(failures);
        }

        public void ReturnUnused()
        {
            try { _cancellationRegistration.Dispose(); }
            finally
            {
                try { _producerCancellation?.Dispose(); }
                finally { _producerCancellation = null; ReturnCore(); }
            }
        }

        private void ReturnCore()
        {
            StreamGroup = null;
            AcceptanceReady = null;
            PeerAccepted = false;
            ProducerStarted = false;
            _table = null;
            Id = 0;
            Kind = default;
            Operation = null;
            Dispatcher = null;
            Deadline = default;
            CancellationToken = CancellationToken.None;
            _producerCancellation = null;
            _completionObserver = null;
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
