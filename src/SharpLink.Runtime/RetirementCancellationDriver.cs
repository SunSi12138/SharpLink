namespace SharpLink.Runtime;

// A cold, module-generation-local cancellation executor. The physical retirement
// task owns Task and consumes its outcome once. Public waiters never own this
// timer or call CTS.Cancel. The constructor only creates suspended host work;
// the caller must root it before completing the shared asynchronous start gate.
internal sealed class RetirementCancellationDriver
{
    private readonly SharpLinkDynamicModule _module;
    private readonly ColdMetadataDomain _domain;
    private readonly RetirementGraceBudget? _budget;
    private readonly Task<bool> _start;
    private readonly Lock _wakeGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private bool _wakePending;
    private bool _forceRequested;
    private bool _closing;
    // A failed DisposeAsync is not evidence that the timer/callback has exited.
    // Keep its actual handle rooted on this retained module owner in that case.
    private ITimer? _ownedTimer;

    internal RetirementCancellationDriver(SharpLinkDynamicModule module, ColdMetadataDomain domain,
        RetirementGraceBudget? budget, Task<bool> start, bool forceRequested)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(start);
        if (start.IsCompleted)
            throw new ArgumentException("Retirement work must be rooted before its start decision.", nameof(start));
        if (budget is null && !forceRequested)
            throw new ArgumentException("A public retirement requires its captured entry budget.", nameof(budget));
        _module = module;
        _domain = domain;
        _budget = budget;
        _start = start;
        _forceRequested = forceRequested;
        Task = RunAsync();
    }

    internal Task<RetirementCancellationOutcome> Task { get; }

    internal void RequestForce()
    {
        lock (_wakeGate)
        {
            if (_closing) return;
            _forceRequested = true;
            SignalLocked();
        }
    }

    private void Signal()
    {
        lock (_wakeGate)
        {
            if (!_closing) SignalLocked();
        }
    }

    private void SignalLocked()
    {
        if (_wakePending) return;
        _wakePending = true;
        _wake.Release();
    }

    private bool IsForced()
    {
        lock (_wakeGate) return _forceRequested;
    }

    private void ConsumeWake()
    {
        lock (_wakeGate) _wakePending = false;
    }

    private async Task<RetirementCancellationOutcome> RunAsync()
    {
        var committed = false;
        var cancellationAttempted = false;
        Exception? primaryFailure = null;
        Exception? cancellationFailure = null;
        Exception? timerFailure = null;
        Exception? wakeFailure = null;
        ITimer? timer = null;
        Task? pendingWake = null;
        try
        {
            committed = await _start.ConfigureAwait(false);
            if (committed)
            {
                var clock = _budget?.CreateClock() ?? default;
                var drain = _module.WaitForDrainAsync();
                while (!drain.IsCompleted && _module.RemainingCalls != 0)
                {
                    if (IsForced() || (_budget.HasValue && clock.IsExpired))
                    {
                        cancellationAttempted = true;
                        using (RetirementCallbackScope.Enter(_domain))
                            _module.CancelRemainingCallsFromRetirementDriver();
                        break;
                    }

                    using (RetirementCallbackScope.Enter(_domain)) clock.Observe();
                    if (clock.IsExpired || IsForced()) continue;
                    if (timer is null)
                    {
                        // Timer callback is a coalescing hint only. A provider may
                        // invoke it synchronously even for this disarmed creation.
                        using (RetirementCallbackScope.Enter(_domain))
                            timer = _budget!.Value.Provider.CreateTimer(static state =>
                                ((RetirementCancellationDriver)state!).Signal(), this,
                                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                        _ownedTimer = timer;
                        using (RetirementCallbackScope.Enter(_domain)) clock.Observe();
                        if (clock.IsExpired || IsForced()) continue;
                    }

                    using (RetirementCallbackScope.Enter(_domain))
                        if (!timer.Change(clock.NextSlice, Timeout.InfiniteTimeSpan))
                            throw new InvalidOperationException("The owned retirement timer rejected arming.");
                    using (RetirementCallbackScope.Enter(_domain)) clock.Observe();
                    if (clock.IsExpired || IsForced()) continue;

                    pendingWake = _wake.WaitAsync();
                    var completed = await System.Threading.Tasks.Task.WhenAny(drain, pendingWake).ConfigureAwait(false);
                    if (ReferenceEquals(completed, drain)) Signal();
                    // Even when drain won, join the actual outstanding semaphore
                    // waiter BEFORE Closing could make Signal a no-op.
                    await pendingWake.ConfigureAwait(false);
                    pendingWake = null;
                    ConsumeWake();
                }
            }
        }
        catch (Exception exception)
        {
            if (cancellationAttempted)
                cancellationFailure = exception;
            else
                primaryFailure = exception;

            // An aborted or failed precommit start never authorizes cancellation.
            // A selected owner's provider failure does; do not wait for public
            // tracking/notification, and never retry an already-attempted Cancel.
            if (committed && !cancellationAttempted && _module.RemainingCalls != 0)
            {
                cancellationAttempted = true;
                try
                {
                    using (RetirementCallbackScope.Enter(_domain))
                        _module.CancelRemainingCallsFromRetirementDriver();
                }
                catch (Exception cancellation) { cancellationFailure = cancellation; }
            }
        }
        finally
        {
            if (pendingWake is not null)
            {
                // No provider or user callback executes here. Closing must not
                // precede this signal/join, including exceptional loop exits.
                Signal();
                try { await pendingWake.ConfigureAwait(false); }
                catch (Exception exception) { wakeFailure = exception; }
                ConsumeWake();
            }
            lock (_wakeGate) _closing = true;
            if (timer is not null)
            {
                try
                {
                    ValueTask disposal;
                    using (RetirementCallbackScope.Enter(_domain)) disposal = timer.DisposeAsync();
                    await disposal.ConfigureAwait(false);
                    _ownedTimer = null;
                }
                catch (Exception exception) { timerFailure = exception; }
            }
            try { _wake.Dispose(); }
            catch (Exception exception)
            {
                wakeFailure = wakeFailure is null ? exception : new AggregateException(wakeFailure, exception);
            }
        }

        // No filtering by exception type/token/reference. A real OCE is data,
        // not a canceled DriverTask. Separate stages can hold the SAME object.
        return new RetirementCancellationOutcome(!committed, cancellationAttempted, _ownedTimer is not null,
            primaryFailure, cancellationFailure, timerFailure, wakeFailure);
    }
}

