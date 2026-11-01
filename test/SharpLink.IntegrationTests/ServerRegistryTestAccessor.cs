using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.IntegrationTests;

internal static class ServerRegistryTestAccessor
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo ConnectionRegistryField =
        RequireField(typeof(SharpLinkServer), "_connectionRegistry");
    private static readonly FieldInfo ActiveConnectionsField =
        RequireField(ConnectionRegistryField.FieldType, "_active");
    private static readonly FieldInfo RetiredConnectionsField =
        RequireField(ConnectionRegistryField.FieldType, "_retired");
    private static readonly FieldInfo ServiceModuleRegistryField =
        RequireField(typeof(SharpLinkServer), "_serviceModuleRegistry");
    private static readonly FieldInfo DynamicModulesField =
        RequireField(ServiceModuleRegistryField.FieldType, "_dynamicModules");
    private static readonly FieldInfo ClientAssemblyRegistryField =
        RequireField(typeof(SharpLinkClient), "_assemblyRegistry");
    internal static int ActiveConnectionCount(SharpLinkServer server)
        => ActiveConnections(server).Count;

    internal static int RetiredConnectionCount(SharpLinkServer server)
        => ReadCount(RetiredConnectionsField.GetValue(ConnectionRegistry(server)), "retired connections");

    internal static ConcurrentDictionary<string, ServerConnectionState> ActiveConnections(SharpLinkServer server)
        => (ConcurrentDictionary<string, ServerConnectionState>)(
            ActiveConnectionsField.GetValue(ConnectionRegistry(server))
            ?? throw new InvalidOperationException("Server active connection registry was null."));

    internal static ServerServiceSnapshot.Lease AcquireServices(SharpLinkServer server)
    {
        var registry = (ServerServiceModuleRegistry)ServiceModuleRegistry(server);
        lock (registry.Gate) return registry.CaptureServices();
    }

    internal static int ServiceCount(SharpLinkServer server)
    {
        using var snapshot = AcquireServices(server);
        return snapshot.Services.Count;
    }

    internal static ServiceOverride OverrideServices(SharpLinkServer server) => new(server);

    // A cold test publication owner, not a raw map setter. Existing bindings deliberately
    // stay live so a selected in-flight continuation cannot be retargeted by map replacement.
    internal sealed class ServiceOverride : IAsyncDisposable
    {
        private readonly SharpLinkServer _server;
        private readonly SharpLinkRuntimeContext _runtime;
        private readonly ServerServiceModuleRegistry _registry;
        private readonly ServerServiceSnapshot.Lease _original;
        private readonly HashSet<ServiceRegistration> _created = [];
        private bool _disposed;

        internal ServiceOverride(SharpLinkServer server)
        {
            _server = server;
            _runtime = (SharpLinkRuntimeContext)(RequireField(typeof(SharpLinkServer), "_runtimeContext").GetValue(server)
                ?? throw new InvalidOperationException("Server runtime was unavailable."));
            _registry = (ServerServiceModuleRegistry)ServiceModuleRegistry(server);
            _original = AcquireServices(server);
        }

        internal FrozenDictionary<long, ServiceRegistration> OriginalServices
        {
            get { ObjectDisposedException.ThrowIf(_disposed, this); return _original.Services; }
        }

        internal void Publish(FrozenDictionary<long, ServiceRegistration> services)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var current = AcquireServices(_server);
            var added = services.Values.Where(static service => service.Binding is null).ToArray();
            if (added.Any(static service => service.HasModule || service.DeclaredFacts is not null))
                throw new InvalidOperationException("Overrides accept only fresh static fixture registrations.");
            _created.EnsureCapacity(checked(_created.Count + added.Length));
            using var storage = _runtime.CreateMetadataStorage();
            // A distinct incoming registration scope in the actual Runtime authority. The
            // descriptors themselves are the exact fully prepared static manifest declarations.
            using var group = new ServerDeclaredMethodFactsQuota(_runtime.Domain).BeginGroup();
            var reservations = added.Select(service =>
            {
                var manifest = _runtime.GeneratedManifests.Single(manifest =>
                    ReferenceEquals(manifest.OwnerAssembly, service.ContractType.Assembly));
                if (manifest is not PreparedGeneratedManifest)
                    throw new InvalidOperationException("Fixture requires an actually prepared manifest.");
                var descriptor = manifest.Contracts.Single(contract => contract.ContractType == service.ContractType);
                if (descriptor.ContractId != service.Stub.InterfaceHash)
                    throw new InvalidOperationException("Fixture stub must match its registered contract identity.");
                var reservation = group.ReserveContract(descriptor.ContractId);
                reservation.SetMethodCount(descriptor.Methods.Count);
                return (Service: service, Descriptor: descriptor, Reservation: reservation);
            }).ToArray();
            group.Seal();
            ServerServiceSnapshot? candidate = null;
            ServerServiceSnapshot? retired = null;
            var published = false;
            try
            {
                foreach (var item in reservations)
                    item.Service.DeclaredFacts = ServerDeclaredMethodFacts.Prepare(item.Descriptor, item.Service.Stub, item.Reservation);
                group.PrepareCommit();
                var plans = Invoke("CreateBindingRegistrationPlans", services, Array.Empty<SharpLinkDynamicModule>(), null)!;
                candidate = new ServerServiceSnapshot(services, storage, storage.ReserveCollection(services.Count));
                lock (_registry.Gate)
                {
                    if (!_registry.IsCurrentServices(current))
                        throw new InvalidOperationException("Fixture publication raced another registration.");
                    var generation = (long)Invoke("NextBindingPublicationGeneration")!;
                    Invoke("PublishBindingRegistrations", plans, generation);
                    group.Commit();
                    retired = _registry.PublishServices(candidate);
                    _registry.GenerationStorage = generation;
                    published = true;
                }
                foreach (var service in added) _created.Add(service);
            }
            finally
            {
                retired?.Dispose();
                if (!published)
                {
                    candidate?.Dispose();
                    foreach (var service in added)
                    {
                        service.Binding?.Retire();
                        service.Binding = null;
                        service.DeclaredFacts?.Dispose();
                        service.DeclaredFacts = null;
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            List<Exception>? failures = null;
            try { Publish(_original.Services); }
            catch (Exception error) { (failures ??= []).Add(error); }
            _disposed = true;
            foreach (var service in _created)
            {
                try { service.Binding?.Retire(); await service.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            _created.Clear();
            try { _original.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
            if (failures is not null) throw new AggregateException(failures);
        }

        private object? Invoke(string name, params object?[] arguments)
        {
            try
            {
                return (typeof(SharpLinkServer).GetMethod(name, InstanceFlags)
                    ?? throw new MissingMethodException(typeof(SharpLinkServer).FullName, name)).Invoke(_server, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    internal static IDictionary DynamicModules(object endpoint)
    {
        if (endpoint is SharpLinkServer server)
        {
            return DynamicModulesField.GetValue(ServiceModuleRegistry(server)) as IDictionary
                   ?? throw new InvalidOperationException("Server dynamic module registry was unavailable.");
        }
        if (endpoint is SharpLinkClient client)
        {
            var registry = ClientAssemblyRegistryField.GetValue(client) as ClientAssemblyRegistry
                ?? throw new InvalidOperationException("Client assembly registry was unavailable.");
            return registry.DynamicModules as IDictionary
                   ?? throw new InvalidOperationException("Client dynamic module registry was unavailable.");
        }

        var field = endpoint.GetType().GetField("_dynamicModules", InstanceFlags)
            ?? throw new MissingFieldException(endpoint.GetType().FullName, "_dynamicModules");
        return field.GetValue(endpoint) as IDictionary
               ?? throw new InvalidOperationException("Dynamic module registry was unavailable.");
    }

    internal static int DynamicModuleCount(object endpoint)
        => DynamicModules(endpoint).Count;

    internal static SharpLinkDynamicModule DynamicModule(object endpoint, Assembly assembly)
        => DynamicModules(endpoint)[assembly] as SharpLinkDynamicModule
           ?? throw new InvalidOperationException(
               $"Dynamic module was not found for '{assembly.FullName}'.");

    private static object ConnectionRegistry(SharpLinkServer server)
        => ConnectionRegistryField.GetValue(server)
           ?? throw new InvalidOperationException("Server connection registry was unavailable.");

    private static object ServiceModuleRegistry(SharpLinkServer server)
        => ServiceModuleRegistryField.GetValue(server)
           ?? throw new InvalidOperationException("Server service/module registry was unavailable.");

    private static FieldInfo RequireField(Type type, string name)
        => type.GetField(name, InstanceFlags)
           ?? throw new MissingFieldException(type.FullName, name);

    private static int ReadCount(object? value, string name)
    {
        if (value is null)
            throw new InvalidOperationException($"{name} registry was null.");
        return (int)(value.GetType().GetProperty("Count", InstanceFlags)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, "Count"));
    }
}
