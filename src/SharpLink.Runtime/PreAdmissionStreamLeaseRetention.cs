namespace SharpLink.Runtime;

/// <summary>
/// Adapts disposable retained-byte permits to the callback retention contract used by
/// <see cref="PreAdmissionStreamDispatcher"/>. Every successful reservation owns exactly one
/// permit; every matching release disposes exactly one permit.
/// </summary>
internal sealed class PreAdmissionStreamLeaseRetention
{
    private readonly Func<int, IDisposable?> _reserveLease;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Queue<IDisposable>> _leasesByRetainedBytes = [];

    internal PreAdmissionStreamLeaseRetention(Func<int, IDisposable?> reserveLease)
    {
        _reserveLease = reserveLease ?? throw new ArgumentNullException(nameof(reserveLease));
    }

    internal bool TryReserve(int retainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedBytes);
        var lease = _reserveLease(retainedBytes);
        if (lease is null)
            return false;

        try
        {
            lock (_gate)
            {
                if (!_leasesByRetainedBytes.TryGetValue(retainedBytes, out var leases))
                {
                    leases = new Queue<IDisposable>();
                    _leasesByRetainedBytes.Add(retainedBytes, leases);
                }
                leases.Enqueue(lease);
            }
            return true;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal void Release(int retainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedBytes);
        IDisposable lease;
        lock (_gate)
        {
            if (!_leasesByRetainedBytes.TryGetValue(retainedBytes, out var leases) || leases.Count == 0)
            {
                throw new InvalidOperationException(
                    "Pre-admission stream retained-byte lease accounting became unbalanced.");
            }

            lease = leases.Dequeue();
            if (leases.Count == 0)
                _leasesByRetainedBytes.Remove(retainedBytes);
        }

        lease.Dispose();
    }
}
