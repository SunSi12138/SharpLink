namespace Issue739;

// Explicit driver ownership: this gate is not a SharpLink optimization.
// Task objects created by contended WaitAsync remain in gross allocated bytes.
internal sealed class ReceiveCredits(int capacity) : IDisposable
{
    internal readonly SemaphoreSlim Gate = new(capacity, capacity);
    private long _acquired, _released, _incompleteWaits;
    private int _outstanding, _peak;
    internal long Acquired => Interlocked.Read(ref _acquired);
    internal long Released => Interlocked.Read(ref _released);
    internal long IncompleteWaits => Interlocked.Read(ref _incompleteWaits);
    internal void ObserveIncompleteWait() => Interlocked.Increment(ref _incompleteWaits);
    internal int Peak => Volatile.Read(ref _peak);
    internal int Outstanding => Volatile.Read(ref _outstanding);
    internal void OnAcquired()
    {
        Interlocked.Increment(ref _acquired);
        int active = Interlocked.Increment(ref _outstanding);
        int peak;
        do { peak = Volatile.Read(ref _peak); if (active <= peak) break; }
        while (Interlocked.CompareExchange(ref _peak, active, peak) != peak);
        if (active > capacity) throw new InvalidOperationException("Receive-credit capacity exceeded");
    }
    internal void ReleaseAfterReceive()
    {
        Interlocked.Decrement(ref _outstanding);
        Interlocked.Increment(ref _released);
        Gate.Release();
    }
    internal void ResetPeak() => Volatile.Write(ref _peak, 0);
    public void Dispose() => Gate.Dispose();
}
