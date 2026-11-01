using System.Linq;

namespace SharpLink.Runtime;

/// <summary>Immutable, instance-scoped runtime services for one SharpLink client or server.</summary>
public sealed class SharpLinkRuntimeContext : IRpcRuntimeContext, IRpcContractCodecProviderResolver, IDisposable
{
    private readonly SharpLinkRuntimeOptions _options;
    private readonly Lock _registrationGate = new();
    private HashSet<RpcGeneratedManifestRegistration> _manifestRegistrations;
    private Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> _manifestCodecProviders;
    private object _registrationVersion = new();
    private RpcGeneratedManifestRegistration[] _manifestDisposalOrder = [];
    private int _disposed;
    private ColdMetadataPreparation? _metadataOwner;
    private ColdMetadataPreparation? _publicationMetadataOwner;
    private GeneratedManifestSnapshot? _generatedManifests;
    internal ColdMetadataAuthority Authority => Domain.Authority;
    internal ColdMetadataDomain Domain { get; }
    internal ColdMetadataPreparation CreateMetadataStorage()
        => (_metadataOwner ?? throw new ObjectDisposedException(nameof(SharpLinkRuntimeContext))).ForkStorage();
    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> GeneratedManifests
        => (_generatedManifests ?? throw new ObjectDisposedException(nameof(SharpLinkRuntimeContext))).Manifests;

    internal SharpLinkRuntimeContext(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        TimeProvider timeProvider,
        GeneratedManifestSnapshot generatedManifests,
        ColdMetadataPreparation preparation)
    {
        Domain = preparation.Domain;
        _metadataOwner = preparation;
        _generatedManifests = generatedManifests;
        preparation.ReserveCollection(0); // Manifest registration set.
        preparation.ReserveCollection(0); // Owner/provider map.
        _manifestRegistrations = [];
        _manifestCodecProviders = new(ReferenceEqualityComparer.Instance);
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options.CloneValidated();
        Concurrency = concurrency.CloneValidated();
        Codecs = new RpcCodecProvider(preparation);
        using var registryCharge = preparation.ReserveCollection(0);
        var generatedRegistrations = new Dictionary<Type, RpcGeneratedCodecRegistration>();
        using var preparedCharge = preparation.ReserveCollection(generatedManifests.Manifests.Count);
        var prepared = new List<RpcGeneratedManifestRegistration>(generatedManifests.Manifests.Count);
        try
        {
            foreach (var manifest in generatedManifests.Manifests)
            {
                var owner = PrepareGeneratedManifest(manifest, validateReferencedDependencies: false);
                prepared.Add(owner);
                foreach (var pair in owner.Codecs)
                {
                    if (generatedRegistrations.TryGetValue(pair.Key, out var existing) &&
                        !HasSameGeneratedCodecIdentity(existing.Factory, pair.Value.Factory))
                    {
                        throw new InvalidOperationException(
                            $"Generated Codec conflict for '{pair.Key.FullName}': " +
                            $"identity '{DescribeGeneratedCodecIdentity(existing.Factory)}' and " +
                            $"'{DescribeGeneratedCodecIdentity(pair.Value.Factory)}'.");
                    }
                    if (!generatedRegistrations.ContainsKey(pair.Key)) registryCharge.GrowTo(checked(generatedRegistrations.Count + 1));
                    generatedRegistrations[pair.Key] = pair.Value;
                }
            }
            ValidateReferencedCodecDependencies(prepared, generatedRegistrations);
            PublishGeneratedCodecs(generatedRegistrations);
            foreach (var registration in prepared)
                AdoptGeneratedManifest(registration);
            Buffers = new SharpLinkBufferWriterPool(bufferPool);
        }
        catch (Exception preparationException)
        {
            try { ThrowAfterConstructionRollback(preparationException, prepared, (RpcCodecProvider)Codecs); }
            finally
            {
                prepared.Clear();
                generatedRegistrations.Clear();
                _manifestRegistrations.Clear();
                Array.Clear(_manifestDisposalOrder);
                foreach (var list in _manifestCodecProviders.Values) list.Clear();
                _manifestCodecProviders.Clear();
                _publicationMetadataOwner?.Dispose();
                _generatedManifests = null;
            }
        }
        prepared.Clear();
        generatedRegistrations.Clear();
    }

