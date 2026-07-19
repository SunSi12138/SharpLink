namespace SharpLink.Runtime;

internal sealed class SharedMemoryAsyncPulse : IValueTaskSource<bool>
{
    private readonly Lock _gate = new();
    private ManualResetValueTaskSourceCore<bool> _source = new()
    {
        RunContinuationsAsynchronously = true
    };
    private int _signalState;
    private int _waiterState;
    private int _completed;

    public ValueTask<bool> WaitAsync()
    {
        if (Interlocked.Exchange(ref _signalState, 0) != 0)
            return ValueTask.FromResult(true);
        if (Volatile.Read(ref _completed) != 0)
            return ValueTask.FromResult(false);

        lock (_gate)
        {
            if (Interlocked.Exchange(ref _signalState, 0) != 0)
                return ValueTask.FromResult(true);
            if (Volatile.Read(ref _completed) != 0)
                return ValueTask.FromResult(false);
            if (Interlocked.CompareExchange(ref _waiterState, 1, 0) != 0)
                throw new InvalidOperationException("Only one shared-memory pulse waiter is supported.");
            if (Interlocked.Exchange(ref _signalState, 0) != 0)
            {
                Volatile.Write(ref _waiterState, 0);
                return ValueTask.FromResult(true);
            }

            _source.Reset();
            return new ValueTask<bool>(this, _source.Version);
        }
    }

    public void Pulse()
    {
        if (Volatile.Read(ref _completed) != 0 ||
            Interlocked.Exchange(ref _signalState, 1) != 0 ||
            Volatile.Read(ref _waiterState) == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (Interlocked.Exchange(ref _waiterState, 0) == 0)
                return;
            Interlocked.Exchange(ref _signalState, 0);
            _source.SetResult(true);
        }
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        lock (_gate)
        {
            if (Interlocked.Exchange(ref _waiterState, 0) == 0)
                return;
            Interlocked.Exchange(ref _signalState, 0);
            _source.SetResult(false);
        }
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _source.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
        => _source.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);
}
