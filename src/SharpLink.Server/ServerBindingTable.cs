namespace SharpLink.Server;

// One table per physical Session. Registry publication may enter this gate; none of
// these paths enter the registry, invoke business code, or call a supplied callback.
internal sealed class ServerBindingTable
{
    private readonly Lock _gate = new();
    private readonly ServerBindingEntry?[] _entries;
    private readonly ulong[] _incarnations;
    private readonly ServerBindingSlotQuota _quota;
    private ulong _highestBindId;
    private ulong _completedBindId;
    private bool _currentBindAttempted;
    private int _nextSlot;
    private int _closed;
    private bool _bindingDraining;

    // A nonzero initial incarnation only skips token values, never revives them. This
    // internal construction seam permits exact exhaustion tests without mutable counter resets.
    internal ServerBindingTable(uint slots, ServerBindingSlotQuota globalQuota, ulong initialIncarnation = 0)
    {
        if (slots is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(slots));
        if (initialIncarnation > Protocol3BindingToken.MaximumIncarnation)
            throw new ArgumentOutOfRangeException(nameof(initialIncarnation));
        ArgumentNullException.ThrowIfNull(globalQuota);
        _quota = globalQuota;
        _entries = new ServerBindingEntry?[checked((int)slots)];
        _incarnations = new ulong[_entries.Length];
        if (initialIncarnation != 0)
            Array.Fill(_incarnations, initialIncarnation);
    }

    internal int SlotCount => _entries.Length;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    // Called for EVERY structurally accepted Bind before contract resolution, including
    // those answered without a registration. The receive-loop controller serializes this
    // observation and its optional TryBind; it owns pending/final-result lifecycle records.
    internal void ObserveBindId(ulong creatorOperationId)
    {
        Protocol3BindingValidation.ValidateOperationId(creatorOperationId);
        lock (_gate)
        {
            if (creatorOperationId <= _highestBindId)
                throw new SharpLinkProtocolViolationException(ProtocolViolationReason.ProtocolState,
                    "New Bind IDs must be strictly increasing within the physical Session.");
            _highestBindId = creatorOperationId;
            _currentBindAttempted = false;
        }
    }

    // Caller has already checked request/receipt/auth scope and holds the registry
    // publication gate for this exact registration until this immutable entry is installed.
    // Only the current observed ID may attempt installation, at most once even after a
    // failed attempt or later Release. Async resolution requires controller serialization.
    internal Protocol3BindStatus TryBind(ulong creatorOperationId,
        ServerBindingRegistration registration, out ServerBindingEntry? entry)
    {
        Protocol3BindingValidation.ValidateOperationId(creatorOperationId);
        ArgumentNullException.ThrowIfNull(registration);
        entry = null;
        lock (_gate)
        {
            if (creatorOperationId != _highestBindId || creatorOperationId <= _completedBindId || _currentBindAttempted)
                throw new SharpLinkProtocolViolationException(ProtocolViolationReason.ProtocolState,
                    "Binding installation must consume the current observed Bind exactly once.");
            _currentBindAttempted = true;
            if (IsClosed || _bindingDraining)
                return Protocol3BindStatus.SessionDraining;
            if (registration.IsRetired)
                return Protocol3BindStatus.RegistrationRetiring;

            var slot = FindEmptySlot();
            if (slot < 0)
                return Protocol3BindStatus.BindingCapacity;
            if (_incarnations[slot] == Protocol3BindingToken.MaximumIncarnation)
            {
                _bindingDraining = true;
                return Protocol3BindStatus.SessionDraining;
            }
            if (!_quota.TryReserve())
                return Protocol3BindStatus.BindingCapacity;

            var incarnation = _incarnations[slot] + 1;
            try
            {
                entry = new ServerBindingEntry(this, (incarnation << 16) | (uint)slot,
                    creatorOperationId, registration);
            }
            catch
            {
                _quota.Release(1);
                throw;
            }
            _incarnations[slot] = incarnation;
            Volatile.Write(ref _entries[slot], entry);
            _nextSlot = slot + 1 == _entries.Length ? 0 : slot + 1;
            return Protocol3BindStatus.Success;
        }
    }

    // Exactly one immutable slot read. A captured entry never follows slot reuse or
    // a registry replacement; Release removes future captures, not already admitted calls.
    internal ServerBindingEntry? Capture(ulong token)
    {
        if (!Protocol3BindingToken.IsValid(token, (uint)_entries.Length))
            return null;
        var entry = Volatile.Read(ref _entries[Protocol3BindingToken.Slot(token)]);
        return entry is not null && entry.Token == token ? entry : null;
    }

    // A captured-before-Release entry remains its exact old selection, never the next
    // slot occupant. This check is not a disposal fence or a physical admission lease.
    // Existing request permits and the post-permit module Running gate own that boundary.
    internal bool CanUseCaptured(ServerBindingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ReferenceEquals(entry.Owner, this) && !IsClosed && !entry.Registration.IsRetired;
    }

