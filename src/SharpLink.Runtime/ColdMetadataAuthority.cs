namespace SharpLink.Runtime;

// Cold logical accounting. These are policy units, not measured CLR allocation sizes.
internal sealed class ColdMetadataAuthority
{
    internal const ulong ContractByteLimit = 16UL * 1024 * 1024;
    internal const ulong RegistrationByteLimit = 64UL * 1024 * 1024;
    internal const ulong AuthorityByteLimit = 256UL * 1024 * 1024;
    internal const int ContractMethodLimit = 65_536;
    internal const int RegistrationMethodLimit = 262_144;
    internal const int AuthorityMethodLimit = 1_048_576;
    internal const ulong HeaderCharge = 256;
    internal const ulong SlotCharge = 128;
    internal readonly Lock Gate = new();
    private ulong _bytes;
    private int _methods;
    internal ulong Bytes { get { lock (Gate) return _bytes; } }
    internal int Methods { get { lock (Gate) return _methods; } }

    internal ColdMetadataDomain CreateDomain(ColdMetadataDomain? parent = null)
    {
        if (parent is not null && !ReferenceEquals(parent.Authority, this))
            throw new InvalidOperationException("A metadata domain cannot change authority.");
        return new ColdMetadataDomain(this, parent);
    }

    internal void Reserve(ulong bytes, int methods, ColdMetadataScope? scope)
    {
        if (bytes > AuthorityByteLimit - _bytes || methods > AuthorityMethodLimit - _methods)
            throw ColdMetadataException.Capacity();
        for (var current = scope; current is not null; current = current.Parent)
        {
            if (!ReferenceEquals(current.Authority, this))
                throw new InvalidOperationException("Metadata reservation belongs to another authority.");
            if (bytes > current.ByteLimit - current.ReservedBytes || methods > current.MethodLimit - current.ReservedMethods)
                throw ColdMetadataException.Capacity();
        }
        _bytes += bytes;
        _methods += methods;
        for (var current = scope; current is not null; current = current.Parent)
        {
            current.ReservedBytes += bytes;
            current.ReservedMethods += methods;
        }
    }

    internal void Release(ulong bytes, int methods, ColdMetadataScope? scope)
    {
        _bytes -= bytes;
        _methods -= methods;
        for (var current = scope; current is not null; current = current.Parent)
        {
            current.ReservedBytes -= bytes;
            current.ReservedMethods -= methods;
        }
    }
}

internal sealed class ColdMetadataScope
{
    internal ColdMetadataScope(ColdMetadataAuthority authority, ColdMetadataScope? parent)
    { Authority = authority; Parent = parent; }
    internal ColdMetadataAuthority Authority { get; }
    internal ColdMetadataScope? Parent { get; }
    internal ulong ReservedBytes;
    internal int ReservedMethods;
    private bool _factsClaimed;
    internal ulong ByteLimit => Parent is null ? ColdMetadataAuthority.RegistrationByteLimit : ColdMetadataAuthority.ContractByteLimit;
    internal int MethodLimit => Parent is null ? ColdMetadataAuthority.RegistrationMethodLimit : ColdMetadataAuthority.ContractMethodLimit;
    internal ulong Bytes { get { lock (Authority.Gate) return ReservedBytes; } }
    internal int Methods { get { lock (Authority.Gate) return ReservedMethods; } }
    internal void ClaimFactsTable()
    {
        lock (Authority.Gate)
        {
            if (Parent is null || _factsClaimed)
                throw new InvalidOperationException("An exact contract domain can own only one declared-facts table.");
            _factsClaimed = true;
        }
    }
    internal ColdMetadataScope CreateContractScope()
    {
        if (Parent is not null)
            throw new InvalidOperationException("A contract metadata scope cannot contain another scope.");
        return new ColdMetadataScope(Authority, this);
    }
}

