namespace SharpLink.Server;

/// <summary>
/// Cold-only identity allocation for the opt-in immutable-route experiment. All methods are used
/// under the server registry lock. Route IDs are scoped to one server object and never reused.
/// This is not an authorization capability or a production preparation API.
/// </summary>
internal sealed class ServerImmutableRouteRegistry
{
    private Dictionary<ServiceRegistration, long> _registrationRoutes = [];
    private long _nextRouteId = 1;

    internal Guid Scope { get; } = Guid.NewGuid();
    internal bool Enabled { get; private set; }
    internal FrozenDictionary<long, long> ContractRoutes { get; private set; }
        = FrozenDictionary<long, long>.Empty;

    internal PreparedSnapshot Prepare(
        FrozenDictionary<long, ServiceRegistration> services,
        bool? enabledOverride = null)
    {
        var enabled = enabledOverride ?? Enabled;
        if (!enabled)
        {
            return new PreparedSnapshot(
                false, services, [], FrozenDictionary<long, long>.Empty);
        }

        var registrations = new Dictionary<ServiceRegistration, long>(services.Count);
        var contracts = new Dictionary<long, long>(services.Count);
        var dispatch = new Dictionary<long, ServiceRegistration>(services.Count);
        foreach (var pair in services)
        {
            if (!_registrationRoutes.TryGetValue(pair.Value, out var route))
            {
                if (_nextRouteId == 0)
                    throw new InvalidOperationException("Immutable route identity space is exhausted.");
                route = _nextRouteId;
                _nextRouteId = route == long.MaxValue ? 0 : route + 1;
            }
            registrations.Add(pair.Value, route);
            contracts.Add(pair.Key, route);
            dispatch.Add(route, pair.Value);
        }

        // Build every fallible dictionary before the caller closes an old module gate or publishes.
        // A failed preparation may burn IDs; it never makes them reusable.
        return new PreparedSnapshot(
            true, dispatch.ToFrozenDictionary(), registrations, contracts.ToFrozenDictionary());
    }

    internal void Publish(PreparedSnapshot snapshot)
    {
        Enabled = snapshot.Enabled;
        _registrationRoutes = snapshot.RegistrationRoutes;
        ContractRoutes = snapshot.ContractRoutes;
    }

    internal readonly record struct PreparedSnapshot(
        bool Enabled,
        FrozenDictionary<long, ServiceRegistration> DispatchServices,
        Dictionary<ServiceRegistration, long> RegistrationRoutes,
        FrozenDictionary<long, long> ContractRoutes);
}
