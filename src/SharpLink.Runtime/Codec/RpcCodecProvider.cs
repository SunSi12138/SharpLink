using System.Text;
using System.Linq;

namespace SharpLink.Runtime;

internal sealed class RpcCodecProvider : IRpcCodecProvider, IDisposable
{
    private readonly Func<Type, IRpcCodec?>? _resolver;
    private readonly ConcurrentDictionary<Type, ResolvedCodec> _resolvedCodecs = new();
    private readonly HashSet<Type> _explicitCodecTypes;
    private GeneratedRegistrationSnapshot _generatedRegistrationSnapshot;
    private int _disposed;

    internal RpcCodecProvider(
        Func<Type, IRpcCodec?>? resolver,
        IReadOnlyDictionary<Type, IRpcCodec> explicitCodecs)
    {
        _resolver = resolver;
        foreach (var pair in explicitCodecs)
            _resolvedCodecs.TryAdd(pair.Key, ResolvedCodec.Explicit(pair.Value));
        _explicitCodecTypes = [.. explicitCodecs.Keys];
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
            if (fastCached.IsExplicit)
                return Cast<T>(fastCached.Codec);
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
                if (cached.IsExplicit ||
                    ReferenceEquals(cached.SnapshotIdentity, snapshot.Identity))
                    return Cast<T>(cached.Codec);

                if (ReferenceEquals(cached.Registration, currentRegistration))
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
                        currentRegistration.GetCodec(this),
                        currentRegistration,
                        snapshot.Identity,
                        isExplicit: false,
                        isFallback: false);
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

                if (cached.IsFallback)
                    return Cast<T>(cached.Codec);
                ((ICollection<KeyValuePair<Type, ResolvedCodec>>)_resolvedCodecs)
                    .Remove(new KeyValuePair<Type, ResolvedCodec>(targetType, cached));
                continue;
            }

            if (currentRegistration is not null)
            {
                var generated = new ResolvedCodec(
                    currentRegistration.GetCodec(this),
                    currentRegistration,
                    snapshot.Identity,
                    isExplicit: false,
                    isFallback: false);
                ThrowIfDisposed();
                if (!IsCurrentRegistration(targetType, currentRegistration))
                    continue;
                var selected = _resolvedCodecs.GetOrAdd(targetType, generated);
                RemoveCandidateAndThrowIfDisposed(targetType, generated, selected);
                if (selected.IsExplicit)
                    return Cast<T>(selected.Codec);
                if (ReferenceEquals(selected.Registration, currentRegistration) &&
                    IsCurrentRegistration(targetType, currentRegistration))
                {
                    return Cast<T>(selected.Codec);
                }
                if (ReferenceEquals(selected, generated))
                    RemoveResolvedCodec(targetType, generated);
                continue;
            }

            var resolved = _resolver?.Invoke(targetType);
            ThrowIfDisposed();
            if (!IsCurrentRegistration(targetType, registration: null))
                continue;
            if (resolved is not null)
            {
                var typed = Cast<T>(resolved);
                var fallback = new ResolvedCodec(
                    typed,
                    registration: null,
                    snapshot.Identity,
                    isExplicit: false,
                    isFallback: true);
                var selected = _resolvedCodecs.GetOrAdd(targetType, fallback);
                RemoveCandidateAndThrowIfDisposed(targetType, fallback, selected);
                if (selected.IsExplicit)
                    return Cast<T>(selected.Codec);
                if (selected.IsFallback && IsCurrentRegistration(targetType, registration: null))
                    return Cast<T>(selected.Codec);
                if (ReferenceEquals(selected, fallback))
                    RemoveResolvedCodec(targetType, fallback);
                continue;
            }

            if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                return UnsafeBlitCodec<T>.Instance;

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

    internal void PublishGeneratedRegistrations(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(
            ref _generatedRegistrationSnapshot,
            new GeneratedRegistrationSnapshot(registrations));
        foreach (var pair in registrations)
        {
            while (_resolvedCodecs.TryGetValue(pair.Key, out var cached) &&
                   !cached.IsExplicit && !ReferenceEquals(cached.Registration, pair.Value))
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
            if (!_explicitCodecTypes.Contains(pair.Key) &&
                ReferenceEquals(pair.Value.Registration?.Owner, owner))
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
            new GeneratedRegistrationSnapshot(
                new Dictionary<Type, RpcGeneratedCodecRegistration>()));
        _resolvedCodecs.Clear();
    }

    private sealed class ResolvedCodec(
        IRpcCodec codec,
        RpcGeneratedCodecRegistration? registration,
        object? snapshotIdentity,
        bool isExplicit,
        bool isFallback)
    {
        internal IRpcCodec Codec { get; } = codec;
        internal RpcGeneratedCodecRegistration? Registration { get; } = registration;
        internal object? SnapshotIdentity { get; } = snapshotIdentity;
        internal bool IsExplicit { get; } = isExplicit;
        internal bool IsFallback { get; } = isFallback;

        internal static ResolvedCodec Explicit(IRpcCodec codec)
            => new(codec, null, snapshotIdentity: null, isExplicit: true, isFallback: false);

        internal ResolvedCodec WithSnapshot(object snapshotIdentity)
            => new(Codec, Registration, snapshotIdentity, IsExplicit, IsFallback);
    }

    private sealed class GeneratedRegistrationSnapshot(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        internal object Identity { get; } = new();
        internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Registrations { get; } =
            registrations;
    }
}

