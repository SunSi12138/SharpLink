namespace SharpLink.Server;

// Cold accounting only. These charges are policy units, not measured CLR allocation sizes.
internal sealed class ServerDeclaredMethodFactsQuota
{
    internal const int ContractMethodLimit = 65_536;
    internal const int GroupMethodLimit = 262_144;
    internal const int ServerMethodLimit = 1_048_576;
    internal const ulong ContractByteLimit = 16UL * 1024 * 1024;
    internal const ulong GroupByteLimit = 64UL * 1024 * 1024;
    internal const ulong ServerByteLimit = 256UL * 1024 * 1024;
    internal const ulong TableCharge = 256;
    internal const ulong MethodCharge = 128;

    internal readonly Lock Gate = new();
    private readonly int _methodLimit;
    private readonly ulong _byteLimit;
    private int _methods;
    private ulong _bytes;

    internal ServerDeclaredMethodFactsQuota(int methodLimit = ServerMethodLimit, ulong byteLimit = ServerByteLimit)
    {
        if (methodLimit < 0 || methodLimit > ServerMethodLimit || byteLimit > ServerByteLimit)
            throw new ArgumentOutOfRangeException(nameof(methodLimit));
        _methodLimit = methodLimit;
        _byteLimit = byteLimit;
    }

    internal int Methods { get { lock (Gate) return _methods; } }
    internal ulong Bytes { get { lock (Gate) return _bytes; } }
    internal ServerDeclaredMethodFactsGroupReservation BeginGroup() => new(this);
    internal static ulong CalculateCharge(ulong methods) => checked(TableCharge + MethodCharge * methods);

    internal void Reserve(int methods, ulong bytes)
    {
        // Caller holds Gate. Subtraction avoids overflowing a provisional sum.
        if (methods > _methodLimit - _methods || bytes > _byteLimit - _bytes)
            throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                "Server declared-method facts capacity is exhausted.");
        _methods = checked(_methods + methods);
        _bytes = checked(_bytes + bytes);
    }

    internal void Release(int methods, ulong bytes)
    {
        if (methods > _methods || bytes > _bytes)
            throw new InvalidOperationException("Declared-method facts charge released more than once.");
        _methods -= methods;
        _bytes -= bytes;
    }
}

// The parent preparation owner must outlive all inline callbacks and rollback cleanup.
// Reserve every table/count before Seal; only then may any facts arrays/callbacks begin.
internal sealed class ServerDeclaredMethodFactsGroupReservation(ServerDeclaredMethodFactsQuota quota) : IDisposable
{
    private readonly List<ServerDeclaredMethodFactsReservation> _reservations = [];
    private int _methods;
    private ulong _bytes;
    private bool _sealed;
    private bool _committed;
    private bool _disposed;
    internal ServerDeclaredMethodFactsQuota Quota => quota;
    internal bool IsEmpty { get { lock (quota.Gate) return _bytes == 0; } }

    internal ServerDeclaredMethodFactsReservation ReserveContract()
    {
        lock (quota.Gate)
        {
            EnsureOpen();
            Reserve(0, ServerDeclaredMethodFactsQuota.TableCharge);
            try
            {
                var reservation = new ServerDeclaredMethodFactsReservation(this);
                _reservations.Add(reservation);
                return reservation;
            }
            catch
            {
                Release(0, ServerDeclaredMethodFactsQuota.TableCharge);
                throw;
            }
        }
    }

