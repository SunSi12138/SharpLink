using System.Text;
using System.Linq;
using System.Collections.Frozen;

namespace SharpLink.Runtime;

internal sealed class RpcCodecProvider : IRpcCodecProvider, IDisposable
{
    private readonly ConcurrentDictionary<Type, ResolvedCodec> _resolvedCodecs = new();
    private GeneratedRegistrationSnapshot _generatedRegistrationSnapshot;
    private int _disposed;
    private readonly ColdMetadataPreparation _metadata;
    private readonly bool _ownsMetadata;

    internal RpcCodecProvider(ColdMetadataPreparation? preparation = null)
    {
        _ownsMetadata = preparation is null;
        _metadata = preparation ?? new ColdMetadataAuthority().CreateDomain().BeginPreparation();
        if (_ownsMetadata) _metadata.Complete();
        _metadata.ReserveCollection(0);
        _generatedRegistrationSnapshot = new GeneratedRegistrationSnapshot(
            new Dictionary<Type, RpcGeneratedCodecRegistration>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IRpcCodec<T> GetCodec<T>()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var targetType = typeof(T);
        if (_resolvedCodecs.TryGetValue(targetType, out var fastCached))
        {
            var fastSnapshot = Volatile.Read(ref _generatedRegistrationSnapshot);
            if (ReferenceEquals(fastCached.SnapshotIdentity, fastSnapshot.Identity))
                return Cast<T>(fastCached.Codec);
        }

        var snapshot = Volatile.Read(ref _generatedRegistrationSnapshot);
        if (!snapshot.Registrations.ContainsKey(targetType) && SharedRpcCodec<T>.Instance is { } shared)
            return shared;

        return ResolveCodec<T>(targetType);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IRpcCodec<T> ResolveCodec<T>(Type targetType)
    {
        while (true)
        {
            ThrowIfDisposed();
            var snapshot = Volatile.Read(ref _generatedRegistrationSnapshot);
            snapshot.Registrations.TryGetValue(targetType, out var currentRegistration);

            if (_resolvedCodecs.TryGetValue(targetType, out var cached))
            {
                if (ReferenceEquals(cached.SnapshotIdentity, snapshot.Identity))
                    return Cast<T>(cached.Codec);

                if (currentRegistration is not null &&
                    ReferenceEquals(cached.Registration, currentRegistration))
                {
                    var refreshed = cached.WithSnapshot(snapshot.Identity);
                    if (_resolvedCodecs.TryUpdate(targetType, refreshed, cached))
                    {
                        if (ReferenceEquals(
                            Volatile.Read(ref _generatedRegistrationSnapshot), snapshot))
                        {
                            return Cast<T>(refreshed.Codec);
                        }
                        RemoveResolvedCodec(targetType, refreshed);
                    }
                    continue;
                }

                if (currentRegistration is not null)
                {
                    var replacement = new ResolvedCodec(
                        currentRegistration.GetCodec(),
                        currentRegistration,
                        snapshot.Identity);
                    ThrowIfDisposed();
                    if (!IsCurrentRegistration(targetType, currentRegistration))
                        continue;
                    if (_resolvedCodecs.TryUpdate(targetType, replacement, cached))
                    {
                        if (IsCurrentRegistration(targetType, currentRegistration))
                            return Cast<T>(replacement.Codec);
                        RemoveResolvedCodec(targetType, replacement);
                    }
                    continue;
                }

                ((ICollection<KeyValuePair<Type, ResolvedCodec>>)_resolvedCodecs)
                    .Remove(new KeyValuePair<Type, ResolvedCodec>(targetType, cached));
                continue;
            }

            if (currentRegistration is not null)
            {
                var generated = new ResolvedCodec(
                    currentRegistration.GetCodec(),
                    currentRegistration,
                    snapshot.Identity);
                ThrowIfDisposed();
                if (!IsCurrentRegistration(targetType, currentRegistration))
                    continue;
                var selected = _resolvedCodecs.GetOrAdd(targetType, generated);
                RemoveCandidateAndThrowIfDisposed(targetType, generated, selected);
                if (ReferenceEquals(selected.Registration, currentRegistration) &&
                    IsCurrentRegistration(targetType, currentRegistration))
                {
                    return Cast<T>(selected.Codec);
                }
                if (ReferenceEquals(selected, generated))
                    RemoveResolvedCodec(targetType, generated);
                continue;
            }

            if (targetType.IsEnum)
                return EnumCodec<T>.Instance;
            if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                throw new PlatformNotSupportedException(
                    $"UnsafeBlit Codec for '{targetType.FullName}' requires source-generated ABI metadata. " +
                    "Use a generated RPC contract or register an explicit Codec/Adapter.");
            }

            throw new NotSupportedException(
                $"Codec for '{targetType.FullName}' was not registered in this SharpLink runtime context.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private bool IsCurrentRegistration(
        Type targetType,
        RpcGeneratedCodecRegistration? registration)
    {
        var snapshot = Volatile.Read(ref _generatedRegistrationSnapshot);
        snapshot.Registrations.TryGetValue(targetType, out var current);
        return ReferenceEquals(current, registration);
    }

    private void RemoveResolvedCodec(Type targetType, ResolvedCodec codec)
        => ((ICollection<KeyValuePair<Type, ResolvedCodec>>)_resolvedCodecs)
            .Remove(new KeyValuePair<Type, ResolvedCodec>(targetType, codec));

    private void RemoveCandidateAndThrowIfDisposed(
        Type targetType,
        ResolvedCodec candidate,
        ResolvedCodec selected)
    {
        if (Volatile.Read(ref _disposed) == 0)
            return;
        if (ReferenceEquals(candidate, selected))
            RemoveResolvedCodec(targetType, candidate);
        ObjectDisposedException.ThrowIf(true, this);
    }

    private static IRpcCodec<T> Cast<T>(IRpcCodec codec)
        => codec as IRpcCodec<T> ?? throw new InvalidOperationException(
            $"The codec registered for '{typeof(T).FullName}' implements an incompatible codec interface.");

    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> CreateGeneratedRegistrationSnapshot()
        => Volatile.Read(ref _generatedRegistrationSnapshot).Registrations;

    internal GeneratedRegistrationSnapshot CaptureGeneratedRegistrationState()
        => Volatile.Read(ref _generatedRegistrationSnapshot);

    internal void PublishGeneratedRegistrations(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
        => CommitGeneratedRegistrations(PrepareGeneratedRegistrations(registrations));

    internal PreparedGeneratedRegistrations PrepareGeneratedRegistrations(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(registrations);
        var expected = Volatile.Read(ref _generatedRegistrationSnapshot);
        // Caller-owned dictionaries never become the live lookup map. Enumerate/copy only here,
        // before the Runtime/Server publication lock; commit sees a framework-owned frozen map.
        var count = registrations.Count;
        if (count < 0) throw ColdMetadataException.Invalid("Codec map count is negative.");
        using var temporary = _metadata.ReserveCollection(count);
        var copy = new Dictionary<Type, RpcGeneratedCodecRegistration>(count, ReferenceEqualityComparer.Instance);
        try
        {
            foreach (var pair in registrations)
            {
                if (copy.Count >= count || pair.Key is null || pair.Value is null || !copy.TryAdd(pair.Key, pair.Value))
                    throw ColdMetadataException.Invalid("Codec map enumeration is inconsistent or duplicated.");
            }
            if (copy.Count != count || registrations.Count != count)
                throw ColdMetadataException.Invalid("Codec map count changed during capture.");
            // SOURCE GATE: retired provider maps still lack exact reader ownership. This charge
            // conservatively survives to provider/root disposal; it is NOT accepted v2 reclamation.
            // Do not add per-RPC refcounting to GetCodec as a shortcut.
            _metadata.ReserveCollection(count);
            return new PreparedGeneratedRegistrations(this, expected,
                new GeneratedRegistrationSnapshot(copy.ToFrozenDictionary<Type, RpcGeneratedCodecRegistration>(ReferenceEqualityComparer.Instance)));
        }
        finally { copy.Clear(); }
    }

    internal bool IsCurrentGeneratedRegistrations(PreparedGeneratedRegistrations publication)
        => Volatile.Read(ref _disposed) == 0 && ReferenceEquals(publication.Owner, this) &&
           ReferenceEquals(Volatile.Read(ref _generatedRegistrationSnapshot), publication.Expected);

    internal void CommitGeneratedRegistrations(PreparedGeneratedRegistrations publication)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ReferenceEquals(publication.Owner, this) ||
            !ReferenceEquals(Interlocked.CompareExchange(ref _generatedRegistrationSnapshot,
                publication.Snapshot, publication.Expected), publication.Expected))
            throw new InvalidOperationException("Prepared Codec publication is stale or belongs to another provider.");
        foreach (var pair in publication.Snapshot.Registrations)
        {
            while (_resolvedCodecs.TryGetValue(pair.Key, out var cached) &&
                   !ReferenceEquals(cached.Registration, pair.Value))
            {
                if (((ICollection<KeyValuePair<Type, ResolvedCodec>>)_resolvedCodecs)
                    .Remove(new KeyValuePair<Type, ResolvedCodec>(pair.Key, cached)))
                {
                    break;
                }
            }
        }
    }

    internal void RemoveResolvedCodecs(RpcGeneratedManifestRegistration owner)
    {
        foreach (var pair in _resolvedCodecs)
        {
            if (ReferenceEquals(pair.Value.Registration?.Owner, owner))
            {
                ((ICollection<KeyValuePair<Type, ResolvedCodec>>)_resolvedCodecs)
                    .Remove(new KeyValuePair<Type, ResolvedCodec>(pair.Key, pair.Value));
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Volatile.Write(
            ref _generatedRegistrationSnapshot,
            GeneratedRegistrationSnapshot.Empty);
        _resolvedCodecs.Clear();
        if (_ownsMetadata) _metadata.Dispose();
    }

    private sealed class ResolvedCodec(
        IRpcCodec codec,
        RpcGeneratedCodecRegistration? registration,
        object? snapshotIdentity)
    {
        internal IRpcCodec Codec { get; } = codec;
        internal RpcGeneratedCodecRegistration? Registration { get; } = registration;
        internal object? SnapshotIdentity { get; } = snapshotIdentity;

        internal ResolvedCodec WithSnapshot(object snapshotIdentity)
            => new(Codec, Registration, snapshotIdentity);
    }

    internal sealed class PreparedGeneratedRegistrations(
        RpcCodecProvider owner, GeneratedRegistrationSnapshot expected, GeneratedRegistrationSnapshot snapshot)
    {
        internal RpcCodecProvider Owner { get; } = owner;
        internal GeneratedRegistrationSnapshot Expected { get; } = expected;
        internal GeneratedRegistrationSnapshot Snapshot { get; } = snapshot;
        internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Registrations => Snapshot.Registrations;
    }

    internal sealed class GeneratedRegistrationSnapshot(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        internal static GeneratedRegistrationSnapshot Empty { get; } = new(FrozenDictionary<Type, RpcGeneratedCodecRegistration>.Empty);
        internal object Identity { get; } = new();
        internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Registrations { get; } = registrations;
    }
}

internal sealed class RpcGeneratedManifestRegistration : IDisposable
{
    private readonly IRpcCodecAdapterScope[] _scopes;
    private IDisposable? _metadataLease;
    private readonly ColdMetadataPreparation _callbackOwner;
    private IRpcCodecProvider? _contractCodecProvider;
    private int _disposed;

    private RpcGeneratedManifestRegistration(
        ISharpLinkGeneratedAssemblyManifest manifest,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> contractCodecs,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> codecs,
        IRpcCodecProvider baseProvider,
        IRpcCodecAdapterScope[] scopes,
        IDisposable metadataLease,
        ColdMetadataPreparation callbackOwner)
    {
        _metadataLease = metadataLease;
        _callbackOwner = callbackOwner;
        Manifest = manifest;
        OwnerAssembly = manifest.OwnerAssembly ?? throw new InvalidOperationException("Generated manifest owner is missing.");
        ContractCodecs = contractCodecs;
        Codecs = codecs;
        BaseProvider = baseProvider;
        _scopes = scopes;
    }

    internal ISharpLinkGeneratedAssemblyManifest Manifest { get; }
    // Captured during private preparation, never invoke a manifest getter during adoption/removal.
    internal System.Reflection.Assembly OwnerAssembly { get; }

    /// <summary>Gets Codec bindings visible only to RPC Contracts owned by this manifest.</summary>
    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> ContractCodecs { get; }

    /// <summary>Gets only Codecs that may participate in the context-global generated registry.</summary>
    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs { get; }

    internal IRpcCodecProvider BaseProvider { get; }

    internal bool HasContractCodecs => ContractCodecs.Count != 0;

    internal IRpcCodecProvider ContractCodecProvider
    {
        get
        {
            ThrowIfDisposed();
            var existing = Volatile.Read(ref _contractCodecProvider);
            if (existing is not null)
                return existing;
            var created = new RpcManifestCodecProvider(
                this,
                BaseProvider,
                RpcGeneratedCodecResolutionScope.Contract);
            return Interlocked.CompareExchange(ref _contractCodecProvider, created, null) ?? created;
        }
    }

    internal void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal static RpcGeneratedManifestRegistration Create(
        PreparedGeneratedManifest manifest,
        IRpcCodecProvider provider,
        ColdMetadataPreparation callbackOwner)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);

        var preparation = manifest.Preparation;
        var budget = manifest.RegistrationScope;
        var count = checked(manifest.Codecs.Count + manifest.ContractCodecs.Count);
        using var scopesCharge = preparation.ReserveCollection(count, budget);
        using var adapterMapCharge = preparation.ReserveCollection(count, budget);
        using var combinedCharge = preparation.ReserveCollection(count, budget);
        var scopes = new List<IRpcCodecAdapterScope>(count);
        IDisposable? metadataLease = preparation.Retain();
        var scopeByAdapterId = new Dictionary<string, AdapterScopeRegistration>(StringComparer.Ordinal);
        var allFactories = new IRpcGeneratedCodecFactory[count];
        try
        {
            for (var index = 0; index < manifest.Codecs.Count; index++) allFactories[index] = manifest.Codecs[index];
            for (var index = 0; index < manifest.ContractCodecs.Count; index++) allFactories[manifest.Codecs.Count + index] = manifest.ContractCodecs[index];
            Array.Sort(allFactories, static (left, right) =>
            {
                var order = StringComparer.Ordinal.Compare(left.AdapterId, right.AdapterId);
                return order != 0 ? order : StringComparer.Ordinal.Compare(left.TargetType.FullName, right.TargetType.FullName);
            });
            foreach (var factory in allFactories)
            {
                ValidateFactory(factory);
                if (factory.AdapterId is null)
                    continue;

                var adapter = factory.Adapter!;
                ValidateAdapter(factory, adapter);
                if (scopeByAdapterId.TryGetValue(factory.AdapterId, out var existing))
                {
                    if (existing.Adapter.GetType() != adapter.GetType())
                    {
                        throw new InvalidOperationException(
                            $"Adapter '{factory.AdapterId}' has inconsistent implementations in manifest '{manifest.OwnerAssembly.FullName}'.");
                    }
                    continue;
                }

                var scope = adapter.CreateScope() ?? throw new InvalidOperationException(
                    $"Adapter '{factory.AdapterId}' returned a null scope.");
                scopes.Add(scope);
                scopeByAdapterId.Add(factory.AdapterId,
                    new AdapterScopeRegistration(adapter, scope));
            }

            var ownerBox = new OwnerBox();
            var publishedCodecs = CreateRegistrations(
                manifest.Codecs,
                RpcGeneratedCodecResolutionScope.Global);
            var contractCodecs = CreateRegistrations(
                manifest.ContractCodecs,
                RpcGeneratedCodecResolutionScope.Contract);
            preparation.ReserveCollection(scopes.Count, budget);
            var registration = new RpcGeneratedManifestRegistration(
                manifest,
                contractCodecs,
                publishedCodecs,
                provider,
                [.. scopes], metadataLease!, callbackOwner);
            ownerBox.Value = registration;

            foreach (var codecRegistration in publishedCodecs.Values)
                codecRegistration.PrepareAdapterCodec();
            foreach (var codecRegistration in contractCodecs.Values)
                codecRegistration.PrepareAdapterCodec();

            metadataLease = null;
            return registration;

            Dictionary<Type, RpcGeneratedCodecRegistration> CreateRegistrations(
                IReadOnlyList<IRpcGeneratedCodecFactory> factories,
                RpcGeneratedCodecResolutionScope resolutionScope)
            {
                preparation.ReserveCollection(factories.Count, budget);
                using var sortedCharge = preparation.ReserveCollection(factories.Count, budget);
                var registrations = new Dictionary<Type, RpcGeneratedCodecRegistration>(ReferenceEqualityComparer.Instance);
                var ordered = new IRpcGeneratedCodecFactory[factories.Count];
                try
                {
                    for (var index = 0; index < ordered.Length; index++) ordered[index] = factories[index];
                    Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.TargetType.FullName, right.TargetType.FullName));
                    foreach (var factory in ordered)
                    {
                        var adapterScope = factory.AdapterId is null
                            ? null
                            : scopeByAdapterId[factory.AdapterId].Scope;
                        var targetType = factory.TargetType;
                        if (!registrations.TryAdd(
                                targetType,
                                new RpcGeneratedCodecRegistration(
                                    ownerBox,
                                    factory,
                                    targetType,
                                    adapterScope,
                                    resolutionScope)))
                        {
                            throw new InvalidOperationException(
                                $"Manifest '{manifest.OwnerAssembly.FullName}' contains duplicate Codec target '{factory.TargetType.FullName}' in one binding scope.");
                        }
                    }
                    return registrations;
                }
                finally { Array.Clear(ordered); }
            }
        }
        catch (Exception preparationException)
        {
            List<Exception>? cleanupFailures = null;
            for (var index = scopes.Count - 1; index >= 0; index--)
            {
                try
                {
                    scopes[index].Dispose();
                }
                catch (Exception cleanupException)
                {
                    (cleanupFailures ??= []).Add(cleanupException);
                }
            }
            scopes.Clear();
            metadataLease?.Dispose();
            if (cleanupFailures is null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationException).Throw();
            cleanupFailures!.Insert(0, preparationException);
            throw new AggregateException(cleanupFailures);
        }
        finally
        {
            Array.Clear(allFactories);
            scopeByAdapterId.Clear();
            scopes.Clear();
        }
    }