    internal bool Release(ulong token)
    {
        if (!Protocol3BindingToken.IsValid(token, (uint)_entries.Length))
            throw Protocol3BindingValidation.Malformed("Release token is outside the Session slot domain.");
        lock (_gate)
        {
            var slot = Protocol3BindingToken.Slot(token);
            var entry = _entries[slot];
            if (entry is null || entry.Token != token)
                return false;
            Volatile.Write(ref _entries[slot], null);
        }
        _quota.Release(1);
        return true;
    }

    // The serialized receive controller calls this only after its one final result has
    // completed publication. A later Cancel for that operation is not a token Release.
    internal void CompleteBindId(ulong creatorOperationId)
    {
        Protocol3BindingValidation.ValidateOperationId(creatorOperationId);
        lock (_gate)
        {
            if (creatorOperationId != _highestBindId || creatorOperationId <= _completedBindId)
                throw new InvalidOperationException("Binding completion must consume the current observed operation once.");
            _completedBindId = creatorOperationId;
        }
    }

    // Bounded maintenance scan; only scalar full tokens escape the table. Retirement is
    // authoritative even if every best-effort notification is dropped by the caller.
    internal int TakeRetiredTokens(Span<ulong> tokens, ref int cursor, int scanLimit = 256)
    {
        if (cursor < 0 || cursor > _entries.Length || scanLimit <= 0 || tokens.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(cursor));
        var count = 0;
        lock (_gate)
        {
            var end = (int)Math.Min(_entries.Length, (long)cursor + scanLimit);
            while (cursor < end && count < tokens.Length)
            {
                var slot = cursor++;
                if (_entries[slot] is not { } entry || !entry.Registration.IsRetired)
                    continue;
                tokens[count++] = entry.Token;
                Volatile.Write(ref _entries[slot], null);
            }
        }
        if (count != 0)
            _quota.Release(count);
        return count;
    }

    // The operation controller owns pending/precommit cancellation. This removes only
    // a committed slot created by this exact ID, never another demand for the same key.
    internal bool Cancel(ulong creatorOperationId)
    {
        Protocol3BindingValidation.ValidateOperationId(creatorOperationId);
        var removed = false;
        lock (_gate)
        {
            if (creatorOperationId > _highestBindId)
                throw new SharpLinkProtocolViolationException(ProtocolViolationReason.ProtocolState,
                    "CancelBind cannot precede its Bind publication.");
            if (creatorOperationId <= _completedBindId)
                return false;
            for (var slot = 0; slot < _entries.Length; slot++)
            {
                if (_entries[slot] is not { } entry || entry.CreatorOperationId != creatorOperationId)
                    continue;
                Volatile.Write(ref _entries[slot], null);
                removed = true;
                break;
            }
        }
        if (removed)
            _quota.Release(1);
        return removed;
    }

    internal void Close()
    {
        var removed = 0;
        lock (_gate)
        {
            // Mark terminal before detaching slots; captured entries see this same cut.
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            for (var slot = 0; slot < _entries.Length; slot++)
            {
                if (_entries[slot] is null)
                    continue;
                Volatile.Write(ref _entries[slot], null);
                removed++;
            }
        }
        if (removed != 0)
            _quota.Release(removed);
    }

    private int FindEmptySlot()
    {
        for (var count = 0; count < _entries.Length; count++)
        {
            var slot = (_nextSlot + count) % _entries.Length;
            if (_entries[slot] is null)
                return slot;
        }
        return -1;
    }
}

internal sealed class ServerBindingEntry
{
    internal ServerBindingEntry(ServerBindingTable owner, ulong token, ulong creatorOperationId,
        ServerBindingRegistration registration)
    {
        Owner = owner;
        Token = token;
        CreatorOperationId = creatorOperationId;
        Registration = registration;
    }

    internal ServerBindingTable Owner { get; }
    internal ulong Token { get; }
    internal ulong CreatorOperationId { get; }
    internal ServerBindingRegistration Registration { get; }
}

// Shared by every table in one server. Reservations precede cold entry allocation and
// publication. No callback or registry/table lock is acquired by quota accounting.
internal sealed class ServerBindingSlotQuota
{
    internal const int DefaultLimit = 65536;
    internal const int HardLimit = 1048576;
    private readonly int _limit;
    private int _used;

    internal ServerBindingSlotQuota(int limit = DefaultLimit)
    {
        if (limit is < 1 or > HardLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
    }

    internal int Used => Volatile.Read(ref _used);

    internal bool TryReserve()
    {
        while (true)
        {
            var current = Volatile.Read(ref _used);
            if (current == _limit)
                return false;
            if (Interlocked.CompareExchange(ref _used, current + 1, current) == current)
                return true;
        }
    }

    internal void Release(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (Interlocked.Add(ref _used, -count) < 0)
            throw new InvalidOperationException("Binding slot quota was released more than once.");
    }
}
