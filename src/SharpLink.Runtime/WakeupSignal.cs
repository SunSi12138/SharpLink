using System;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.Runtime;

/// <summary>
/// Reusable zero-allocation wakeup for the send-pump loop. The entire producer/pump readiness
/// protocol lives in one atomic state word <see cref="_state"/>: <c>0</c> = idle, <c>1</c> = a
/// signal latched before any arm was published, bit 1 marks a timed arm, and bits 2+ carry the
/// waiter generation (<c>++generation &lt;&lt; 2</c>). The latch and timed marks therefore remain
/// orthogonal to the generation and cannot alias a later arm.
/// </summary>
/// <remarks>
/// A timed wait does not introduce a second readiness authority. The deadline timer races to
/// claim the same arm token as a producer signal and completes that arm with <c>false</c> when it
/// wins. Producer signals complete with <c>true</c>. A timer is owned by its specific generation,
/// so a stale callback can never complete a later arm. Untimed idle waits allocate nothing and
/// never touch deadline ownership state on their successful signal path.
/// </remarks>
internal sealed class WakeupSignal : IValueTaskSource<bool>
{
    private const long Idle = 0;
    private const long Latched = 1;
    private const long DeadlineBit = 2;
    private const int GenerationShift = 2;

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
    {
        _core.Reset();
        var arm = ++_generation << GenerationShift;
        var prev = Interlocked.Exchange(ref _state, arm);
        if (prev == Latched &&
            Interlocked.CompareExchange(ref _state, Idle, arm) == arm)
        {
            _core.SetResult(true);
        }
        return new ValueTask<bool>(this, _core.Version);
    }

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

        _core.Reset();
        var arm = (++_generation << GenerationShift) | DeadlineBit;
        var deadline = new DeadlineArm(this, timeProvider, timeout, arm);
        Volatile.Write(ref _deadline, deadline);

        var prev = Interlocked.Exchange(ref _state, arm);
        if (prev == Latched &&
            Interlocked.CompareExchange(ref _state, Idle, arm) == arm)
        {
            CancelDeadline(arm);
            _core.SetResult(true);
        }

        // Arm only after the state token is visible. DeadlineArm's lock-free cancellation
        // handshake handles a producer claim that lands between publication and Start().
        deadline.Start();
        return new ValueTask<bool>(this, _core.Version);
    }

    internal void Signal()
    {
        // Fast path: claim the live arm without touching the latch. Untimed arms never read
        // deadline ownership state after the claim, keeping the common idle/wake path identical
        // to the original zero-allocation signal protocol apart from the timed-bit test.
        var s = Volatile.Read(ref _state);
        if (s > Latched &&
            Interlocked.CompareExchange(ref _state, Idle, s) == s)
        {
            if ((s & DeadlineBit) != 0)
                CancelDeadline(s & ~Latched);
            _core.SetResult(true);
            return;
        }

        // No claimable arm (idle, already latched, or lost the claim race): latch. Bit 0 is
        // reserved for the latch mark, so OR-ing it onto an arm preserves both its generation
        // and timed mark. The re-check loop claims any arm the latch lands on, covering the
        // signal/arm publication crossing without leaving stale state for the next generation.
        BeforeLatchWrite?.Invoke();
        Interlocked.Or(ref _state, Latched);
        while (true)
        {
            var t = Volatile.Read(ref _state);
            if (t <= Latched)
                return;
            if (Interlocked.CompareExchange(ref _state, Idle, t) == t)
            {
                if ((t & DeadlineBit) != 0)
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
            return;

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
        private ITimer? _timer;
        private int _cancelled;

        internal DeadlineArm(
            WakeupSignal owner,
            TimeProvider timeProvider,
            TimeSpan timeout,
            long armToken)
        {
            _owner = owner;
            _timeProvider = timeProvider;
            _timeout = timeout;
            ArmToken = armToken;
        }

        internal long ArmToken { get; }

        internal void Start()
        {
            if (Volatile.Read(ref _cancelled) != 0)
                return;

            // Create disabled first. Cancellation may race before or after publication; the
            // second cancelled check and atomic timer exchange close both windows without a
            // monitor on the timed wake path.
            var timer = _timeProvider.CreateTimer(
                static state => ((DeadlineArm)state!).Fire(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            if (Interlocked.CompareExchange(ref _timer, timer, null) is not null)
            {
                timer.Dispose();
                throw new InvalidOperationException("deadline timer was already published");
            }

            if (Volatile.Read(ref _cancelled) != 0)
            {
                if (Interlocked.CompareExchange(ref _timer, null, timer) == timer)
                    timer.Dispose();
                return;
            }

            try
            {
                _ = timer.Change(_timeout, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _cancelled) != 0)
            {
                // A producer claimed and disposed this arm after the second cancellation
                // check but before Change(). That producer is already the authoritative winner.
            }
        }

        internal void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0)
                return;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }

        private void Fire()
        {
            _owner.OnDeadline(ArmToken, this);
            Cancel();
        }
    }
}
