namespace SharpLink.Client;

/// <summary>
/// Owns the one-shot timer and approximate wake/re-arm state for pending-call deadlines.
/// The scheduler never owns pending calls and never removes or completes a table slot.
/// </summary>
internal sealed class PendingDeadlineScheduler : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly Action _scanExpiredDeadlines;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private RpcDeadline _approximateEarliestDeadline;
    private long _revision;
    private bool _hasApproximateEarliestDeadline;
    private int _scanRunning;
    private int _disposed;

    internal PendingDeadlineScheduler(
        TimeProvider timeProvider,
        Action scanExpiredDeadlines)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(scanExpiredDeadlines);

        _timeProvider = timeProvider;
        _scanExpiredDeadlines = scanExpiredDeadlines;
        _timer = _timeProvider.CreateTimer(
            static state => ((PendingDeadlineScheduler)state!).RunScan(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal void Observe(RpcDeadline deadline)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (_hasApproximateEarliestDeadline &&
                _approximateEarliestDeadline.IsEarlierOrEqual(
                    deadline,
                    _timeProvider.GetTimestamp()))
            {
                return;
            }

            _approximateEarliestDeadline = deadline;
            _hasApproximateEarliestDeadline = true;
            _revision++;
        }

        ReconcileTimer();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
        _timer.Dispose();
    }

    private void RunScan()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                _approximateEarliestDeadline = default;
                _hasApproximateEarliestDeadline = false;
                _revision++;
            }

            if (Volatile.Read(ref _disposed) == 0)
                _scanExpiredDeadlines();
        }
        finally
        {
            Volatile.Write(ref _scanRunning, 0);
            ReconcileTimer();
        }
    }

    private void ReconcileTimer()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            RpcDeadline next;
            long revision;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_hasApproximateEarliestDeadline)
                    return;
                next = _approximateEarliestDeadline;
                revision = _revision;
            }

            ArmTimer(next);

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    !_hasApproximateEarliestDeadline ||
                    revision == _revision)
                {
                    return;
                }
            }
        }
    }

    private void ArmTimer(RpcDeadline deadline)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var delay = deadline.GetRemaining(_timeProvider);
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