internal sealed class RpcGeneratedManifestRegistration : IDisposable
{
    private readonly IRpcCodecAdapterScope[] _scopes;
    private IRpcCodecProvider? _contractCodecProvider;
    private int _disposed;

    private RpcGeneratedManifestRegistration(
        ISharpLinkGeneratedAssemblyManifest manifest,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> allCodecs,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> codecs,
        IRpcCodecProvider baseProvider,
        IRpcCodecAdapterScope[] scopes)
    {
        Manifest = manifest;
        AllCodecs = allCodecs;
        Codecs = codecs;
        BaseProvider = baseProvider;
        _scopes = scopes;
    }

    internal ISharpLinkGeneratedAssemblyManifest Manifest { get; }

    /// <summary>Gets every Codec owned by this manifest, including assembly-routed targets.</summary>
    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> AllCodecs { get; }

    /// <summary>Gets only Codecs that may participate in the context-global generated registry.</summary>
    internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs { get; }

    internal IRpcCodecProvider BaseProvider { get; }

    internal bool HasManifestScopedCodecs => Manifest.ManifestScopedCodecTargets.Count != 0;

    internal IRpcCodecProvider ContractCodecProvider
    {
        get
        {
            if (!HasManifestScopedCodecs)
                return BaseProvider;
            var existing = Volatile.Read(ref _contractCodecProvider);
            if (existing is not null)
                return existing;
            var created = new RpcManifestCodecProvider(this, BaseProvider);
            return Interlocked.CompareExchange(ref _contractCodecProvider, created, null) ?? created;
        }
    }

