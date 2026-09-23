using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace SharpLink.FlowStatePhaseB;

internal interface IOwnerCommand
{
    void Execute();
    void Fail(Exception error);
}

// Completion ownership is independent of credit/state ownership. A slot remains
// busy until GetResult, not merely until SetResult. Publication and consumption
// serialize so Reset cannot race the producer's final access to the value source.
internal abstract class ReusableOwnerCommand<T> : IOwnerCommand, IValueTaskSource<T>
{
    private readonly Lock _completionGate = new();
    private ManualResetValueTaskSourceCore<T> _source = new() { RunContinuationsAsynchronously = true };
    private CancellationTokenRegistration _registration;
    private bool _busy;
    private bool _consumed;

    internal bool IsBusy => Volatile.Read(ref _busy);

    protected ValueTask<T> Begin(CancellationToken token = default, Action<object?>? wake = null, object? owner = null)
    {
        lock (_completionGate)
        {
            if (_busy) throw new InvalidOperationException("The previous completion has not been consumed.");
            _source.Reset();
            _consumed = false;
            Volatile.Write(ref _busy, true);
            _registration = wake is null ? default : token.UnsafeRegister(wake, owner);
            return new ValueTask<T>(this, _source.Version);
        }
    }

    internal void Complete(T result)
    {
        lock (_completionGate) _source.SetResult(result);
    }

    public void Fail(Exception error)
    {
        lock (_completionGate) _source.SetException(error);
    }

    public abstract void Execute();
    protected virtual void OnConsumed() => Volatile.Write(ref _busy, false);

    public T GetResult(short token)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        lock (_completionGate)
        {
            // Invalid, pending, or duplicate consumption must not release a live slot.
            if (_source.GetStatus(token) == ValueTaskSourceStatus.Pending || _consumed)
                throw new InvalidOperationException("Completion is pending or already consumed.");
            _consumed = true;
            try { result = _source.GetResult(token); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally { _source.Reset(); } // release continuation/context/result references after consumption
        }
        try
        {
            // Do not hold the completion gate while taking stream ownership or waiting
            // for cancellation callbacks. Owner publication takes stream then completion.
            _registration.Dispose();
        }
        finally { OnConsumed(); }
        failure?.Throw();
        return result;
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        lock (_completionGate) return _source.GetStatus(token);
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        lock (_completionGate) _source.OnCompleted(continuation, state, token, flags);
    }
}
