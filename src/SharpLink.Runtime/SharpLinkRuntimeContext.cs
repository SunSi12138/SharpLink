namespace SharpLink.Runtime;

/// <summary>Immutable, instance-scoped runtime services for one SharpLink client or server.</summary>
public sealed class SharpLinkRuntimeContext : IRpcRuntimeContext, IDisposable
{
    private readonly SharpLinkRuntimeOptions _options;
    private readonly Lock _registrationGate = new();
    private readonly HashSet<RpcGeneratedManifestRegistration> _manifestRegistrations = [];
    private int _disposed;

    internal SharpLinkRuntimeContext(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        TimeProvider timeProvider,
        Func<Type, IRpcCodec?>? resolver,
        IReadOnlyDictionary<Type, IRpcCodec> codecs,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> generatedManifests)
    {
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
            try
            {
                prepared[index].Dispose();
            }
            catch (Exception cleanupException)
            {
                (cleanupFailures ??= []).Add(cleanupException);
            }
        }
        try
        {
            codecProvider.Dispose();
        }
        catch (Exception cleanupException)
        {
            (cleanupFailures ??= []).Add(cleanupException);
        }
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
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);
        return RpcGeneratedManifestRegistration.Create(manifest, Codecs);
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
            _manifestRegistrations.Add(registration);
        }
    }

    internal RpcGeneratedCodecRegistration? FindGeneratedCodec(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Type targetType)
    {
        lock (_registrationGate)
        {
            foreach (var registration in _manifestRegistrations)
            {
                if (ReferenceEquals(registration.Manifest, manifest) &&
                    registration.Codecs.TryGetValue(targetType, out var codec))
                {
                    return codec;
                }
            }
        }
        return null;
    }

    internal void ReleaseGeneratedManifest(RpcGeneratedManifestRegistration registration)
    {
        ((RpcCodecProvider)Codecs).RemoveResolvedCodecs(registration);
        lock (_registrationGate)
            _manifestRegistrations.Remove(registration);
        registration.Dispose();
    }

    /// <summary>Releases all generated Adapter scopes owned by this runtime Context.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        RpcGeneratedManifestRegistration[] registrations;
        lock (_registrationGate)
        {
            registrations = [.. _manifestRegistrations];
            _manifestRegistrations.Clear();
        }
        ((RpcCodecProvider)Codecs).Dispose();
        Buffers.Dispose();
        List<Exception>? failures = null;
        for (var index = registrations.Length - 1; index >= 0; index--)
        {
            try
            {
                registrations[index].Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }
}

/// <summary>Builds and validates an immutable <see cref="SharpLinkRuntimeContext"/>.</summary>
public sealed class SharpLinkRuntimeContextBuilder
{
    private readonly SharpLinkRuntimeOptions _options = new();
    private readonly RuntimeConcurrencyOptions _concurrency = new();
    private readonly BufferWriterPoolOptions _bufferPool = new();
    private readonly Dictionary<Type, IRpcCodec> _codecs = [];
    private IGeneratedManifestSource _generatedManifestSource = GlobalCatalogManifestSource.Instance;
    private TimeProvider _timeProvider = TimeProvider.System;
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

    /// <summary>Sets the optional fallback codec resolver for this context.</summary>
    public SharpLinkRuntimeContextBuilder UseCodecResolver(Func<Type, IRpcCodec?>? resolver)
    {
        _resolver = resolver;
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

    /// <summary>Registers an explicit codec in this context.</summary>
    public SharpLinkRuntimeContextBuilder AddCodec<T>(IRpcCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (SharedRpcCodec<T>.Instance is not null)
        {
            throw new InvalidOperationException(
                $"The built-in codec for '{typeof(T).FullName}' is immutable and cannot be replaced.");
        }

        if (!_codecs.TryAdd(typeof(T), codec))
            throw new InvalidOperationException($"A codec for '{typeof(T)}' is already registered in this context builder.");
        return this;
    }

    /// <summary>Validates and freezes a new context.</summary>
    public SharpLinkRuntimeContext Build()
        => MaterializeStandalone(Compile());

    internal SharpLinkRuntimeContext Build(bool includeGeneratedAssemblyCatalog)
        => MaterializeStandalone(Compile(includeGeneratedAssemblyCatalog
            ? GlobalCatalogManifestSource.Instance
            : FixedGeneratedManifestSource.Empty));

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
            _resolver,
            new Dictionary<Type, IRpcCodec>(_codecs),
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
