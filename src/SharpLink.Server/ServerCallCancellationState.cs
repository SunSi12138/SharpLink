namespace SharpLink.Server;

internal enum ServerCallCancellationReason : byte
{
    None,
    RemoteCancel,
    DeadlineExceeded,
    ServerStopping,
    ConnectionClosed,
    Completed
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
    private CancellationTokenRegistration _deadlineRegistration;
    private CancellationTokenRegistration _serverStoppingRegistration;
    private CancellationTokenRegistration _connectionClosedRegistration;
    private int _reason;
    private int _abandonedRecorded;
    private bool _disposeRequested;
    private int _externalUsers;

    private ServerCallCancellationState()
    {
    }

    public long RequestId { get; private set; }

    public DateTimeOffset? Deadline { get; private set; }

    public CancellationToken InvocationToken
        => _invocationCancellation?.Token ?? CancellationToken.None;

    public ServerCallCancellationReason Reason
        => (ServerCallCancellationReason)Volatile.Read(ref _reason);

    public bool IsAbandoned => Reason is not (ServerCallCancellationReason.None or ServerCallCancellationReason.Completed);

    public static ServerCallCancellationState Rent(
        long requestId,
        DateTimeOffset? deadline,
        CancellationToken connectionClosedToken,
        CancellationToken serverStoppingToken,
        bool supportsCooperativeCancellation)
    {
        if (!Pool.TryPop(out var state))
            state = new ServerCallCancellationState();
        else
            Interlocked.Decrement(ref s_retainedCount);

        state.RequestId = requestId;
        state.Deadline = deadline;
        state._reason = (int)ServerCallCancellationReason.None;
        state._abandonedRecorded = 0;
        state._disposeRequested = false;
        state._externalUsers = 0;
        state._deadlineRegistration = default;
        state._serverStoppingRegistration = default;
        state._connectionClosedRegistration = default;
        state._invocationCancellation = supportsCooperativeCancellation || deadline is not null
            ? new CancellationTokenSource()
            : null;

        if (deadline is { } absoluteDeadline)
        {
            var cancellation = state._invocationCancellation!;
            state._deadlineRegistration = cancellation.Token.UnsafeRegister(
                static callbackState =>
                    ((ServerCallCancellationState)callbackState!).InvokeCancellation(
                        ServerCallCancellationReason.DeadlineExceeded),
                state);
            var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                state.TryCancel(ServerCallCancellationReason.DeadlineExceeded);
            else
                cancellation.CancelAfter(remaining);
        }

        // Register server shutdown before connection closure so forced server shutdown has a
        // deterministic reason when both tokens have already been canceled.
        if (serverStoppingToken.CanBeCanceled)
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
                    ((ServerCallCancellationState)callbackState!).InvokeCancellation(
                        ServerCallCancellationReason.ConnectionClosed),
                state);
        }

        return state;
    }

    public bool TryAcquire(long expectedRequestId)
    {
        lock (_lifetimeGate)
        {
            if (_disposeRequested || RequestId != expectedRequestId)
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

        if (Deadline is { } deadline && deadline <= DateTimeOffset.UtcNow)
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
        _connectionClosedRegistration.Dispose();
        _serverStoppingRegistration.Dispose();
        _deadlineRegistration.Dispose();
        _invocationCancellation?.Dispose();
        _invocationCancellation = null;
        _connectionClosedRegistration = default;
        _serverStoppingRegistration = default;
        _deadlineRegistration = default;
        RequestId = 0;
        Deadline = null;
        _reason = (int)ServerCallCancellationReason.None;
        _abandonedRecorded = 0;

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
