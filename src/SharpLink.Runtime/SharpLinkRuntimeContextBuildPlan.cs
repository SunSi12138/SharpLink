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
    private readonly SharpLinkGeneratedManifestSource _manifestSource;

    internal SharpLinkRuntimeContextBuildPlan(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        TimeProvider timeProvider,
        Func<Type, IRpcCodec?>? resolver,
        IReadOnlyDictionary<Type, IRpcCodec> codecs,
        SharpLinkGeneratedManifestSource manifestSource)
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
        _manifestSource = manifestSource ?? throw new ArgumentNullException(nameof(manifestSource));
    }

    /// <summary>Gets the frozen performance profile required by client construction planning.</summary>
    internal SharpLinkPerformanceProfile PerformanceProfile => _options.PerformanceProfile;

    /// <summary>Gets the application-owned time source. The runtime Context never disposes it.</summary>
    internal TimeProvider TimeProvider { get; }

    /// <summary>Gets the optional application-owned fallback resolver.</summary>
    internal Func<Type, IRpcCodec?>? Resolver { get; }

    /// <summary>Creates the Context-owned pool, codec provider, and generated registration scopes.</summary>
    internal SharpLinkRuntimeContext Materialize()
        => new(
            _options,
            _concurrency,
            _bufferPool,
            TimeProvider,
            Resolver,
            _codecs,
            _manifestSource.CreateMaterializationSnapshot());
}

/// <summary>
/// Owns one strong, point-in-time generated-manifest snapshot. It deliberately has no discovery
/// behavior after construction, so compile-time validation never observes a changing catalog.
/// </summary>
internal sealed class SharpLinkGeneratedManifestSource
{
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _manifests;

    private SharpLinkGeneratedManifestSource(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var snapshot = new ISharpLinkGeneratedAssemblyManifest[manifests.Count];
        for (var index = 0; index < snapshot.Length; index++)
            snapshot[index] = manifests[index] ?? throw new ArgumentException("Generated manifest snapshots cannot contain null.", nameof(manifests));
        _manifests = Array.AsReadOnly(snapshot);
    }

    /// <summary>Captures the process catalog exactly once for one compile operation.</summary>
    internal static SharpLinkGeneratedManifestSource FromCatalog()
        => new(SharpLinkGeneratedAssemblyCatalog.CreateSnapshot());

    /// <summary>Freezes a caller-supplied manifest snapshot for one compile operation.</summary>
    internal static SharpLinkGeneratedManifestSource FromSnapshot(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
        => new(manifests);

    /// <summary>Creates an explicit catalog-free source for isolated runtime construction.</summary>
    internal static SharpLinkGeneratedManifestSource Empty { get; } = new([]);

    /// <summary>Returns a fresh array so no materializer can mutate the frozen source.</summary>
    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateMaterializationSnapshot()
    {
        var snapshot = new ISharpLinkGeneratedAssemblyManifest[_manifests.Count];
        for (var index = 0; index < snapshot.Length; index++)
            snapshot[index] = _manifests[index];
        return snapshot;
    }

    /// <summary>
    /// Performs pure API/Protocol, descriptor-shape, and ownership validation against the frozen
    /// manifest snapshot without creating Codec or adapter resources.
    /// </summary>
    internal void ValidateForPlanCompilation()
    {
        for (var index = 0; index < _manifests.Count; index++)
            SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(_manifests[index]);
    }
}
