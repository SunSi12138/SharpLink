using System.Reflection;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace SharpLink.Runtime;

internal sealed class RpcGeneratedManifestRegistration : IDisposable
{
    internal readonly Lock Gate = new();
    internal RpcCodecOwnerIdentity Identity { get; }
    private readonly IRpcCodecAdapterScope[] _scopes;
    private ImmutableMetadata? _metadata;
    internal RpcCodecMetadataLifetime MetadataLifetime { get; }
    internal ColdMetadataPreparation Storage { get; }
    internal ColdMetadataScope RegistrationScope { get; }
    private IRpcCodecProvider? _baseProvider;
    private RpcManifestCodecProvider? _contractCodecProvider;
    private RpcManifestCodecProvider? _globalCodecProvider;
    private readonly TaskCompletionSource _usersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RpcCodecColdUse _cleanupFrame;
    private int _activeUsers;
    private bool _retiring;
    private Dictionary<Type, RpcGeneratedCodecRegistration>? _dependencies;
    private IDisposable[]? _dependencyLeases;
    private readonly Dictionary<RpcManifestCodecProvider, ColdMetadataReservation> _consumers = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Dependencies => _dependencies ??
        throw new InvalidOperationException("Codec dependencies must be bound before factory invocation.");

    internal sealed record ImmutableMetadata(ISharpLinkGeneratedAssemblyManifest Manifest,
        Assembly OwnerAssembly, IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> ContractCodecs,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs);

