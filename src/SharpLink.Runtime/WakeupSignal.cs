using System;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.Runtime;

/// <summary>
/// Reusable zero-allocation wakeup for the send-pump loop. The single waiter (the pump)
/// publishes an arm token before it sleeps; a writer claims the token with an interlocked
/// exchange and completes the value-task source. The exchange is the single arbiter, so
/// each arm completes exactly once: a writer racing the re-arm fails against the
/// superseded token, and a signal that arrives while no arm is claimable is latched and
/// consumed by the next WaitAsync, which is why the pump never has to abandon an armed
/// wait. A successful arm claim never touches the latch, so a real wake cannot leave a
/// stale latch behind that would spuriously complete the next arm.
/// </summary>
internal sealed class WakeupSignal : IValueTaskSource<bool>
{
    private ManualResetValueTaskSourceCore<bool> _core;
    private long _generation;
    private long _armToken;
    private int _signaled;

    internal WakeupSignal()
    {
        _core = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true,
        };
    }

    internal ValueTask<bool> WaitAsync()
    {
        _core.Reset();
        var token = ++_generation;
        Volatile.Write(ref _armToken, token);
        // Consume a latched signal that arrived before the arm was published so
        // the returned value task completes synchronously instead of hanging.
        if (Interlocked.Exchange(ref _signaled, 0) != 0 &&
            Interlocked.CompareExchange(ref _armToken, 0, token) == token)
        {
            _core.SetResult(true);
        }
        return new ValueTask<bool>(this, _core.Version);
    }

    internal void Signal()
    {
        // Claim a live arm directly: this is the hot path and must not touch the
        // latch, otherwise the next WaitAsync would consume the residue as a stale
        // signal and complete one extra empty pump iteration per real wake.
        var token = Volatile.Read(ref _armToken);
        if (token != 0 &&
            Interlocked.CompareExchange(ref _armToken, 0, token) == token)
        {
            _core.SetResult(true);
            return;
        }

        // No arm was claimable (not yet published, superseded, or already claimed):
        // latch the signal for the next WaitAsync. The frame is already queued, so
        // consuming the latch there is a correct, not spurious, wake.
        Volatile.Write(ref _signaled, 1);
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