internal readonly record struct RetirementCancellationOutcome(bool Aborted, bool CancellationAttempted, bool HasRetainedTimer,
    Exception? PrimaryFailure, Exception? CancellationFailure, Exception? TimerFailure, Exception? WakeFailure)
{
    internal void AppendFailures(ref Exception? failure)
    {
        Append(ref failure, PrimaryFailure);
        Append(ref failure, CancellationFailure);
        Append(ref failure, TimerFailure);
        Append(ref failure, WakeFailure);
    }

    private static void Append(ref Exception? failure, Exception? next)
    {
        if (next is not null) failure = failure is null ? next : new AggregateException(failure, next);
    }
}

// Direct same-thread reentry protection ONLY. Never keep this scope across await;
// it does not follow user-created cross-thread joins or add RPC ambient ownership.
internal readonly struct RetirementCallbackScope : IDisposable
{
    [ThreadStatic] private static Frame? _current;
    private readonly Frame? _previous;

    private RetirementCallbackScope(ColdMetadataDomain domain)
    {
        _previous = _current;
        _current = new Frame(domain, _previous);
    }

    internal static RetirementCallbackScope Enter(ColdMetadataDomain domain) => new(domain);

    internal static void ThrowIfReentry(ColdMetadataDomain domain)
    {
        for (var frame = _current; frame is not null; frame = frame.Previous)
            if (frame.Domain.IsWithin(domain))
                throw new InvalidOperationException("A retirement callback cannot join its own lifecycle owner.");
    }

    public void Dispose() => _current = _previous;

    private sealed record Frame(ColdMetadataDomain Domain, Frame? Previous);
}
