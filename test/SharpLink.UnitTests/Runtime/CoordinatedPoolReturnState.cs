namespace SharpLink.UnitTests.Runtime;

/// <summary>Owns the gates and detached signal for one pool-return competition.</summary>
internal sealed class CoordinatedPoolReturnState : IStreamDispatchState, IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly ManualResetEventSlim _bothPrechecksEntered = new();
    private readonly ManualResetEventSlim _releaseDelayedReturn = new();
    private readonly TaskCompletionSource _detached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _coordinateReturns;
    private int _detachedReads;

    internal CoordinatedPoolReturnState(TimeSpan timeout) => _timeout = timeout;

    public bool HasActiveDispatches => false;

    public bool IsDetached
    {
        get
        {
            if (Volatile.Read(ref _coordinateReturns) == 0)
                return false;

            switch (Interlocked.Increment(ref _detachedReads))
            {
                case 1:
                    if (!_bothPrechecksEntered.Wait(_timeout))
                        throw new TimeoutException("The second pool-return contender did not enter its precheck.");
                    return true;
                case 2:
                    _bothPrechecksEntered.Set();
                    if (!_releaseDelayedReturn.Wait(_timeout))
                        throw new TimeoutException("The delayed pool-return contender was not released.");
                    return true;
                default:
                    return true;
            }
        }
    }

    public void Close()
    {
    }

    public ValueTask WaitForDispatchesDrainedAsync() => ValueTask.CompletedTask;

    public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? new ValueTask(_detached.Task.WaitAsync(cancellationToken))
            : new ValueTask(_detached.Task);

    public void CoordinateReturns()
    {
        Volatile.Write(ref _coordinateReturns, 1);
        _detached.TrySetResult();
    }

    public bool WaitForBothPrechecks(TimeSpan timeout) => _bothPrechecksEntered.Wait(timeout);

    public void ReleaseDelayedReturn() => _releaseDelayedReturn.Set();

    // Cleanup must release both contenders even if the second never reached its precheck.
    // The fixture joins every worker before disposing these gates or clearing its static pool.
    public void ReleaseAllForCleanup()
    {
        CoordinateReturns();
        _bothPrechecksEntered.Set();
        _releaseDelayedReturn.Set();
    }

    public void Dispose()
    {
        _bothPrechecksEntered.Dispose();
        _releaseDelayedReturn.Dispose();
    }
}