    private static void ValidateFactory(IRpcGeneratedCodecFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(factory.TargetType);
        if (factory.CodecHash.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Generated Codec factory for '{factory.TargetType.FullName}' has no deterministic CodecHash.");
        }

        var hasAdapterId = factory.AdapterId is not null;
        var hasAdapter = factory.Adapter is not null;
        if (hasAdapterId != hasAdapter ||
            (hasAdapterId && string.IsNullOrWhiteSpace(factory.AdapterId)))
        {
            throw new InvalidOperationException(
                $"Generated Codec factory for '{factory.TargetType.FullName}' has inconsistent adapter metadata.");
        }
    }

    private static void ValidateAdapter(IRpcGeneratedCodecFactory factory, IRpcCodecAdapter adapter)
    {
        var actualId = factory is PreparedCodecFactory prepared ? prepared.CapturedAdapterId : adapter.AdapterId;
        if (!string.Equals(actualId, factory.AdapterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Codec adapter '{adapter.GetType().FullName}' lifecycle identity does not match its generated registration metadata.");
        }
    }



    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        using var callback = _callbackOwner.EnterCallback();
        List<Exception>? failures = null;
        for (var index = _scopes.Length - 1; index >= 0; index--)
        {
            try
            {
                _scopes[index].Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        Array.Clear(_scopes);
        Interlocked.Exchange(ref _metadataLease, null)?.Dispose();
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private sealed record AdapterScopeRegistration(
        IRpcCodecAdapter Adapter,
        IRpcCodecAdapterScope Scope);

    internal sealed class OwnerBox
    {
        internal RpcGeneratedManifestRegistration Value { get; set; } = null!;
    }
}

internal sealed class RpcGeneratedCodecRegistration
{
    private readonly RpcGeneratedManifestRegistration.OwnerBox _owner;
    private readonly IRpcCodecAdapterScope? _adapterScope;
    private readonly RpcGeneratedCodecResolutionScope _resolutionScope;
    private IRpcCodec? _preparedCodec;

    internal RpcGeneratedCodecRegistration(
        RpcGeneratedManifestRegistration.OwnerBox owner,
        IRpcGeneratedCodecFactory factory,
        Type targetType,
        IRpcCodecAdapterScope? adapterScope,
        RpcGeneratedCodecResolutionScope resolutionScope)
    {
        _owner = owner;
        Factory = factory;
        TargetType = targetType;
        UnsafeBlitRequirement = (factory as IRpcGeneratedUnsafeBlitCodecFactory)?.Requirement;
        _adapterScope = adapterScope;
        _resolutionScope = resolutionScope;
    }

    internal RpcGeneratedManifestRegistration Owner => _owner.Value;
    internal IRpcGeneratedCodecFactory Factory { get; }
    internal IRpcGeneratedCodecFactory FactoryIdentity => Factory is PreparedCodecFactory prepared ? prepared.Original : Factory;
    internal Type TargetType { get; }
    internal SharpLinkGeneratedUnsafeBlitRequirement? UnsafeBlitRequirement { get; }

    internal void PrepareAdapterCodec()
    {
        if (_adapterScope is null)
            return;
        Owner.ThrowIfDisposed();
        var codec = Factory.Create(GetOwnerProvider(), _adapterScope) ?? throw new InvalidOperationException(
            $"Generated Codec factory for '{Factory.TargetType.FullName}' returned null.");
        ValidateCodec(codec);
        Volatile.Write(ref _preparedCodec, codec);
    }

    internal IRpcCodec GetCodec()
    {
        Owner.ThrowIfDisposed();
        var codec = Volatile.Read(ref _preparedCodec);
        if (codec is null)
        {
            codec = Factory.Create(GetOwnerProvider(), adapterScope: null) ?? throw new InvalidOperationException(
                $"Generated Codec factory for '{Factory.TargetType.FullName}' returned null.");
            ValidateCodec(codec);
        }
        Owner.ThrowIfDisposed();
        return codec;
    }

    private IRpcCodecProvider GetOwnerProvider()
        => RpcGeneratedCodecResolver.GetProvider(Owner, _resolutionScope);

    private void ValidateCodec(IRpcCodec codec)
    {
        if (!Factory.IsCompatibleCodec(codec))
        {
            throw new InvalidOperationException(
                $"Codec returned for '{Factory.TargetType.FullName}' implements an incompatible IRpcCodec<T>.");
        }
    }
}

internal static class SharedRpcCodec<T>
{
    public static readonly IRpcCodec<T>? Instance = Create();

    private static IRpcCodec<T>? Create()
    {
        if (BuiltinRpcCodecs.TryGet(typeof(T), out var builtin))
            return (IRpcCodec<T>)builtin;
        return null;
    }
}

internal static class BuiltinRpcCodecs
{
    private static readonly IReadOnlyDictionary<Type, IRpcCodec> Codecs = Create();

    public static bool TryGet(Type type, out IRpcCodec codec) => Codecs.TryGetValue(type, out codec!);

    private static IReadOnlyDictionary<Type, IRpcCodec> Create()
    {
        var codecs = new Dictionary<Type, IRpcCodec>();
        Add(codecs, BoolCodec.Instance); Add(codecs, NullableBoolCodec.Instance);
        Add(codecs, ByteCodec.Instance); Add(codecs, NullableByteCodec.Instance);
        Add(codecs, SByteCodec.Instance); Add(codecs, NullableSByteCodec.Instance);
        Add(codecs, Int16Codec.Instance); Add(codecs, NullableInt16Codec.Instance);
        Add(codecs, UInt16Codec.Instance); Add(codecs, NullableUInt16Codec.Instance);
        Add(codecs, CharCodec.Instance); Add(codecs, NullableCharCodec.Instance);
        Add(codecs, HalfCodec.Instance); Add(codecs, NullableHalfCodec.Instance);
        Add(codecs, Int32Codec.Instance); Add(codecs, NullableInt32Codec.Instance);
        Add(codecs, UInt32Codec.Instance); Add(codecs, NullableUInt32Codec.Instance);
        Add(codecs, FloatCodec.Instance); Add(codecs, NullableFloatCodec.Instance);
        Add(codecs, RuneCodec.Instance); Add(codecs, NullableRuneCodec.Instance);
        Add(codecs, IndexCodec.Instance); Add(codecs, NullableIndexCodec.Instance);
        Add(codecs, Int64Codec.Instance); Add(codecs, NullableInt64Codec.Instance);
        Add(codecs, UInt64Codec.Instance); Add(codecs, NullableUInt64Codec.Instance);
        Add(codecs, DoubleCodec.Instance); Add(codecs, NullableDoubleCodec.Instance);
        Add(codecs, RangeCodec.Instance); Add(codecs, NullableRangeCodec.Instance);
        Add(codecs, Int128Codec.Instance); Add(codecs, NullableInt128Codec.Instance);
        Add(codecs, UInt128Codec.Instance); Add(codecs, NullableUInt128Codec.Instance);
        Add(codecs, GuidCodec.Instance); Add(codecs, NullableGuidCodec.Instance);
        Add(codecs, DecimalCodec.Instance); Add(codecs, NullableDecimalCodec.Instance);
        Add(codecs, DateTimeCodec.Instance); Add(codecs, NullableDateTimeCodec.Instance);
        Add(codecs, DateTimeOffsetCodec.Instance); Add(codecs, NullableDateTimeOffsetCodec.Instance);
        Add(codecs, DateOnlyCodec.Instance); Add(codecs, NullableDateOnlyCodec.Instance);
        Add(codecs, TimeOnlyCodec.Instance); Add(codecs, NullableTimeOnlyCodec.Instance);
        Add(codecs, TimeSpanCodec.Instance); Add(codecs, NullableTimeSpanCodec.Instance);
        Add(codecs, StringCodec.Instance);

        AddBlitCollections<bool>(codecs); AddBlitCollections<byte>(codecs);
        AddBlitCollections<sbyte>(codecs); AddBlitCollections<short>(codecs);
        AddBlitCollections<ushort>(codecs); AddBlitCollections<char>(codecs);
        AddBlitCollections<Half>(codecs); AddBlitCollections<int>(codecs);
        AddBlitCollections<uint>(codecs); AddBlitCollections<float>(codecs);
        AddBlitCollections<Rune>(codecs); AddBlitCollections<long>(codecs);
        AddBlitCollections<ulong>(codecs); AddBlitCollections<double>(codecs);
        AddBlitCollections<Guid>(codecs); AddBlitCollections<decimal>(codecs);
        Add(codecs, DateTimeOffsetArrayCodec.Instance);
        Add(codecs, DateTimeOffsetListCodec.Instance);
        Add(codecs, DateTimeOffsetMemoryCodec.Instance);
        Add(codecs, DateTimeOffsetReadOnlyMemoryCodec.Instance);
        Add(codecs, DateTimeOffsetImmutableArrayCodec.Instance);
        AddBlitCollections<DateTime>(codecs);
        AddBlitCollections<DateOnly>(codecs); AddBlitCollections<TimeOnly>(codecs);
        AddBlitCollections<TimeSpan>(codecs); AddBlitCollections<Int128>(codecs);
        AddBlitCollections<UInt128>(codecs); AddBlitCollections<Index>(codecs);
        AddBlitCollections<Range>(codecs);
        return codecs;
    }

    private static void Add<T>(Dictionary<Type, IRpcCodec> codecs, IRpcCodec<T> codec)
        => codecs.Add(typeof(T), codec);

    private static void AddBlitCollections<T>(Dictionary<Type, IRpcCodec> codecs) where T : unmanaged
    {
        Add(codecs, BlitArrayCodec<T>.Instance);
        Add(codecs, BlitImmutableArrayCodec<T>.Instance);
        Add(codecs, BlitListCodec<T>.Instance);
        Add(codecs, BlitMemoryCodec<T>.Instance);
        Add(codecs, BlitReadOnlyMemoryCodec<T>.Instance);
    }
}
