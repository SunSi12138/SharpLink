namespace SharpLink.Server;

// Server facts are another physical metadata copy in the SAME Runtime authority. This
// adapter does not create an additional server-wide byte allowance.
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
    internal ColdMetadataAuthority Authority { get; }
    internal ColdMetadataDomain Domain { get; }

    internal ServerDeclaredMethodFactsQuota(ColdMetadataDomain domain)
    {
        Domain = domain ?? throw new ArgumentNullException(nameof(domain));
        Authority = domain.Authority;
    }

    // Kept for exact component owners; production always supplies its shared Runtime domain.
    internal ServerDeclaredMethodFactsQuota() : this(new ColdMetadataAuthority().CreateDomain()) { }

    internal int Methods => Authority.Methods;
    internal ulong Bytes => Authority.Bytes;
    internal ServerDeclaredMethodFactsGroupReservation BeginGroup(PreparedGeneratedManifest manifest)
        => new(this, manifest.Preparation, manifest.RegistrationScope, manifest, ownsPreparation: false);
    internal ServerDeclaredMethodFactsGroupReservation BeginGroup()
    {
        var preparation = Domain.BeginPreparation();
        try { return new(this, preparation, preparation.CreateRegistrationScope(), null, ownsPreparation: true); }
        catch { preparation.Dispose(); throw; }
    }
    internal static ulong CalculateCharge(ulong count) => checked(TableCharge + MethodCharge * count);
}

internal sealed class ServerDeclaredMethodFactsGroupReservation : IDisposable
{
    private readonly List<ServerDeclaredMethodFactsReservation> _reservations;
    private readonly ColdMetadataPreparation _preparation;
    private readonly ColdMetadataScope _scope;
    private PreparedGeneratedManifest? _manifest;
    private readonly bool _ownsPreparation;
    private readonly IDisposable _bundle;
    private readonly ColdMetadataReservation _listCharge;
    private bool _sealed;
    private bool _committed;
    private bool _readyToCommit;
    private bool _disposed;
    internal ServerDeclaredMethodFactsQuota Quota { get; }
    internal bool IsEmpty { get { lock (Quota.Gate) return _reservations.Count == 0; } }
    internal ColdMetadataPreparation Preparation => _preparation;
    internal ColdMetadataScope Scope => _scope;

    internal ServerDeclaredMethodFactsGroupReservation(ServerDeclaredMethodFactsQuota quota,
        ColdMetadataPreparation preparation, ColdMetadataScope scope,
        PreparedGeneratedManifest? manifest, bool ownsPreparation)
    {
        Quota = quota;
        _preparation = preparation;
        _scope = scope;
        _manifest = manifest;
        _ownsPreparation = ownsPreparation;
        _bundle = preparation.Retain();
        try
        {
            _listCharge = preparation.ReserveCollection(0, scope);
            _reservations = [];
        }
        catch { _bundle.Dispose(); throw; }
    }

    internal ServerDeclaredMethodFactsReservation ReserveContract(long? contractId = null)
    {
        lock (Quota.Gate)
        {
            EnsureOpen();
            // Local descriptors already own their exact method domain. Imported service
            // contracts create a new incoming-registration domain, never value-deduplicated.
            var local = contractId.HasValue && _manifest is not null &&
                _manifest.Contracts.Any(item => item.ContractId == contractId.Value);
            var scope = local ? _manifest!.GetContractScope(contractId!.Value) : _scope.CreateContractScope();
            _listCharge.GrowTo(checked(_reservations.Count + 1));
            var table = _preparation.ReserveCollection(0, scope);
            try
            {
                var reservation = new ServerDeclaredMethodFactsReservation(this, scope, table, local,
                    local ? _manifest!.GetContractMethodReservation(contractId!.Value) : null);
                _reservations.Add(reservation);
                return reservation;
            }
            catch { table.Dispose(); throw; }
        }
    }

