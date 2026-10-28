using System.Linq;

namespace SharpLink.Runtime;

/// <summary>Immutable, instance-scoped runtime services for one SharpLink client or server.</summary>
public sealed class SharpLinkRuntimeContext : IRpcRuntimeContext, IRpcContractCodecProviderResolver, IDisposable
{
    private readonly SharpLinkRuntimeOptions _options;
    private readonly Lock _registrationGate = new();
    private HashSet<RpcGeneratedManifestRegistration> _manifestRegistrations = [];
    private Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> _manifestCodecProviders = [];
    private int _disposed;
    private long _registrationRevision;

    internal SharpLinkRuntimeContext(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        TimeProvider timeProvider,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> generatedManifests)
    {
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options.CloneValidated();
        Concurrency = concurrency.CloneValidated();
        Codecs = new RpcCodecProvider();
        var generatedRegistrations = new Dictionary<Type, RpcGeneratedCodecRegistration>();
        var prepared = new List<RpcGeneratedManifestRegistration>(generatedManifests.Count);
        try
        {
            foreach (var manifest in generatedManifests)
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
                    generatedRegistrations[pair.Key] = pair.Value;
                }
            }
            ValidateReferencedCodecDependencies(prepared, generatedRegistrations);
            PublishGeneratedCodecs(generatedRegistrations);
            foreach (var registration in prepared)
                AdoptGeneratedManifest(registration);
        }
        catch (Exception preparationException)
        {
            ThrowAfterConstructionRollback(preparationException, prepared, (RpcCodecProvider)Codecs);
        }
        Buffers = new SharpLinkBufferWriterPool(bufferPool);
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

    internal RpcGeneratedManifestRegistration PrepareGeneratedManifest(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => PrepareGeneratedManifest(manifest, validateReferencedDependencies: true);

    private RpcGeneratedManifestRegistration PrepareGeneratedManifest(
        ISharpLinkGeneratedAssemblyManifest manifest,
        bool validateReferencedDependencies)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(manifest);
        SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);
        SharpLinkGeneratedManifestStructureValidator.Validate(manifest);
        var registration = RpcGeneratedManifestRegistration.Create(manifest, Codecs);
        if (!validateReferencedDependencies)
            return registration;
        try
        {
            ValidateReferencedCodecDependencies([registration], CreateGeneratedCodecSnapshot());
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> CreateGeneratedCodecSnapshot()
        => ((RpcCodecProvider)Codecs).CreateGeneratedRegistrationSnapshot();

    internal void PublishGeneratedCodecs(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
        RpcGeneratedManifestRegistration? pendingRegistration = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RpcGeneratedManifestRegistration[] manifests;
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> previousCodecs;
        long revision;
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            manifests = [.. _manifestRegistrations];
            previousCodecs = CreateGeneratedCodecSnapshot();
            revision = _registrationRevision;
        }
        // Metadata is a public SPI: do not hold the catalog gate while invoking it.
        // Every referenced dependency is resolved against this same candidate map.
        var candidateOwners = registrations.Values.Select(static codec => codec.Owner).Distinct().ToArray();
        ValidateReferencedCodecDependencies(manifests, registrations);
        if (pendingRegistration is not null)
            ValidateReferencedCodecDependencies([pendingRegistration], registrations);
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (revision != _registrationRevision &&
                !CanPublishAfterManifestRemoval(manifests, previousCodecs, candidateOwners, revision))
                throw new InvalidOperationException("The runtime catalog changed during Codec publication validation.");
            ((RpcCodecProvider)Codecs).PublishGeneratedRegistrations(registrations);
            _registrationRevision++;
        }
    }

    // The caller holds the catalog gate. This checks only captured identities and internal
    // collections, never manifest/provider metadata. The remaining owners must be covered
    // by the validated snapshot, its codec map must still be current, and the candidate must
    // not revive a removed owner (including through a referenced-codec dependency).
    private bool CanPublishAfterManifestRemoval(
        RpcGeneratedManifestRegistration[] validated,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> previousCodecs,
        RpcGeneratedManifestRegistration[] candidateOwners,
        long revision)
    {
        var removedCount = validated.Length - _manifestRegistrations.Count;
        if (removedCount <= 0 || _registrationRevision - revision != removedCount ||
            !_manifestRegistrations.IsSubsetOf(validated) ||
            !ReferenceEquals(previousCodecs, CreateGeneratedCodecSnapshot()))
            return false;
        foreach (var owner in candidateOwners)
            if (!_manifestRegistrations.Contains(owner) && Array.IndexOf(validated, owner) >= 0)
                return false;
        return true;
    }

    private static void ValidateReferencedCodecDependencies(
        IEnumerable<RpcGeneratedManifestRegistration> manifests,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        foreach (var registration in manifests)
        {
            if (registration.Manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest)
                continue;
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
    }

    internal void AdoptGeneratedManifest(RpcGeneratedManifestRegistration registration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_manifestRegistrations.Add(registration))
                return;
            _registrationRevision++;
            var provider = RpcGeneratedCodecResolver.GetProvider(registration);
            if (!_manifestCodecProviders.TryGetValue(registration.Manifest.OwnerAssembly, out var providers))
            {
                providers = [];
                _manifestCodecProviders.Add(registration.Manifest.OwnerAssembly, providers);
            }
            providers.Add(new ManifestCodecProviderEntry(registration, provider));
        }
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

        var loadedGeneratedOwner = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Any(manifest =>
            ReferenceEquals(manifest.OwnerAssembly, ownerAssembly));
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
                if (ReferenceEquals(registration.Manifest, manifest) &&
                    registration.Codecs.TryGetValue(targetType, out var codec))
                    return codec;
            }
        }
        return null;
    }

    internal void ReleaseGeneratedManifest(RpcGeneratedManifestRegistration registration)
    {
        ((RpcCodecProvider)Codecs).RemoveResolvedCodecs(registration);
        lock (_registrationGate)
        {
            _manifestRegistrations.Remove(registration);
            _registrationRevision++;
            if (_manifestCodecProviders.TryGetValue(registration.Manifest.OwnerAssembly, out var providers))
            {
                for (var index = providers.Count - 1; index >= 0; index--)
                {
                    if (ReferenceEquals(providers[index].Registration, registration))
                        providers.RemoveAt(index);
                }
                if (providers.Count == 0)
                    _manifestCodecProviders.Remove(registration.Manifest.OwnerAssembly);
            }
        }
        registration.Dispose();
    }

    /// <summary>Releases context-owned Codec registrations, Adapter scopes, and pooled buffers.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        RpcGeneratedManifestRegistration[] registrations;
        lock (_registrationGate)
        {
            registrations = [.. _manifestRegistrations];
            _registrationRevision++;
            _manifestRegistrations.Clear();
            _manifestCodecProviders.Clear();
        }
        ((RpcCodecProvider)Codecs).Dispose();
        Buffers.Dispose();
        List<Exception>? failures = null;
        for (var index = registrations.Length - 1; index >= 0; index--)
        {
            try { registrations[index].Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    internal long RegistrationRevision
    {
        get { lock (_registrationGate) return _registrationRevision; }
    }

    internal void ValidateRegistrationRevision(long expected)
    {
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_registrationRevision != expected)
                throw new InvalidOperationException("The prepared runtime registration revision is stale.");
        }
    }

    /// <summary>
    /// Preallocates adoption for implementation-only manifests. Their external Contract/Codec graph
    /// is immutable, so no global Codec publication, cache invalidation, or resolver rebinding occurs.
    /// The server owns admission closure and physical disposal; this object certifies neither.
    /// </summary>
    internal PreparedImplementationManifests PrepareImplementationManifests(
        IReadOnlyList<RpcGeneratedManifestRegistration> removed,
        IReadOnlyList<RpcGeneratedManifestRegistration> added)
    {
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var revision = _registrationRevision;
            var registrations = new HashSet<RpcGeneratedManifestRegistration>(_manifestRegistrations);
            var providers = _manifestCodecProviders.ToDictionary(
                static pair => pair.Key, static pair => new List<ManifestCodecProviderEntry>(pair.Value));
            foreach (var registration in removed)
            {
                ValidateImplementationOnly(registration);
                if (!registrations.Remove(registration))
                    throw new InvalidOperationException("The exact old implementation manifest is not adopted.");
                var owner = registration.Manifest.OwnerAssembly;
                providers[owner].RemoveAll(entry => ReferenceEquals(entry.Registration, registration));
                if (providers[owner].Count == 0)
                    providers.Remove(owner);
            }
            foreach (var registration in added)
            {
                ValidateImplementationOnly(registration);
                registration.ThrowIfDisposed();
                ValidateReferencedCodecDependencies([registration], CreateGeneratedCodecSnapshot());
                if (!registrations.Add(registration))
                    throw new InvalidOperationException("The candidate implementation manifest is already adopted.");
                var owner = registration.Manifest.OwnerAssembly;
                if (!providers.TryGetValue(owner, out var entries))
                    providers.Add(owner, entries = []);
                entries.Add(new ManifestCodecProviderEntry(registration,
                    RpcGeneratedCodecResolver.GetProvider(registration)));
            }
            if (_registrationRevision != revision || Volatile.Read(ref _disposed) != 0)
                throw new InvalidOperationException("The runtime catalog changed during preparation.");
            return new PreparedImplementationManifests(this, revision, registrations, providers);
        }
    }

    private static void ValidateImplementationOnly(RpcGeneratedManifestRegistration registration)
    {
        if (registration.Manifest.Contracts.Count != 0 || registration.Codecs.Count != 0 ||
            registration.ContractCodecs.Count != 0)
            throw new InvalidOperationException("Prepared implementation replacement cannot change a Contract or Codec owner.");
    }

    internal sealed class PreparedImplementationManifests
    {
        private readonly SharpLinkRuntimeContext _owner;
        private readonly long _revision;
        private readonly HashSet<RpcGeneratedManifestRegistration> _registrations;
        private readonly Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> _providers;
        private bool _committed;

        internal PreparedImplementationManifests(
            SharpLinkRuntimeContext owner, long revision,
            HashSet<RpcGeneratedManifestRegistration> registrations,
            Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> providers)
        {
            _owner = owner;
            _revision = revision;
            _registrations = registrations;
            _providers = providers;
        }

        internal void ValidateBase()
        {
            lock (_owner._registrationGate)
                ValidateBaseLocked();
        }

        private void ValidateBaseLocked()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _owner._disposed) != 0, _owner);
            if (_committed || _revision != _owner._registrationRevision)
                throw new InvalidOperationException("The prepared runtime manifest base is stale.");
        }

        // All allocations, metadata/property reads, provider construction and user code happened
        // during preparation. After validation this only swaps prebuilt cold-reader state.
        internal void Commit()
        {
            lock (_owner._registrationGate)
            {
                ValidateBaseLocked();
                _owner._manifestRegistrations = _registrations;
                _owner._manifestCodecProviders = _providers;
                _owner._registrationRevision++;
                _committed = true;
            }
        }
    }

    internal readonly record struct ManifestCodecProviderEntry(
        RpcGeneratedManifestRegistration Registration,
        IRpcCodecProvider Provider);
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

    /// <summary>
    /// Validates and freezes the Context inputs without allocating Context-owned resources. Builders
    /// materialize the returned plan inside their synchronous construction transaction.
    /// </summary>
    internal SharpLinkRuntimeContextBuildPlan Compile(IGeneratedManifestSource manifestSource)
    {
        ArgumentNullException.ThrowIfNull(manifestSource);
        var options = _options.CloneValidated();
        var concurrency = _concurrency.CloneValidated();
        var bufferPool = _bufferPool.CloneValidated();
        var generatedManifests = GeneratedManifestSnapshot.Capture(manifestSource);
        generatedManifests.ValidateForPlanCompilation();
        return new SharpLinkRuntimeContextBuildPlan(
            options,
            concurrency,
            bufferPool,
            _timeProvider,
            generatedManifests);
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