    private static bool HasSameGeneratedCodecIdentity(
        IRpcGeneratedCodecFactory left,
        IRpcGeneratedCodecFactory right)
        => left.CodecHash == right.CodecHash;

    private static string DescribeGeneratedCodecIdentity(IRpcGeneratedCodecFactory factory)
        => $"codec:{factory.CodecHash}";

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAfterConstructionRollback(
        Exception preparationException,
        IReadOnlyList<RpcGeneratedManifestRegistration> prepared,
        RpcCodecProvider codecProvider)
    {
        List<Exception>? cleanupFailures = null;
        for (var index = prepared.Count - 1; index >= 0; index--)
        {
            try { prepared[index].Dispose(); }
            catch (Exception cleanupException) { (cleanupFailures ??= []).Add(cleanupException); }
        }
        try { codecProvider.Dispose(); }
        catch (Exception cleanupException) { (cleanupFailures ??= []).Add(cleanupException); }
        if (cleanupFailures is null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationException).Throw();
        cleanupFailures!.Insert(0, preparationException);
        throw new AggregateException(cleanupFailures);
    }

    /// <summary>Gets an isolated copy of the frozen runtime options.</summary>
    public SharpLinkRuntimeOptions Options => _options.CloneValidated();

    /// <inheritdoc />
    public IRpcCodecProvider Codecs { get; }

    /// <summary>Gets the context-owned packet writer pool.</summary>
    public SharpLinkBufferWriterPool Buffers { get; }

    IRpcBufferWriterPool IRpcRuntimeContext.Buffers => Buffers;
    internal RuntimeConcurrencyOptions Concurrency { get; }

    /// <summary>
    /// Gets the application-owned time source used for monotonic runtime scheduling.
    /// SharpLink never disposes this instance.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    internal SharpLinkProtocolOptions Protocol => _options.Protocol;
    internal SharpLinkFlowControlOptions FlowControl => _options.FlowControl;
    internal SharpLinkCompressionOptions Compression => _options.Compression;
    internal SharpLinkPerformanceProfile PerformanceProfile => _options.PerformanceProfile;

    internal RpcGeneratedManifestRegistration PrepareGeneratedManifest(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        if (manifest is PreparedGeneratedManifest prepared)
            return PrepareGeneratedManifest(prepared, validateReferencedDependencies: true);
        using var preparation = Domain.BeginPreparation();
        var captured = PreparedGeneratedManifest.Capture(manifest, preparation);
        return PrepareGeneratedManifest(captured, validateReferencedDependencies: true);
    }

