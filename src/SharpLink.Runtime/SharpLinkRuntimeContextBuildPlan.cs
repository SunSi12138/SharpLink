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
    private readonly GeneratedManifestSnapshot _generatedManifests;

    internal SharpLinkRuntimeContextBuildPlan(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        TimeProvider timeProvider,
        GeneratedManifestSnapshot generatedManifests)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _concurrency = concurrency ?? throw new ArgumentNullException(nameof(concurrency));
        _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _generatedManifests = generatedManifests ?? throw new ArgumentNullException(nameof(generatedManifests));
    }

    /// <summary>Gets the frozen performance profile required by client construction planning.</summary>
    internal SharpLinkPerformanceProfile PerformanceProfile => _options.PerformanceProfile;

    /// <summary>Gets the application-owned time source. The runtime Context never disposes it.</summary>
    internal TimeProvider TimeProvider { get; }

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
            _generatedManifests.Manifests);
}
