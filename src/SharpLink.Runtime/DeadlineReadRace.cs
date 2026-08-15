using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace SharpLink.Runtime;

/// <summary>
/// Races two pending channel reads (normal and protocol-progress) against a deadline timer
/// without <see cref="Task.WhenAny(Task, Task)"/>,
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>, or a per-pump
/// <see cref="CancellationTokenSource"/>. A single
/// instance is reused for every deadline wait of one send pump, so only the arm itself
/// allocates: up to three continuation closures per wait plus one <see cref="ITimer"/> from the
/// owner's <see cref="TimeProvider"/>.
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
/// (a single-threaded send pump) satisfies this by construction. Callbacks that outlive their
/// arm are neutralized by an atomic claim: each arm publishes a unique token, and the read
/// callback and the timer callback race to claim that token with a single
/// <see cref="Interlocked.CompareExchange(ref long, long, long)"/>. A stale callback's token no
/// longer matches the published one, so it can never dispose a later arm's timer or complete a
/// later arm's source, no matter how late it runs. The timer is additionally created in a
/// disabled state and armed via <see cref="ITimer.Change(TimeSpan, TimeSpan)"/> only after the
/// field that owns it has been published, so a deadline already in the past can never invoke a
/// callback that observes an unpublished timer. A read that completes while an arm is being set
/// up is still handled correctly: the continuation registered by
/// <see cref="TaskAwaiter{TResult}.UnsafeOnCompleted(Action)"/> runs inline for completed
/// tasks, and the timer field is already published at that point.
/// </para>
/// </remarks>
internal sealed class DeadlineReadRace : IValueTaskSource<bool>, IDisposable
{
    internal enum RaceOutcome
    {
        Pending,
        DataAvailable,
        ProgressAvailable,
        ReadClosed,
        TimedOut,
    }

    private const long ReadClaimBit = 1;
    private const long TimerClaimBit = 2;
    private const long ProgressClaimBit = 4;

    private readonly TimeProvider _timeProvider;
    private ManualResetValueTaskSourceCore<bool> _core;
    private ITimer? _timer;
    private RaceOutcome _outcome;
    private long _armGeneration;
    private long _armClaim;

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
    /// <see cref="WaitForReadsOrTimeout"/> has been awaited to completion.
    /// </summary>
    internal RaceOutcome Outcome =>
        (RaceOutcome)Volatile.Read(ref Unsafe.As<RaceOutcome, int>(ref _outcome));

    /// <summary>
    /// Waits until one of the reads completes or <paramref name="timeout"/> expires.
    /// The returned value task completes with the winner's result when a read wins, and with
    /// <c>false</c> when the timer wins; a faulted or canceled read is propagated. The losing
    /// read is deliberately left unconsumed: its <see cref="Task{TResult}"/> stays registered
    /// on the channel and the owner is expected to retain and re-observe it later. The winner
    /// is surfaced through <see cref="Outcome"/>: <see cref="RaceOutcome.DataAvailable"/> for
    /// the normal read, <see cref="RaceOutcome.ProgressAvailable"/> for the progress read,
    /// and <see cref="RaceOutcome.ReadClosed"/> for either read reporting a closed channel.
    /// </summary>
    internal ValueTask<bool> WaitForReadsOrTimeout(
        Task<bool> read,
        Task<bool> progressRead,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(progressRead);
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
        if (progressRead.IsCompleted)
        {
            Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome),
                (int)(progressRead.IsCompletedSuccessfully && progressRead.Result
                    ? RaceOutcome.ProgressAvailable
                    : RaceOutcome.ReadClosed));
            return new ValueTask<bool>(progressRead);
        }

        var token = (++_armGeneration) << 3;
        Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome), (int)RaceOutcome.Pending);
        _core.Reset();

        // Publish the arm token before either callback can run, then publish the timer before
        // it can fire: create it disabled, arm it via Change, and only then register the read
        // continuations (which run inline for a read that completes during the setup).
        Volatile.Write(ref _armClaim, token);
        _timer = _timeProvider.CreateTimer(
            _ => OnTimerFired(token), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(timeout, Timeout.InfiniteTimeSpan);
        read.GetAwaiter().UnsafeOnCompleted(() => OnReadCompleted(read, token));
        progressRead.GetAwaiter().UnsafeOnCompleted(() => OnProgressReadCompleted(progressRead, token));
        return new ValueTask<bool>(this, _core.Version);
    }

    private void OnReadCompleted(Task<bool> read, long token)
    {
        if (Interlocked.CompareExchange(ref _armClaim, token | ReadClaimBit, token) != token)
            return; // Superseded arm or already claimed by the timer: the read stays unconsumed.

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

    private void OnProgressReadCompleted(Task<bool> progressRead, long token)
    {
        if (Interlocked.CompareExchange(ref _armClaim, token | ProgressClaimBit, token) != token)
            return; // Superseded arm or already claimed by the timer or normal read.

        _timer!.Dispose();
        if (progressRead.IsCompletedSuccessfully)
        {
            Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome),
                (int)(progressRead.Result ? RaceOutcome.ProgressAvailable : RaceOutcome.ReadClosed));
            _core.SetResult(progressRead.Result);
        }
        else
        {
            Volatile.Write(ref Unsafe.As<RaceOutcome, int>(ref _outcome), (int)RaceOutcome.ReadClosed);
            _core.SetException(
                (Exception?)progressRead.Exception ?? new InvalidOperationException("pending progress read failed."));
        }
    }

    private void OnTimerFired(long token)
    {
        if (Interlocked.CompareExchange(ref _armClaim, token | TimerClaimBit, token) != token)
            return; // Superseded arm or already claimed by a read.

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
