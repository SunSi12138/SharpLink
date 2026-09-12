using System.Collections.Frozen;

namespace SharpLink.Runtime;

/// <summary>
/// A validated, immutable input snapshot for materializing one runtime Context.
/// The plan does not own disposable runtime resources; <see cref="Materialize"/> creates them.
/// </summary>
internal sealed class SharpLinkRuntimeContextBuildPlan
{
    private readonly SharpLinkRuntimeOptions _options;
    private readonly RuntimeConcurrencyOptions _concurrency;
    private readonly BufferWriterPoolOptions _bufferPool;
    private readonly FrozenDictionary<Type, IRpcCodec> _codecs;
    private readonly GeneratedManifestSnapshot _generatedManifests;

    internal SharpLinkRuntimeContextBuildPlan(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        TimeProvider timeProvider,
        Func<Type, IRpcCodec?>? resolver,
        IReadOnlyDictionary<Type, IRpcCodec> codecs,
        GeneratedManifestSnapshot generatedManifests)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _concurrency = concurrency ?? throw new ArgumentNullException(nameof(concurrency));
        _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Resolver = resolver;
        ArgumentNullException.ThrowIfNull(codecs);
        _codecs = codecs.Count == 0
            ? FrozenDictionary<Type, IRpcCodec>.Empty
            : codecs.ToFrozenDictionary();
        _generatedManifests = generatedManifests ?? throw new ArgumentNullException(nameof(generatedManifests));
    }

    /// <summary>Gets the frozen performance profile required by client construction planning.</summary>
    internal SharpLinkPerformanceProfile PerformanceProfile => _options.PerformanceProfile;

    /// <summary>Gets the application-owned time source. The runtime Context never disposes it.</summary>
    internal TimeProvider TimeProvider { get; }

    /// <summary>Gets the optional application-owned fallback resolver.</summary>
    internal Func<Type, IRpcCodec?>? Resolver { get; }

    /// <summary>
    /// Gets the exact frozen manifest snapshot shared by Runtime, Client, and Server planning.
    /// </summary>
    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> GeneratedManifests
        => _generatedManifests.Manifests;

    /// <summary>Creates the Context-owned pool, codec provider, and generated registration scopes.</summary>
    internal SharpLinkRuntimeContext Materialize()
        => new(
            _options,
            _concurrency,
            _bufferPool,
            TimeProvider,
            Resolver,
            _codecs,
            _generatedManifests.Manifests);
}
