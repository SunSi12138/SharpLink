using System.Collections;
using System.Collections.Frozen;
using System.Reflection;

namespace SharpLink.Server;

/// <summary>
/// Owns the server service snapshot and dynamic-module bookkeeping that must move together under
/// one registry synchronization boundary.
/// </summary>
internal sealed class ServerServiceModuleRegistry
{
    private readonly Lock _gate = new();
    private ServerServiceSnapshot? _services;
    private readonly Dictionary<Assembly, SharpLinkDynamicModule> _dynamicModules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)> _unregisterOperations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SharpLinkDynamicModule, ServerDetachedServices> _detachedModuleServices = [];
    private long _generation;

    internal ServerServiceModuleRegistry(ServerServiceSnapshot services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        DynamicModules = new DynamicModuleTable(this);
        UnregisterOperations = new UnregisterOperationTable(this);
        DetachedModuleServices = new DetachedModuleServiceTable(this);
    }

    /// <summary>
    /// Synchronizes correlated module/service publication. Existing server registration transactions
    /// deliberately retain this single boundary while ownership moves out of <see cref="SharpLinkServer"/>.
    /// </summary>
    internal Lock Gate => _gate;

    internal FrozenDictionary<long, ServiceRegistration> ServicesStorage => _services!.Services;
    // All callers hold Gate. Returned old ownership is released outside Gate.
    internal ServerServiceSnapshot.Lease CaptureServices() => _services!.Retain();
    internal bool IsCurrentServices(ServerServiceSnapshot.Lease snapshot) => ReferenceEquals(_services, snapshot.Owner);
    internal ServerServiceSnapshot PublishServices(ServerServiceSnapshot next)
    {
        var previous = _services!;
        _services = next;
        return previous;
    }
    internal ServerServiceSnapshot? DetachServices() => Interlocked.Exchange(ref _services, null);

    internal ref long GenerationStorage => ref _generation;

    // Caller holds Gate. Retiring and pending-outcome identities reserve the existing module
    // capacity until their exact operation entry is removed; shared module/operation keys count once.
    internal int ModuleAdmissionSlots => SharpLinkModuleAdmissionCapacity.Count(_dynamicModules, _unregisterOperations);

    internal DynamicModuleTable DynamicModules { get; }

    internal UnregisterOperationTable UnregisterOperations { get; }

    internal DetachedModuleServiceTable DetachedModuleServices { get; }

    internal sealed class DynamicModuleTable : IEnumerable<KeyValuePair<Assembly, SharpLinkDynamicModule>>
    {
        private readonly ServerServiceModuleRegistry _owner;

        internal DynamicModuleTable(ServerServiceModuleRegistry owner) => _owner = owner;

        internal int Count => _owner._dynamicModules.Count;
        internal void EnsureCapacity(int capacity) => _owner._dynamicModules.EnsureCapacity(capacity);

        internal IEnumerable<Assembly> Keys => _owner._dynamicModules.Keys;

        internal IEnumerable<SharpLinkDynamicModule> Values => _owner._dynamicModules.Values;

        internal bool ContainsKey(Assembly assembly)
            => _owner._dynamicModules.ContainsKey(assembly);

        internal bool TryGetValue(Assembly assembly, out SharpLinkDynamicModule module)
        {
            if (_owner._dynamicModules.TryGetValue(assembly, out var current))
            {
                module = current;
                return true;
            }

            module = null!;
            return false;
        }

        internal void Add(Assembly assembly, SharpLinkDynamicModule module)
            => _owner._dynamicModules.Add(assembly, module);

        internal bool Remove(Assembly assembly)
            => _owner._dynamicModules.Remove(assembly);

        public IEnumerator<KeyValuePair<Assembly, SharpLinkDynamicModule>> GetEnumerator()
            => _owner._dynamicModules.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class UnregisterOperationTable
    {
        private readonly ServerServiceModuleRegistry _owner;

        internal UnregisterOperationTable(ServerServiceModuleRegistry owner) => _owner = owner;

        internal int Count => _owner._unregisterOperations.Count;
        internal void EnsureCapacity(int capacity) => _owner._unregisterOperations.EnsureCapacity(capacity);

        internal IEnumerable<KeyValuePair<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)>> Entries
            => _owner._unregisterOperations;

        internal bool ContainsKey(Assembly assembly)
            => _owner._unregisterOperations.ContainsKey(assembly);

        internal bool TryGetValue(
            Assembly assembly,
            out (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module) operation)
        {
            if (_owner._unregisterOperations.TryGetValue(assembly, out var current))
            {
                operation = current;
                return true;
            }

            operation = default;
            return false;
        }

        internal void Add(Assembly assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module) operation)
            => _owner._unregisterOperations.Add(assembly, operation);

        internal bool Remove(Assembly assembly)
            => _owner._unregisterOperations.Remove(assembly);
    }

    internal sealed class DetachedModuleServiceTable
    {
        private readonly ServerServiceModuleRegistry _owner;

        internal DetachedModuleServiceTable(ServerServiceModuleRegistry owner) => _owner = owner;

        internal int Count => _owner._detachedModuleServices.Count;
        internal void EnsureCapacity(int capacity) => _owner._detachedModuleServices.EnsureCapacity(capacity);

        internal IEnumerable<ServiceRegistration[]> Values => _owner._detachedModuleServices.Values.Select(static item => item.Services);

        internal bool TryGetValue(SharpLinkDynamicModule module, out ServiceRegistration[] services)
        {
            if (_owner._detachedModuleServices.TryGetValue(module, out var owner))
            { services = owner.Services; return true; }
            services = null!;
            return false;
        }

        internal void Add(SharpLinkDynamicModule module, ServerDetachedServices services)
            => _owner._detachedModuleServices.Add(module, services);

        /// <summary>
        /// Transfers detached service ownership exactly once to the module cleanup path.
        /// </summary>
        internal bool Remove(SharpLinkDynamicModule module, out ServerDetachedServices services)
        {
            if (_owner._detachedModuleServices.Remove(module, out var removed))
            {
                services = removed;
                return true;
            }

            services = null!;
            return false;
        }
    }
}

