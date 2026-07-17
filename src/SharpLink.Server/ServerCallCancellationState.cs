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
/// Owns cancellation and terminal-response eligibility for one server invocation.
/// </summary>
internal sealed class ServerCallCancellationState : IDisposable
{
    private const int MaxRetained = 4096;
    private static readonly ConcurrentStack<ServerCallCancellationState> Pool = new();
    private static int s_retainedCount;

    private readonly Lock _cancellationGate = new();
    private CancellationTokenSource? _invocationCancellation;
    private CancellationToken _serverLoopToken;
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
        => _invocationCancellation?.Token ?? _serverLoopToken;

    public ServerCallCancellationReason Reason
        => (ServerCallCancellationReason)Volatile.Read(ref _reason);

    public bool IsAbandoned => Reason is not (ServerCallCancellationReason.None or ServerCallCancellationReason.Completed);

    public static ServerCallCancellationState Rent(
        long requestId,
        DateTimeOffset? deadline,
        CancellationToken serverLoopToken,
        bool supportsCooperativeCancellation)
    {
        if (!Pool.TryPop(out var state))
            state = new ServerCallCancellationState();
        else
            Interlocked.Decrement(ref s_retainedCount);

        state.RequestId = requestId;
        state.Deadline = deadline;
        state._serverLoopToken = serverLoopToken;
        state._reason = (int)ServerCallCancellationReason.None;
        state._abandonedRecorded = 0;
        state._disposeRequested = false;
        state._externalUsers = 0;
        if (supportsCooperativeCancellation)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverLoopToken);
            if (deadline is { } absoluteDeadline)
            {
                var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    cancellation.Cancel();
                else
                    cancellation.CancelAfter(remaining);
            }
            state._invocationCancellation = cancellation;
        }
        else
        {
            state._invocationCancellation = null;
        }
        return state;
    }

    public bool TryAcquire(long expectedRequestId)
    {
        lock (_cancellationGate)
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
        lock (_cancellationGate)
        {
            if (--_externalUsers < 0)
                throw new InvalidOperationException("Server call cancellation state use count underflowed.");
            shouldDispose = _disposeRequested && _externalUsers == 0;
        }
        if (shouldDispose)
            ReturnCore();
    }

    public bool TryAbandon(ServerCallCancellationReason reason)
    {
        if (reason is ServerCallCancellationReason.None or ServerCallCancellationReason.Completed)
            throw new ArgumentOutOfRangeException(nameof(reason));

        if (Interlocked.CompareExchange(ref _reason, (int)reason, (int)ServerCallCancellationReason.None) !=
            (int)ServerCallCancellationReason.None)
        {
            return false;
        }

        _invocationCancellation?.Cancel();
        return true;
    }

    public bool TryClaimResponse()
    {
        if (Reason != ServerCallCancellationReason.None)
            return false;

        if (Deadline is { } deadline && deadline <= DateTimeOffset.UtcNow)
        {
            TryAbandon(ServerCallCancellationReason.DeadlineExceeded);
            return false;
        }

        if (_serverLoopToken.IsCancellationRequested)
        {
            TryAbandon(ServerCallCancellationReason.ConnectionClosed);
            return false;
        }

        // CancelAfter uses a timer and may become observable a few scheduler ticks before the
        // wall-clock comparison reaches the serialized absolute deadline. It is still the
        // deadline source owned by this state, not an arbitrary service cancellation.
        if (Deadline is not null && _invocationCancellation?.IsCancellationRequested == true)
        {
            TryAbandon(ServerCallCancellationReason.DeadlineExceeded);
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
        lock (_cancellationGate)
        {
            if (_disposeRequested)
                return;
            _disposeRequested = true;
            shouldDispose = _externalUsers == 0;
        }
        if (shouldDispose)
            ReturnCore();
    }

    private void ReturnCore()
    {
        _invocationCancellation?.Dispose();
        _invocationCancellation = null;
        RequestId = 0;
        Deadline = null;
        _serverLoopToken = CancellationToken.None;
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
