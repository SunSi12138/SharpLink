using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    /// <summary>
    /// Acquires the exclusive cold writer and validates metadata only. No admission/quiescence
    /// claim is made here, and no candidate service or Codec factory is invoked before the proof.
    /// </summary>
    internal PreparedImplementationReplacement PrepareImplementationReplacement(
        IReadOnlyList<(Assembly OldAssembly, Assembly NewAssembly)> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0 || replacements.Count > MaximumDynamicModules)
            throw new ArgumentException("An explicit nonempty bounded replacement set is required.", nameof(replacements));
        var pairs = replacements.ToArray();
        var incoming = new ISharpLinkGeneratedAssemblyManifest[pairs.Length];
        for (var index = 0; index < pairs.Length; index++)
        {
            var result = SharpLinkAssemblyManifestLoader.TryLoad(pairs[index].NewAssembly, out var manifest);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Error!.Message);
            incoming[index] = manifest!;
        }
        PreparedImplementationReplacement plan;
        lock (_registryGate)
        {
            if (CurrentState != ServerState.Running)
                throw new InvalidOperationException("Implementation maintenance requires a running server.");
            var oldModules = new SharpLinkDynamicModule[pairs.Length];
            var oldAssemblies = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
            var newAssemblies = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < pairs.Length; index++)
            {
                var pair = pairs[index];
                if (!oldAssemblies.Add(pair.OldAssembly) || !newAssemblies.Add(pair.NewAssembly) ||
                    !_dynamicModules.TryGetValue(pair.OldAssembly, out var module) ||
                    module.State != SharpLinkDynamicModuleState.Running || IsAssemblyRegistered(pair.NewAssembly))
                    throw new InvalidOperationException("Replacement identities must be distinct, exact running modules and unregistered candidates.");
                oldModules[index] = module;
            }
            plan = new PreparedImplementationReplacement(this, oldModules, incoming,
                _serviceModuleRegistry.CaptureModules(), Volatile.Read(ref _services), _registryGeneration);
            _serviceModuleRegistry.AcquireColdWriter(plan);
        }
        try
        {
            plan.ValidateMetadata();
            return plan;
        }
        catch
        {
            plan.RejectBeforeSeal();
            throw;
        }
    }

    /// <summary>
    /// A local catalog transaction, not a drain certificate. Only the framework coordinator can
    /// supply the proof needed to prepare, retire, release and publish while the domain is paused.
    /// </summary>
    internal sealed class PreparedImplementationReplacement : IAsyncDisposable
    {
        private SharpLinkServer? _server;
        private SharpLinkDynamicModule[]? _oldModules;
        private ISharpLinkGeneratedAssemblyManifest[]? _incoming;
        private Dictionary<Assembly, SharpLinkDynamicModule>? _baseModules;
        private FrozenDictionary<long, ServiceRegistration>? _baseServices;
        private Dictionary<Assembly, SharpLinkDynamicModule>? _targetModules;
        private FrozenDictionary<long, ServiceRegistration>? _targetServices;
        private SharpLinkDynamicModule[]? _newModules;
        private RpcGeneratedManifestRegistration[]? _oldCodecs;
        private int[]? _releaseOrder;
        private SharpLinkRuntimeContext.PreparedImplementationManifests? _runtimePublication;
        private readonly long _runtimeBaseRevision;
        private Dictionary<Assembly, RpcHash128>? _immutableOwnerHashes;
        private Task? _releaseTask;
        private Task? _disposeTask;
        private bool _sealed;
        private bool _released;
        private bool _committed;
        private bool _terminal;

        internal PreparedImplementationReplacement(
            SharpLinkServer server, SharpLinkDynamicModule[] oldModules,
            ISharpLinkGeneratedAssemblyManifest[] incoming,
            Dictionary<Assembly, SharpLinkDynamicModule> baseModules,
            FrozenDictionary<long, ServiceRegistration> baseServices, long generation)
        {
            _server = server;
            _oldModules = oldModules;
            _incoming = incoming;
            _baseModules = baseModules;
            _baseServices = baseServices;
            _runtimeBaseRevision = server._runtimeContext.RegistrationRevision;
            BaseGeneration = generation;
            TargetGeneration = checked(generation + 1);
        }

        internal long BaseGeneration { get; }
        internal long TargetGeneration { get; }
        internal bool IsSealed => Volatile.Read(ref _sealed);
        internal bool IsCommitted => Volatile.Read(ref _committed);
        internal ProtocolV2ContractManifest TargetManifest { get; private set; } = null!;
        internal Task? ReleaseTask => _releaseTask;

        internal void ValidateMetadata()
        {
            var retained = _server!._staticManifests.Concat(_baseModules!.Values
                .Where(module => !_oldModules!.Contains(module)).Select(static module => module.Manifest)).ToArray();
            ServerImplementationReplacementValidation.Validate(_oldModules!, _incoming!, retained);
            var hashes = retained.ToDictionary(static manifest => manifest.OwnerAssembly,
                static manifest => manifest.RpcAssemblyHash);
            if (_immutableOwnerHashes is not null &&
                (hashes.Count != _immutableOwnerHashes.Count || hashes.Any(pair =>
                    !_immutableOwnerHashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value)))
                throw new InvalidOperationException("An immutable Contract/Codec dependency owner hash changed.");
            _immutableOwnerHashes ??= hashes;
            _releaseOrder = ServerImplementationReplacementValidation.DependantsFirst(
                _oldModules!.Select(static module => module.Manifest).ToArray());
        }

        internal Task SealOldAndReleaseAsync(ServerMaintenanceCoordinator.QuiescenceProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);
            var server = _server ?? throw new InvalidOperationException("The replacement plan is terminal.");
            proof.Validate(server, this);
            return StartRelease(() => proof.Validate(server, this));
        }

        private Task StartRelease(Action validateRetirementAuthority)
        {
            var server = _server ?? throw new InvalidOperationException("The replacement plan is terminal.");
            lock (server._registryGate)
            {
                if (_releaseTask is not null)
                    return _releaseTask;
                ValidateBaseLocked();
                return _releaseTask = PrepareSealReleaseCoreAsync(validateRetirementAuthority);
            }
        }

        private async Task PrepareSealReleaseCoreAsync(Action validateRetirementAuthority)
        {
            // Publish this operation's Task under the registry gate before running any user code.
            await Task.Yield();
            try
            {
                PrepareCandidate();
                // Final authority observation follows every application preparation callback.
                // No await or application callback separates it from the registry-locked seal.
                // Keep validation outside that lock: a custom TimeProvider may reenter host APIs.
                validateRetirementAuthority();
                var server = _server!;
                lock (server._registryGate)
                {
                    ValidateBaseLocked();
                    _runtimePublication!.ValidateBase();
                    // Irreversible before touching the first old gate. A later failure cannot be
                    // classified as rollback or make even one old registration callable again.
                    Volatile.Write(ref _sealed, true);
                    foreach (var module in _oldModules!)
                    {
                        if (!module.TryBeginDraining())
                            throw new InvalidOperationException("An old module retired outside the prepared transaction.");
                    }
                }
                foreach (var module in _oldModules!)
                    await module.WaitForDrainAsync().ConfigureAwait(false);
                await ReleaseOldResourcesAsync().ConfigureAwait(false);
                _released = true;
            }
            catch (Exception failure)
            {
                Exception? cleanupFailure = null;
                try { await DisposeCandidatesAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailure = exception; }
                if (!IsSealed)
                    RejectBeforeSeal();
                if (cleanupFailure is not null)
                    throw new AggregateException(failure, cleanupFailure);
                throw;
            }
        }

        private void PrepareCandidate()
        {
            ValidateMetadata();
            var server = _server!;
            var oldSet = _oldModules!.ToHashSet();
            _oldCodecs = _oldModules!.Select(static module => module.CodecRegistration).ToArray();
            var retainedModules = _baseModules!.Values.Where(module => !oldSet.Contains(module)).ToArray();
            _targetModules = _baseModules.Where(pair => !oldSet.Contains(pair.Value))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            _targetServices = _baseServices!.Where(pair => !oldSet.Contains(pair.Value.Module!))
                .ToFrozenDictionary();
            _newModules = new SharpLinkDynamicModule[_incoming!.Length];
            for (var index = 0; index < _incoming.Length; index++)
            {
                var manifest = _incoming[index];
                var registration = server._runtimeContext.PrepareGeneratedManifest(manifest);
                var module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, registration);
                _newModules[index] = module;
                _targetModules.Add(manifest.OwnerAssembly, module);
            }
            foreach (var module in _newModules)
            {
                var candidate = server.BuildRegistrationCandidate(module.Manifest, module, _targetServices,
                    retainedModules.Concat(_newModules.Where(other => !ReferenceEquals(other, module))).ToArray(),
                    server._runtimeContext.CreateGeneratedCodecSnapshot(), out var error);
                if (error is not null)
                    throw new InvalidOperationException(error.Message);
                _targetServices = candidate.Services;
            }
            _runtimePublication = server._runtimeContext.PrepareImplementationManifests(
                _oldCodecs, _newModules.Select(static module => module.CodecRegistration).ToArray());
            // Candidate StubFactories can execute application code. Revalidate the immutable
            // owner/dependency boundary after all preparation callbacks, before retiring any gate.
            ValidateMetadata();
            TargetManifest = new ProtocolV2ContractManifest(TargetGeneration, _targetServices
                .OrderBy(static pair => pair.Key).Select(pair => new KeyValuePair<long, RpcHash128>(
                    pair.Key, _immutableOwnerHashes![pair.Value.ContractType.Assembly])).ToArray());
        }

        private async Task ReleaseOldResourcesAsync()
        {
            var connections = _server!._connectionRegistry.SnapshotOwned();
            List<Exception>? failures = null;
            foreach (var index in _releaseOrder!)
            {
                var module = _oldModules![index];
                var services = _baseServices!.Values.Where(service => ReferenceEquals(service.Module, module)).ToArray();
                foreach (var connection in connections)
                {
                    foreach (var service in services)
                    {
                        try { await connection.DisposeServiceAsync(service).ConfigureAwait(false); }
                        catch (Exception exception) { (failures ??= []).Add(exception); }
                    }
                }
                foreach (var service in services)
                {
                    try { await service.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { (failures ??= []).Add(exception); }
                }
                try { _oldCodecs![index].Dispose(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            if (failures is not null)
                throw new AggregateException("Old implementation physical disposal failed; the domain remains closed.", failures);
        }

        internal void CommitPaused(ServerMaintenanceCoordinator.QuiescenceProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);
            var server = _server ?? throw new InvalidOperationException("The replacement plan is terminal.");
            proof.Validate(server, this);
            CommitPausedCore();
        }

        private void CommitPausedCore()
        {
            var server = _server ?? throw new InvalidOperationException("The replacement plan is terminal.");
            lock (server._registryGate)
            {
                ValidateBaseLocked();
                if (!_released || _releaseTask?.IsCompletedSuccessfully != true)
                    throw new InvalidOperationException("Old physical resource release has not completed successfully.");
                // The only fallible operation is exact runtime-base validation, before publication.
                // Afterwards: prebuilt map swaps and permanent module retirement, no user callbacks.
                _runtimePublication!.Commit();
                server._serviceModuleRegistry.CommitPreparedModules(_targetModules!);
                Volatile.Write(ref server._services, _targetServices!);
                server._registryGeneration = TargetGeneration;
                foreach (var module in _oldModules!)
                    module.MarkReleased();
                Volatile.Write(ref _committed, true);
                ClearCatalogRoots();
            }
        }

        private void ValidateBaseLocked()
        {
            var server = _server!;
            server._serviceModuleRegistry.ValidateColdWriter(this);
            server._runtimeContext.ValidateRegistrationRevision(_runtimeBaseRevision);
            if (_terminal || _committed || server._serviceModuleRegistry.HasRetirementFailure || server.CurrentState != ServerState.Running ||
                server._registryGeneration != BaseGeneration || !ReferenceEquals(server._services, _baseServices))
                throw new InvalidOperationException("The prepared server catalog base or lifecycle is stale.");
            if (server._dynamicModules.Count != _baseModules!.Count)
                throw new InvalidOperationException("The prepared server module roster changed.");
            foreach (var pair in _baseModules)
            {
                if (!server._dynamicModules.TryGetValue(pair.Key, out var module) || !ReferenceEquals(module, pair.Value))
                    throw new InvalidOperationException("An exact prepared module identity changed.");
            }
        }

        internal void Complete()
        {
            var server = _server ?? throw new InvalidOperationException("The replacement plan is terminal.");
            lock (server._registryGate)
            {
                if (!_committed || server.CurrentState != ServerState.Running)
                    throw new InvalidOperationException("Only a committed running replacement can complete.");
                server._serviceModuleRegistry.ReleaseColdWriter(this);
                _terminal = true;
                _server = null;
                _releaseTask = null;
                _disposeTask = null;
            }
            server.ScheduleContractManifestPublish();
        }

        internal void RejectBeforeSeal()
        {
            var server = _server!;
            lock (server._registryGate)
            {
                if (_sealed)
                    throw new InvalidOperationException("A retired implementation cannot roll back.");
                server._serviceModuleRegistry.ReleaseColdWriter(this);
                _terminal = true;
                ClearCatalogRoots();
                _server = null;
                _releaseTask = null;
                _disposeTask = null;
            }
            server.ScheduleContractManifestPublish();
        }

        public ValueTask DisposeAsync()
        {
            var server = Volatile.Read(ref _server);
            if (server is null)
                return ValueTask.CompletedTask;
            lock (server._registryGate)
                return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            await Task.Yield();
            var release = _releaseTask;
            if (release is not null)
            {
                try { await release.ConfigureAwait(false); }
                catch { /* The operation's caller observes its original failure. */ }
            }
            if (_terminal)
                return;
            try
            {
                if (!_committed)
                    await DisposeCandidatesAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!_sealed)
                    RejectBeforeSeal();
                else
                    DetachFailedClosed();
            }
        }

        private void DetachFailedClosed()
        {
            var server = _server!;
            lock (server._registryGate)
            {
                // Keep a root-free closed latch, not a retained receipt -> old implementation graph.
                server._serviceModuleRegistry.RetainFailedClosedWriter(this);
                _terminal = true;
                ClearCatalogRoots();
                _server = null;
                _releaseTask = null;
                _disposeTask = null;
            }
        }

        private async Task DisposeCandidatesAsync()
        {
            var services = _targetServices;
            _targetServices = null;
            var modules = _newModules;
            _newModules = null;
            List<Exception>? failures = null;
            if (services is not null)
            {
                foreach (var service in services.Values)
                {
                    if (_baseServices!.Values.Contains(service))
                        continue;
                    try { await service.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { (failures ??= []).Add(exception); }
                }
            }
            if (modules is not null)
            {
                foreach (var module in modules)
                {
                    if (module is null)
                        continue;
                    try { module.CodecRegistration.Dispose(); }
                    catch (Exception exception) { (failures ??= []).Add(exception); }
                    module.TryBeginDraining();
                    module.MarkReleased();
                }
            }
            _targetModules = null;
            _runtimePublication = null;
            if (failures is not null)
                throw new AggregateException(failures);
        }

        private void ClearCatalogRoots()
        {
            _immutableOwnerHashes = null;
            _oldModules = null;
            _incoming = null;
            _baseModules = null;
            _baseServices = null;
            _targetModules = null;
            _targetServices = null;
            _newModules = null;
            _oldCodecs = null;
            _releaseOrder = null;
            _runtimePublication = null;
        }
    }
}
