using System.Linq;

namespace SharpLink.Runtime;

/// <summary>Immutable, instance-scoped runtime services for one SharpLink client or server.</summary>
public sealed class SharpLinkRuntimeContext : IRpcRuntimeContext, IRpcContractCodecProviderResolver, IDisposable
{
    private readonly SharpLinkRuntimeOptions _options;
    private readonly Lock _registrationGate = new();
    private readonly HashSet<RpcGeneratedManifestRegistration> _manifestRegistrations = [];
    private readonly Dictionary<System.Reflection.Assembly, List<ManifestCodecProviderEntry>> _manifestCodecProviders = [];
    private readonly Dictionary<Type, List<ManifestCodecProviderEntry>> _contractCodecProviders = [];
    private int _disposed;

    internal SharpLinkRuntimeContext(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        Func<Type, IRpcCodec?>? resolver,
        IReadOnlyDictionary<Type, IRpcCodec> codecs,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> generatedManifests)
    {
        _options = options.CloneValidated();
        Concurrency = concurrency.CloneValidated();
        Codecs = new RpcCodecProvider(resolver, codecs);
        var generatedRegistrations = new Dictionary<Type, RpcGeneratedCodecRegistration>();
        var prepared = new List<RpcGeneratedManifestRegistration>(generatedManifests.Count);
        try
        {
            foreach (var manifest in generatedManifests)
            {
                var owner = PrepareGeneratedManifest(manifest);
                prepared.Add(owner);
                foreach (var pair in owner.Codecs)
                {
                    if (generatedRegistrations.TryGetValue(pair.Key, out var existing) &&
                        (!string.Equals(existing.Factory.SchemaId, pair.Value.Factory.SchemaId, StringComparison.Ordinal) ||
                         !string.Equals(existing.Factory.WireFormatId, pair.Value.Factory.WireFormatId, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"Generated Codec conflict for '{pair.Key.FullName}': " +
                            $"schema/wire '{existing.Factory.SchemaId}'/'{existing.Factory.WireFormatId}' and " +
                            $"'{pair.Value.Factory.SchemaId}'/'{pair.Value.Factory.WireFormatId}'.");
                    }
                    generatedRegistrations[pair.Key] = pair.Value;
                }
            }
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
    internal SharpLinkProtocolOptions Protocol => _options.Protocol;
    internal SharpLinkFlowControlOptions FlowControl => _options.FlowControl;
    internal SharpLinkCompressionOptions Compression => _options.Compression;
    internal SharpLinkPerformanceProfile PerformanceProfile => _options.PerformanceProfile;

    internal RpcGeneratedManifestRegistration PrepareGeneratedManifest(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateGeneratedManifestCompatibility(manifest);
        return RpcGeneratedManifestRegistration.Create(manifest, Codecs);
    }

    private static void ValidateGeneratedManifestCompatibility(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        if (manifest.ApiVersion == SharpLinkGeneratedManifestVersions.Api &&
            manifest.ProtocolVersion == SharpLinkGeneratedManifestVersions.Protocol)
            return;
        throw new InvalidOperationException(
            $"Generated manifest '{manifest.OwnerAssembly.FullName}' is incompatible: " +
            $"API={manifest.ApiVersion}, Protocol={manifest.ProtocolVersion}, Generator={manifest.GeneratorVersion}.");
    }

    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> CreateGeneratedCodecSnapshot()
        => ((RpcCodecProvider)Codecs).CreateGeneratedRegistrationSnapshot();

    internal void PublishGeneratedCodecs(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ((RpcCodecProvider)Codecs).PublishGeneratedRegistrations(registrations);
    }

    internal void AdoptGeneratedManifest(RpcGeneratedManifestRegistration registration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_manifestRegistrations.Add(registration))
                return;

            var assemblyProvider = RpcGeneratedCodecResolver.GetProvider(registration);
            if (!_manifestCodecProviders.TryGetValue(registration.Manifest.OwnerAssembly, out var providers))
            {
                providers = [];
                _manifestCodecProviders.Add(registration.Manifest.OwnerAssembly, providers);
            }
            providers.Add(new ManifestCodecProviderEntry(registration, assemblyProvider));

            foreach (var contract in registration.ContractRegistrations)
            {
                if (!_contractCodecProviders.TryGetValue(contract.Key, out var contractProviders))
                {
                    contractProviders = [];
                    _contractCodecProviders.Add(contract.Key, contractProviders);
                }
                contractProviders.Add(new ManifestCodecProviderEntry(registration, contract.Value.CodecProvider));
            }
        }
    }

    IRpcCodecProvider IRpcContractCodecProviderResolver.GetContractCodecProvider(Type contractType)
        => GetContractCodecProvider(contractType);

    internal IRpcCodecProvider GetContractCodecProvider(Type contractType)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(contractType);

        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_contractCodecProviders.TryGetValue(contractType, out var providers) && providers.Count != 0)
                return providers[^1].Provider;
        }

        var loadedRequiresAdoption = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Any(manifest =>
            manifest.ContractCodecSets.Any(set =>
                set.ContractType == contractType && (set.HasCompileTimePolicy || set.Codecs.Count != 0)));
        if (!loadedRequiresAdoption)
            return Codecs;

        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_contractCodecProviders.TryGetValue(contractType, out var providers) && providers.Count != 0)
                return providers[^1].Provider;
        }

        throw new InvalidOperationException(
            $"Generated Contract '{contractType.FullName}' has generated Codec bindings but its manifest was not adopted by this SharpLink runtime context. Rebuild the client/server runtime after loading the Contract assembly.");
    }

    // API-4 compatibility bridge. New generated source resolves by Contract Type.
    internal IRpcCodecProvider GetManifestCodecProvider(System.Reflection.Assembly ownerAssembly)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        lock (_registrationGate)
        {
            if (_manifestCodecProviders.TryGetValue(ownerAssembly, out var providers) && providers.Count != 0)
                return providers[^1].Provider;
        }

        var loadedGeneratedOwner = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Any(manifest =>
            ReferenceEquals(manifest.OwnerAssembly, ownerAssembly) &&
            (manifest.Codecs.Count != 0 || manifest.ContractCodecs.Count != 0 ||
             manifest.ContractCodecSets.Any(static set => set.Codecs.Count != 0 || set.HasCompileTimePolicy)));
        if (!loadedGeneratedOwner)
            return Codecs;
        lock (_registrationGate)
        {
            if (_manifestCodecProviders.TryGetValue(ownerAssembly, out var providers) && providers.Count != 0)
                return providers[^1].Provider;
        }
        throw new InvalidOperationException(
            $"Generated Contract owner '{ownerAssembly.FullName}' has generated Codec bindings but its manifest was not adopted by this SharpLink runtime context.");
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
            if (_manifestCodecProviders.TryGetValue(registration.Manifest.OwnerAssembly, out var providers))
            {
                RemoveProviderEntries(providers, registration);
                if (providers.Count == 0)
                    _manifestCodecProviders.Remove(registration.Manifest.OwnerAssembly);
            }
            foreach (var contractType in registration.ContractRegistrations.Keys)
            {
                if (!_contractCodecProviders.TryGetValue(contractType, out var contractProviders))
                    continue;
                RemoveProviderEntries(contractProviders, registration);
                if (contractProviders.Count == 0)
                    _contractCodecProviders.Remove(contractType);
            }
        }
        registration.Dispose();
    }

    private static void RemoveProviderEntries(
        List<ManifestCodecProviderEntry> providers,
        RpcGeneratedManifestRegistration registration)
    {
        for (var index = providers.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(providers[index].Registration, registration))
                providers.RemoveAt(index);
        }
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
            _contractCodecProviders.Clear();
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

    internal static SharpLinkRuntimeContext Default { get; } =
        new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);

    private readonly record struct ManifestCodecProviderEntry(
        RpcGeneratedManifestRegistration Registration,
        IRpcCodecProvider Provider);
}

