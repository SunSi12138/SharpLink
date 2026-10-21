using System.Collections.Frozen;

namespace ImmutableRouteProbe;

// Sequential model with explicitly ordered events. This is not the actual server, module lease,
// connection pool, concurrency implementation or collectible-assembly lifetime.
internal static class RouteModelChecks
{
    internal static void Run(List<object> checks, long contractId, long oldRoute, long newRoute)
    {
        var oldRegistration = new Registration("R1", contractId);
        var newRegistration = new Registration("R2", contractId);
        var legacy = new Dictionary<long, Registration> { [contractId] = oldRegistration };
        var delayedLegacyRequest = contractId;
        legacy[contractId] = newRegistration;
        Program.Require(legacy[delayedLegacyRequest].Name == "R2",
            "The legacy same-key retarget counterexample was not reproduced in the model.");
        checks.Add(new { name = "legacy_delayed_request_model", testPassed = true, misdispatch = "R1 request resolves R2" });

        var registry = new RouteRegistry();
        registry.Publish(oldRoute, oldRegistration);
        var delayedRequest = oldRoute; // Client prepared before replacement, not yet sent.
        var queuedRequest = oldRoute; // Server admitted payload later reparses this same token.
        oldRegistration.Retire();
        registry.Publish(newRoute, newRegistration);
        Program.Require(registry.Acquire(delayedRequest) is null, "Delayed old request was accepted after retirement.");
        Program.Require(registry.Acquire(queuedRequest) is null, "Queued old request was accepted after retirement.");
        using (var lease = registry.Acquire(newRoute))
            Program.Require(lease?.Registration.Name == "R2", "New route did not invoke new registration.");
        registry.Remove(oldRoute);
        Program.Require(registry.Acquire(oldRoute) is null, "Removed route was accepted.");
        RequireThrows(() => registry.Publish(oldRoute, newRegistration), "Retired route token was reused.");
        checks.Add(new { name = "immutable_route_delayed_queued_removed_model", testPassed = true });

        var inFlight = new Registration("held", contractId);
        using (var lease = inFlight.Acquire())
        {
            Program.Require(lease is not null, "Initial acquisition failed.");
            inFlight.Retire();
            Program.Require(!inFlight.Disposed, "Held invocation did not protect disposal.");
            Program.Require(inFlight.Acquire() is null, "Retirement reopened admission.");
        }
        Program.Require(inFlight.Disposed, "Retired object did not dispose after its lease ended.");
        checks.Add(new { name = "monotonic_lease_model", testPassed = true, actualAlcUnload = "not_tested" });

        var scope = new Scope("server-1", 7, "tenant-a");
        var pool = new PreparedPool(scope);
        pool.Publish(new Session(scope));
        RequireThrows(() => pool.Publish(new Session(scope with { Server = "server-2" })), "Wrong server admitted.");
        RequireThrows(() => pool.Publish(new Session(scope with { Epoch = 8 })), "Restarted registry admitted.");
        RequireThrows(() => pool.Publish(new Session(scope with { Tenant = "tenant-b" })), "Wrong tenant admitted.");
        Program.Require(pool.ReadyCount == 1, "Rejected session reached Ready publication.");
        checks.Add(new { name = "cold_scope_admission_model", testPassed = true, realReconnectAndRefresh = "not_tested" });

        var exhausted = new RouteAllocator(long.MaxValue);
        Program.Require(exhausted.Next() == long.MaxValue, "Final valid route missing.");
        RequireThrows(() => exhausted.Next(), "Route allocator wrapped and reused identity.");
        checks.Add(new { name = "route_exhaustion_model", testPassed = true });
    }

    private static void RequireThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }

    private sealed class RouteRegistry
    {
        private FrozenDictionary<long, Registration> _routes = FrozenDictionary<long, Registration>.Empty;
        private readonly HashSet<long> _everUsed = [];

        internal void Publish(long route, Registration registration)
        {
            if (!_everUsed.Add(route))
                throw new InvalidOperationException("Route tokens are never reused within this scope.");
            var next = _routes.ToDictionary();
            next.Add(route, registration);
            _routes = next.ToFrozenDictionary();
        }

        internal void Remove(long route)
            => _routes = _routes.Where(pair => pair.Key != route).ToFrozenDictionary();

        internal Lease? Acquire(long route)
            => _routes.TryGetValue(route, out var registration) ? registration.Acquire() : null;
    }

    private sealed class Registration(string name, long logicalContractId)
    {
        private bool _retired;
        private int _leases;
        internal string Name { get; } = name;
        internal long LogicalContractId { get; } = logicalContractId;
        internal bool Disposed { get; private set; }

        internal Lease? Acquire()
        {
            if (_retired)
                return null;
            _leases++;
            return new Lease(this);
        }

        internal void Retire()
        {
            _retired = true;
            Disposed = _leases == 0;
        }

        internal void Release()
        {
            _leases--;
            Disposed = _retired && _leases == 0;
        }
    }

    private sealed class Lease(Registration registration) : IDisposable
    {
        private bool _disposed;
        internal Registration Registration { get; } = registration;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Registration.Release();
        }
    }

    private readonly record struct Scope(string Server, long Epoch, string Tenant);
    private sealed record Session(Scope Scope);
    private sealed class PreparedPool(Scope scope)
    {
        internal int ReadyCount { get; private set; }
        internal void Publish(Session session)
        {
            if (session.Scope != scope)
                throw new InvalidOperationException("Prepared scope mismatch before Ready.");
            ReadyCount++;
        }
    }

    private sealed class RouteAllocator(long next)
    {
        private long _next = next;
        private bool _exhausted;
        internal long Next()
        {
            if (_exhausted)
                throw new InvalidOperationException("Route identity space exhausted.");
            var result = _next;
            if (result == long.MaxValue)
                _exhausted = true;
            else
                _next++;
            return result;
        }
    }
}