    private RpcGeneratedManifestRegistration(ISharpLinkGeneratedAssemblyManifest manifest,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> contractCodecs,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> codecs, IRpcCodecProvider baseProvider,
        IRpcCodecAdapterScope[] scopes, IDisposable metadataLease, ColdMetadataPreparation storage,
        ColdMetadataScope scope, RpcCodecOwnerIdentity identity)
    {
        Identity = identity;
        _metadata = new(manifest, manifest.OwnerAssembly, contractCodecs, codecs);
        MetadataLifetime = new RpcCodecMetadataLifetime(_metadata, metadataLease);
        _baseProvider = baseProvider; _scopes = scopes; Storage = storage; RegistrationScope = scope;
        _cleanupFrame = new RpcCodecColdUse(this, storage.Domain, null, counted: false);
    }
    internal ImmutableMetadata Metadata => (ImmutableMetadata)MetadataLifetime.Payload;
    internal ISharpLinkGeneratedAssemblyManifest Manifest => Metadata.Manifest;
    internal Assembly OwnerAssembly => Metadata.OwnerAssembly;
    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> ContractCodecs => Metadata.ContractCodecs;
    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs => Metadata.Codecs;
    internal IRpcCodecProvider BaseProvider => _baseProvider ?? throw new ObjectDisposedException(nameof(RpcGeneratedManifestRegistration));
    internal bool HasContractCodecs => ContractCodecs.Count != 0;
    internal IRpcCodecProvider ContractCodecProvider => GetProvider(RpcGeneratedCodecResolutionScope.Contract);
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Identity.IsClosed, this);

    internal RpcCodecColdUse AcquireColdUse()
    {
        var root = AcquireSingleColdUse();
        if (_dependencies is null || _dependencies.Count == 0) return root;
        ColdMetadataReservation? stored = null;
        List<RpcCodecColdUse>? uses = null;
        try
        {
            using var visitedCharge = Storage.ReserveCollection(0, RegistrationScope);
            using var queueCharge = Storage.ReserveCollection(0, RegistrationScope);
            using var usesCharge = Storage.ReserveCollection(0, RegistrationScope);
            stored = Storage.ReserveCollection(0, RegistrationScope);
            var visited = new HashSet<RpcGeneratedManifestRegistration>(ReferenceEqualityComparer.Instance);
            var queue = new List<RpcGeneratedManifestRegistration>(); uses = new List<RpcCodecColdUse>();
            visitedCharge.GrowTo(1); queueCharge.GrowTo(1); visited.Add(this); queue.Add(this);
            for (var index = 0; index < queue.Count; index++)
            {
                var owner = queue[index];
                // This exact owner's active use protects its dependency table from physical clear.
                if (owner._dependencies is null) continue; // Private binding phase, no factory yet.
                foreach (var binding in owner._dependencies.Values)
                {
                    var dependency = binding.Owner;
                    if (visited.Contains(dependency)) continue;
                    visitedCharge.GrowTo(checked(visited.Count + 1)); queueCharge.GrowTo(checked(queue.Count + 1));
                    stored.GrowTo(checked(uses.Count + 1)); usesCharge.GrowTo(checked(uses.Count + 1));
                    var use = dependency.AcquireSingleColdUse();
                    uses.Add(use); visited.Add(dependency); queue.Add(dependency);
                }
            }
            root.AttachDependencies([.. uses], stored); stored = null; uses.Clear();
            return root;
        }
        catch
        {
            if (uses is not null) for (var index = uses.Count - 1; index >= 0; index--) uses[index].Dispose();
            stored?.Dispose(); root.Dispose(); throw;
        }
    }
    private RpcCodecColdUse AcquireSingleColdUse()
    {
        var charge = Storage.ReserveCollection(0, RegistrationScope);
        RpcCodecColdUse use;
        try
        {
            use = new RpcCodecColdUse(this, Storage.Domain, charge);
            lock (Gate) { ThrowIfDisposed(); _activeUsers = checked(_activeUsers + 1); }
        }
        catch { charge.Dispose(); throw; }
        use.Enter();
        return use;
    }
    internal void ReleaseColdUse()
    {
        lock (Gate)
            if (--_activeUsers == 0 && _retiring) _usersDrained.TrySetResult();
    }
    internal RpcManifestCodecProvider GetProvider(RpcGeneratedCodecResolutionScope scope)
    {
        using var use = AcquireColdUse();
        lock (Gate)
        {
            ThrowIfDisposed();
            var existing = scope == RpcGeneratedCodecResolutionScope.Contract ? _contractCodecProvider : _globalCodecProvider;
            if (existing is not null) return existing;
        }
        var created = new RpcManifestCodecProvider(this, BaseProvider, scope);
        try
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                ref var slot = ref (scope == RpcGeneratedCodecResolutionScope.Contract ? ref _contractCodecProvider : ref _globalCodecProvider);
                if (slot is null) { slot = created; return created; }
                return slot;
            }
        }
        finally
        {
            bool selected;
            lock (Gate) selected = ReferenceEquals(created, _contractCodecProvider) || ReferenceEquals(created, _globalCodecProvider);
            if (!selected) created.Clear();
        }
    }
    internal void BindDependencies(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        if (_dependencies is not null) return; // Old owners never rebind to a republished generation.
        using var use = AcquireColdUse();
        var count = 0;
        foreach (var binding in registrations.Values)
            if (!ReferenceEquals(binding.Owner, this) && RpcManifestCodecProvider.IsAllowedDependency(this, binding)) count = checked(count + 1);
        Storage.ReserveCollection(count, RegistrationScope); // Exact bound map.
        Storage.ReserveCollection(count, RegistrationScope); // Retained metadata-reference array.
        using var temporary = Storage.ReserveCollection(count, RegistrationScope);
        var dependencies = new Dictionary<Type, RpcGeneratedCodecRegistration>(ReferenceEqualityComparer.Instance);
        var leases = new List<IDisposable>();
        try
        {
            foreach (var pair in registrations)
            {
                if (ReferenceEquals(pair.Value.Owner, this) || !RpcManifestCodecProvider.IsAllowedDependency(this, pair.Value)) continue;
                leases.Add(pair.Value.Owner.MetadataLifetime.Retain());
                dependencies.Add(pair.Key, pair.Value);
            }
            lock (Gate)
            {
                ThrowIfDisposed();
                if (_dependencies is not null) throw new InvalidOperationException("Dependency binding is one-shot.");
                _dependencies = dependencies; _dependencyLeases = [.. leases]; leases.Clear();
            }
        }
        catch { dependencies.Clear(); foreach (var lease in leases) lease.Dispose(); throw; }
    }
    internal IDisposable RegisterCacheConsumer(RpcManifestCodecProvider provider)
    {
        // Caller holds this exact owner's active cold use. Both observer and retirement-copy
        // storage are precharged before publication; final cleanup needs no fresh admission.
        var charge = Storage.ReserveCollection(2, RegistrationScope);
        try
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                if (_consumers.ContainsKey(provider)) throw new InvalidOperationException("Duplicate cache consumer registration.");
                _consumers.Add(provider, charge);
            }
            return new ConsumerLease(this, provider);
        }
        catch { charge.Dispose(); throw; }
    }
    private void RemoveConsumer(RpcManifestCodecProvider provider)
    {
        ColdMetadataReservation? charge;
        lock (Gate) _consumers.Remove(provider, out charge);
        charge?.Dispose();
    }
    private sealed class ConsumerLease(RpcGeneratedManifestRegistration owner, RpcManifestCodecProvider provider) : IDisposable
    {
        private RpcGeneratedManifestRegistration? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveConsumer(provider);
    }

    internal void InvalidateResolvedGraph()
    {
        if (!Identity.Close()) return;
        InvalidateConsumersAndCaches();
    }
    private void InvalidateConsumersAndCaches()
    {
        KeyValuePair<RpcManifestCodecProvider, ColdMetadataReservation>[] consumers;
        lock (Gate) { consumers = _consumers.ToArray(); _consumers.Clear(); }
        for (var index = 0; index < consumers.Length; index++)
        { var pair = consumers[index]; consumers[index] = default; pair.Key.RemoveOrigin(Identity); pair.Value.Dispose(); }
        Array.Clear(consumers);
        (_baseProvider as RpcCodecProvider)?.RemoveResolvedCodecs(this);
        _contractCodecProvider?.Clear(); _globalCodecProvider?.Clear();
    }

    internal void PrepareCodecs()
    {
        using var use = AcquireColdUse();
        foreach (var registration in Codecs.Values) registration.PrepareAdapterCodec();
        foreach (var registration in ContractCodecs.Values) registration.PrepareAdapterCodec();
    }

    internal static RpcGeneratedManifestRegistration Create(
        PreparedGeneratedManifest manifest,
        IRpcCodecProvider provider,
        ColdMetadataPreparation callbackOwner)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);

        var preparation = manifest.Preparation;
        var budget = manifest.RegistrationScope;
        var count = checked(manifest.Codecs.Count + manifest.ContractCodecs.Count);
        using var scopesCharge = preparation.ReserveCollection(count, budget);
        using var adapterMapCharge = preparation.ReserveCollection(count, budget);
        using var combinedCharge = preparation.ReserveCollection(count, budget);
        var scopes = new List<IRpcCodecAdapterScope>(count);
        IDisposable? metadataLease = null;
        ColdMetadataPreparation? storage = null;
        var scopeByAdapterId = new Dictionary<string, AdapterScopeRegistration>(StringComparer.Ordinal);
        var allFactories = new IRpcGeneratedCodecFactory[count];
        try
        {
            metadataLease = preparation.Retain();
            storage = callbackOwner.ForkStorage();
            storage.ReserveCollection(0, budget); // Owner/cold-state header.
            storage.ReserveCollection(0, budget); // Reverse cache-consumer registry header.
            storage.ReserveCollection(0, budget); // Pre-owned retirement-copy header.
            storage.ReserveCollection(0, budget); // Pre-owned retirement callback frame.
            storage.ReserveCollection(2, budget); // Two cold drain/completion promises.
            for (var index = 0; index < manifest.Codecs.Count; index++) allFactories[index] = manifest.Codecs[index];
            for (var index = 0; index < manifest.ContractCodecs.Count; index++) allFactories[manifest.Codecs.Count + index] = manifest.ContractCodecs[index];
            Array.Sort(allFactories, static (left, right) =>
            {
                var order = StringComparer.Ordinal.Compare(left.AdapterId, right.AdapterId);
                return order != 0 ? order : StringComparer.Ordinal.Compare(left.TargetType.FullName, right.TargetType.FullName);
            });
            foreach (var factory in allFactories)
            {
                ValidateFactory(factory);
                if (factory.AdapterId is null)
                    continue;

                var adapter = factory.Adapter!;
                ValidateAdapter(factory, adapter);
                if (scopeByAdapterId.TryGetValue(factory.AdapterId, out var existing))
                {
                    if (existing.Adapter.GetType() != adapter.GetType())
                    {
                        throw new InvalidOperationException(
                            $"Adapter '{factory.AdapterId}' has inconsistent implementations in manifest '{manifest.OwnerAssembly.FullName}'.");
                    }
                    continue;
                }

                var scope = adapter.CreateScope() ?? throw new InvalidOperationException(
                    $"Adapter '{factory.AdapterId}' returned a null scope.");
                scopes.Add(scope);
                scopeByAdapterId.Add(factory.AdapterId,
                    new AdapterScopeRegistration(adapter, scope));
            }

            var ownerBox = new OwnerBox();
            var publishedCodecs = CreateRegistrations(
                manifest.Codecs,
                RpcGeneratedCodecResolutionScope.Global);
            var contractCodecs = CreateRegistrations(
                manifest.ContractCodecs,
                RpcGeneratedCodecResolutionScope.Contract);
            preparation.ReserveCollection(scopes.Count, budget);
            var registration = new RpcGeneratedManifestRegistration(
                manifest,
                contractCodecs,
                publishedCodecs,
                provider,
                [.. scopes], metadataLease!, storage!, budget, ownerBox.Identity);
            ownerBox.Value = registration;

            metadataLease = null;
            storage = null;
            return registration;

            Dictionary<Type, RpcGeneratedCodecRegistration> CreateRegistrations(
                IReadOnlyList<IRpcGeneratedCodecFactory> factories,
                RpcGeneratedCodecResolutionScope resolutionScope)
            {
                preparation.ReserveCollection(factories.Count, budget);
                using var sortedCharge = preparation.ReserveCollection(factories.Count, budget);
                var registrations = new Dictionary<Type, RpcGeneratedCodecRegistration>(ReferenceEqualityComparer.Instance);
                var ordered = new IRpcGeneratedCodecFactory[factories.Count];
                try
                {
                    for (var index = 0; index < ordered.Length; index++) ordered[index] = factories[index];
                    Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.TargetType.FullName, right.TargetType.FullName));
                    foreach (var factory in ordered)
                    {
                        var adapterScope = factory.AdapterId is null
                            ? null
                            : scopeByAdapterId[factory.AdapterId].Scope;
                        var targetType = factory.TargetType;
                        if (!registrations.TryAdd(
                                targetType,
                                new RpcGeneratedCodecRegistration(
                                    ownerBox,
                                    factory,
                                    targetType,
                                    adapterScope,
                                    resolutionScope)))
                        {
                            throw new InvalidOperationException(
                                $"Manifest '{manifest.OwnerAssembly.FullName}' contains duplicate Codec target '{factory.TargetType.FullName}' in one binding scope.");
                        }
                    }
                    return registrations;
                }
                finally { Array.Clear(ordered); }
            }
        }
        catch (Exception preparationException)
        {
            List<Exception>? cleanupFailures = null;
            for (var index = scopes.Count - 1; index >= 0; index--)
            {
                try
                {
                    scopes[index].Dispose();
                }
                catch (Exception cleanupException)
                {
                    (cleanupFailures ??= []).Add(cleanupException);
                }
            }
            scopes.Clear();
            try { metadataLease?.Dispose(); } catch (Exception error) { (cleanupFailures ??= []).Add(error); }
            try { storage?.Dispose(); } catch (Exception error) { (cleanupFailures ??= []).Add(error); }
            if (cleanupFailures is null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationException).Throw();
            cleanupFailures!.Insert(0, preparationException);
            throw new AggregateException(cleanupFailures);
        }
        finally
        {
            Array.Clear(allFactories);
            scopeByAdapterId.Clear();
            scopes.Clear();
        }
    }

    private static void ValidateFactory(IRpcGeneratedCodecFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(factory.TargetType);
        if (factory.CodecHash.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Generated Codec factory for '{factory.TargetType.FullName}' has no deterministic CodecHash.");
        }

        var hasAdapterId = factory.AdapterId is not null;
        var hasAdapter = factory.Adapter is not null;
        if (hasAdapterId != hasAdapter ||
            (hasAdapterId && string.IsNullOrWhiteSpace(factory.AdapterId)))
        {
            throw new InvalidOperationException(
                $"Generated Codec factory for '{factory.TargetType.FullName}' has inconsistent adapter metadata.");
        }
    }

    private static void ValidateAdapter(IRpcGeneratedCodecFactory factory, IRpcCodecAdapter adapter)
    {
        var actualId = factory is PreparedCodecFactory prepared ? prepared.CapturedAdapterId : adapter.AdapterId;
        if (!string.Equals(actualId, factory.AdapterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Codec adapter '{adapter.GetType().FullName}' lifecycle identity does not match its generated registration metadata.");
        }
    }




    public void Dispose()
    {
        RpcCodecColdUse.ThrowIfHeld(this);
        bool winner;
        lock (Gate)
        {
            winner = !_retiring;
            if (winner)
            {
                _retiring = true; Identity.Close();
                if (_activeUsers == 0) _usersDrained.TrySetResult();
            }
        }
        if (!winner)
        {
            var previous = _retired.Task.GetAwaiter().GetResult();
            if (previous is not null) ExceptionDispatchInfo.Capture(previous).Throw();
            return;
        }
        List<Exception>? failures = null;
        try { InvalidateConsumersAndCaches(); } catch (Exception error) { (failures ??= []).Add(error); }
        _usersDrained.Task.GetAwaiter().GetResult();
        void Attempt(Action action) { try { action(); } catch (Exception error) { (failures ??= []).Add(error); } }
        _cleanupFrame.Enter();
        try
        {
            var metadata = _metadata!;
            // Closed admission + drained users makes this a physical cut, distinct from unpublish.
            Attempt(() => _contractCodecProvider?.Clear());
            Attempt(() => _globalCodecProvider?.Clear());
            _contractCodecProvider = null; _globalCodecProvider = null;
            foreach (var registration in metadata.Codecs.Values) registration.ClearRuntimeState();
            foreach (var registration in metadata.ContractCodecs.Values) registration.ClearRuntimeState();
            for (var index = _scopes.Length - 1; index >= 0; index--) Attempt(_scopes[index].Dispose);
            Array.Clear(_scopes);
            _dependencies?.Clear(); _dependencies = null;
            if (_dependencyLeases is { } dependencies)
            {
                _dependencyLeases = null;
                foreach (var lease in dependencies) Attempt(lease.Dispose);
                Array.Clear(dependencies);
            }
            _baseProvider = null; _metadata = null;
            Attempt(MetadataLifetime.Dispose);
        }
        finally
        {
            Attempt(_cleanupFrame.Dispose);
            Attempt(Storage.Dispose);
            Exception? failure = failures is null ? null : failures.Count == 1 ? failures[0] : new AggregateException(failures);
            _retired.TrySetResult(failure);
        }
        var result = _retired.Task.GetAwaiter().GetResult();
        if (result is not null) ExceptionDispatchInfo.Capture(result).Throw();
    }

    private sealed record AdapterScopeRegistration(IRpcCodecAdapter Adapter, IRpcCodecAdapterScope Scope);
    internal sealed class OwnerBox { internal RpcCodecOwnerIdentity Identity { get; } = new(); internal RpcGeneratedManifestRegistration Value { get; set; } = null!; }
}

