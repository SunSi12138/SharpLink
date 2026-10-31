namespace SharpLink.Client;

// One client-owned budget shared by all physical Sessions, independent of pool/endpoint count.
internal sealed class ClientBindingBudget
{
    private int _operations;
    private int _trackingSlots;
    internal const int MaximumOperations = 1024;
    internal const int MaximumTrackingSlots = 65536;
    internal int Operations => Volatile.Read(ref _operations);
    internal int TrackingSlots => Volatile.Read(ref _trackingSlots);

    internal bool TryReserveOperation() => TryReserve(ref _operations, 1, MaximumOperations);
    internal bool TryReserveTrackingSlots(int slots) => TryReserve(ref _trackingSlots, slots, MaximumTrackingSlots);
    internal void ReleaseOperation() => Release(ref _operations, 1);
    internal void ReleaseTrackingSlots(int slots) => Release(ref _trackingSlots, slots);

    private static bool TryReserve(ref int counter, int amount, int maximum)
    {
        if (amount <= 0 || amount > maximum)
            return false;
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current > maximum - amount)
                return false;
            if (Interlocked.CompareExchange(ref counter, current + amount, current) == current)
                return true;
        }
    }
    private static void Release(ref int counter, int amount)
    {
        if (Interlocked.Add(ref counter, -amount) < 0)
            throw new InvalidOperationException("Binding budget was released more than once.");
    }
}