    internal void SetMethodCount(ServerDeclaredMethodFactsReservation reservation, int count)
    {
        lock (quota.Gate)
        {
            EnsureOpen();
            if (reservation.CountAssigned || reservation.Released)
                throw new InvalidOperationException("A table count can be assigned only once.");
            if (count < 0)
                throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "A declared method count cannot be negative.");
            if (count > ServerDeclaredMethodFactsQuota.ContractMethodLimit ||
                ServerDeclaredMethodFactsQuota.CalculateCharge((ulong)count) > ServerDeclaredMethodFactsQuota.ContractByteLimit)
                throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                    "Contract declared-method facts capacity is exhausted.");
            Reserve(count, checked((ulong)count * ServerDeclaredMethodFactsQuota.MethodCharge));
            reservation.Count = count;
            reservation.CountAssigned = true;
        }
    }

    internal void Seal()
    {
        lock (quota.Gate)
        {
            EnsureOpen();
            _reservations.RemoveAll(static item => item.Released);
            if (_reservations.Any(static item => !item.CountAssigned))
                throw new InvalidOperationException("Every table count must be reserved before preparation.");
            _sealed = true;
        }
    }

    internal void DiscardContract(ServerDeclaredMethodFactsReservation reservation)
    {
        lock (quota.Gate)
        {
            EnsureOpen();
            if (!ReferenceEquals(reservation.Group, this) || reservation.CountAssigned || reservation.Started)
                throw new InvalidOperationException("Only unused header-only reservations may be discarded.");
            ReleaseTable(reservation);
        }
    }

    internal void BeginPreparation(ServerDeclaredMethodFactsReservation reservation)
    {
        lock (quota.Gate)
        {
            if (!_sealed || _disposed || _committed || reservation.Started || reservation.Released)
                throw new InvalidOperationException("Facts preparation requires one sealed, live reservation.");
            reservation.Started = true;
        }
    }

    internal void CompletePreparation(ServerDeclaredMethodFactsReservation reservation)
    {
        lock (quota.Gate)
        {
            if (_disposed || reservation.Released)
                throw new InvalidOperationException("Preparation owner was released before its callback returned.");
            reservation.Prepared = true;
        }
    }

    internal void Commit()
    {
        lock (quota.Gate)
        {
            if (!_sealed || _disposed || _committed || _reservations.Any(static item => !item.Prepared || item.Released))
                throw new InvalidOperationException("Only a completely prepared group can transfer ownership.");
            _committed = true;
            // Each table now owns its reservation. No retained graph of sibling tables/registrations.
            _reservations.Clear();
        }
    }

    internal void ReleaseTable(ServerDeclaredMethodFactsReservation reservation)
    {
        lock (quota.Gate)
        {
            if (reservation.Released)
                return;
            reservation.Released = true;
            Release(reservation.Count, ServerDeclaredMethodFactsQuota.CalculateCharge((ulong)reservation.Count));
        }
    }

    public void Dispose()
    {
        lock (quota.Gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (!_committed)
            {
                foreach (var reservation in _reservations)
                    ReleaseTable(reservation);
                _reservations.Clear();
            }
        }
    }

    private void EnsureOpen()
    {
        if (_sealed || _disposed)
            throw new InvalidOperationException("The facts reservation prepass is closed.");
    }

    private void Reserve(int methods, ulong bytes)
    {
        if (methods > ServerDeclaredMethodFactsQuota.GroupMethodLimit - _methods ||
            bytes > ServerDeclaredMethodFactsQuota.GroupByteLimit - _bytes)
            throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                "Registration-group declared-method facts capacity is exhausted.");
        quota.Reserve(methods, bytes);
        _methods = checked(_methods + methods);
        _bytes = checked(_bytes + bytes);
    }

    private void Release(int methods, ulong bytes)
    {
        quota.Release(methods, bytes);
        _methods -= methods;
        _bytes -= bytes;
    }
}

internal sealed class ServerDeclaredMethodFactsReservation(ServerDeclaredMethodFactsGroupReservation group)
{
    internal ServerDeclaredMethodFactsGroupReservation Group => group;
    internal int Count;
    internal bool CountAssigned;
    internal bool Started;
    internal bool Prepared;
    internal bool Released;
    internal void SetMethodCount(int count) => group.SetMethodCount(this, count);
}

// Transaction-local exception. Parent maps these scalar fields to the existing reference-free result.
internal sealed class ServerDeclaredMethodFactsException(
    SharpLinkAssemblyRegistrationErrorCode code, string message, long? contractId = null, long? methodId = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    internal SharpLinkAssemblyRegistrationErrorCode Code { get; } = code;
    internal long? ContractId { get; } = contractId;
    internal long? MethodId { get; } = methodId;
}