internal sealed class RpcGeneratedCodecRegistration
{
    private readonly RpcGeneratedManifestRegistration.OwnerBox _owner;
    private IRpcCodecAdapterScope? _adapterScope;
    private readonly RpcGeneratedCodecResolutionScope _resolutionScope;
    private IRpcCodec? _preparedCodec;

    internal RpcGeneratedCodecRegistration(
        RpcGeneratedManifestRegistration.OwnerBox owner,
        IRpcGeneratedCodecFactory factory,
        Type targetType,
        IRpcCodecAdapterScope? adapterScope,
        RpcGeneratedCodecResolutionScope resolutionScope)
    {
        _owner = owner;
        Identity = new(owner.Identity);
        Factory = factory;
        TargetType = targetType;
        UnsafeBlitRequirement = (factory as IRpcGeneratedUnsafeBlitCodecFactory)?.Requirement;
        _adapterScope = adapterScope;
        _resolutionScope = resolutionScope;
    }

    internal RpcGeneratedManifestRegistration Owner => _owner.Value;
    internal RpcCodecBindingIdentity Identity { get; }
    internal void ClearRuntimeState() { _adapterScope = null; _preparedCodec = null; }
    internal IRpcGeneratedCodecFactory Factory { get; }
    internal IRpcGeneratedCodecFactory FactoryIdentity => Factory is PreparedCodecFactory prepared ? prepared.Original : Factory;
    internal Type TargetType { get; }
    internal SharpLinkGeneratedUnsafeBlitRequirement? UnsafeBlitRequirement { get; }

