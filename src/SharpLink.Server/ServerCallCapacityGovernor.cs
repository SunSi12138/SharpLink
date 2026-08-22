namespace SharpLink.Server;

/// <summary>
/// Phase 0 primitive for the #273 two-phase call lifecycle.
/// A reservation consumes call capacity immediately and remains capacity-owning
/// when it is activated; activation only changes lifecycle accounting.
/// </summary>
internal sealed class ServerCallCapacityGovernor
{
    // High 32 bits: reserved calls. Low 32 bits: active calls.
    // Keeping both counters in one atomic word makes every stable snapshot satisfy
    // reserved + active <= capacity without a request-path lock.
    private long _state;
    private readonly int _capacity;

    internal ServerCallCapacityGovernor(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal int Capacity => _capacity;

    internal bool TryReserve(out ServerCallReservation reservation)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var reserved = GetReserved(observed);
            var active = GetActive(observed);
            if ((long)reserved + active >= _capacity)
            {
                reservation = null!;
                return false;
            }

            var updated = Pack(reserved + 1, active);
            if (Interlocked.CompareExchange(ref _state, updated, observed) != observed)
                continue;

            reservation = new ServerCallReservation(this);
            return true;
        }
    }

    internal ServerCallCapacitySnapshot CaptureSnapshot()
    {
        var state = Volatile.Read(ref _state);
        return new ServerCallCapacitySnapshot(
            GetReserved(state),
            GetActive(state),
            _capacity);
    }

    internal void AssertInvariant()
    {
        var snapshot = CaptureSnapshot();
        if (snapshot.ReservedCalls < 0 || snapshot.ActiveCalls < 0)
            throw new InvalidOperationException("Server call capacity accounting became negative.");
        if ((long)snapshot.ReservedCalls + snapshot.ActiveCalls > snapshot.Capacity)
        {
            throw new InvalidOperationException(
                "Server call capacity invariant violated: reserved + active exceeds capacity.");
        }
    }

    private void ActivateReservation()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var reserved = GetReserved(observed);
            var active = GetActive(observed);
            if (reserved == 0)
                throw new InvalidOperationException("No reserved call is available to activate.");

            var updated = Pack(reserved - 1, checked(active + 1));
            if (Interlocked.CompareExchange(ref _state, updated, observed) == observed)
                return;
        }
    }

    private void ReleaseReservation()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var reserved = GetReserved(observed);
            var active = GetActive(observed);
            if (reserved == 0)
                throw new InvalidOperationException("Server reserved call count underflowed.");

            var updated = Pack(reserved - 1, active);
            if (Interlocked.CompareExchange(ref _state, updated, observed) == observed)
                return;
        }
    }

    private void ReleaseActiveCall()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var reserved = GetReserved(observed);
            var active = GetActive(observed);
            if (active == 0)
                throw new InvalidOperationException("Server active call count underflowed.");

            var updated = Pack(reserved, active - 1);
            if (Interlocked.CompareExchange(ref _state, updated, observed) == observed)
                return;
        }
    }

    private static int GetReserved(long state) => unchecked((int)(uint)(state >> 32));

    private static int GetActive(long state) => unchecked((int)(uint)state);

    private static long Pack(int reserved, int active)
        => ((long)(uint)reserved << 32) | (uint)active;

    /// <summary>
    /// Identity-bearing Phase 0 reservation for one capacity slot. Aliases refer to
    /// the same lifecycle state, so a stale reference cannot release a later lease.
    /// Production wiring should fold this identity/state into the unique request permit
    /// or request context instead of treating this standalone allocation as the target shape.
    /// </summary>
    internal sealed class ServerCallReservation : IDisposable
    {
        private const int Reserved = 0;
        private const int Activating = 1;
        private const int Active = 2;
        private const int Disposed = 3;

        private readonly ServerCallCapacityGovernor _owner;
        private int _state = Reserved;

        internal ServerCallReservation(ServerCallCapacityGovernor owner)
        {
            _owner = owner;
        }

        internal bool IsReserved => Volatile.Read(ref _state) == Reserved;

        internal bool IsActive => Volatile.Read(ref _state) == Active;

        internal void Activate()
        {
            var observed = Interlocked.CompareExchange(ref _state, Activating, Reserved);
            if (observed != Reserved)
            {
                if (observed == Disposed)
                    throw new ObjectDisposedException(nameof(ServerCallReservation));

                throw new InvalidOperationException("Only a reserved call can be activated.");
            }

            try
            {
                _owner.ActivateReservation();
                Volatile.Write(ref _state, Active);
            }
            catch
            {
                Volatile.Write(ref _state, Reserved);
                throw;
            }
        }

        public void Dispose()
        {
            var spinner = new SpinWait();
            while (true)
            {
                var observed = Volatile.Read(ref _state);
                switch (observed)
                {
                    case Reserved:
                        if (Interlocked.CompareExchange(ref _state, Disposed, Reserved) != Reserved)
                            continue;

                        _owner.ReleaseReservation();
                        return;
                    case Activating:
                        spinner.SpinOnce();
                        continue;
                    case Active:
                        if (Interlocked.CompareExchange(ref _state, Disposed, Active) != Active)
                            continue;

                        _owner.ReleaseActiveCall();
                        return;
                    case Disposed:
                        return;
                    default:
                        throw new InvalidOperationException("Unknown server call reservation state.");
                }
            }
        }
    }
}

internal readonly record struct ServerCallCapacitySnapshot(
    int ReservedCalls,
    int ActiveCalls,
    int Capacity)
{
    internal int OccupiedCalls => checked(ReservedCalls + ActiveCalls);
}
