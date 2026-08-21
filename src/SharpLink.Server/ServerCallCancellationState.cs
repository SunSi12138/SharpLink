namespace SharpLink.Server;

internal enum ServerCallCancellationReason : byte
{
    None,
    RemoteCancel,
    ConsumerAbandoned,
    DeadlineExceeded,
    ModuleDraining,
    ServerStopping,
    ConnectionClosed,
    AdmissionResourceExhausted,
    Completed
}

/// <summary>
/// An immutable lookup/snapshot lease that binds a pooled call state to one request generation.
/// </summary>
internal readonly struct ServerCallCancellationLease
{
    private readonly ServerCallCancellationState? _state;

    internal ServerCallCancellationLease(
        ServerCallCancellationState state,
        long requestId,
        long generation)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        RequestId = requestId;
        Generation = generation;
    }

    internal long RequestId { get; }

    internal long Generation { get; }

    internal ServerCallCancellationState State
        => _state ?? throw new InvalidOperationException("The server call cancellation lease is empty.");

    internal bool TryAcquire()
        => _state?.TryAcquire(RequestId, Generation) == true;

    internal void ReleaseUse() => State.ReleaseUse();
}

/// <summary>
/// Owns cancellation, deadline timing and terminal-response eligibility for one server invocation.
/// The first terminal source wins and all later sources become no-ops.
/// </summary>
internal sealed class ServerCallCancellationState : IDisposable
{
    private const int MaxRetained = 4096;
    private static readonly ConcurrentStack<ServerCallCancellationState> Pool = new();
    private static int s_retainedCount;

    private readonly Lock _lifetimeGate = new();
    private CancellationTokenSource? _invocationCancellation;
    private CancellationTokenRegistration _serverStoppingRegistration;
    private CancellationTokenRegistration _connectionClosedRegistration;
    private CancellationTokenRegistration _moduleDrainingRegistration;
    private CancellationToken _serverStoppingToken;
    private int _reason;
    private int _abandonedRecorded;
    private int _moduleDrainResponseClaimed;
    private bool _acceptsRemoteCancellation;
    private bool _serverStoppingFlowsThroughConnection;
    private bool _disposeRequested;
    private int _externalUsers;
    private long _leaseGeneration;
    private AdmissionLease? _admissionLease;
    private SharpLinkBufferWriterPool? _payloadPool;
    private IRpcByteBufferWriter? _payloadOwner;
    private TimeProvider? _timeProvider;

    private ServerCallCancellationState()
    {
    }

    public long RequestId { get; private set; }

    public RpcDeadline Deadline { get; private set; }

    public CancellationToken InvocationToken
        => _invocationCancellation?.Token ?? CancellationToken.None;

    public ServerCallCancellationReason Reason
        => (ServerCallCancellationReason)Volatile.Read(ref _reason);

    public bool IsAbandoned => Reason is not (ServerCallCancellationReason.None or ServerCallCancellationReason.Completed);

    internal bool AcceptsRemoteCancellation => _acceptsRemoteCancellation;

    public static ServerCallCancellationState Rent(
        long requestId,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken connectionClosedToken,
        CancellationToken serverStoppingToken,
        bool supportsCooperativeCancellation,
        bool acceptsRemoteCancellation = true,
        bool serverStoppingFlowsThroughConnection = false)
        => Rent(
            requestId,
            deadline,
            timeProvider,
            connectionClosedToken,
            serverStoppingToken,
            CancellationToken.None,
            supportsCooperativeCancellation,
            acceptsRemoteCancellation,
            serverStoppingFlowsThroughConnection);

    public static ServerCallCancellationState Rent(
        long requestId,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken connectionClosedToken,
        CancellationToken serverStoppingToken,
        CancellationToken moduleDrainingToken,
        bool supportsCooperativeCancellation,
        bool acceptsRemoteCancellation = true,
        bool serverStoppingFlowsThroughConnection = false)
    {
        if (!Pool.TryPop(out var state))
            state = new ServerCallCancellationState();
        else
            Interlocked.Decrement(ref s_retainedCount);

        _ = Interlocked.Increment(ref state._leaseGeneration);
        state.RequestId = requestId;
        state.Deadline = deadline;
        state._timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        state._reason = (int)ServerCallCancellationReason.None;
        state._abandonedRecorded = 0;
        state._moduleDrainResponseClaimed = 0;
        state._acceptsRemoteCancellation = acceptsRemoteCancellation;
        state._serverStoppingToken = serverStoppingToken;
        state._serverStoppingFlowsThroughConnection = serverStoppingFlowsThroughConnection;
        state._admissionLease = null;
        state._payloadPool = null;
        state._payloadOwner = null;
        state._disposeRequested = false;
        state._externalUsers = 0;
        state._serverStoppingRegistration = default;
        state._connectionClosedRegistration = default;
        state._moduleDrainingRegistration = default;
        state._invocationCancellation = supportsCooperativeCancellation
            ? new CancellationTokenSource()
            : null;
        if (moduleDrainingToken.CanBeCanceled)
        {
            state._moduleDrainingRegistration = moduleDrainingToken.UnsafeRegister(
                static callbackState =>
                    ((ServerCallCancellationState)callbackState!).InvokeCancellation(
                        ServerCallCancellationReason.ModuleDraining),
                state);
        }
        // ServerConnectionState links force-stop into the connection token. Production call
        // states can therefore avoid a second registration on the same stop source while still
        // distinguishing force-stop from an independent connection close in the callback below.
        if (serverStoppingToken.CanBeCanceled && !serverStoppingFlowsThroughConnection)
        {
            state._serverStoppingRegistration = serverStoppingToken.UnsafeRegister(
                static callbackState =>
                    ((ServerCallCancellationState)callbackState!).InvokeCancellation(
                        ServerCallCancellationReason.ServerStopping),
                state);
        }
        if (connectionClosedToken.CanBeCanceled)
        {
            state._connectionClosedRegistration = connectionClosedToken.UnsafeRegister(
                static callbackState =>
                    ((ServerCallCancellationState)callbackState!).InvokeConnectionCancellation(),
                state);
        }

        return state;
    }

