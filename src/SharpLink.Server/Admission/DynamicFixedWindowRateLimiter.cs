using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal enum DynamicFixedWindowActivationMode
{
    Immediate,
    NextWindowBoundary
}

/// <summary>
/// Immutable FixedWindow policy view over one stable logical counter. Synchronous attempts use the
/// policy captured by their AdmissionProgram. Rate waiters are a later admission attempt and follow
/// the latest published current-window target. Immediate updates may change only the limit; an
/// explicitly deferred target activates limit and window together at the next natural boundary.
/// </summary>
internal sealed partial class DynamicFixedWindowRateLimiter : RateLimiter
{
    private readonly Counter _counter;
    private readonly long _sequence;
    private readonly int _permitLimit;
    private readonly long _windowTimestampTicks;
    private readonly DynamicFixedWindowActivationMode _activationMode;
    private int _preActivationLimit;
    private long _activationBoundary;
    private int _committed;
    private int _published;
    private int _disposed;

    internal DynamicFixedWindowRateLimiter(
        int permitLimit,
        TimeSpan window,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        var provider = timeProvider ?? TimeProvider.System;
        _counter = new Counter(permitLimit, window, provider);
        _sequence = 1;
        _permitLimit = permitLimit;
        _windowTimestampTicks = _counter.ToTimestampTicks(window.Ticks);
        _activationMode = DynamicFixedWindowActivationMode.Immediate;
        _preActivationLimit = permitLimit;
        _committed = 1;
        _published = 1;
    }

    private DynamicFixedWindowRateLimiter(
        Counter counter,
        long sequence,
        int permitLimit,
        long windowTimestampTicks,
        DynamicFixedWindowActivationMode activationMode)
    {
        _counter = counter;
        _sequence = sequence;
        _permitLimit = permitLimit;
        _windowTimestampTicks = windowTimestampTicks;
        _activationMode = activationMode;
    }

    internal int PermitLimit => _permitLimit;

    internal TimeSpan Window => _counter.TimestampDeltaToTimeSpan(_windowTimestampTicks);

    internal DynamicFixedWindowActivationMode ActivationModeForTests => _activationMode;

    internal int WaitingCount => _counter.WaitingCount;

    internal long ConsumedForTests => _counter.Consumed;

    internal int ActiveLimitForTests => _counter.ActiveLimit;

    internal int QueuedLimitForTests => _counter.QueuedLimit;

    internal TimeSpan ActiveWindowForTests => _counter.ActiveWindow;

    internal bool HasPendingWindowForTests => _counter.HasPendingTarget;

    internal long CounterIdentityForTests => _counter.Identity;

    internal DynamicFixedWindowRateLimiter CreateSuccessor(
        int permitLimit,
        TimeSpan window,
        DynamicFixedWindowActivationMode? activationMode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        ThrowIfDisposed();

        var windowTimestampTicks = _counter.ToTimestampTicks(window.Ticks);
        var resolvedActivation = _counter.ResolveActivation(windowTimestampTicks, activationMode);
        return _counter.CreateSuccessor(
            permitLimit,
            window,
            resolvedActivation);
    }

    /// <summary>
    /// Finalizes a winning successor without mutating the live counter. The actual shared target is
    /// installed only after publication, so a losing candidate and the commit-before-pointer window
    /// cannot leak target state into requests still bound to the old publication.
    /// </summary>
    internal void CommitTransitionTo(DynamicFixedWindowRateLimiter target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        if (target._activationMode == DynamicFixedWindowActivationMode.Immediate &&
            target._windowTimestampTicks != _windowTimestampTicks)
        {
            throw new InvalidOperationException(
                "Immediate FixedWindow updates may change PermitLimit only. Change Window with NextWindowBoundary activation.");
        }
        _counter.CommitTransition(this, target);
    }

    /// <summary>
    /// Installs this policy as the published target. Server publication calls this after the program
    /// pointer is visible. Acquisition also calls it as a lazy fallback for direct kernel tests and
    /// other internal publication paths.
    /// </summary>
    internal void OnPublished()
    {
        ThrowIfDisposed();
        _counter.Publish(this);
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ValidatePermitCount(permitCount);
        if (Volatile.Read(ref _disposed) != 0)
            return FailedLease.Instance;
        _counter.Publish(this);
        return _counter.AttemptAcquire(this);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        if (Volatile.Read(ref _disposed) != 0)
            return ValueTask.FromResult<RateLimitLease>(FailedLease.Instance);
        _counter.Publish(this);
        return _counter.AcquireAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _counter.ReleaseView();
    }

    private void FinalizeForCommit(int preActivationLimit, long activationBoundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preActivationLimit);
        if (Interlocked.Exchange(ref _committed, 1) != 0)
            throw new InvalidOperationException("Dynamic FixedWindow policy view was committed more than once.");
        _preActivationLimit = preActivationLimit;
        _activationBoundary = activationBoundary;
    }

    private void MarkPublishedLocked()
        => Volatile.Write(ref _published, 1);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DynamicFixedWindowRateLimiter));
    }

    private static void ValidatePermitCount(int permitCount)
    {
        if (permitCount != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount),
                "Admission FixedWindow limiters acquire exactly one permit.");
        }
    }
}