    internal void SetMethodCount(ServerDeclaredMethodFactsReservation reservation, int count)
    {
        lock (Quota.Gate)
        {
            EnsureOpen();
            if (reservation.CountAssigned || reservation.Released)
                throw new InvalidOperationException("A table count can be assigned only once.");
            if (count < 0)
                throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "A declared method count cannot be negative.");
            reservation.Scope.ClaimFactsTable();
            if (reservation.ReusesDeclaredDomain && reservation.Scope.Methods != count)
                throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "Facts count must match its exact prepared method domain.");
            if (!reservation.ReusesDeclaredDomain)
                reservation.MethodCharge = _preparation.ReserveMethods(count, reservation.Scope);
            reservation.TableCharge.GrowTo(count);
            // Separate, simultaneously live descriptor array used for drift verification.
            reservation.SnapshotCharge = _preparation.ReserveCollection(count, reservation.Scope);
            reservation.Count = count;
            reservation.CountAssigned = true;
        }
    }

    internal void Seal()
    {
        lock (Quota.Gate)
        {
            EnsureOpen();
            if (_reservations.Any(static item => !item.CountAssigned))
                throw new InvalidOperationException("Every table count must be reserved before preparation.");
            _sealed = true;
        }
    }
    internal void DiscardContract(ServerDeclaredMethodFactsReservation reservation)
    {
        lock (Quota.Gate)
        {
            EnsureOpen();
            if (!ReferenceEquals(reservation.Group, this) || reservation.CountAssigned || reservation.Started)
                throw new InvalidOperationException("Only unused reservations may be discarded.");
            _reservations.Remove(reservation);
            ReleaseTable(reservation);
        }
    }
    internal void BeginPreparation(ServerDeclaredMethodFactsReservation reservation)
    {
        lock (Quota.Gate)
        {
            if (!_sealed || _disposed || _committed || reservation.Started || reservation.Released)
                throw new InvalidOperationException("Facts preparation requires one sealed, live reservation.");
            reservation.Started = true;
        }
    }
    internal void CompletePreparation(ServerDeclaredMethodFactsReservation reservation)
    {
        lock (Quota.Gate)
        {
            if (_disposed || reservation.Released)
                throw new InvalidOperationException("Preparation owner was released before its callback returned.");
            reservation.Prepared = true;
        }
    }
    internal void PrepareCommit()
    {
        lock (Quota.Gate)
        {
            if (!_sealed || _disposed || _committed || _readyToCommit ||
                _reservations.Any(static item => !item.Prepared || item.Released))
                throw new InvalidOperationException("Only a completely prepared group can prepare transfer.");
            // Allocate exact cold token leases before entering the registry publication gate.
            try
            {
                foreach (var item in _reservations)
                {
                    item.TableLease = item.TableCharge.Retain();
                    item.MethodLease = (item.LocalMethodToken ?? item.MethodCharge
                        ?? throw new InvalidOperationException("Facts method domain is missing.")).Retain();
                }
            }
            catch
            {
                // Originals still protect every candidate array. Drop only the partial
                // transfer leases; ordinary physical candidate cleanup owns final refund.
                foreach (var item in _reservations)
                {
                    item.TableLease?.Dispose(); item.TableLease = null;
                    item.MethodLease?.Dispose(); item.MethodLease = null;
                }
                throw;
            }
            foreach (var item in _reservations)
            {
                // No failing operation remains after the complete exact-token retain pass.
                // Borrowed metadata originals are never disposed by the facts owner.
                item.TableCharge.Dispose();
                item.MethodCharge?.Dispose();
            }
            _readyToCommit = true;
        }
    }
    internal void Commit()
    {
        // Final publication is scalar/root transfer only. Dispose is deliberately separate
        // and must run outside registry/table gates after either success or rollback.
        ValidateCommit();
        CommitValidated();
    }

    internal void ValidateCommit()
    {
        if (!_readyToCommit || _disposed || _committed)
            throw new InvalidOperationException("Facts transfer was not prepared exactly once.");
    }

    // Only the exclusive candidate owner calls this after ValidateCommit, with
    // no intervening Dispose/Commit/callback. Destructive publication has begun;
    // this bounded root transfer must not repeat a fallible preflight predicate.
    internal void CommitValidated()
    {
        _committed = true;
        foreach (var item in _reservations) item.DetachPreparationOwner();
        _reservations.Clear();
        _manifest = null;
    }
    internal void ReleaseTable(ServerDeclaredMethodFactsReservation reservation) => reservation.Release();
    public void Dispose()
    {
        lock (Quota.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_committed)
                foreach (var item in _reservations) ReleaseTable(item);
            _reservations.Clear();
            _manifest = null;
            _listCharge.Dispose();
            _bundle.Dispose();
            if (_ownsPreparation) _preparation.Dispose();
        }
    }
    private void EnsureOpen()
    {
        if (_sealed || _disposed) throw new InvalidOperationException("The facts reservation prepass is closed.");
    }
}

internal sealed class ServerDeclaredMethodFactsReservation(ServerDeclaredMethodFactsGroupReservation group,
    ColdMetadataScope scope, ColdMetadataReservation tableCharge, bool reusesDeclaredDomain,
    ColdMetadataReservation? localMethodToken)
{
    private ServerDeclaredMethodFactsGroupReservation? _group = group;
    internal ServerDeclaredMethodFactsQuota Quota { get; } = group.Quota;
    internal ServerDeclaredMethodFactsGroupReservation Group => _group ??
        throw new InvalidOperationException("Committed facts no longer retain their preparation group.");
    internal ColdMetadataScope Scope => scope;
    internal ColdMetadataReservation TableCharge => tableCharge;
    internal bool ReusesDeclaredDomain => reusesDeclaredDomain;
    internal ColdMetadataReservation? LocalMethodToken => localMethodToken;
    internal ColdMetadataReservation? MethodCharge;
    internal ColdMetadataReservation? SnapshotCharge;
    internal IDisposable? TableLease;
    internal IDisposable? MethodLease;
    internal int Count;
    internal bool CountAssigned;
    internal bool Started;
    internal bool Prepared;
    internal bool Released;
    internal void SetMethodCount(int count) => Group.SetMethodCount(this, count);
    internal void DetachPreparationOwner() => _group = null;
    internal void Release()
    {
        lock (Quota.Gate)
        {
            if (Released) return;
            Released = true;
            SnapshotCharge?.Dispose();
            TableCharge.Dispose();
            MethodCharge?.Dispose();
            TableLease?.Dispose();
            MethodLease?.Dispose();
            SnapshotCharge = null;
            TableLease = null;
            MethodLease = null;
            _group = null;
        }
    }
}

internal sealed class ServerDeclaredMethodFactsException(
    SharpLinkAssemblyRegistrationErrorCode code, string message, long? contractId = null, long? methodId = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    internal SharpLinkAssemblyRegistrationErrorCode Code { get; } = code;
    internal long? ContractId { get; } = contractId;
    internal long? MethodId { get; } = methodId;
}