    internal void AttachAdmissionLease(AdmissionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (Interlocked.CompareExchange(ref _admissionLease, lease, null) is not null)
            throw new InvalidOperationException("An admission lease is already attached to this call.");
    }

    internal void AttachPayloadOwner(
        SharpLinkBufferWriterPool pool,
        IRpcByteBufferWriter owner)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(owner);
        if (Interlocked.CompareExchange(ref _payloadOwner, owner, null) is not null)
            throw new InvalidOperationException("A retained payload owner is already attached to this call.");
        _payloadPool = pool;
    }

    internal ServerCallCancellationLease CaptureLease(long requestId)
        => new(this, requestId, Volatile.Read(ref _leaseGeneration));

    internal bool TryAcquire(long expectedRequestId, long expectedGeneration)
    {
        lock (_lifetimeGate)
        {
            if (_disposeRequested || RequestId != expectedRequestId ||
                _leaseGeneration != expectedGeneration)
                return false;
            _externalUsers++;
            return true;
        }
    }

    public void ReleaseUse()
    {
        var shouldDispose = false;
        lock (_lifetimeGate)
        {
            if (--_externalUsers < 0)
                throw new InvalidOperationException("Server call cancellation state use count underflowed.");
            shouldDispose = _disposeRequested && _externalUsers == 0;
        }
        if (shouldDispose)
            ReturnCore();
    }

    public bool TryCancel(ServerCallCancellationReason reason)
    {
        if (reason is ServerCallCancellationReason.None or ServerCallCancellationReason.Completed)
            throw new ArgumentOutOfRangeException(nameof(reason));

        if (Interlocked.CompareExchange(ref _reason, (int)reason, (int)ServerCallCancellationReason.None) !=
            (int)ServerCallCancellationReason.None)
        {
            return false;
        }

        try
        {
            _invocationCancellation?.Cancel();
        }
        catch
        {
            // User cancellation callbacks cannot be allowed to escape into a protocol loop,
            // timer callback or server shutdown path. Cancellation remains observable.
        }
        return true;
    }

    public bool TryClaimResponse()
    {
        if (Reason != ServerCallCancellationReason.None)
            return false;

        if (Deadline.IsExpired(_timeProvider ?? throw new InvalidOperationException(
                "Server call state has no time provider.")))
        {
            TryCancel(ServerCallCancellationReason.DeadlineExceeded);
            return false;
        }

        return Interlocked.CompareExchange(
                   ref _reason,
                   (int)ServerCallCancellationReason.Completed,
                   (int)ServerCallCancellationReason.None) ==
               (int)ServerCallCancellationReason.None;
    }

    public bool TryRecordAbandoned()
        => IsAbandoned && Interlocked.Exchange(ref _abandonedRecorded, 1) == 0;

    public bool TryClaimModuleDrainResponse()
        => Reason == ServerCallCancellationReason.ModuleDraining &&
           Interlocked.Exchange(ref _moduleDrainResponseClaimed, 1) == 0;

    public void Dispose()
    {
        var shouldDispose = false;
        lock (_lifetimeGate)
        {
            if (_disposeRequested)
                return;
            _disposeRequested = true;
            shouldDispose = _externalUsers == 0;
        }
        if (shouldDispose)
            ReturnCore();
    }

    private void InvokeConnectionCancellation()
    {
        var reason = _serverStoppingFlowsThroughConnection && _serverStoppingToken.IsCancellationRequested
            ? ServerCallCancellationReason.ServerStopping
            : ServerCallCancellationReason.ConnectionClosed;
        InvokeCancellation(reason);
    }

    private void InvokeCancellation(ServerCallCancellationReason reason)
    {
        lock (_lifetimeGate)
        {
            if (_disposeRequested)
                return;
            _externalUsers++;
        }

        try
        {
            TryCancel(reason);
        }
        finally
        {
            ReleaseUse();
        }
    }

    private void ReturnCore()
    {
        _moduleDrainingRegistration.Dispose();
        _connectionClosedRegistration.Dispose();
        _serverStoppingRegistration.Dispose();
        _invocationCancellation?.Dispose();
        Interlocked.Exchange(ref _admissionLease, null)?.Dispose();
        var payloadOwner = Interlocked.Exchange(ref _payloadOwner, null);
        var payloadPool = Interlocked.Exchange(ref _payloadPool, null);
        if (payloadOwner is not null)
            (payloadPool ?? throw new InvalidOperationException("A retained payload has no owning pool."))
                .Return(payloadOwner);
        _invocationCancellation = null;
        _connectionClosedRegistration = default;
        _serverStoppingRegistration = default;
        _moduleDrainingRegistration = default;
        _serverStoppingToken = default;
        RequestId = 0;
        Deadline = default;
        _timeProvider = null;
        _reason = (int)ServerCallCancellationReason.None;
        _abandonedRecorded = 0;
        _moduleDrainResponseClaimed = 0;
        _acceptsRemoteCancellation = false;
        _serverStoppingFlowsThroughConnection = false;

        while (true)
        {
            var retained = Volatile.Read(ref s_retainedCount);
            if (retained >= MaxRetained)
                return;
            if (Interlocked.CompareExchange(ref s_retainedCount, retained + 1, retained) == retained)
                break;
        }
        Pool.Push(this);
    }
}
