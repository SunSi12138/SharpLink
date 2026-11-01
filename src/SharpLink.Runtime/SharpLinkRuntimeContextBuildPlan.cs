namespace SharpLink.Runtime;

/// <summary>
/// A validated, immutable input snapshot for materializing one runtime Context.
/// The pending plan owns cold metadata reservations until one-shot transfer or abandonment.
/// </summary>
internal sealed class SharpLinkRuntimeContextBuildPlan : IDisposable
{
    private readonly SharpLinkRuntimeOptions _options;
    private readonly RuntimeConcurrencyOptions _concurrency;
    private readonly BufferWriterPoolOptions _bufferPool;
    private GeneratedManifestSnapshot? _generatedManifests;
    private ColdMetadataPreparation? _preparation;
    private int _state;
    internal ColdMetadataPreparation Preparation => _preparation ?? throw new InvalidOperationException("Plan metadata ownership has transferred or ended.");
    internal ColdMetadataAuthority Authority => Domain.Authority;
    internal ColdMetadataDomain Domain { get; }

    internal SharpLinkRuntimeContextBuildPlan(
        SharpLinkRuntimeOptions options,
        RuntimeConcurrencyOptions concurrency,
        BufferWriterPoolOptions bufferPool,
        TimeProvider timeProvider,
        GeneratedManifestSnapshot generatedManifests,
        ColdMetadataPreparation preparation)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _concurrency = concurrency ?? throw new ArgumentNullException(nameof(concurrency));
        _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _generatedManifests = generatedManifests ?? throw new ArgumentNullException(nameof(generatedManifests));
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        Domain = preparation.Domain;
    }

    /// <summary>Gets the frozen performance profile required by client construction planning.</summary>
    internal SharpLinkPerformanceProfile PerformanceProfile => _options.PerformanceProfile;

    /// <summary>Gets the application-owned time source. The runtime Context never disposes it.</summary>
    internal TimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the exact frozen manifest snapshot shared by Runtime, Client, and Server planning.
    /// </summary>
    internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> GeneratedManifests
        => (_generatedManifests ?? throw new InvalidOperationException("Plan metadata ownership has ended.")).Manifests;

    /// <summary>Creates the Context-owned pool, codec provider, and generated registration scopes.</summary>
    internal SharpLinkRuntimeContext Materialize()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("A Runtime plan can materialize only once.");
        var preparation = _preparation!;
        var snapshot = _generatedManifests!;
        using var transaction = new SynchronousBuildTransaction();
        try
        {
            transaction.Own(preparation, static owner => owner.Dispose(),
                SynchronousBuildResourceMetadata.FrameworkOwned("Runtime cold metadata"));
            var context = new SharpLinkRuntimeContext(_options, _concurrency, _bufferPool,
                TimeProvider, snapshot, preparation);
            _generatedManifests = null;
            _preparation = null;
            preparation.Complete();
            transaction.Commit();
            Volatile.Write(ref _state, 2);
            return context;
        }
        catch (Exception exception)
        {
            _generatedManifests = null;
            _preparation = null;
            Volatile.Write(ref _state, 3);
            transaction.Rollback(exception);
            throw new System.Diagnostics.UnreachableException();
        }
    }
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 3, 0) != 0) return;
        _generatedManifests = null;
        Interlocked.Exchange(ref _preparation, null)?.Dispose();
    }
}