/// <summary>Builds and validates an immutable <see cref="SharpLinkRuntimeContext"/>.</summary>
public sealed class SharpLinkRuntimeContextBuilder
{
    private readonly SharpLinkRuntimeOptions _options = new();
    private readonly RuntimeConcurrencyOptions _concurrency = new();
    private readonly BufferWriterPoolOptions _bufferPool = new();
    private readonly Dictionary<Type, IRpcCodec> _codecs = [];
    private Func<Type, IRpcCodec?>? _resolver;

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

    /// <summary>Sets the optional fallback Codec resolver for this context.</summary>
    public SharpLinkRuntimeContextBuilder UseCodecResolver(Func<Type, IRpcCodec?>? resolver)
    {
        _resolver = resolver;
        return this;
    }

    /// <summary>Registers an explicit Codec in this context.</summary>
    public SharpLinkRuntimeContextBuilder AddCodec<T>(IRpcCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (BuiltinRpcCodecs.TryGet(typeof(T), out _))
            throw new InvalidOperationException($"The built-in codec for '{typeof(T).FullName}' is immutable and cannot be replaced.");
        if (!_codecs.TryAdd(typeof(T), codec))
            throw new InvalidOperationException($"A codec for '{typeof(T)}' is already registered in this context builder.");
        return this;
    }

    /// <summary>Validates and freezes a new runtime context.</summary>
    public SharpLinkRuntimeContext Build()
        => Build(SharpLinkGeneratedAssemblyCatalog.CreateSnapshot());

    internal SharpLinkRuntimeContext Build(bool includeGeneratedAssemblyCatalog)
        => Build(includeGeneratedAssemblyCatalog ? SharpLinkGeneratedAssemblyCatalog.CreateSnapshot() : []);

    internal SharpLinkRuntimeContext Build(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> generatedManifests)
    {
        ArgumentNullException.ThrowIfNull(generatedManifests);
        var options = _options.CloneValidated();
        var concurrency = _concurrency.CloneValidated();
        var bufferPool = _bufferPool.CloneValidated();
        return new SharpLinkRuntimeContext(options, concurrency, bufferPool, _resolver,
            new Dictionary<Type, IRpcCodec>(_codecs), generatedManifests);
    }
}
