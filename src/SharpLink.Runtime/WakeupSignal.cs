using System;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.Runtime;

/// <summary>
/// Reusable zero-allocation wakeup for the send-pump loop. The entire producer/pump readiness
/// protocol lives in one atomic state word <see cref="_state"/>: <c>0</c> = idle, <c>1</c> = a
/// signal latched before any arm was published, and even values >= 2 = an armed waiter
/// (<c>++generation &lt;&lt; 1</c>). Bit 0 is reserved exclusively for the latch mark.
/// </summary>
/// <remarks>
/// A timed wait does not introduce a second readiness authority. The deadline timer races to
/// claim the same arm token as a producer signal and completes that arm with <c>false</c> when it
/// wins. Producer signals complete with <c>true</c>. A timer is owned by its specific generation,
/// so a stale callback can never complete a later arm. Untimed idle waits allocate nothing.
/// </remarks>
internal sealed class WakeupSignal : IValueTaskSource<bool>
{
    private const long Idle = 0;
    private const long Latched = 1;

    private ManualResetValueTaskSourceCore<bool> _core;
    private long _generation;
    private long _state;
    private DeadlineArm? _deadline;

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
        => Arm(deadline: null);

    /// <summary>
    /// Discards a latched signal that the single consumer has already accounted for by
    /// inspecting/draining its mailbox. The caller must re-check mailbox visibility after this
    /// call before arming a wait: a producer crossing this CAS may have published new data while
    /// its signal is being coalesced with the old latch.
    /// </summary>
    internal void ConsumeLatched()
        => Interlocked.CompareExchange(ref _state, Idle, Latched);

    /// <summary>
    /// Waits for a producer signal or for <paramref name="timeout"/> to expire. Both outcomes
    /// claim the same arm: <c>true</c> means producer data/stop/fault was signalled and
    /// <c>false</c> means the deadline won. The caller must await the returned value task before
    /// arming another wait.
    /// </summary>
    internal ValueTask<bool> WaitAsync(TimeProvider timeProvider, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return Arm(new DeadlineArm(this, timeProvider, timeout));
    }

    private ValueTask<bool> Arm(DeadlineArm? deadline)
    {
        _core.Reset();
        // Arm values are even (generation << 1); bit 0 stays reserved for the latch mark.
        var arm = ++_generation << 1;
        deadline?.Bind(arm);
        if (deadline is not null)
            Volatile.Write(ref _deadline, deadline);

        // Publish the arm and consume a pending latch in one atomic exchange.
        var prev = Interlocked.Exchange(ref _state, arm);
        if (prev == Latched)
        {
            // The arm was born latched: the pending signal belongs to this arm. Claim it
            // ourselves with a CAS — a writer racing this CAS claims the same arm, so the
            // arm completes exactly once.
            if (Interlocked.CompareExchange(ref _state, Idle, arm) == arm)
            {
                CancelDeadline(arm);
                _core.SetResult(true);
            }
        }

        // Arm the timer only after the state token is visible. DeadlineArm handles a producer
        // claim that happens between publication and this call by observing its cancelled flag
        // and never publishing a live timer for the abandoned generation.
        deadline?.Start();
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
            CancelDeadline(s & ~Latched);
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
                CancelDeadline(t & ~Latched);
                _core.SetResult(true);
                return;
            }
        }
    }

    private void CancelDeadline(long arm)
    {
        var deadline = Volatile.Read(ref _deadline);
        if (deadline is null || deadline.ArmToken != arm)
            return;
        if (Interlocked.CompareExchange(ref _deadline, null, deadline) == deadline)
            deadline.Cancel();
    }

    private void OnDeadline(long arm, DeadlineArm deadline)
    {
        if (Interlocked.CompareExchange(ref _state, Idle, arm) != arm)
            return; // Producer signal or a superseding state transition already claimed it.

        _ = Interlocked.CompareExchange(ref _deadline, null, deadline);
        _core.SetResult(false);
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    private sealed class DeadlineArm
    {
        private readonly WakeupSignal _owner;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _timeout;
        private readonly Lock _gate = new();
        private ITimer? _timer;
        private bool _cancelled;

        internal DeadlineArm(WakeupSignal owner, TimeProvider timeProvider, TimeSpan timeout)
        {
            _owner = owner;
            _timeProvider = timeProvider;
            _timeout = timeout;
        }

        internal long ArmToken { get; private set; }

        internal void Bind(long arm) => ArmToken = arm;

        internal void Start()
        {
            lock (_gate)
            {
                if (_cancelled)
                    return;

                // Create disabled, publish ownership, then arm. This keeps even an already-due
                // timeout from observing an unpublished timer and mirrors the repository's
                // established timer-publication discipline.
                _timer = _timeProvider.CreateTimer(
                    static state => ((DeadlineArm)state!).Fire(),
                    this,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
                _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
            }
        }

        internal void Cancel()
        {
            ITimer? timer;
            lock (_gate)
            {
                if (_cancelled)
                    return;
                _cancelled = true;
                timer = _timer;
                _timer = null;
            }
            timer?.Dispose();
        }

        private void Fire()
        {
            _owner.OnDeadline(ArmToken, this);
            Cancel();
        }
    }
}
