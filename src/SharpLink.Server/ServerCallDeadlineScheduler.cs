using System.Buffers;
using System.Diagnostics;

namespace SharpLink.Server;

/// <summary>
/// Uses one timer per physical connection and scans the already bounded call table only when a
/// deadline expires. Normal response completion does not remove timer nodes or take a scheduler lock.
/// </summary>
internal sealed class ServerCallDeadlineScheduler : IDisposable
{
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly StripedLongMap<ServerCallCancellationState> _calls;
    private readonly int _maxCalls;
    private readonly Timer _timer;
    private long _approximateEarliestDeadline = long.MaxValue;
    private int _scanRunning;
    private int _disposed;

    internal ServerCallDeadlineScheduler(
        StripedLongMap<ServerCallCancellationState> calls,
        int maxCalls)
    {
        _calls = calls ?? throw new ArgumentNullException(nameof(calls));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCalls);
        _maxCalls = maxCalls;
        _timer = new Timer(
            static state => ((ServerCallDeadlineScheduler)state!).ScanExpiredDeadlines(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal void Register(ServerCallCancellationState call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.DeadlineTimestamp > 0)
            UpdateEarliestDeadline(call.DeadlineTimestamp);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _timer.Dispose();
    }

    private void UpdateEarliestDeadline(long deadlineTimestamp)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            var current = Volatile.Read(ref _approximateEarliestDeadline);
            if (current <= deadlineTimestamp)
                return;
            if (Interlocked.CompareExchange(
                    ref _approximateEarliestDeadline,
                    deadlineTimestamp,
                    current) != current)
            {
                continue;
            }

            ArmDeadlineTimer(deadlineTimestamp);
            return;
        }
    }

    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0)
        {
            return;
        }

        var snapshot = ArrayPool<KeyValuePair<long, ServerCallCancellationState>>.Shared.Rent(_maxCalls);
        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var count = _calls.CopyEntries(snapshot);
            var now = Stopwatch.GetTimestamp();
            for (var index = 0; index < count; index++)
            {
                var entry = snapshot[index];
                var requestId = entry.Key;
                var call = entry.Value;
                if (!call.TryAcquire(requestId))
                    continue;
                try
                {
                    var deadlineTimestamp = call.DeadlineTimestamp;
                    if (deadlineTimestamp <= 0)
                        continue;
                    if (deadlineTimestamp <= now)
                        call.TryCancel(ServerCallCancellationReason.DeadlineExceeded);
                    else
                        UpdateEarliestDeadline(deadlineTimestamp);
                }
                finally
                {
                    call.ReleaseUse();
                }
            }
        }
        catch (ArgumentException)
        {
            // Session admission makes this unreachable. Never let an invariant violation escape
            // a timer callback; retry after a bounded delay so deadlines are not lost.
            UpdateEarliestDeadline(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        }
        finally
        {
            ArrayPool<KeyValuePair<long, ServerCallCancellationState>>.Shared.Return(
                snapshot,
                clearArray: true);
            Volatile.Write(ref _scanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }

    private void ArmDeadlineTimer(long deadlineTimestamp)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        var delay = remainingTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
        if (delay > MaxTimerDelay)
            delay = MaxTimerDelay;
        try
        {
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }
}
