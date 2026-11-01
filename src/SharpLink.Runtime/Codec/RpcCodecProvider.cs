using System.Text;
using System.Linq;
using System.Collections.Frozen;

namespace SharpLink.Runtime;

internal sealed class RpcCodecProvider : IRpcCodecProvider, IDisposable
{
    private readonly Lock _gate = new();
    private ConcurrentDictionary<Type, ResolvedCodec> _resolvedCodecs = new();
    private GeneratedRegistrationSnapshot _snapshot;
    private object _publicationIdentity;
    private int _disposed;
    private readonly ColdMetadataPreparation _metadata;
    private ColdMetadataReservation _cacheHeader;
    private readonly RpcCodecBindingIdentity _sharedIdentity = new(new RpcCodecOwnerIdentity());

    internal RpcCodecProvider(ColdMetadataPreparation? preparation = null)
    {
        _metadata = preparation?.ForkStorage() ?? new ColdMetadataAuthority().CreateDomain().BeginPreparation();
        if (preparation is null) _metadata.Complete();
        try
        {
            _cacheHeader = _metadata.ReserveCollection(0);
            _snapshot = GeneratedRegistrationSnapshot.Create(_metadata, FrozenDictionary<Type, RpcGeneratedCodecRegistration>.Empty);
            _publicationIdentity = _snapshot.Identity;
        }
        catch { _metadata.Dispose(); throw; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IRpcCodec<T> GetCodec<T>()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _resolvedCodecs).TryGetValue(typeof(T), out var cached) &&
            cached.Codec is { } hit && ReferenceEquals(cached.PublicationIdentity, Volatile.Read(ref _publicationIdentity)) &&
            !cached.BindingIdentity.Owner.IsClosed)
            return Cast<T>(hit);
        return ResolveCodec<T>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IRpcCodec<T> ResolveCodec<T>()
    {
        var target = typeof(T);
        while (true)
        {
            using var view = CaptureGeneratedRegistrationState();
            if (!view.Registrations.TryGetValue(target, out var registration))
            {
                IRpcCodec? fallback = SharedRpcCodec<T>.Instance;
                if (fallback is null && target.IsEnum) fallback = EnumCodec<T>.Instance;
                if (fallback is null)
                {
                    if (target.IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                        throw new PlatformNotSupportedException($"UnsafeBlit Codec for '{target.FullName}' requires source-generated ABI metadata. Use a generated RPC contract or register an explicit Codec/Adapter.");
                    throw new NotSupportedException($"Codec for '{target.FullName}' was not registered in this SharpLink runtime context.");
                }
                ResolvedCodec? shared = new(fallback, _sharedIdentity, view.Identity, _metadata.ReserveCollectionSlot());
                try
                {
                    lock (_gate)
                    {
                        ThrowIfDisposed();
                        if (!ReferenceEquals(view.Identity, _publicationIdentity)) continue;
                        var selected = _resolvedCodecs.GetOrAdd(target, shared);
                        if (ReferenceEquals(selected, shared)) shared = null;
                        return Cast<T>(selected.Codec!);
                    }
                }
                finally { shared?.Dispose(); }
            }
            using var use = registration.Owner.AcquireColdUse();
            var codec = registration.GetCodec();
            var candidate = new ResolvedCodec(codec, registration.Identity, view.Identity,
                _metadata.ReserveCollectionSlot(registration.Owner.RegistrationScope));
            try
            {
                lock (_gate)
                {
                    ThrowIfDisposed(); registration.Owner.ThrowIfDisposed();
                    if (!ReferenceEquals(view.Identity, _publicationIdentity)) continue;
                    var selected = _resolvedCodecs.GetOrAdd(target, candidate);
                    if (ReferenceEquals(selected, candidate)) candidate = null!;
                    return Cast<T>(selected.Codec!);
                }
            }
            finally { candidate?.Dispose(); }
        }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    private static IRpcCodec<T> Cast<T>(IRpcCodec codec) => codec as IRpcCodec<T> ?? throw new InvalidOperationException(
        $"The codec registered for '{typeof(T).FullName}' implements an incompatible codec interface.");

    // Internal lexical, nonconcurrent view. No map/registration/enumerator may survive disposal.
    internal GeneratedRegistrationView CaptureGeneratedRegistrationState()
    {
        lock (_gate) { ThrowIfDisposed(); return _snapshot.Acquire(); }
    }
    internal void PublishGeneratedRegistrations(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    { using var publication = PrepareGeneratedRegistrations(registrations); CommitGeneratedRegistrations(publication); }
    internal PreparedGeneratedRegistrations PrepareGeneratedRegistrations(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        object expected;
        lock (_gate) { ThrowIfDisposed(); expected = _publicationIdentity; }
        // User dictionary callbacks run only while freezing, outside publication gates.
        using var callback = RpcCodecColdUse.EnterPreparation(_metadata);
        var snapshot = GeneratedRegistrationSnapshot.Create(_metadata, registrations);
        ColdMetadataReservation? header = null;
        try { header = _metadata.ReserveCollection(0); return new PreparedGeneratedRegistrations(this, expected, snapshot, header); }
        catch { snapshot.Dispose(); header?.Dispose(); throw; }
    }
    internal bool IsCurrentGeneratedRegistrations(PreparedGeneratedRegistrations publication)
    {
        lock (_gate) return _disposed == 0 && ReferenceEquals(publication.Owner, this) &&
            ReferenceEquals(_publicationIdentity, publication.ExpectedIdentity) && publication.IsAvailable;
    }
    internal void CommitGeneratedRegistrations(PreparedGeneratedRegistrations publication)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(publication.Owner, this) || !ReferenceEquals(publication.ExpectedIdentity, _publicationIdentity) || !publication.IsAvailable)
                throw new InvalidOperationException("Prepared Codec publication is stale or belongs to another provider.");
            foreach (var registration in publication.Registrations.Values) registration.Owner.ThrowIfDisposed();
            var next = publication.Take();
            publication.Retire(_snapshot, _resolvedCodecs, _cacheHeader);
            _cacheHeader = publication.TakeCacheHeader();
            _snapshot = next;
            Volatile.Write(ref _resolvedCodecs, publication.EmptyCache);
            // Sole warm-path publication scalar; no immutable map root is loaded by a warm hit.
            Volatile.Write(ref _publicationIdentity, next.Identity);
        }
    }
    internal void RemoveResolvedCodecs(RpcGeneratedManifestRegistration owner)
    {
        // Called only after owner admission closes. A cold filler rechecks under the same cache
        // gate, so it cannot reinstall an entry after this eviction and refund.
        foreach (var pair in _resolvedCodecs)
        {
            ResolvedCodec? removed = null;
            lock (_gate)
                if (ReferenceEquals(pair.Value.BindingIdentity.Owner, owner.Identity) &&
                    ((ICollection<KeyValuePair<Type, ResolvedCodec>>)_resolvedCodecs).Remove(pair)) removed = pair.Value;
            removed?.Dispose();
        }
    }
    public void Dispose()
    {
        GeneratedRegistrationSnapshot old;
        ConcurrentDictionary<Type, ResolvedCodec> cache;
        lock (_gate)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            old = _snapshot; cache = _resolvedCodecs;
            _snapshot = null!; _resolvedCodecs = new();
        }
        ClearCache(cache); _cacheHeader.Dispose(); old.Dispose(); _metadata.Dispose();
    }
    private static void ClearCache(ConcurrentDictionary<Type, ResolvedCodec> cache)
    {
        foreach (var pair in cache) if (cache.TryRemove(pair.Key, out var entry)) entry.Dispose();
    }
    internal sealed class ResolvedCodec(IRpcCodec codec, RpcCodecBindingIdentity binding,
        object publicationIdentity, ColdMetadataReservation charge) : IDisposable
    {
        private IRpcCodec? _codec = codec;
        internal IRpcCodec? Codec => Volatile.Read(ref _codec);
        internal RpcCodecBindingIdentity BindingIdentity { get; } = binding;
        internal object PublicationIdentity { get; } = publicationIdentity;
        public void Dispose() { Interlocked.Exchange(ref _codec, null); charge.Dispose(); }
    }
    internal sealed class PreparedGeneratedRegistrations : IDisposable
    {
        private GeneratedRegistrationSnapshot? _snapshot;
        private GeneratedRegistrationSnapshot? _retired;
        private ConcurrentDictionary<Type, ResolvedCodec>? _retiredCache;
        private ColdMetadataReservation? _cacheHeader;
        private ColdMetadataReservation? _retiredCacheHeader;
        internal RpcCodecProvider Owner { get; }
        internal object ExpectedIdentity { get; }
        internal ConcurrentDictionary<Type, ResolvedCodec> EmptyCache { get; } = new();
        internal PreparedGeneratedRegistrations(RpcCodecProvider owner, object expected, GeneratedRegistrationSnapshot snapshot, ColdMetadataReservation cacheHeader)
        { Owner = owner; ExpectedIdentity = expected; _snapshot = snapshot; _cacheHeader = cacheHeader; }
        internal bool IsAvailable => _snapshot is not null && _cacheHeader is not null;
        internal ColdMetadataReservation TakeCacheHeader() => Interlocked.Exchange(ref _cacheHeader, null)!;
        internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Registrations => _snapshot!.Registrations;
        internal GeneratedRegistrationSnapshot Take() => Interlocked.Exchange(ref _snapshot, null) ?? throw new InvalidOperationException("Publication already consumed.");
        internal void Retire(GeneratedRegistrationSnapshot snapshot, ConcurrentDictionary<Type, ResolvedCodec> cache, ColdMetadataReservation cacheHeader)
        { _retired = snapshot; _retiredCache = cache; _retiredCacheHeader = cacheHeader; }
        public void Dispose()
        {
            var candidateHeader = Interlocked.Exchange(ref _cacheHeader, null);
            if (candidateHeader is not null) { EmptyCache.Clear(); candidateHeader.Dispose(); }
            Interlocked.Exchange(ref _snapshot, null)?.Dispose();
            var cache = Interlocked.Exchange(ref _retiredCache, null);
            if (cache is not null) ClearCache(cache);
            Interlocked.Exchange(ref _retiredCacheHeader, null)?.Dispose();
            Interlocked.Exchange(ref _retired, null)?.Dispose();
        }
    }
    internal sealed class GeneratedRegistrationSnapshot : IDisposable
    {
        private readonly Lock _gate = new();
        private int _references = 1;
        private IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration>? _registrations;
        private IDisposable[]? _metadataLeases;
        private ColdMetadataPreparation? _storage;
        internal object Identity { get; } = new();
        internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Registrations => _registrations!;
        private GeneratedRegistrationSnapshot(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
            IDisposable[] leases, ColdMetadataPreparation storage)
        { _registrations = registrations; _metadataLeases = leases; _storage = storage; }
        internal static GeneratedRegistrationSnapshot Create(ColdMetadataPreparation preparation,
            IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> input)
        {
            var storage = preparation.ForkStorage();
            List<IDisposable>? leases = null;
            Dictionary<Type, RpcGeneratedCodecRegistration>? map = null;
            try
            {
                var count = input.Count;
                using var copyCharge = storage.ReserveCollection(count);
                using var ownerSetCharge = storage.ReserveCollection(count);
                using var leaseListCharge = storage.ReserveCollection(count);
                storage.ReserveCollection(count); // Immutable map.
                storage.ReserveCollection(count); // Standing metadata reference array.
                leases = new List<IDisposable>(count);
                map = new(count, ReferenceEqualityComparer.Instance);
                var owners = new HashSet<RpcGeneratedManifestRegistration>(ReferenceEqualityComparer.Instance);
                foreach (var pair in input)
                {
                    if (map.Count >= count || pair.Key is null || pair.Value is null || !map.TryAdd(pair.Key, pair.Value))
                        throw ColdMetadataException.Invalid("Codec map enumeration is inconsistent or duplicated.");
                    if (owners.Add(pair.Value.Owner)) leases.Add(pair.Value.Owner.MetadataLifetime.Retain());
                }
                if (map.Count != count || input.Count != count) throw ColdMetadataException.Invalid("Codec map count changed during capture.");
                var frozen = map.ToFrozenDictionary<Type, RpcGeneratedCodecRegistration>(ReferenceEqualityComparer.Instance);
                var snapshot = new GeneratedRegistrationSnapshot(frozen, [.. leases], storage);
                leases.Clear(); return snapshot;
            }
            catch { if (leases is not null) foreach (var lease in leases) lease.Dispose(); storage.Dispose(); throw; }
            finally { map?.Clear(); leases?.Clear(); }
        }
        internal GeneratedRegistrationView Acquire()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_references == 0, this);
                var charge = _storage!.ReserveCollection(0);
                var view = new GeneratedRegistrationView(this, charge);
                _references = checked(_references + 1);
                return view;
            }
        }
        public void Dispose()
        {
            IDisposable[]? leases; ColdMetadataPreparation? storage;
            lock (_gate)
            {
                if (--_references != 0) return;
                _registrations = null; leases = _metadataLeases; _metadataLeases = null;
                storage = _storage; _storage = null;
            }
            foreach (var lease in leases!) lease.Dispose();
            Array.Clear(leases!); storage!.Dispose();
        }
    }
    internal sealed class GeneratedRegistrationView(GeneratedRegistrationSnapshot snapshot,
        ColdMetadataReservation charge) : IDisposable
    {
        private GeneratedRegistrationSnapshot? _snapshot = snapshot;
        internal object Identity => (_snapshot ?? throw new ObjectDisposedException(nameof(GeneratedRegistrationView))).Identity;
        internal IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Registrations =>
            (_snapshot ?? throw new ObjectDisposedException(nameof(GeneratedRegistrationView))).Registrations;
        public void Dispose()
        { var owner = Interlocked.Exchange(ref _snapshot, null); if (owner is null) return; charge.Dispose(); owner.Dispose(); }
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
