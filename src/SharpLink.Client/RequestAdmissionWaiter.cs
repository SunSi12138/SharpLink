using System.Threading.Tasks.Sources;

namespace SharpLink.Client;

/// <summary>Single-consumer admission signal, independent of the reusable pending slot.</summary>
internal sealed class RequestAdmissionWaiter : IValueTaskSource<bool>
{
    private const int MaxRetained = 4096;
    private static readonly ConcurrentQueue<RequestAdmissionWaiter> Pool = new();
    private static int s_retainedCount;
    private ManualResetValueTaskSourceCore<bool> _source = new() { RunContinuationsAsynchronously = true };
    private int _owners;

    internal static RequestAdmissionWaiter Rent()
    {
        if (!Pool.TryDequeue(out var waiter))
            waiter = new RequestAdmissionWaiter();
        else
            Interlocked.Decrement(ref s_retainedCount);
        // Completion and consumption must both finish before Reset. In particular, a queued
        // continuation can consume the result before SetResult returns on the receive loop.
        waiter._owners = 2;
        return waiter;
    }

    internal ValueTask<bool> WaitAsync() => new(this, _source.Version);

    internal void Complete(bool accepted)
    {
        _source.SetResult(accepted);
        Release();
    }

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        var result = _source.GetResult(token);
        Release();
        return result;
    }

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _source.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state,
        short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);

    private void Release()
    {
        if (Interlocked.Decrement(ref _owners) != 0)
            return;
        _source.Reset();
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