internal sealed class ColdMetadataDomain
{
    private bool _closed;
    private int _active;
    private TaskCompletionSource? _joined;
    internal ColdMetadataDomain(ColdMetadataAuthority authority, ColdMetadataDomain? parent)
    { Authority = authority; Parent = parent; }
    internal ColdMetadataAuthority Authority { get; }
    internal ColdMetadataDomain? Parent { get; }
    internal bool IsWithin(ColdMetadataDomain other)
    {
        for (ColdMetadataDomain? current = this; current is not null; current = current.Parent)
            if (ReferenceEquals(current, other)) return true;
        return false;
    }
    internal ColdMetadataPreparation BeginPreparation()
    {
        ColdMetadataPreparation.ThrowIfCallbackReentry(this);
        lock (Authority.Gate)
        {
            for (ColdMetadataDomain? current = this; current is not null; current = current.Parent)
                if (current._closed)
                    throw new ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                        "Metadata preparation admission is closed.");
            Authority.Reserve(ColdMetadataAuthority.HeaderCharge, 0, null);
            try
            {
                var owner = new ColdMetadataPreparation(this);
                for (ColdMetadataDomain? current = this; current is not null; current = current.Parent)
                    current._active++;
                return owner;
            }
            catch { Authority.Release(ColdMetadataAuthority.HeaderCharge, 0, null); throw; }
        }
    }
    internal Task SealAndJoinAsync()
    {
        ColdMetadataPreparation.ThrowIfCallbackReentry(this);
        lock (Authority.Gate)
        {
            _closed = true;
            return _active == 0 ? Task.CompletedTask :
                (_joined ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
    internal void Complete()
    {
        lock (Authority.Gate)
        {
            for (ColdMetadataDomain? current = this; current is not null; current = current.Parent)
                if (--current._active == 0) current._joined?.TrySetResult();
        }
    }
}

internal sealed class ColdMetadataPreparation : IDisposable
{
    private static readonly AsyncLocal<ColdMetadataPreparation?> CallbackOwner = new();
    private ColdMetadataReservation? _reservations;
    private SharedLease? _shared;
    private int _references = 1;
    private int _initialDisposed;
    private int _completed;
    internal ColdMetadataPreparation(ColdMetadataDomain domain) => Domain = domain;
    internal ColdMetadataDomain Domain { get; }
    internal ColdMetadataAuthority Authority => Domain.Authority;
    internal ColdMetadataScope CreateRegistrationScope() => new(Authority, null);
    // Host-only derived storage, not a new management operation. This is also available during
    // admitted shutdown cleanup; it runs no callbacks and cannot reopen a closed domain.
    internal ColdMetadataPreparation ForkStorage()
    {
        lock (Authority.Gate)
        {
            if (_references == 0) throw new ObjectDisposedException(nameof(ColdMetadataPreparation));
            Authority.Reserve(ColdMetadataAuthority.HeaderCharge, 0, null);
            try { return new ColdMetadataPreparation(Domain) { _completed = 1 }; }
            catch { Authority.Release(ColdMetadataAuthority.HeaderCharge, 0, null); throw; }
        }
    }
    internal void Share(ColdMetadataPreparation source)
    {
        if (ReferenceEquals(source, this)) return;
        if (!ReferenceEquals(source.Authority, Authority))
            throw new InvalidOperationException("Prepared metadata cannot cross authorities without a charged recapture.");
        var charge = ReserveCollection(0);
        IDisposable? lease = null;
        try
        {
            lease = source.Retain();
            lock (Authority.Gate)
            {
                var node = new SharedLease(lease, _shared);
                _shared = node;
            }
        }
        catch { lease?.Dispose(); charge.Dispose(); throw; }
    }
    internal ColdMetadataReservation ReserveCollection(int capacity, ColdMetadataScope? scope = null)
    {
        if (capacity < 0) throw ColdMetadataException.Invalid("Metadata collection count is negative.");
        return Reserve(checked(ColdMetadataAuthority.HeaderCharge + ColdMetadataAuthority.SlotCharge * (ulong)capacity), 0, capacity, scope);
    }
    internal ColdMetadataReservation ReserveMethods(int count, ColdMetadataScope scope)
    {
        if (count < 0) throw ColdMetadataException.Invalid("Declared method count is negative.");
        return Reserve(0, count, 0, scope);
    }
    private ColdMetadataReservation Reserve(ulong bytes, int methods, int capacity, ColdMetadataScope? scope)
    {
        lock (Authority.Gate)
        {
            if (_references == 0) throw new ObjectDisposedException(nameof(ColdMetadataPreparation));
            Authority.Reserve(bytes, methods, scope);
            try
            {
                var reservation = new ColdMetadataReservation(this, scope, bytes, methods, capacity, _reservations);
                if (_reservations is not null) _reservations.Previous = reservation;
                _reservations = reservation;
                return reservation;
            }
            catch { Authority.Release(bytes, methods, scope); throw; }
        }
    }
    internal IDisposable Retain()
    {
        lock (Authority.Gate)
        {
            if (_references == 0) throw new ObjectDisposedException(nameof(ColdMetadataPreparation));
            var lease = new Lease(this);
            _references = checked(_references + 1);
            return lease;
        }
    }
    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) Domain.Complete();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _initialDisposed, 1) != 0) return;
        Complete();
        ReleaseReference();
    }
    private void ReleaseReference()
    {
        lock (Authority.Gate)
        {
            if (--_references != 0) return;
            var reservation = _reservations;
            _reservations = null;
            while (reservation is not null)
            {
                var next = reservation.Next;
                reservation.Dispose();
                reservation = next;
            }
            var shared = _shared;
            _shared = null;
            while (shared is not null)
            {
                shared.Lease.Dispose();
                shared = shared.Next;
            }
            Authority.Release(ColdMetadataAuthority.HeaderCharge, 0, null);
        }
    }
    internal void Remove(ColdMetadataReservation reservation)
    {
        if (reservation.Previous is { } previous) previous.Next = reservation.Next;
        else if (ReferenceEquals(_reservations, reservation)) _reservations = reservation.Next;
        if (reservation.Next is { } next) next.Previous = reservation.Previous;
        reservation.Next = null;
        reservation.Previous = null;
    }
    internal CallbackScope EnterCallback()
    {
        var previous = CallbackOwner.Value;
        CallbackOwner.Value = this;
        return new CallbackScope(previous);
    }
    internal static void ThrowIfCallbackReentry(ColdMetadataDomain domain)
    {
        if (CallbackOwner.Value is { } owner && owner.Domain.IsWithin(domain))
            throw new ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "A metadata callback cannot reenter its owning admission or shutdown domain.");
    }
    internal readonly struct CallbackScope(ColdMetadataPreparation? previous) : IDisposable
    { public void Dispose() => CallbackOwner.Value = previous; }
    private sealed class Lease(ColdMetadataPreparation owner) : IDisposable
    {
        private ColdMetadataPreparation? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseReference();
    }
    private sealed record SharedLease(IDisposable Lease, SharedLease? Next);
}

