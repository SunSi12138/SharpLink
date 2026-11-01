namespace SharpLink.Runtime;

// Scalar identities deliberately carry no Type, manifest, factory, provider or owner root.
internal sealed class RpcCodecOwnerIdentity
{
    private int _closed;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;
    internal bool Close() => Interlocked.Exchange(ref _closed, 1) == 0;
}

internal sealed class RpcCodecBindingIdentity(RpcCodecOwnerIdentity owner)
{
    internal RpcCodecOwnerIdentity Owner { get; } = owner;
}

// A frame is an actual cold use, never a permission inherited from ExecutionContext. Every
// acquisition counts independently. Expired flowed frames have no owner/charge and confer no use.
internal sealed class RpcCodecColdUse : IDisposable
{
    private static readonly AsyncLocal<RpcCodecColdUse?> Current = new();
    private RpcGeneratedManifestRegistration? _owner;
    private ColdMetadataReservation? _charge;
    private RpcCodecColdUse? _previous;
    private readonly ColdMetadataDomain _domain;
    private readonly bool _counted;
    private int _active = 1;
    private RpcCodecColdUse[]? _dependencies;
    private ColdMetadataReservation? _dependencyStorage;

    internal RpcCodecColdUse(RpcGeneratedManifestRegistration? owner, ColdMetadataDomain domain,
        ColdMetadataReservation? charge, bool counted = true)
    { _owner = owner; _domain = domain; _charge = charge; _counted = counted; }

    internal static RpcCodecColdUse EnterPreparation(ColdMetadataPreparation preparation)
    {
        var charge = preparation.ReserveCollection(0);
        try { var use = new RpcCodecColdUse(null, preparation.Domain, charge, counted: false); use.Enter(); return use; }
        catch { charge.Dispose(); throw; }
    }

    internal void AttachDependencies(RpcCodecColdUse[] dependencies, ColdMetadataReservation storage)
    { _dependencies = dependencies; _dependencyStorage = storage; }

    internal void Enter()
    { _previous = Current.Value; Current.Value = this; }

    internal static void ThrowIfHeld(RpcGeneratedManifestRegistration owner)
    {
        for (var frame = Current.Value; frame is not null; frame = frame._previous)
            if (ReferenceEquals(Volatile.Read(ref frame._owner), owner))
                throw new InvalidOperationException("A Codec callback cannot retire an owner held by its current cold operation.");
    }

    internal static void ThrowIfDomainReentry(ColdMetadataDomain domain)
    {
        for (var frame = Current.Value; frame is not null; frame = frame._previous)
            if (Volatile.Read(ref frame._active) != 0 && frame._domain.IsWithin(domain))
                throw new ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "A metadata callback cannot reenter its owning admission or shutdown domain.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0) return;
        var dependencies = _dependencies; _dependencies = null;
        if (dependencies is not null)
        {
            for (var index = dependencies.Length - 1; index >= 0; index--) dependencies[index].Dispose();
            Array.Clear(dependencies);
        }
        Interlocked.Exchange(ref _dependencyStorage, null)?.Dispose();
        var owner = Interlocked.Exchange(ref _owner, null);
        if (ReferenceEquals(Current.Value, this)) Current.Value = _previous;
        // Keep ancestry for a flowed child while an outer live use still exists. Expired frames
        // retain only scalar domain ancestry, not an owner/manifest/provider payload.
        try { Interlocked.Exchange(ref _charge, null)?.Dispose(); }
        finally { if (_counted) owner!.ReleaseColdUse(); }
    }
}

// Standing metadata references are NOT active callback users. Retirement never waits for them.
// The payload is immutable while any reference exists; only the last reference drops its root
// and then refunds the exact preparation lease. Lexical map views must not escape registrations.
internal sealed class RpcCodecMetadataLifetime : IDisposable
{
    private readonly Lock _gate = new();
    private object? _payload;
    private IDisposable? _charge;
    private int _references = 1;
    internal RpcCodecMetadataLifetime(object payload, IDisposable charge)
    { _payload = payload; _charge = charge; }
    internal object Payload => _payload ?? throw new ObjectDisposedException(nameof(RpcCodecMetadataLifetime));
    internal IDisposable Retain()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_references == 0, this);
            var lease = new Lease(this);
            _references = checked(_references + 1);
            return lease;
        }
    }
    public void Dispose()
    {
        IDisposable? charge;
        lock (_gate)
        {
            if (--_references != 0) return;
            _payload = null;
            charge = _charge; _charge = null;
        }
        charge?.Dispose();
    }
    private sealed class Lease(RpcCodecMetadataLifetime lifetime) : IDisposable
    {
        private RpcCodecMetadataLifetime? _lifetime = lifetime;
        public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.Dispose();
    }
}
