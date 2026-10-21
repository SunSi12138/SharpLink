using System.Collections.Frozen;
using System.Linq;
using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

// Actual cold registry tests, not transport, concurrency or collectible-ALC tests.
public class ImmutableRouteRegistryTests
{
    [Test]
    public void DefaultDispatchShouldReuseTheLogicalSnapshot()
    {
        var registration = CreateRegistration();
        var services = Services((101, registration));
        var owner = new ServerServiceModuleRegistry(services);
        lock (owner.Gate)
        {
            var candidate = owner.ImmutableRoutes.Prepare(services);
            owner.PublishServices(services, candidate);
            Ensure(!owner.ImmutableRoutes.Enabled, "legacy registry must not opt in implicitly");
            Ensure(ReferenceEquals(owner.ServicesStorage, services), "logical snapshot identity");
            Ensure(ReferenceEquals(owner.DispatchServicesStorage, services), "legacy dispatch must reuse the same snapshot");
            Ensure(owner.ImmutableRoutes.ContractRoutes.Count == 0, "legacy mode must not advertise route identities");
        }
    }

    [Test]
    public void EmptyOptInShouldRetainScopeAndApplyToTheFirstRegistration()
    {
        var empty = FrozenDictionary<long, ServiceRegistration>.Empty;
        var owner = new ServerServiceModuleRegistry(empty);
        var scope = owner.ImmutableRoutes.Scope;
        Ensure(scope != Guid.Empty, "route scope must be nonempty before contracts exist");
        Ensure(scope != new ServerImmutableRouteRegistry().Scope, "separate server registries must have separate scopes");
        lock (owner.Gate)
        {
            owner.PublishServices(empty, owner.ImmutableRoutes.Prepare(empty, enabledOverride: true));
            Ensure(owner.ImmutableRoutes.Enabled, "empty opt-in must not collapse to legacy mode");
            Ensure(owner.DispatchServicesStorage.Count == 0, "empty route dispatch");
            Ensure(owner.ImmutableRoutes.ContractRoutes.Count == 0, "empty route manifest");
            var registration = CreateRegistration();
            var services = Services((101, registration));
            owner.PublishServices(services, owner.ImmutableRoutes.Prepare(services));
            var route = owner.ImmutableRoutes.ContractRoutes[101];
            Ensure(owner.ImmutableRoutes.Scope == scope, "first registration must retain prepared scope");
            Ensure(route > 0 && route != 101, "first dispatch key must be an allocated route, not the logical contract");
            Ensure(ReferenceEquals(owner.DispatchServicesStorage[route], registration), "allocated route registration identity");
        }
    }

    [Test]
    public void RetainedRegistrationShouldKeepItsRouteButReplacementShouldNot()
    {
        var first = CreateRegistration();
        var retained = CreateRegistration();
        var replacement = CreateRegistration();
        var owner = new ServerServiceModuleRegistry(FrozenDictionary<long, ServiceRegistration>.Empty);
        lock (owner.Gate)
        {
            var initial = Services((101, first), (202, retained));
            owner.PublishServices(initial, owner.ImmutableRoutes.Prepare(initial, enabledOverride: true));
            var firstRoute = owner.ImmutableRoutes.ContractRoutes[101];
            var retainedRoute = owner.ImmutableRoutes.ContractRoutes[202];
            var oldDispatch = owner.DispatchServicesStorage;

            var next = Services((101, replacement), (202, retained));
            owner.PublishServices(next, owner.ImmutableRoutes.Prepare(next));
            var replacementRoute = owner.ImmutableRoutes.ContractRoutes[101];
            Ensure(replacementRoute != firstRoute && replacementRoute != retainedRoute, "replacement needs a fresh route");
            Ensure(owner.ImmutableRoutes.ContractRoutes[202] == retainedRoute, "unaffected registration route must remain stable");
            Ensure(!owner.DispatchServicesStorage.ContainsKey(firstRoute), "removed old route must not target the replacement");
            Ensure(ReferenceEquals(owner.DispatchServicesStorage[replacementRoute], replacement), "new route must target exact replacement");
            Ensure(ReferenceEquals(oldDispatch[firstRoute], first), "a reader holding the old snapshot must still see the original registration");
        }
    }

    [Test]
    public void RemovalShouldDropCurrentRegistryRootsAndNeverReuseAnOldRoute()
    {
        var registration = CreateRegistration();
        var owner = new ServerServiceModuleRegistry(FrozenDictionary<long, ServiceRegistration>.Empty);
        lock (owner.Gate)
        {
            var services = Services((101, registration));
            owner.PublishServices(services, owner.ImmutableRoutes.Prepare(services, enabledOverride: true));
            var oldRoute = owner.ImmutableRoutes.ContractRoutes[101];
            var empty = FrozenDictionary<long, ServiceRegistration>.Empty;
            owner.PublishServices(empty, owner.ImmutableRoutes.Prepare(empty));
            Ensure(owner.ServicesStorage.Count == 0 && owner.DispatchServicesStorage.Count == 0,
                "removal must drop both current service snapshots");
            Ensure(owner.ImmutableRoutes.ContractRoutes.Count == 0 && RegistrationRoots(owner.ImmutableRoutes).Count == 0,
                "cold logical and reverse maps must not permanently root a removed registration");

            // Even re-registering the very same object after removal cannot revive an old token.
            owner.PublishServices(services, owner.ImmutableRoutes.Prepare(services));
            Ensure(owner.ImmutableRoutes.ContractRoutes[101] != oldRoute, "removed route identity must never be recycled");
            Ensure(!owner.DispatchServicesStorage.ContainsKey(oldRoute), "old route must remain absent after re-registration");
        }
    }

