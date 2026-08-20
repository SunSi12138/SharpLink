using System.Buffers;
namespace SharpLink.Server;

/// <summary>
/// Uses one timer per physical connection and scans the already bounded call table only when a
/// deadline expires. Normal response completion does not remove timer nodes or take a scheduler lock.
/// </summary>
internal sealed class ServerCallDeadlineScheduler : IDisposable
{
    private const int MinimumSnapshotCapacity = 16;
    private const int SnapshotHeadroom = 8;
    private const int MaximumSnapshotAttempts = 5;

    private readonly StripedLongMap<ServerCallCancellationState> _calls;
    private readonly int _maxCalls;
    private readonly TimeProvider _timeProvider;
    private readonly ArrayPool<ServerCallCancellationLease> _snapshotPool;
    private readonly ITimer _timer;
    private long _approximateEarliestDeadline = long.MaxValue;
    private int _scanRunning;
    private int _disposed;

    internal ServerCallDeadlineScheduler(
        StripedLongMap<ServerCallCancellationState> calls,
        int maxCalls,
        TimeProvider timeProvider)
        : this(
            calls,
            maxCalls,
            timeProvider,
            ArrayPool<ServerCallCancellationLease>.Shared)
    {
    }

    internal ServerCallDeadlineScheduler(
        StripedLongMap<ServerCallCancellationState> calls,
        int maxCalls,
        TimeProvider timeProvider,
        ArrayPool<ServerCallCancellationLease> snapshotPool)
    {
        _calls = calls ?? throw new ArgumentNullException(nameof(calls));
        if (maxCalls is < 1 or > SharpLinkFlowControlOptions.MaximumConcurrentCallsPerConnection)
            throw new ArgumentOutOfRangeException(nameof(maxCalls));
        _maxCalls = maxCalls;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _snapshotPool = snapshotPool ?? throw new ArgumentNullException(nameof(snapshotPool));
        _timer = _timeProvider.CreateTimer(
            static state => ((ServerCallDeadlineScheduler)state!).ScanExpiredDeadlines(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal void Register(ServerCallCancellationState call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Deadline.HasValue)
            UpdateEarliestDeadline(call.Deadline.Timestamp);
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

        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var activeHint = Math.Min(_maxCalls, _calls.Count);
            if (activeHint == 0)
                return;

            var requestedCapacity = GetInitialSnapshotCapacity(activeHint);
            for (var attempt = 0; attempt < MaximumSnapshotAttempts; attempt++)
            {
                var snapshot = _snapshotPool.Rent(requestedCapacity);
                var capturedCount = 0;
                try
                {
                    var usableCapacity = Math.Min(snapshot.Length, _maxCalls);
                    if (_calls.TryCopyEntries(
                            snapshot.AsSpan(0, usableCapacity),
                            static (requestId, state) => state.CaptureLease(requestId),
                            out capturedCount))
                    {
                        ScanSnapshot(snapshot, capturedCount);
                        return;
                    }
                }
                finally
                {
                    if (capturedCount != 0)
                        Array.Clear(snapshot, 0, capturedCount);
                    _snapshotPool.Return(snapshot, clearArray: false);
                }

                if (requestedCapacity >= _maxCalls)
                    break;

                requestedCapacity = GetNextSnapshotCapacity(
                    requestedCapacity,
                    attempt,
                    Math.Min(_maxCalls, _calls.Count));
            }

            // Reaching the configured upper bound without fitting means admission/map invariants
            // were violated. Keep the timer callback bounded and retry later rather than dropping
            // deadline processing or spinning indefinitely.
            ScheduleInvariantRetry();
        }
        finally
        {
            Volatile.Write(ref _scanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }

    private int GetInitialSnapshotCapacity(int activeHint)
        => Math.Min(
            _maxCalls,
            Math.Max(
                Math.Min(MinimumSnapshotCapacity, _maxCalls),
                SaturatingAdd(activeHint, SnapshotHeadroom)));

    private int GetNextSnapshotCapacity(
        int currentCapacity,
        int attempt,
        int activeHint)
    {
        if (attempt == MaximumSnapshotAttempts - 2)
            return _maxCalls;

        var doubled = currentCapacity > _maxCalls / 2
            ? _maxCalls
            : currentCapacity * 2;
        var hinted = Math.Min(_maxCalls, SaturatingAdd(activeHint, SnapshotHeadroom));
        return Math.Max(doubled, hinted);
    }

    private static int SaturatingAdd(int value, int addend)
        => value > int.MaxValue - addend ? int.MaxValue : value + addend;

    private void ScanSnapshot(
        ServerCallCancellationLease[] snapshot,
        int count)
    {
        var now = _timeProvider.GetTimestamp();
        for (var index = 0; index < count; index++)
        {
            var callLease = snapshot[index];
            if (!callLease.TryAcquire())
                continue;
            try
            {
                var call = callLease.State;
                var deadline = call.Deadline;
                if (!deadline.HasValue)
                    continue;
                if (deadline.Timestamp <= now)
                    call.TryCancel(ServerCallCancellationReason.DeadlineExceeded);
                else
                    UpdateEarliestDeadline(deadline.Timestamp);
            }
            finally
            {
                callLease.ReleaseUse();
            }
        }
    }

    private void ScheduleInvariantRetry()
    {
        var now = _timeProvider.GetTimestamp();
        var frequency = _timeProvider.TimestampFrequency;
        UpdateEarliestDeadline(now > long.MaxValue - frequency
            ? long.MaxValue
            : now + frequency);
    }

    private void ArmDeadlineTimer(long deadlineTimestamp)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var delay = RpcDeadline.GetRemaining(
            deadlineTimestamp,
            _timeProvider.GetTimestamp(),
            _timeProvider.TimestampFrequency);
        if (delay > SharpLinkTimer.MaximumDelay)
            delay = SharpLinkTimer.MaximumDelay;
        try
        {
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }
}
