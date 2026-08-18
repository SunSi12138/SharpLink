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
    Exception? Exception);

internal interface IPendingCallOwner
{
    void OnPendingCallRegistered();
    void OnPendingCallCompleted(in PendingCallCompletion completion);
    void OnProducerCancellationCallbackFailed(Exception exception);
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
internal sealed class PendingRequestTable : IDisposable
{
    private readonly int _indexMask;
    private readonly PendingCall?[] _slots;
    private readonly IRpcCodecProvider _codecProvider;
    private readonly IPendingCallOwner _owner;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _slotAvailable;
    private readonly ITimer _deadlineTimer;
    private long _nextId;
    private long _approximateEarliestDeadline = long.MaxValue;
    private int _deadlineScanRunning;
    private int _activeSlots;
    private int _waiterCount;
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

        _slots = new PendingCall?[capacity];
        _indexMask = capacity - 1;
        _codecProvider = codecProvider;
        _owner = owner;
        _timeProvider = timeProvider;
        _slotAvailable = new SemaphoreSlim(0, capacity);
        _deadlineTimer = _timeProvider.CreateTimer(
            static state => ((PendingRequestTable)state!).ScanExpiredDeadlines(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public int Capacity => _slots.Length;

    public int Count
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _slots.Length; index++)
                if (Volatile.Read(ref _slots[index]) is not null)
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
        bool responseNullable = false)
    {
        ArgumentNullException.ThrowIfNull(responseCodec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRent(
                responseCodec, kind, deadline, cancellationToken, hasResponsePayload, responseNullable,
                completionObserver, out id, out var operation))
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
                Interlocked.Decrement(ref _waiterCount);
            }

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
                responseNullable: false))
        {
            return new PendingRequestLease<RpcEmptyRequest>(id, operation);
        }

        operation.ReturnError();
        throw CreateResourceExhaustedException();
    }

    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref _slots[index]);
        if (current is not null && current.Id == id &&
            current.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            // A successful Response is only the server's acknowledgement; StreamComplete owns
            // the terminal transition for server and duplex streams. The callback shares the
            // per-call completion gate with terminal removal, so a matching acknowledgement is
            // observed before cancellation, deadline, or disconnect can report the terminal result.
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current) ||
                    current.Id != id ||
                    current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
                {
                    return false;
                }

                current.CompletionObserver?.OnResponseObserved();
                return true;
            }
        }

        if (!TryTakeMatchingCall(id, out var call))
            return false;

        CompleteTakenCall(call!, PendingCallCompletionReason.Response, exception: null, ref payload);
        return true;
    }

    public bool DispatchError(long id, Exception exception)
        => TryComplete(id, PendingCallCompletionReason.RemoteError, exception);

    public bool TryComplete(
        long id,
        PendingCallCompletionReason reason,
        Exception? exception = null)
    {
        if (!TryTakeMatchingCall(id, out var call))
            return false;

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(call!, reason, exception, ref emptyPayload);
        return true;
    }

    public bool Contains(long id)
    {
        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);
        return call is not null && call.Id == id;
    }

    public CancellationToken GetProducerCancellationToken(long id)
    {
        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);
        if (call is null || call.Id != id)
            return new CancellationToken(canceled: true);
        return call.ProducerCancellationToken;
    }

    public long AllocateRequestId()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return NextRequestId();
    }

    public void FailAllPendingRequests(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var index = 0; index < _slots.Length; index++)
        {
            if (!TryTakeCallAtIndex(index, out var call))
                continue;

            var payload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(
                call!,
                PendingCallCompletionReason.ConnectionClosed,
                exception,
                ref payload);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        FailAllPendingRequests(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "Pending request table is disposed."));
        _deadlineTimer.Dispose();
        _slotAvailable.Dispose();
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
        out RpcRequestOperation<T> operation)
    {
        operation = RpcOperationPool<T>.Rent();
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
                responseNullable))
        {
            return true;
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
        bool responseNullable)
    {
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        var published = false;
        try
        {
            for (var attempt = 0; attempt < _slots.Length; attempt++)
            {
                id = NextRequestId();
                var index = (int)(id & _indexMask);
                if (Volatile.Read(ref _slots[index]) is not null)
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
                if (Interlocked.CompareExchange(ref _slots[index], call, null) is null)
                {
                    published = true;
                    OnRegistered(call);
                    CompleteRegistrationIfDisposed(call);
                    return true;
                }

                call.ReturnUnused();
            }

            id = 0;
            return false;
        }
        finally
        {
            if (!published)
                ReleaseCapacity();
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
            for (var attempt = 0; attempt < _slots.Length; attempt++)
            {
                id = NextRequestId();
                var index = (int)(id & _indexMask);
                if (Volatile.Read(ref _slots[index]) is not null)
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
                if (Interlocked.CompareExchange(ref _slots[index], call, null) is null)
                {
                    published = true;
                    OnRegistered(call);
                    CompleteRegistrationIfDisposed(call);
                    return true;
                }

                call.ReturnUnused();
            }

            id = 0;
            return false;
        }
        finally
        {
            if (!published)
                ReleaseCapacity();
        }
    }

    private bool TryAcquireCapacity()
    {
        while (true)
        {
            var active = Volatile.Read(ref _activeSlots);
            if (active >= _slots.Length)
                return false;
            if (Interlocked.CompareExchange(ref _activeSlots, active + 1, active) == active)
                return true;
        }
    }

    private void OnRegistered(PendingCall call)
    {
        SharpLinkTelemetry.AddPendingRequests(1);
        _owner.OnPendingCallRegistered();
        call.MarkRegistered();
        if (call.Deadline.HasValue)
            UpdateEarliestDeadline(call.Deadline.Timestamp);
        if (call.CancellationToken.IsCancellationRequested)
            TryComplete(call.Id, PendingCallCompletionReason.UserCancellation);
    }

    private void CompleteRegistrationIfDisposed(PendingCall call)
    {
        if (Volatile.Read(ref _disposed) != 0)
            TryComplete(call.Id, PendingCallCompletionReason.ConnectionClosed);
    }

    private bool TryTakeMatchingCall(long id, out PendingCall? call)
    {
        var index = (int)(id & _indexMask);
        while (true)
        {
            var current = Volatile.Read(ref _slots[index]);
            if (current is null || current.Id != id)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current) || current.Id != id)
                    continue;

                var exchanged = Interlocked.CompareExchange(ref _slots[index], null, current);
                if (!ReferenceEquals(exchanged, current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                ReleaseSlot();
                return true;
            }
        }
    }

    private bool TryTakeCallAtIndex(int index, out PendingCall? call)
    {
        while (true)
        {
            var current = Volatile.Read(ref _slots[index]);
            if (current is null)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current))
                    continue;

                if (!ReferenceEquals(Interlocked.CompareExchange(ref _slots[index], null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                ReleaseSlot();
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
            exception);
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
        Interlocked.Decrement(ref _activeSlots);
        if (Volatile.Read(ref _waiterCount) == 0)
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

    private long NextRequestId()
    {
        var id = Interlocked.Increment(ref _nextId);
        return id != 0 ? id : Interlocked.Increment(ref _nextId);
    }

    private void UpdateEarliestDeadline(long deadlineTimestamp)
    {
        while (true)
        {
            var current = Volatile.Read(ref _approximateEarliestDeadline);
            if (current <= deadlineTimestamp)
                return;
            if (Interlocked.CompareExchange(
                    ref _approximateEarliestDeadline,
                    deadlineTimestamp,
                    current) != current)
            {
                continue;
            }

            ArmDeadlineTimer(deadlineTimestamp);
            return;
        }
    }

    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _deadlineScanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var now = _timeProvider.GetTimestamp();
            for (var index = 0; index < _slots.Length; index++)
            {
                var call = Volatile.Read(ref _slots[index]);
                if (call is null || !call.Deadline.HasValue)
                    continue;
                if (call.Deadline.Timestamp <= now)
                    TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                else
                    UpdateEarliestDeadline(call.Deadline.Timestamp);
            }
        }
        finally
        {
            Volatile.Write(ref _deadlineScanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }

    private void ArmDeadlineTimer(long deadlineTimestamp)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var delay = RpcDeadline.GetRemaining(
            deadlineTimestamp,
            _timeProvider.GetTimestamp(),
            _timeProvider.TimestampFrequency);
        if (delay > SharpLinkTimer.MaximumDelay)
            delay = SharpLinkTimer.MaximumDelay;
        try
        {
            _deadlineTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
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

    private sealed class PendingCall
    {
        private const int MaxRetained = 4096;
        private static readonly ConcurrentQueue<PendingCall> Pool = new();
        private static int s_retainedCount;

        private PendingRequestTable? _table;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationTokenSource? _producerCancellation;
        private IPendingCallCompletionObserver? _completionObserver;
        private int _registered;

        public object CompletionGate { get; } = new();

        public long Id { get; private set; }
        public PendingCallKind Kind { get; private set; }
        public IRpcOperation? Operation { get; private set; }
        public IStreamDispatcher? Dispatcher { get; private set; }
        public RpcDeadline Deadline { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public CancellationToken ProducerCancellationToken
            => _producerCancellation?.Token ?? CancellationToken.None;
        public IPendingCallCompletionObserver? CompletionObserver => _completionObserver;

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

        public Exception? CancelProducer(PendingCallCompletionReason reason)
        {
            var producerCancellation = _producerCancellation;
            if (producerCancellation is null)
                return null;
            _producerCancellation = null;
            if (reason == PendingCallCompletionReason.LocalStreamComplete)
            {
                producerCancellation.Dispose();
                return null;
            }
            return CancelAndDisposeProducer(producerCancellation);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception? CancelAndDisposeProducer(
            CancellationTokenSource producerCancellation)
        {
            Exception? callbackFailure = null;
            try
            {
                producerCancellation.Cancel();
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
            }
            finally
            {
                producerCancellation.Dispose();
            }
            return callbackFailure;
        }

        public void ReturnUnused()
        {
            _cancellationRegistration.Dispose();
            _producerCancellation?.Dispose();
            _producerCancellation = null;
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