    [Test]
    public void ExhaustedPreparationShouldLeavePublishedMapsUnchangedAndBurnTheLastId()
    {
        var stable = CreateRegistration();
        var owner = new ServerServiceModuleRegistry(FrozenDictionary<long, ServiceRegistration>.Empty);
        lock (owner.Gate)
        {
            var initial = Services((101, stable));
            owner.PublishServices(initial, owner.ImmutableRoutes.Prepare(initial, enabledOverride: true));
            var routes = owner.ImmutableRoutes.ContractRoutes;
            var dispatch = owner.DispatchServicesStorage;
            var roots = RegistrationRoots(owner.ImmutableRoutes);
            NextRouteIdField().SetValue(owner.ImmutableRoutes, long.MaxValue);
            var tooMany = Services((101, stable), (202, CreateRegistration()), (303, CreateRegistration()));
            ExpectThrows<InvalidOperationException>(() => owner.ImmutableRoutes.Prepare(tooMany));
            Ensure(ReferenceEquals(owner.ImmutableRoutes.ContractRoutes, routes), "failed prepare must not publish a partial manifest");
            Ensure(ReferenceEquals(owner.DispatchServicesStorage, dispatch), "failed prepare must not publish partial dispatch");
            Ensure(ReferenceEquals(RegistrationRoots(owner.ImmutableRoutes), roots) && roots.Count == 1,
                "failed prepare must not retain candidate registrations in the published reverse map");
            Ensure((long)NextRouteIdField().GetValue(owner.ImmutableRoutes)! == 0,
                "the final allocated ID must be burned rather than reused after failure");
            ExpectThrows<InvalidOperationException>(() => owner.ImmutableRoutes.Prepare(Services((202, CreateRegistration()))));

            // Exhaustion blocks new identities, not safe publication/removal of existing ones.
            owner.PublishServices(initial, owner.ImmutableRoutes.Prepare(initial));
            Ensure(owner.ImmutableRoutes.ContractRoutes[101] == routes[101], "existing identity must survive allocator exhaustion");
            var empty = FrozenDictionary<long, ServiceRegistration>.Empty;
            owner.PublishServices(empty, owner.ImmutableRoutes.Prepare(empty));
            Ensure(owner.DispatchServicesStorage.Count == 0, "exhaustion must not prevent cleanup");
        }
    }

    [Test]
    public void InvalidCandidateShouldNotPublishOrRecycleAllocatedIds()
    {
        var owner = new ServerServiceModuleRegistry(FrozenDictionary<long, ServiceRegistration>.Empty);
        lock (owner.Gate)
        {
            var empty = FrozenDictionary<long, ServiceRegistration>.Empty;
            owner.PublishServices(empty, owner.ImmutableRoutes.Prepare(empty, enabledOverride: true));
            var routes = owner.ImmutableRoutes.ContractRoutes;
            var duplicate = CreateRegistration();
            // This is deliberately malformed cold input: one exact registration under two contracts.
            ExpectThrows<ArgumentException>(() => owner.ImmutableRoutes.Prepare(Services((101, duplicate), (202, duplicate))));
            var nextAfterFailure = (long)NextRouteIdField().GetValue(owner.ImmutableRoutes)!;
            Ensure(nextAfterFailure > 1, "fallible preparation must not roll route allocation back");
            Ensure(ReferenceEquals(owner.ImmutableRoutes.ContractRoutes, routes) && owner.DispatchServicesStorage.Count == 0,
                "invalid candidate must not mutate published service/route state");
            Ensure(RegistrationRoots(owner.ImmutableRoutes).Count == 0, "invalid candidate must not leak into current reverse roots");
            var services = Services((303, CreateRegistration()));
            owner.PublishServices(services, owner.ImmutableRoutes.Prepare(services));
            Ensure(owner.ImmutableRoutes.ContractRoutes[303] == nextAfterFailure,
                "the next successful preparation must skip every burned route identity");
        }
    }

    private static FieldInfo NextRouteIdField()
        => typeof(ServerImmutableRouteRegistry).GetField("_nextRouteId", BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new Exception("Missing route allocator field.");

    private static Dictionary<ServiceRegistration, long> RegistrationRoots(ServerImmutableRouteRegistry registry)
        => (Dictionary<ServiceRegistration, long>)(typeof(ServerImmutableRouteRegistry)
               .GetField("_registrationRoutes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(registry)
           ?? throw new Exception("Missing route registry reverse map."));

    private static FrozenDictionary<long, ServiceRegistration> Services(
        params (long ContractId, ServiceRegistration Registration)[] entries)
        => entries.ToFrozenDictionary(static pair => pair.ContractId, static pair => pair.Registration);

    private static ServiceRegistration CreateRegistration()
        => ServiceRegistration.CreateSingleton(typeof(object), new StubMarker(), new object(), ownsService: false);

    private static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class StubMarker : IRpcStub
    {
        public long InterfaceHash => 101;
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
