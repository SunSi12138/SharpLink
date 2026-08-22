namespace SharpLink.Server;

/// <summary>
/// Allocation-free Phase 0 primitive for the #273 two-phase call lifecycle.
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
                reservation = default;
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
    /// Single-owner value representing one capacity slot. The request path must not
    /// copy this value after acquisition; ownership is transferred by ref until it is
    /// either activated and eventually disposed, or disposed while still reserved.
    /// </summary>
    internal struct ServerCallReservation : IDisposable
    {
        private ServerCallCapacityGovernor? _owner;
        private ReservationState _state;

        internal ServerCallReservation(ServerCallCapacityGovernor owner)
        {
            _owner = owner;
            _state = ReservationState.Reserved;
        }

        internal bool IsReserved => _owner is not null && _state == ReservationState.Reserved;

        internal bool IsActive => _owner is not null && _state == ReservationState.Active;

        internal void Activate()
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(ServerCallReservation));
            if (_state != ReservationState.Reserved)
                throw new InvalidOperationException("Only a reserved call can be activated.");

            owner.ActivateReservation();
            _state = ReservationState.Active;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null)
                return;

            switch (_state)
            {
                case ReservationState.Reserved:
                    owner.ReleaseReservation();
                    break;
                case ReservationState.Active:
                    owner.ReleaseActiveCall();
                    break;
                default:
                    throw new InvalidOperationException("Unknown server call reservation state.");
            }

            _state = ReservationState.None;
            _owner = null;
        }

        private enum ReservationState : byte
        {
            None,
            Reserved,
            Active
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
