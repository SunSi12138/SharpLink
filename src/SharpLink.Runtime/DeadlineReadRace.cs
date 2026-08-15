using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace SharpLink.Runtime;

/// <summary>
/// Races a pending channel read against a deadline timer without
/// <see cref="Task.WhenAny(Task, Task)"/>,
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>, or a per-pump
/// <see cref="CancellationTokenSource"/>. A single
/// instance is reused for every deadline wait of one send pump, so only the arm itself
/// allocates: one <see cref="ManualResetValueTaskSourceCore{TResult}"/> continuation closure
/// per wait plus one <see cref="ITimer"/> from the owner's <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// When the timer wins, the read is deliberately left unconsumed: its <see cref="Task{TResult}"/>
/// stays registered on the channel and the owner is expected to retain and re-observe it later
/// (pending-read retention). When the read wins, the timer is disposed and the result is
/// surfaced through the returned <see cref="ValueTask{TResult}"/>.
/// </para>
/// <para>
/// The instance is single-flight: an arm must be fully awaited before the next arm. The owner
/// (a single-threaded send pump) satisfies this by construction. A read that completes while an
/// arm is being set up is still handled correctly: the continuation registered by
/// <see cref="TaskAwaiter{TResult}.UnsafeOnCompleted(Action)"/> runs inline for completed
/// tasks, which is why the timer is created before the continuation is registered, and why each
/// arm captures its own read so a late continuation from a previous arm can never act on the
/// current arm's state (the identity check makes stale completions no-ops).
/// </para>
/// </remarks>
internal sealed class DeadlineReadRace : IValueTaskSource<bool>, IDisposable
{
    internal enum RaceOutcome
    {
        Pending,
        DataAvailable,
        ReadClosed,
        TimedOut,
    }

    private static readonly TimerCallback s_timerCallback =
        static state => ((DeadlineReadRace)state!).OnTimerFired();

    private readonly TimeProvider _timeProvider;
    private ManualResetValueTaskSourceCore<bool> _core;
    private Task<bool>? _read;
    private ITimer? _timer;
    private RaceOutcome _outcome;
    private int _readAbandoned;

    internal DeadlineReadRace(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _core = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true,
        };
    }

    /// <summary>
    /// Gets how the most recent wait resolved. Only meaningful after the value task returned by
    /// <see cref="WaitForReadOrTimeout"/> has been awaited to completion.
    /// </summary>
    internal RaceOutcome Outcome =>
        (RaceOutcome)Volatile.Read(ref Unsafe.As<RaceOutcome, int>(ref _outcome));

    /// <summary>
    /// Waits until <paramref name="read"/> completes or <paramref name="timeout"/> expires.
    /// The returned value task completes with the read's result when the read wins, and with
    /// <c>false</c> when the timer wins; a faulted or canceled read is propagated. The read is
    /// consumed exactly once by the winner and stays available to the owner otherwise.
    /// </summary>
    internal ValueTask<bool> WaitForReadOrTimeout(Task<bool> read, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.IsCompleted)
        {
            // Data arrived (or the channel closed) between the caller's completedness check and
            // this arm: surface the already-available outcome without starting a race.
            Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome),
                (int)(read.IsCompletedSuccessfully && read.Result
                    ? RaceOutcome.DataAvailable
                    : RaceOutcome.ReadClosed));
            return new ValueTask<bool>(read);
        }

        _read = read;
        Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome), (int)RaceOutcome.Pending);
        Volatile.Write(ref _readAbandoned, 0);
        _core.Reset();

        // The timer must be armed before the read continuation is registered: a read that
        // completes in this window invokes the continuation inline, and the read-win path
        // disposes the timer it expects to exist.
        _timer = _timeProvider.CreateTimer(s_timerCallback, this, timeout, Timeout.InfiniteTimeSpan);

        // Each arm registers a fresh closure capturing its own read. A closure from an earlier
        // arm that fires late must not be able to act on this arm's state: the identity check
        // against the current read makes stale completions no-ops.
        read.GetAwaiter().UnsafeOnCompleted(() => OnReadCompleted(read));
        return new ValueTask<bool>(this, _core.Version);
    }

    private void OnReadCompleted(Task<bool> read)
    {
        if (!ReferenceEquals(read, _read))
            return; // Stale completion from a previous arm: never touch the current cycle's state.

        if (Interlocked.Exchange(ref _readAbandoned, 1) != 0)
            return; // The timer won first: the read stays unconsumed for later reuse.

        _timer!.Dispose();
        if (read.IsCompletedSuccessfully)
        {
            Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome),
                (int)(read.Result ? RaceOutcome.DataAvailable : RaceOutcome.ReadClosed));
            _core.SetResult(read.Result);
        }
        else
        {
            Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome), (int)RaceOutcome.ReadClosed);
            _core.SetException(
                (Exception?)read.Exception ?? new InvalidOperationException("pending read failed."));
        }
    }

    private void OnTimerFired()
    {
        if (Interlocked.Exchange(ref _readAbandoned, 1) != 0)
            return; // The read completed first and disposed the timer.

        _timer!.Dispose();
        Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome), (int)RaceOutcome.TimedOut);
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

    public void Dispose() => _timer?.Dispose();
}