    internal static RpcGeneratedManifestRegistration Create(
        ISharpLinkGeneratedAssemblyManifest manifest,
        IRpcCodecProvider provider)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);

        var scopes = new List<IRpcCodecAdapterScope>();
        try
        {
            var scopeByAdapterId = new Dictionary<string, AdapterScopeRegistration>(StringComparer.Ordinal);
            foreach (var factory in manifest.Codecs.OrderBy(static factory => factory.AdapterId, StringComparer.Ordinal)
                         .ThenBy(static factory => factory.WireFormatId, StringComparer.Ordinal)
                         .ThenBy(static factory => factory.TargetType.FullName, StringComparer.Ordinal))
            {
                ValidateFactory(factory);
                if (factory.AdapterId is null)
                    continue;

                var adapter = factory.Adapter!;
                ValidateAdapter(factory, adapter);
                if (scopeByAdapterId.TryGetValue(factory.AdapterId, out var existing))
                {
                    if (existing.Adapter.GetType() != adapter.GetType() ||
                        !string.Equals(existing.WireFormatId, factory.WireFormatId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Adapter '{factory.AdapterId}' has inconsistent implementation or wire-format metadata in manifest '{manifest.OwnerAssembly.FullName}'.");
                    }
                    continue;
                }

                var scope = adapter.CreateScope() ?? throw new InvalidOperationException(
                    $"Adapter '{factory.AdapterId}' returned a null scope.");
                scopes.Add(scope);
                scopeByAdapterId.Add(factory.AdapterId,
                    new AdapterScopeRegistration(adapter, factory.WireFormatId, scope));
            }

            var ownerBox = new OwnerBox();
            var allCodecs = new Dictionary<Type, RpcGeneratedCodecRegistration>();
            foreach (var factory in manifest.Codecs.OrderBy(static factory => factory.TargetType.FullName, StringComparer.Ordinal))
            {
                IRpcCodec? preparedCodec = null;
                if (factory.AdapterId is not null)
                {
                    var scope = scopeByAdapterId[factory.AdapterId].Scope;
                    preparedCodec = factory.Create(provider, scope) ?? throw new InvalidOperationException(
                        $"Generated Codec factory for '{factory.TargetType.FullName}' returned null.");
                    ValidateCodec(factory, preparedCodec);
                }
                if (allCodecs.ContainsKey(factory.TargetType))
                    throw new InvalidOperationException(
                        $"Manifest '{manifest.OwnerAssembly.FullName}' contains duplicate Codec target '{factory.TargetType.FullName}'.");
                allCodecs.Add(factory.TargetType, new RpcGeneratedCodecRegistration(
                    ownerBox, factory, preparedCodec));
            }

            var manifestScopedTargets = new HashSet<Type>(manifest.ManifestScopedCodecTargets);
            var publishedCodecs = allCodecs
                .Where(pair => !manifestScopedTargets.Contains(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            var registration = new RpcGeneratedManifestRegistration(
                manifest,
                allCodecs,
                publishedCodecs,
                provider,
                [.. scopes]);
            ownerBox.Value = registration;
            return registration;
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
            if (cleanupFailures is null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationException).Throw();
            cleanupFailures!.Insert(0, preparationException);
            throw new AggregateException(cleanupFailures);
        }
    }

    private static void ValidateFactory(IRpcGeneratedCodecFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(factory.TargetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(factory.SchemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(factory.WireFormatId);
        if (factory.AdapterId is null)
        {
            if (factory.Adapter is not null ||
                !string.Equals(factory.WireFormatId, "sharplink-native/v1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Native Codec factory for '{factory.TargetType.FullName}' has invalid adapter metadata.");
            }
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(factory.AdapterId);
        if (factory.Adapter is null)
            throw new InvalidOperationException(
                $"Adapter Codec factory for '{factory.TargetType.FullName}' has no adapter instance.");
    }

    private static void ValidateAdapter(IRpcGeneratedCodecFactory factory, IRpcCodecAdapter adapter)
    {
        if (!string.Equals(adapter.AdapterId, factory.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(adapter.WireFormatId, factory.WireFormatId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Codec adapter '{adapter.GetType().FullName}' runtime identity does not match its generated registration metadata.");
        }
    }

    private static void ValidateCodec(IRpcGeneratedCodecFactory factory, IRpcCodec codec)
    {
        if (!factory.IsCompatibleCodec(codec))
            throw new InvalidOperationException(
                $"Codec returned for '{factory.TargetType.FullName}' implements an incompatible IRpcCodec<T>.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
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
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private sealed record AdapterScopeRegistration(
        IRpcCodecAdapter Adapter,
        string WireFormatId,
        IRpcCodecAdapterScope Scope);

    internal sealed class OwnerBox
    {
        internal RpcGeneratedManifestRegistration Value { get; set; } = null!;
    }
}

internal sealed class RpcGeneratedCodecRegistration
{
    private readonly RpcGeneratedManifestRegistration.OwnerBox _owner;
    private readonly IRpcCodec? _preparedCodec;

    internal RpcGeneratedCodecRegistration(
        RpcGeneratedManifestRegistration.OwnerBox owner,
        IRpcGeneratedCodecFactory factory,
        IRpcCodec? preparedCodec)
    {
        _owner = owner;
        Factory = factory;
        _preparedCodec = preparedCodec;
    }

    internal RpcGeneratedManifestRegistration Owner => _owner.Value;
    internal IRpcGeneratedCodecFactory Factory { get; }

    internal IRpcCodec GetCodec(IRpcCodecProvider provider)
        => _preparedCodec ?? Factory.Create(provider, adapterScope: null);
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