// A reservation is one accounting token, independent of its original preparation owner.
// Retention is used only at cold ownership transfer, never at RPC lookup/dispatch.
internal sealed class ColdMetadataReservation : IDisposable
{
    private readonly ColdMetadataAuthority _authority;
    private ColdMetadataPreparation? _owner;
    private readonly ColdMetadataScope? _scope;
    private ulong _bytes;
    private int _methods;
    private int _capacity;
    private int _references = 1;
    private bool _originalDisposed;
    internal ColdMetadataReservation(ColdMetadataPreparation owner, ColdMetadataScope? scope,
        ulong bytes, int methods, int capacity, ColdMetadataReservation? next)
    {
        _authority = owner.Authority; _owner = owner; _scope = scope;
        _bytes = bytes; _methods = methods; _capacity = capacity; Next = next;
    }
    internal ColdMetadataReservation? Next { get; set; }
    internal ColdMetadataReservation? Previous { get; set; }
    internal bool IsDetached
    {
        get { lock (_authority.Gate) return _owner is null && Next is null && Previous is null; }
    }

    // The returned lease owns only this token. Disposing the original owner detaches the
    // linked node even if a table lease remains, so it cannot retain sibling reservations.
    internal IDisposable Retain()
    {
        lock (_authority.Gate)
        {
            ObjectDisposedException.ThrowIf(_references == 0, this);
            var references = checked(_references + 1);
            var lease = new Lease(this);
            _references = references;
            return lease;
        }
    }
    internal void GrowTo(int capacity)
    {
        if (capacity < 0) throw ColdMetadataException.Invalid("Metadata collection count overflowed.");
        lock (_authority.Gate)
        {
            // Detached accounting tokens are frozen; retained table owners do not reopen
            // the original preparation or acquire permission to grow its collection.
            ObjectDisposedException.ThrowIf(_originalDisposed, this);
            if (capacity <= _capacity) return;
            var extra = checked((ulong)(capacity - _capacity) * ColdMetadataAuthority.SlotCharge);
            _authority.Reserve(extra, 0, _scope);
            _bytes += extra;
            _capacity = capacity;
        }
    }
    public void Dispose()
    {
        lock (_authority.Gate)
        {
            if (_originalDisposed) return;
            _originalDisposed = true;
            _owner!.Remove(this);
            _owner = null;
            // Remove clears both neighbors. The retained token now reaches only the
            // authority and scalar scope ancestry, not a preparation/domain/callback graph.
            ReleaseReferenceLocked();
        }
    }
    private void ReleaseReference()
    {
        lock (_authority.Gate) ReleaseReferenceLocked();
    }
    private void ReleaseReferenceLocked()
    {
        if (--_references != 0) return;
        _authority.Release(_bytes, _methods, _scope);
        _bytes = 0;
        _methods = 0;
        _capacity = 0;
    }
    private sealed class Lease(ColdMetadataReservation reservation) : IDisposable
    {
        private ColdMetadataReservation? _reservation = reservation;
        public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.ReleaseReference();
    }
}

internal sealed class ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode code, string message)
    : InvalidOperationException(message)
{
    internal SharpLinkAssemblyRegistrationErrorCode Code { get; } = code;
    internal static ColdMetadataException Capacity() => new(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
        "Cold metadata capacity is exhausted.");
    internal static ColdMetadataException Invalid(string message) => new(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest, message);
}
