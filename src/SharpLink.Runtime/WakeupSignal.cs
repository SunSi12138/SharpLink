using System;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.Runtime;

/// <summary>
/// Reusable zero-allocation wakeup for the send-pump loop. The entire protocol lives in one
/// atomic state word <see cref="_state"/>: <c>0</c> = idle, <c>1</c> = a signal latched before
/// any arm was published, and even values ≥ 2 = an armed waiter
/// (<c>++generation &lt;&lt; 1</c>). Bit 0 is reserved exclusively for the latch mark, so
/// OR-ing it onto an armed arm produces <c>arm | 1</c> — still that same arm — and can never
/// alias another generation's armed value. The single waiter (the pump) publishes an arm
/// with one atomic exchange that also consumes a pending latch, so a signal that arrived
/// before the arm completes the arm synchronously. A writer either claims the live arm with
/// one CAS, or latches; the latch write (<see cref="Interlocked.Or(ref long, long)"/>) keeps
/// a concurrently published arm intact, and the re-check loop claims any arm the latch lands
/// on, so a signal crossing the arm-publication boundary — the latch write landing just after
/// the next <see cref="WaitAsync"/> has already consumed the latch — still completes that
/// pending arm instead of being lost. Claiming always returns the state to idle, so a real
/// wake never leaves a stale latch behind that would spuriously complete the next arm.
/// </summary>
internal sealed class WakeupSignal : IValueTaskSource<bool>
{
    private const long Idle = 0;
    private const long Latched = 1;

    private ManualResetValueTaskSourceCore<bool> _core;
    private long _generation;
    private long _state;

    internal WakeupSignal()
    {
        _core = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true,
        };
    }

    /// <summary>
    /// Test-only seam invoked on the latch path before the latch bit is written. Lets
    /// WakeupSignalTests deterministically park a writer in the crossing window where an arm
    /// is published after the writer observed the idle state.
    /// </summary>
    internal Action? BeforeLatchWrite { get; set; }

    internal ValueTask<bool> WaitAsync()
    {
        _core.Reset();
        // Arm values are even (generation << 1); bit 0 stays reserved for the latch mark.
        var arm = ++_generation << 1;
        // Publish the arm and consume a pending latch in one atomic exchange.
        var prev = Interlocked.Exchange(ref _state, arm);
        if (prev == Latched)
        {
            // The arm was born latched: the pending signal belongs to this arm. Claim it
            // ourselves with a CAS — a writer racing this CAS claims the same arm, so the
            // arm completes exactly once.
            if (Interlocked.CompareExchange(ref _state, Idle, arm) == arm)
            {
                _core.SetResult(true);
            }
        }
        return new ValueTask<bool>(this, _core.Version);
    }

    internal void Signal()
    {
        // Fast path: claim the live arm without touching the latch, so a real wake leaves
        // no residue for the next arm.
        var s = Volatile.Read(ref _state);
        if (s > Latched &&
            Interlocked.CompareExchange(ref _state, Idle, s) == s)
        {
            _core.SetResult(true);
            return;
        }

        // No claimable arm (idle, already latched, or lost the claim race): latch. Bit 0 is
        // reserved for the latch mark, so the Or turns an armed arm into arm | 1 — still that
        // same arm, never another generation's armed value. The loop below claims any arm the
        // latch lands on, so a signal crossing the arm-publication boundary (the latch write
        // landing after the next WaitAsync already consumed the latch) still completes that
        // arm instead of being lost.
        BeforeLatchWrite?.Invoke();
        Interlocked.Or(ref _state, Latched);
        while (true)
        {
            var t = Volatile.Read(ref _state);
            if (t <= Latched)
                return; // Idle or latched: the next WaitAsync consumes the latch.
            if (Interlocked.CompareExchange(ref _state, Idle, t) == t)
            {
                _core.SetResult(true);
                return;
            }
        }
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
