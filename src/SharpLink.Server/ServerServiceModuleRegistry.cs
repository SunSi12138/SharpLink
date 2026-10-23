using System.Collections;
using System.Collections.Frozen;
using System.Reflection;

namespace SharpLink.Server;

/// <summary>
/// Owns the server service snapshot and dynamic-module bookkeeping that must move together under
/// one registry synchronization boundary.
/// </summary>
internal sealed partial class ServerServiceModuleRegistry
{
    private readonly Lock _gate = new();
    private FrozenDictionary<long, ServiceRegistration> _services;
    private Dictionary<Assembly, SharpLinkDynamicModule> _dynamicModules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Assembly, Task<SharpLinkAssemblyUnregisterResult>> _unregisterOperations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SharpLinkDynamicModule, ServiceRegistration[]> _detachedModuleServices = [];
    private long _generation;
    private object? _coldWriter;
    private static readonly object FailedClosedWriter = new();

    // Cold paths access this only while holding Gate. Ordinary dispatch never participates.
    internal bool IsColdWriterHeld => _coldWriter is not null;
    internal object? ColdWriter => _coldWriter;

    internal void AcquireColdWriter(object writer)
    {
        if (_coldWriter is not null || _preparationWriters != 0 || _retirementFailed || _unregisterOperations.Count != 0 ||
            _dynamicModules.Values.Any(static module => module.State != SharpLinkDynamicModuleState.Running))
            throw new InvalidOperationException("The server catalog has an active writer or retiring module.");
        _coldWriter = writer;
    }

    internal void ValidateColdWriter(object writer)
    {
        if (!ReferenceEquals(_coldWriter, writer))
            throw new InvalidOperationException("The prepared server catalog no longer owns its cold writer.");
    }

    internal void ReleaseColdWriter(object writer)
    {
        ValidateColdWriter(writer);
        _coldWriter = null;
    }

    internal void RetainFailedClosedWriter(object writer)
    {
        ValidateColdWriter(writer);
        _coldWriter = FailedClosedWriter;
    }

    internal Dictionary<Assembly, SharpLinkDynamicModule> CaptureModules()
        => new(_dynamicModules, ReferenceEqualityComparer.Instance);

    internal void CommitPreparedModules(Dictionary<Assembly, SharpLinkDynamicModule> modules)
        => _dynamicModules = modules;


    internal ServerServiceModuleRegistry(FrozenDictionary<long, ServiceRegistration> services)
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

    internal ref FrozenDictionary<long, ServiceRegistration> ServicesStorage => ref _services;

    internal ref long GenerationStorage => ref _generation;

    internal DynamicModuleTable DynamicModules { get; }

    internal UnregisterOperationTable UnregisterOperations { get; }

    internal DetachedModuleServiceTable DetachedModuleServices { get; }

    internal sealed class DynamicModuleTable : IEnumerable<KeyValuePair<Assembly, SharpLinkDynamicModule>>
    {
        private readonly ServerServiceModuleRegistry _owner;

        internal DynamicModuleTable(ServerServiceModuleRegistry owner) => _owner = owner;

        internal int Count => _owner._dynamicModules.Count;

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

        internal bool ContainsKey(Assembly assembly)
            => _owner._unregisterOperations.ContainsKey(assembly);

        internal bool TryGetValue(
            Assembly assembly,
            out Task<SharpLinkAssemblyUnregisterResult> operation)
        {
            if (_owner._unregisterOperations.TryGetValue(assembly, out var current))
            {
                operation = current;
                return true;
            }

            operation = null!;
            return false;
        }

        internal void Add(Assembly assembly, Task<SharpLinkAssemblyUnregisterResult> operation)
            => _owner._unregisterOperations.Add(assembly, operation);

        internal bool Remove(Assembly assembly)
            => _owner._unregisterOperations.Remove(assembly);
    }

    internal sealed class DetachedModuleServiceTable
    {
        private readonly ServerServiceModuleRegistry _owner;

        internal DetachedModuleServiceTable(ServerServiceModuleRegistry owner) => _owner = owner;

        internal int Count => _owner._detachedModuleServices.Count;

        internal void Add(SharpLinkDynamicModule module, ServiceRegistration[] services)
            => _owner._detachedModuleServices.Add(module, services);

        /// <summary>
        /// Transfers detached service ownership exactly once to the module cleanup path.
        /// </summary>
        internal bool Remove(SharpLinkDynamicModule module, out ServiceRegistration[] services)
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

