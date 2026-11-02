namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private const int MaximumRegistrationPreparations = 16;
    private static readonly AsyncLocal<RegistrationPreparation?> CurrentRegistrationPreparation = new();
    private int _registrationPreparations;
    private bool _registrationPreparationSealed;
    private TaskCompletionSource? _registrationPreparationsDrained;
    private readonly ServerDeclaredMethodFactsQuota _declaredFactsQuota;

    private void ThrowIfRegistrationCallbackReentry()
    {
        RetirementCallbackScope.ThrowIfReentry(_runtimeContext.Domain);
        ColdMetadataPreparation.ThrowIfCallbackReentry(_runtimeContext.Domain);
        for (var current = CurrentRegistrationPreparation.Value; current is not null; current = current.Previous)
            if (current.IsActive && ReferenceEquals(current.Server, this))
                throw new InvalidOperationException("A registration preparation callback cannot mutate or stop its own server.");
    }

    private bool TryBeginRegistrationPreparation(out RegistrationPreparation? owner,
        out SharpLinkAssemblyRegistrationError? error)
    {
        ThrowIfRegistrationCallbackReentry();
        lock (_registryGate)
        {
            if (_registrationPreparationSealed || CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                owner = null;
                error = new(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "The server no longer accepts registration preparation.");
                return false;
            }
            if (_registrationPreparations == MaximumRegistrationPreparations)
            {
                owner = null;
                error = new(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                    "The server registration preparation capacity is full.");
                return false;
            }
            if (_registrationPreparations == 0)
                _registrationPreparationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _registrationPreparations++;
        }
        try
        {
            // Slot is already admitted, but neither quota operations nor constructors run
            // under the publication gate. Stop joins this slot through its original fence.
            owner = new RegistrationPreparation(this, CurrentRegistrationPreparation.Value);
        }
        catch (Exception exception)
        {
            lock (_registryGate)
                if (--_registrationPreparations == 0)
                    _registrationPreparationsDrained!.TrySetResult();
            if (exception is ColdMetadataException metadata)
            {
                owner = null;
                error = new(metadata.Code, metadata.Message);
                return false;
            }
            throw;
        }
        CurrentRegistrationPreparation.Value = owner;
        error = null;
        return true;
    }

    // Called only under the existing registry publication gate at the Stop cut.
    private void SealRegistrationPreparationsLocked() => _registrationPreparationSealed = true;

    private Task CaptureRegistrationPreparationsDrain()
    {
        lock (_registryGate)
            return _registrationPreparationsDrained?.Task ?? Task.CompletedTask;
    }

    private sealed class RegistrationPreparation : IDisposable
    {
        internal SharpLinkServer Server { get; }
        internal RegistrationPreparation? Previous { get; }
        private readonly List<(SharpLinkDynamicModule Module, RpcGeneratedManifestRegistration Registration)> _dependencies;
        private readonly ColdMetadataReservation _dependenciesCharge;
        private HashSet<System.Reflection.Assembly>? _selectedDependencies;
        private ColdMetadataReservation? _selectedDependenciesCharge;
        private int _disposed;
        internal bool IsActive => Volatile.Read(ref _disposed) == 0;
        internal ColdMetadataPreparation Metadata { get; }
        private readonly ServerMetadataWorkspace _snapshotWorkspace;
        private ColdMetadataReservation? _moduleSnapshotCharge;
        private ColdMetadataReservation? _serviceSnapshotCharge;
        private ColdMetadataReservation? _detachedServicesCharge;
        private ColdMetadataReservation? _retainedServiceMapCharge;
        private SnapshotRoot? _snapshotRoots;
        private ServerServiceSnapshot.Lease? _serviceSnapshot;
        private int _moduleSnapshotCapacity;
        private int _serviceSnapshotCapacity;

        internal RegistrationPreparation(SharpLinkServer server, RegistrationPreparation? previous)
        {
            Server = server;
            Previous = previous;
            // Header admission precedes loader construction and every metadata getter.
            Metadata = server._runtimeContext.Domain.BeginPreparation();
            try
            {
                _dependenciesCharge = Metadata.ReserveCollection(0);
                _dependencies = [];
                _snapshotWorkspace = new(Metadata);
            }
            catch { Metadata.Dispose(); throw; }
        }

        internal void ReserveRegistrySnapshots(bool replacement = false)
        {
            int modules, services;
            lock (Server._registryGate)
            {
                modules = Server._dynamicModules.Count;
                services = Server._services.Count;
            }
            _moduleSnapshotCharge ??= _snapshotWorkspace.Reserve(0);
            _serviceSnapshotCharge ??= _snapshotWorkspace.Reserve(0);
            _moduleSnapshotCharge.GrowTo(modules);
            _serviceSnapshotCharge.GrowTo(services);
            if (replacement)
            {
                _detachedServicesCharge ??= Metadata.ReserveCollection(0);
                _retainedServiceMapCharge ??= _snapshotWorkspace.Reserve(0);
                _detachedServicesCharge.GrowTo(services);
                _retainedServiceMapCharge.GrowTo(services);
            }
            _moduleSnapshotCapacity = Math.Max(_moduleSnapshotCapacity, modules);
            _serviceSnapshotCapacity = Math.Max(_serviceSnapshotCapacity, services);
        }
        internal ServerDetachedServices TransferDetachedSnapshot(ServiceRegistration[] services)
        {
            var charge = _detachedServicesCharge ?? throw new InvalidOperationException("Detached array must be reserved first.");
            var owner = new ServerDetachedServices(services, Metadata, charge);
            _detachedServicesCharge = null;
            return owner;
        }

        internal FrozenDictionary<long, ServiceRegistration> CaptureServicesLocked()
        {
            if (_serviceSnapshot is not null) throw new InvalidOperationException("Service snapshot capture cannot replay.");
            _serviceSnapshot = Server._serviceModuleRegistry.CaptureServices();
            return _serviceSnapshot.Services;
        }

        internal T[] TrackSnapshot<T>(T[] snapshot)
        {
            _snapshotRoots = new SnapshotRoot(snapshot, _snapshotRoots);
            return snapshot;
        }
        private sealed record SnapshotRoot(Array Value, SnapshotRoot? Next);

        internal bool SnapshotCapacityIsCurrentLocked()
            => Server._dynamicModules.Count <= _moduleSnapshotCapacity &&
               Server._services.Count <= _serviceSnapshotCapacity;

        // Frozen metadata traversal/reflection remains outside the registry gate. Only
        // exact required Assembly identities are pinned, not every unrelated live module.
        internal void SelectDependencies(PreparedGeneratedManifest manifest)
        {
            using var callback = Metadata.EnterCallback();
            if (_selectedDependencies is not null)
                throw new InvalidOperationException("Dependency selection cannot replay.");
            _selectedDependenciesCharge = Metadata.ReserveCollection(0, manifest.RegistrationScope);
            _selectedDependencies = new(ReferenceEqualityComparer.Instance);
            void Add(System.Reflection.Assembly? assembly)
            {
                if (assembly is null || ReferenceEquals(assembly, manifest.OwnerAssembly) ||
                    _selectedDependencies.Contains(assembly)) return;
                _selectedDependenciesCharge.GrowTo(checked(_selectedDependencies.Count + 1));
                _selectedDependencies.Add(assembly);
            }
            foreach (var identity in EnumerateManifestDependencies(manifest))
                Add(SharpLinkGeneratedDependencyBinding.ResolveDeclared(manifest, identity));
            foreach (var service in manifest.Services) Add(service.ContractType.Assembly);
            if (manifest is ISharpLinkReferencedCodecDependencyManifest referenced)
                foreach (var dependency in referenced.ReferencedCodecDependencies) Add(dependency.TargetType.Assembly);
            _dependenciesCharge.GrowTo(_selectedDependencies.Count);
            _dependencies.EnsureCapacity(_selectedDependencies.Count);
        }

        // The caller holds Server._registryGate. No generated/user callbacks occur here.
        internal bool TryPinDependencies(IEnumerable<SharpLinkDynamicModule> modules)
        {
            if (_dependencies.Count != 0)
                throw new InvalidOperationException("A preparation cannot silently replay dependency capture.");
            foreach (var module in modules)
            {
                if (_selectedDependencies is null || !_selectedDependencies.Contains(module.Assembly))
                    continue;
                if (!module.TryAcquirePreparation())
                    return false;
                try { _dependencies.Add((module, module.CodecRegistration)); }
                catch { module.ReleasePreparation(); throw; }
            }
            return true;
        }

        internal bool DependenciesAreRunning()
        {
            if (_serviceSnapshot is not null && !Server._serviceModuleRegistry.IsCurrentServices(_serviceSnapshot))
                return false;
            foreach (var dependency in _dependencies)
            {
                var module = dependency.Module;
                if (module.State != SharpLinkDynamicModuleState.Running ||
                    !Server._dynamicModules.TryGetValue(module.Assembly, out var current) ||
                    !ReferenceEquals(current, module) ||
                    !ReferenceEquals(current.CodecRegistration, dependency.Registration)) return false;
            }
            return true;
        }

        // Caller scope restoration is scalar and synchronous. Resource ownership
        // can then transfer to the suspended physical worker without leaving this
        // caller's AsyncLocal pointing at an asynchronously disposed preparation.
        internal void LeaveCallerScope()
        {
            if (ReferenceEquals(CurrentRegistrationPreparation.Value, this))
                CurrentRegistrationPreparation.Value = Previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            LeaveCallerScope();
            List<Exception>? failures = null;
            try
            {
                foreach (var dependency in _dependencies)
                {
                    try { dependency.Module.ReleasePreparation(); }
                    catch (Exception exception) { (failures ??= []).Add(exception); }
                }
                _dependencies.Clear();
                _selectedDependencies?.Clear();
                _selectedDependencies = null;
                _selectedDependenciesCharge?.Dispose();
                _dependenciesCharge.Dispose();
                var root = _snapshotRoots;
                _snapshotRoots = null;
                while (root is not null) { Array.Clear(root.Value); root = root.Next; }
                _snapshotWorkspace.Dispose();
                _detachedServicesCharge?.Dispose();
                _serviceSnapshot?.Dispose();
                _serviceSnapshot = null;
                try { Metadata.Dispose(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            finally
            {
                TaskCompletionSource? completed = null;
                lock (Server._registryGate)
                {
                    if (--Server._registrationPreparations < 0)
                        throw new InvalidOperationException("Registration preparation owner underflowed.");
                    if (Server._registrationPreparations == 0)
                        completed = Server._registrationPreparationsDrained;
                }
                completed?.TrySetResult();
            }
            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is not null)
                throw new AggregateException(failures);
        }
    }
}
