namespace SharpLink.Server;

// Cold snapshot references only. Request dispatch owns exact binding scalar facts and does
// not read the Server service map; no RPC entry/exit counter is introduced here.
internal sealed class ServerServiceSnapshot : IDisposable
{
    private FrozenDictionary<long, ServiceRegistration>? _services;
    private readonly ColdMetadataReservation _charge;
    private readonly IDisposable _bundle;
    private int _references = 1;
    private int _disposed;
    internal ServerServiceSnapshot(FrozenDictionary<long, ServiceRegistration> services,
        ColdMetadataPreparation preparation, ColdMetadataReservation charge)
    {
        _services = services;
        _charge = charge;
        _bundle = preparation.Retain();
    }
    internal FrozenDictionary<long, ServiceRegistration> Services => Volatile.Read(ref _services)
        ?? throw new ObjectDisposedException(nameof(ServerServiceSnapshot));
    internal Lease Retain()
    {
        var current = Volatile.Read(ref _references);
        while (current != 0)
        {
            var observed = Interlocked.CompareExchange(ref _references, checked(current + 1), current);
            if (observed == current) return new Lease(this);
            current = observed;
        }
        throw new ObjectDisposedException(nameof(ServerServiceSnapshot));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Release();
    }
    private void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        _services = null;
        _charge.Dispose();
        _bundle.Dispose();
    }
    internal sealed class Lease(ServerServiceSnapshot owner) : IDisposable
    {
        private ServerServiceSnapshot? _owner = owner;
        internal ServerServiceSnapshot Owner => _owner ?? throw new ObjectDisposedException(nameof(Lease));
        internal FrozenDictionary<long, ServiceRegistration> Services => Owner.Services;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

internal sealed class ServerDetachedServices : IDisposable
{
    private ServiceRegistration[]? _services;
    private readonly ColdMetadataReservation _charge;
    private readonly IDisposable _bundle;
    internal ServerDetachedServices(ServiceRegistration[] services,
        ColdMetadataPreparation preparation, ColdMetadataReservation charge)
    {
        _services = services;
        _charge = charge;
        _bundle = preparation.Retain();
    }
    internal ServiceRegistration[] Services => _services ?? throw new ObjectDisposedException(nameof(ServerDetachedServices));
    public void Dispose()
    {
        var services = Interlocked.Exchange(ref _services, null);
        if (services is null) return;
        Array.Clear(services);
        _charge.Dispose();
        _bundle.Dispose();
    }
}
