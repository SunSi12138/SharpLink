namespace SharpLink.Runtime;

/// <summary>Immutable, instance-scoped runtime services for one SharpLink client or server.</summary>
public sealed class SharpLinkRuntimeContext : IRpcRuntimeContext
{
    private readonly SharpLinkRuntimeOptions _options;

    internal SharpLinkRuntimeContext(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        Func<Type, IRpcCodec?>? resolver,
        IReadOnlyDictionary<Type, IRpcCodec> codecs,
        bool includeGeneratedAssemblyCatalog)
    {
        _options = options.CloneValidated();
        Concurrency = concurrency.CloneValidated();
        var generatedFactories = new Dictionary<Type, IRpcGeneratedCodecFactory>(
            RpcGeneratedCodecRegistry.CreateSnapshot());
        var manifests = includeGeneratedAssemblyCatalog
            ? SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            : [];
        foreach (var manifest in manifests)
        {
            foreach (var factory in manifest.Codecs)
            {
                if (generatedFactories.TryGetValue(factory.TargetType, out var existing) &&
                    !string.Equals(existing.SchemaId, factory.SchemaId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Generated Codec schema conflict for '{factory.TargetType.FullName}': " +
                        $"'{existing.SchemaId}' and '{factory.SchemaId}'.");
                }
                generatedFactories[factory.TargetType] = factory;
            }
        }
        Codecs = new RpcCodecProvider(resolver, codecs, generatedFactories);
        Buffers = new SharpLinkBufferWriterPool(bufferPool);
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

    internal IReadOnlyDictionary<Type, IRpcGeneratedCodecFactory> CreateGeneratedCodecSnapshot()
        => ((RpcCodecProvider)Codecs).CreateGeneratedFactorySnapshot();

    internal void PublishGeneratedCodecs(IReadOnlyDictionary<Type, IRpcGeneratedCodecFactory> factories)
        => ((RpcCodecProvider)Codecs).PublishGeneratedFactories(factories);

    internal void RemoveResolvedGeneratedCodecs(IEnumerable<Type> types)
        => ((RpcCodecProvider)Codecs).RemoveResolvedCodecs(types);

    // This process-wide fallback is used only before an instance-owned Context is attached.
    // It must never snapshot weak manifest entries because it has no unregister boundary and
    // would otherwise become a permanent root for collectible plugin load contexts.
    internal static SharpLinkRuntimeContext Default { get; } =
        new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
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

    /// <summary>Sets the optional fallback codec resolver for this context.</summary>
    public SharpLinkRuntimeContextBuilder UseCodecResolver(Func<Type, IRpcCodec?>? resolver)
    {
        _resolver = resolver;
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
        => Build(includeGeneratedAssemblyCatalog: true);

    internal SharpLinkRuntimeContext Build(bool includeGeneratedAssemblyCatalog)
    {
        var options = _options.CloneValidated();
        var concurrency = _concurrency.CloneValidated();
        var bufferPool = _bufferPool.CloneValidated();
        return new SharpLinkRuntimeContext(options, concurrency, bufferPool, _resolver,
            new Dictionary<Type, IRpcCodec>(_codecs), includeGeneratedAssemblyCatalog);
    }
}
