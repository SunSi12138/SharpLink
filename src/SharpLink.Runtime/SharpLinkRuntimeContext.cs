using System.Linq;

namespace SharpLink.Runtime;

/// <summary>Immutable, instance-scoped runtime services for one SharpLink client or server.</summary>
public sealed class SharpLinkRuntimeContext : IRpcRuntimeContext, IRpcContractCodecProviderResolver, IDisposable
{
    private readonly SharpLinkRuntimeOptions _options;
    private readonly Lock _registrationGate = new();
    private HashSet<RpcGeneratedManifestRegistration> _manifestRegistrations = [];
    private Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> _manifestCodecProviders = [];
    private object _registrationVersion = new();
    private int _disposed;

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
        // Existing callers retain explicit validation. Callers holding a publication lock must
        // migrate to Prepare outside / Commit inside; this wrapper is not a validation bypass.
        using var publication = PrepareGeneratedCodecPublication(registrations, pendingRegistration);
        CommitGeneratedCodecPublication(publication);
    }

    internal PreparedGeneratedCodecPublication PrepareGeneratedCodecPublication(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
        RpcGeneratedManifestRegistration? pendingRegistration = null,
        bool adoptPendingRegistration = false)
        => PrepareGeneratedCodecPublicationCore(registrations, pendingRegistration, adoptPendingRegistration, null);

    private PreparedGeneratedCodecPublication PrepareGeneratedCodecPublicationCore(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
        RpcGeneratedManifestRegistration? pendingRegistration,
        bool adoptPendingRegistration,
        object? requiredVersion)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(registrations);
        if (adoptPendingRegistration && pendingRegistration is null)
            throw new ArgumentException("Adoption requires the exact pending manifest registration.", nameof(pendingRegistration));
        RpcGeneratedManifestRegistration[] manifests;
        Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> providers;
        object version;
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            version = _registrationVersion;
            if (requiredVersion is not null && !ReferenceEquals(requiredVersion, version))
                throw new InvalidOperationException("Runtime Codec registry changed before adoption preparation.");
            manifests = [.. _manifestRegistrations];
            providers = _manifestCodecProviders.ToDictionary(static pair => pair.Key,
                static pair => new List<ManifestCodecProviderEntry>(pair.Value));
        }
        var codecs = ((RpcCodecProvider)Codecs).PrepareGeneratedRegistrations(registrations);
        foreach (var manifest in manifests)
            manifest.ThrowIfDisposed();
        ValidateReferencedCodecDependencies(manifests, codecs.Registrations);
        if (pendingRegistration is not null)
        {
            pendingRegistration.ThrowIfDisposed();
            ValidateReferencedCodecDependencies([pendingRegistration], codecs.Registrations);
        }
        var nextManifests = new HashSet<RpcGeneratedManifestRegistration>(manifests);
        if (adoptPendingRegistration && nextManifests.Add(pendingRegistration!))
        {
            var owner = pendingRegistration!.OwnerAssembly;
            var provider = RpcGeneratedCodecResolver.GetProvider(pendingRegistration);
            if (!providers.TryGetValue(owner, out var list))
                providers.Add(owner, list = []);
            list.Add(new ManifestCodecProviderEntry(pendingRegistration, provider));
        }
        return new PreparedGeneratedCodecPublication(this, version,
            new PreparedCodecPublicationPayload(codecs, nextManifests, providers, pendingRegistration));
    }

    internal void CommitGeneratedCodecPublication(PreparedGeneratedCodecPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        // Take is one-shot even when a stale/foreign/disposed commit fails. No callbacks replay.
        var payload = publication.Take();
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!ReferenceEquals(publication.Owner, this) || !ReferenceEquals(publication.Version, _registrationVersion))
                throw new InvalidOperationException("Prepared Runtime Codec publication is stale or belongs to another context.");
            payload.PendingRegistration?.ThrowIfDisposed();
            // Only framework-owned immutable maps and pre-created adoption records reach commit.
            ((RpcCodecProvider)Codecs).CommitGeneratedRegistrations(payload.Codecs);
            _manifestRegistrations = payload.Manifests;
            _manifestCodecProviders = payload.Providers;
            _registrationVersion = payload.NextVersion;
        }
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
        var nextVersion = new object();
        ((RpcCodecProvider)Codecs).RemoveResolvedCodecs(registration);
        lock (_registrationGate)
        {
            _manifestRegistrations.Remove(registration);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        RpcGeneratedManifestRegistration[] registrations;
        lock (_registrationGate)
        {
            registrations = [.. _manifestRegistrations];
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

    internal readonly record struct ManifestCodecProviderEntry(
        RpcGeneratedManifestRegistration Registration,
        IRpcCodecProvider Provider);

    // This is an exact-version foundation, not the still-required unrelated-publication merge.
    // The owner of the preparation must keep dependency/candidate lifetimes until commit or rollback.
    internal sealed class PreparedGeneratedCodecPublication : IDisposable
    {
        private PreparedCodecPublicationPayload? _payload;
        internal SharpLinkRuntimeContext Owner { get; }
        internal object Version { get; }
        internal PreparedGeneratedCodecPublication(SharpLinkRuntimeContext owner, object version,
            PreparedCodecPublicationPayload payload)
        { Owner = owner; Version = version; _payload = payload; }
        internal PreparedCodecPublicationPayload Take()
            => Interlocked.Exchange(ref _payload, null)
                ?? throw new InvalidOperationException("Prepared Codec publication has already been consumed or disposed.");
        public void Dispose() => Interlocked.Exchange(ref _payload, null);
    }

    internal sealed class PreparedCodecPublicationPayload(
        RpcCodecProvider.PreparedGeneratedRegistrations codecs,
        HashSet<RpcGeneratedManifestRegistration> manifests,
        Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> providers,
        RpcGeneratedManifestRegistration? pendingRegistration)
    {
        internal RpcCodecProvider.PreparedGeneratedRegistrations Codecs { get; } = codecs;
        internal HashSet<RpcGeneratedManifestRegistration> Manifests { get; } = manifests;
        internal Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> Providers { get; } = providers;
        internal RpcGeneratedManifestRegistration? PendingRegistration { get; } = pendingRegistration;
        internal object NextVersion { get; } = new();
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