    internal void PrepareAdapterCodec()
    {
        if (_adapterScope is null)
            return;
        using var use = Owner.AcquireColdUse();
        var codec = Factory.Create(GetOwnerProvider(), _adapterScope) ?? throw new InvalidOperationException(
            $"Generated Codec factory for '{Factory.TargetType.FullName}' returned null.");
        ValidateCodec(codec);
        lock (Owner.Gate) { Owner.ThrowIfDisposed(); Volatile.Write(ref _preparedCodec, codec); }
    }

    internal IRpcCodec GetCodec()
    {
        using var use = Owner.AcquireColdUse();
        var codec = Volatile.Read(ref _preparedCodec);
        if (codec is null)
        {
            codec = Factory.Create(GetOwnerProvider(), adapterScope: null) ?? throw new InvalidOperationException(
                $"Generated Codec factory for '{Factory.TargetType.FullName}' returned null.");
            ValidateCodec(codec);
        }
        Owner.ThrowIfDisposed();
        return codec;
    }

    private IRpcCodecProvider GetOwnerProvider()
        => RpcGeneratedCodecResolver.GetProvider(Owner, _resolutionScope);

    private void ValidateCodec(IRpcCodec codec)
    {
        if (!Factory.IsCompatibleCodec(codec))
        {
            throw new InvalidOperationException(
                $"Codec returned for '{Factory.TargetType.FullName}' implements an incompatible IRpcCodec<T>.");
        }
    }
}