    private RpcGeneratedManifestRegistration PrepareGeneratedManifest(
        ISharpLinkGeneratedAssemblyManifest manifest, bool validateReferencedDependencies)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (manifest is not PreparedGeneratedManifest prepared || !ReferenceEquals(prepared.Preparation.Authority, Authority))
            throw new InvalidOperationException("Codec preparation requires this authority's exact prepared manifest.");
        var callbackOwner = _metadataOwner ?? throw new ObjectDisposedException(nameof(SharpLinkRuntimeContext));
        using var callback = callbackOwner.EnterCallback();
        SharpLinkGeneratedManifestStructureValidator.ValidatePrepared(prepared);
        var registration = RpcGeneratedManifestRegistration.Create(prepared, Codecs, callbackOwner);
        try
        {
            if (validateReferencedDependencies)
                ValidateReferencedCodecDependency(registration, CreateGeneratedCodecSnapshot());
            return registration;
        }
        catch { registration.Dispose(); throw; }
    }

    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> CreateGeneratedCodecSnapshot()
        => ((RpcCodecProvider)Codecs).CreateGeneratedRegistrationSnapshot();

    internal void PublishGeneratedCodecs(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
        RpcGeneratedManifestRegistration? pendingRegistration = null)
    {
        // Existing callers retain explicit validation. Callers holding a publication lock must
        // migrate to Prepare outside / Commit inside; this wrapper is not a validation bypass.
        using var publication = PrepareGeneratedCodecPublication(registrations, pendingRegistration);
        CommitGeneratedCodecPublication(publication);
    }

    internal PreparedGeneratedCodecPublication PrepareGeneratedCodecPublication(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
        RpcGeneratedManifestRegistration? pendingRegistration = null,
        bool adoptPendingRegistration = false,
        CapturedGeneratedCodecs? sourceSnapshot = null)
    {
        if (sourceSnapshot is not null)
        {
            sourceSnapshot.ThrowIfUnavailable(this);
            return PrepareGeneratedCodecPublicationCore(registrations, pendingRegistration,
                adoptPendingRegistration, sourceSnapshot.Version, sourceSnapshot.ProviderIdentity);
        }
        return PrepareGeneratedCodecPublicationCore(registrations, pendingRegistration, adoptPendingRegistration, null);
    }

    internal CapturedGeneratedCodecs CaptureGeneratedCodecSnapshot()
    {
        var storage = CreateMetadataStorage();
        try
        {
            lock (_registrationGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var snapshot = ((RpcCodecProvider)Codecs).CaptureGeneratedRegistrationState();
                storage.ReserveCollection(snapshot.Registrations.Count);
                var copy = new Dictionary<Type, RpcGeneratedCodecRegistration>(snapshot.Registrations.Count, ReferenceEqualityComparer.Instance);
                foreach (var pair in snapshot.Registrations) copy.Add(pair.Key, pair.Value);
                return new CapturedGeneratedCodecs(this, _registrationVersion, snapshot.Identity, copy, storage);
            }
        }
        catch { storage.Dispose(); throw; }
    }

    private PreparedGeneratedCodecPublication PrepareGeneratedCodecPublicationCore(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
        RpcGeneratedManifestRegistration? pendingRegistration,
        bool adoptPendingRegistration,
        object? requiredVersion,
        object? requiredProviderIdentity = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(registrations);
        if (adoptPendingRegistration && pendingRegistration is null)
            throw new ArgumentException("Adoption requires the exact pending manifest registration.", nameof(pendingRegistration));
        var storage = (_metadataOwner ?? throw new ObjectDisposedException(nameof(SharpLinkRuntimeContext))).ForkStorage();
        RpcGeneratedManifestRegistration[]? manifests = null;
        Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>>? providers = null;
        HashSet<RpcGeneratedManifestRegistration>? nextManifests = null;
        try
        {
            object version;
            lock (_registrationGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                version = _registrationVersion;
                if (requiredVersion is not null && !ReferenceEquals(requiredVersion, version))
                    throw new InvalidOperationException("Runtime Codec registry changed before adoption preparation.");
                storage.ReserveCollection(_manifestRegistrations.Count);
                storage.ReserveCollection(checked(_manifestCodecProviders.Count + (adoptPendingRegistration ? 1 : 0)));
                foreach (var pair in _manifestCodecProviders)
                    storage.ReserveCollection(checked(pair.Value.Count + (adoptPendingRegistration ? 1 : 0)));
                manifests = [.. _manifestRegistrations];
                providers = new(ReferenceEqualityComparer.Instance);
                foreach (var pair in _manifestCodecProviders)
                    providers.Add(pair.Key, new List<ManifestCodecProviderEntry>(pair.Value));
            }
            var codecs = ((RpcCodecProvider)Codecs).PrepareGeneratedRegistrations(registrations);
            if (requiredProviderIdentity is not null && !ReferenceEquals(codecs.Expected.Identity, requiredProviderIdentity))
                throw new InvalidOperationException("The captured source Codec map changed before preparation.");
            foreach (var manifest in manifests) manifest.ThrowIfDisposed();
            ValidateReferencedCodecDependencies(manifests, codecs.Registrations);
            if (pendingRegistration is not null)
            {
                pendingRegistration.ThrowIfDisposed();
                ValidateReferencedCodecDependency(pendingRegistration, codecs.Registrations);
            }
            storage.ReserveCollection(checked(manifests.Length + (adoptPendingRegistration ? 1 : 0)));
            nextManifests = new HashSet<RpcGeneratedManifestRegistration>(manifests);
            if (adoptPendingRegistration && nextManifests.Add(pendingRegistration!))
            {
                var owner = pendingRegistration!.OwnerAssembly;
                var provider = RpcGeneratedCodecResolver.GetProvider(pendingRegistration);
                if (!providers.TryGetValue(owner, out var list))
                {
                    storage.ReserveCollection(1);
                    providers.Add(owner, list = []);
                }
                list.Add(new ManifestCodecProviderEntry(pendingRegistration, provider));
            }
            storage.ReserveCollection(nextManifests.Count);
            var disposalOrder = new RpcGeneratedManifestRegistration[nextManifests.Count];
            nextManifests.CopyTo(disposalOrder);
            var publication = new PreparedGeneratedCodecPublication(this, version,
                new PreparedCodecPublicationPayload(codecs, nextManifests, providers, disposalOrder, pendingRegistration, storage));
            Array.Clear(manifests);
            return publication;
        }
        catch
        {
            if (manifests is not null) Array.Clear(manifests);
            nextManifests?.Clear();
            if (providers is not null)
            {
                foreach (var list in providers.Values) list.Clear();
                providers.Clear();
            }
            storage.Dispose();
            throw;
        }
    }

    internal bool IsCurrentGeneratedCodecPublication(PreparedGeneratedCodecPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        lock (_registrationGate)
            return Volatile.Read(ref _disposed) == 0 && ReferenceEquals(publication.Owner, this) &&
                ReferenceEquals(publication.Version, _registrationVersion) && publication.Peek() is { } payload &&
                ((RpcCodecProvider)Codecs).IsCurrentGeneratedRegistrations(payload.Codecs);
    }

    internal void CommitGeneratedCodecPublication(PreparedGeneratedCodecPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var payload = publication.Take();
        try
        {
            lock (_registrationGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (!ReferenceEquals(publication.Owner, this) || !ReferenceEquals(publication.Version, _registrationVersion))
                    throw new InvalidOperationException("Prepared Runtime Codec publication is stale or belongs to another context.");
                payload.PendingRegistration?.ThrowIfDisposed();
                ((RpcCodecProvider)Codecs).CommitGeneratedRegistrations(payload.Codecs);
                // Old cold-map readers copied under this gate. Cleanup is transferred to the
                // token and runs only when its caller disposes it outside publication locks.
                publication.Retire(_manifestRegistrations, _manifestCodecProviders,
                    _manifestDisposalOrder, _publicationMetadataOwner);
                _manifestRegistrations = payload.Manifests;
                _manifestCodecProviders = payload.Providers;
                _manifestDisposalOrder = payload.DisposalOrder;
                _publicationMetadataOwner = payload.TransferStorage();
                _registrationVersion = payload.NextVersion;
            }
        }
        catch { publication.Failed(payload); throw; }
    }

    private static void ValidateReferencedCodecDependencies(
        IEnumerable<RpcGeneratedManifestRegistration> manifests,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        foreach (var registration in manifests)
            ValidateReferencedCodecDependency(registration, registrations);
    }

    private static void ValidateReferencedCodecDependency(RpcGeneratedManifestRegistration registration,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        if (registration.Manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest)
            return;
        var dependencies = dependencyManifest.ReferencedCodecDependencies
            ?? throw new InvalidOperationException(
                $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' returned null referenced Codec dependencies.");
        foreach (var dependency in dependencies)
        {
            if (dependency is null)
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' contains a null referenced Codec dependency.");
            }
            var targetType = dependency.TargetType ?? throw new InvalidOperationException(
                $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' contains a referenced Codec dependency with no target Type.");
            if (dependency.ExpectedCodecHash.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' with an empty expected CodecHash.");
            }
            if (!registrations.TryGetValue(targetType, out var actual))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' from the exact bound runtime Type/assembly generation with expected CodecHash '{dependency.ExpectedCodecHash}', but no generated Codec is registered for that exact Type.");
            }
            if (!ReferenceEquals(actual.Owner.Manifest.OwnerAssembly, targetType.Assembly))
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' from assembly generation '{targetType.Assembly.FullName}', but the registered Codec is owned by '{actual.Owner.Manifest.OwnerAssembly.FullName}'.");
            }
            if (actual.Factory.CodecHash != dependency.ExpectedCodecHash)
            {
                throw new InvalidOperationException(
                    $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' with expected CodecHash '{dependency.ExpectedCodecHash}', but the exact registered Type has CodecHash '{actual.Factory.CodecHash}'.");
            }
        }
    }


    internal void AdoptGeneratedManifest(RpcGeneratedManifestRegistration registration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> codecs;
        object version;
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_manifestRegistrations.Contains(registration))
                return;
            version = _registrationVersion;
            codecs = CreateGeneratedCodecSnapshot();
        }
        using var publication = PrepareGeneratedCodecPublicationCore(codecs, registration, true, version);
        CommitGeneratedCodecPublication(publication);
    }

    IRpcCodecProvider IRpcContractCodecProviderResolver.GetContractCodecProvider(System.Reflection.Assembly ownerAssembly)
        => GetManifestCodecProvider(ownerAssembly);

    internal IRpcCodecProvider GetManifestCodecProvider(System.Reflection.Assembly ownerAssembly)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_manifestCodecProviders.TryGetValue(ownerAssembly, out var providers) && providers.Count != 0)
                return providers[^1].Provider;
        }

        using var lookupPreparation = Domain.BeginPreparation();
        var loadedGeneratedOwner = ColdMetadataCatalog.ContainsManifestOwner(lookupPreparation, ownerAssembly);
        if (!loadedGeneratedOwner)
            return Codecs;

        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_manifestCodecProviders.TryGetValue(ownerAssembly, out var providers) && providers.Count != 0)
                return providers[^1].Provider;
        }

        throw new InvalidOperationException(
            $"Generated Contract owner '{ownerAssembly.FullName}' has a generated manifest but it was not adopted by this SharpLink runtime context. Rebuild the client/server runtime after loading the Contract assembly.");
    }

    internal RpcGeneratedCodecRegistration? FindGeneratedCodec(ISharpLinkGeneratedAssemblyManifest manifest, Type targetType)
    {
        lock (_registrationGate)
        {
            foreach (var registration in _manifestRegistrations)
            {
                if ((ReferenceEquals(registration.Manifest, manifest) ||
                     registration.Manifest is PreparedGeneratedManifest prepared && ReferenceEquals(prepared.OriginalManifest, manifest)) &&
                    registration.Codecs.TryGetValue(targetType, out var codec))
                    return codec;
            }
        }
        return null;
    }

    internal void ReleaseGeneratedManifest(RpcGeneratedManifestRegistration registration)
    {
        var nextVersion = new object();
        ((RpcCodecProvider)Codecs).RemoveResolvedCodecs(registration);
        lock (_registrationGate)
        {
            _manifestRegistrations.Remove(registration);
            for (var index = 0; index < _manifestDisposalOrder.Length; index++)
                if (ReferenceEquals(_manifestDisposalOrder[index], registration)) _manifestDisposalOrder[index] = null!;
            if (_manifestCodecProviders.TryGetValue(registration.OwnerAssembly, out var providers))
            {
                for (var index = providers.Count - 1; index >= 0; index--)
                {
                    if (ReferenceEquals(providers[index].Registration, registration))
                        providers.RemoveAt(index);
                }
                if (providers.Count == 0)
                    _manifestCodecProviders.Remove(registration.OwnerAssembly);
            }
            _registrationVersion = nextVersion;
        }
        registration.Dispose();
    }

    /// <summary>Releases context-owned Codec registrations, Adapter scopes, and pooled buffers.</summary>
    public void Dispose()
    {
        ColdMetadataPreparation.ThrowIfCallbackReentry(Domain);
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Domain.SealAndJoinAsync().GetAwaiter().GetResult();
        RpcGeneratedManifestRegistration[] registrations;
        lock (_registrationGate)
        {
            registrations = _manifestDisposalOrder;
            _manifestDisposalOrder = [];
            _manifestRegistrations.Clear();
            foreach (var list in _manifestCodecProviders.Values) list.Clear();
            _manifestCodecProviders.Clear();
        }
        List<Exception>? failures = null;
        try { ((RpcCodecProvider)Codecs).Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { Buffers.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        for (var index = registrations.Length - 1; index >= 0; index--)
        {
            try { registrations[index]?.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        Array.Clear(registrations);
        Interlocked.Exchange(ref _publicationMetadataOwner, null)?.Dispose();
        _generatedManifests = null;
        Interlocked.Exchange(ref _metadataOwner, null)?.Dispose();
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    internal sealed class CapturedGeneratedCodecs : IDisposable
    {
        private ColdMetadataPreparation? _storage;
        private readonly Dictionary<Type, RpcGeneratedCodecRegistration> _registrations;
        internal CapturedGeneratedCodecs(SharpLinkRuntimeContext owner, object version,
            object providerIdentity,
            Dictionary<Type, RpcGeneratedCodecRegistration> registrations, ColdMetadataPreparation storage)
        { Owner = owner; Version = version; ProviderIdentity = providerIdentity; _registrations = registrations; _storage = storage; }
        internal SharpLinkRuntimeContext Owner { get; }
        internal object Version { get; }
        internal object ProviderIdentity { get; }
        internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Registrations
            => _storage is not null ? _registrations : throw new ObjectDisposedException(nameof(CapturedGeneratedCodecs));
        internal void ThrowIfUnavailable(SharpLinkRuntimeContext owner)
        {
            ObjectDisposedException.ThrowIf(_storage is null, this);
            if (!ReferenceEquals(owner, Owner)) throw new InvalidOperationException("Captured Codec map belongs to another Runtime.");
        }
        public void Dispose()
        {
            var storage = Interlocked.Exchange(ref _storage, null);
            if (storage is null) return;
            _registrations.Clear();
            storage.Dispose();
        }
    }

    internal readonly record struct ManifestCodecProviderEntry(
        RpcGeneratedManifestRegistration Registration,
        IRpcCodecProvider Provider);

    // This is an exact-version foundation, not the still-required unrelated-publication merge.
    // The owner of the preparation must keep dependency/candidate lifetimes until commit or rollback.
    internal sealed class PreparedGeneratedCodecPublication : IDisposable
    {
        private PreparedCodecPublicationPayload? _payload;
        private PreparedCodecPublicationPayload? _failed;
        private ColdMetadataPreparation? _retiredStorage;
        private HashSet<RpcGeneratedManifestRegistration>? _retiredManifests;
        private Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>>? _retiredProviders;
        private RpcGeneratedManifestRegistration[]? _retiredOrder;
        internal SharpLinkRuntimeContext Owner { get; }
        internal object Version { get; }
        internal PreparedGeneratedCodecPublication(SharpLinkRuntimeContext owner, object version,
            PreparedCodecPublicationPayload payload)
        { Owner = owner; Version = version; _payload = payload; }
        internal PreparedCodecPublicationPayload Take()
            => Interlocked.Exchange(ref _payload, null)
                ?? throw new InvalidOperationException("Prepared Codec publication has already been consumed or disposed.");
        internal PreparedCodecPublicationPayload? Peek() => Volatile.Read(ref _payload);
        internal void Failed(PreparedCodecPublicationPayload payload) => _failed = payload;
        internal void Retire(HashSet<RpcGeneratedManifestRegistration> manifests,
            Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> providers,
            RpcGeneratedManifestRegistration[] order, ColdMetadataPreparation? storage)
        { _retiredManifests = manifests; _retiredProviders = providers; _retiredOrder = order; _retiredStorage = storage; }
        public void Dispose()
        {
            Interlocked.Exchange(ref _payload, null)?.Dispose();
            Interlocked.Exchange(ref _failed, null)?.Dispose();
            Interlocked.Exchange(ref _retiredManifests, null)?.Clear();
            var providers = Interlocked.Exchange(ref _retiredProviders, null);
            if (providers is not null)
            {
                foreach (var list in providers.Values) list.Clear();
                providers.Clear();
            }
            var order = Interlocked.Exchange(ref _retiredOrder, null);
            if (order is not null) Array.Clear(order);
            Interlocked.Exchange(ref _retiredStorage, null)?.Dispose();
        }
    }

    internal sealed class PreparedCodecPublicationPayload(
        RpcCodecProvider.PreparedGeneratedRegistrations codecs,
        HashSet<RpcGeneratedManifestRegistration> manifests,
        Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> providers,
        RpcGeneratedManifestRegistration[] disposalOrder,
        RpcGeneratedManifestRegistration? pendingRegistration,
        ColdMetadataPreparation storage) : IDisposable
    {
        private ColdMetadataPreparation? _storage = storage;
        internal RpcCodecProvider.PreparedGeneratedRegistrations Codecs { get; } = codecs;
        internal HashSet<RpcGeneratedManifestRegistration> Manifests { get; } = manifests;
        internal Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> Providers { get; } = providers;
        internal RpcGeneratedManifestRegistration[] DisposalOrder { get; } = disposalOrder;
        internal RpcGeneratedManifestRegistration? PendingRegistration { get; } = pendingRegistration;
        internal object NextVersion { get; } = new();
        internal ColdMetadataPreparation TransferStorage()
            => Interlocked.Exchange(ref _storage, null) ?? throw new InvalidOperationException("Publication storage already transferred.");
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _storage, null);
            if (owner is null) return;
            Manifests.Clear();
            Array.Clear(DisposalOrder);
            foreach (var list in Providers.Values) list.Clear();
            Providers.Clear();
            owner.Dispose();
        }
    }
}

/// <summary>Builds and validates an immutable <see cref="SharpLinkRuntimeContext"/>.</summary>
public sealed class SharpLinkRuntimeContextBuilder
{
    private readonly SharpLinkRuntimeOptions _options = new();
    private readonly RuntimeConcurrencyOptions _concurrency = new();
    private readonly BufferWriterPoolOptions _bufferPool = new();
    private IGeneratedManifestSource _generatedManifestSource = GlobalCatalogManifestSource.Instance;
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>Configures runtime and protocol limits.</summary>
    public SharpLinkRuntimeContextBuilder Configure(Action<SharpLinkRuntimeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    /// <summary>Configures the context-owned writer pool.</summary>
    public SharpLinkRuntimeContextBuilder ConfigureBufferPool(Action<BufferWriterPoolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_bufferPool);
        return this;
    }

    /// <summary>Configures striped state containers created by this context.</summary>
    public SharpLinkRuntimeContextBuilder ConfigureStateStores(Action<RuntimeConcurrencyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_concurrency);
        return this;
    }

    /// <summary>
    /// Uses an application-owned time source for contexts built by this builder.
    /// The context stores the reference but never disposes it.
    /// </summary>
    public SharpLinkRuntimeContextBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        return this;
    }

    /// <summary>
    /// Uses an instance-scoped bootstrap source for subsequent Compile operations. The source is
    /// application-owned and queried exactly once by each Compile; materialized Contexts never retain it.
    /// </summary>
    internal SharpLinkRuntimeContextBuilder UseGeneratedManifestSource(IGeneratedManifestSource source)
    {
        _generatedManifestSource = source ?? throw new ArgumentNullException(nameof(source));
        return this;
    }

    /// <summary>Validates and freezes a new context.</summary>
    public SharpLinkRuntimeContext Build()
        => MaterializeStandalone(Compile());

    internal SharpLinkRuntimeContext Build(bool includeGeneratedAssemblyCatalog)
        => MaterializeStandalone(Compile(includeGeneratedAssemblyCatalog
            ? _generatedManifestSource
            : ReferenceEquals(_generatedManifestSource, GlobalCatalogManifestSource.Instance)
                ? FixedGeneratedManifestSource.Empty
                : _generatedManifestSource));

    internal SharpLinkRuntimeContext Build(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> generatedManifests)
        => MaterializeStandalone(Compile(new FixedGeneratedManifestSource(generatedManifests)));

    internal SharpLinkRuntimeContextBuildPlan Compile() => Compile(_generatedManifestSource);
    internal SharpLinkRuntimeContextBuildPlan Compile(ColdMetadataDomain domain) => Compile(_generatedManifestSource, domain);
    internal SharpLinkRuntimeContextBuildPlan Compile(IGeneratedManifestSource manifestSource)
        => Compile(manifestSource, new ColdMetadataAuthority().CreateDomain());
    internal SharpLinkRuntimeContextBuildPlan Compile(IGeneratedManifestSource manifestSource, ColdMetadataDomain domain)
    {
        ArgumentNullException.ThrowIfNull(manifestSource);
        var preparation = domain.BeginPreparation();
        try
        {
            var options = _options.CloneValidated();
            var concurrency = _concurrency.CloneValidated();
            var bufferPool = _bufferPool.CloneValidated();
            var generatedManifests = GeneratedManifestSnapshot.Capture(manifestSource, preparation);
            generatedManifests.ValidateForPlanCompilation();
            return new SharpLinkRuntimeContextBuildPlan(options, concurrency, bufferPool,
                _timeProvider, generatedManifests, preparation);
        }
        catch { preparation.Dispose(); throw; }
    }

    private static SharpLinkRuntimeContext MaterializeStandalone(SharpLinkRuntimeContextBuildPlan plan)
    {
        using var transaction = new SynchronousBuildTransaction();
        try
        {
            var context = transaction.Own(
                plan.Materialize(),
                static value => value.Dispose(),
                SynchronousBuildResourceMetadata.FrameworkOwned("Standalone RuntimeContext"));
            transaction.Commit();
            return context;
        }
        catch (Exception buildException)
        {
            transaction.Rollback(buildException);
            throw new System.Diagnostics.UnreachableException();
        }
    }
}
