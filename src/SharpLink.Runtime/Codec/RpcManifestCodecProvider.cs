using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SharpLink.Runtime;

internal enum RpcGeneratedCodecResolutionScope
{
    Global,
    Contract
}

/// <summary>Resolves generated codecs using the immutable policy owned by one Contract assembly generation.</summary>
public static class RpcGeneratedCodecResolver
{
    /// <summary>Gets the Codec provider bound to one generated Contract assembly.</summary>
    public static IRpcCodecProvider GetProvider(
        IRpcRuntimeContext runtimeContext,
        Assembly ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        if (runtimeContext is IRpcContractCodecProviderResolver resolver)
            return resolver.GetContractCodecProvider(ownerAssembly);
        throw new NotSupportedException(
            $"Runtime context '{runtimeContext.GetType().FullName}' must implement {nameof(IRpcContractCodecProviderResolver)} to construct generated Contract artifacts.");
    }

    /// <summary>
    /// Compatibility overload for generated source that still names a Contract Type. The Type does
    /// not define an independent policy namespace; it is canonicalized to its owner assembly.
    /// </summary>
    public static IRpcCodecProvider GetProvider(
        IRpcRuntimeContext runtimeContext,
        Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return GetProvider(runtimeContext, contractType.Assembly);
    }

    internal static IRpcCodecProvider GetProvider(RpcGeneratedManifestRegistration registration)
        => GetProvider(registration, RpcGeneratedCodecResolutionScope.Contract);

    internal static IRpcCodecProvider GetProvider(
        RpcGeneratedManifestRegistration registration,
        RpcGeneratedCodecResolutionScope scope)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration.GetProvider(scope);
    }

    internal static IRpcCodecProvider GetProvider(
        RpcGeneratedManifestRegistration registration,
        Type contractType)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(contractType);
        if (!ReferenceEquals(contractType.Assembly, registration.Manifest.OwnerAssembly))
        {
            throw new InvalidOperationException(
                $"Contract '{contractType.FullName}' is not owned by generated manifest '{registration.Manifest.OwnerAssembly.FullName}'.");
        }
        return GetProvider(registration);
    }
}

internal sealed class RpcManifestCodecProvider : IRpcCodecProvider, IRpcGeneratedUnsafeBlitCodecProvider
{
    private readonly RpcGeneratedManifestRegistration _owner;
    private readonly RpcGeneratedCodecResolutionScope _scope;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Type, CachedCodec> _resolved = new();
    private readonly Dictionary<RpcCodecOwnerIdentity, IDisposable> _origins = new(ReferenceEqualityComparer.Instance);
    private ColdMetadataReservation? _header;
    private ColdMetadataReservation? _originHeader;
    private bool _closed;

    internal RpcManifestCodecProvider(RpcGeneratedManifestRegistration owner, IRpcCodecProvider baseProvider,
        RpcGeneratedCodecResolutionScope scope = RpcGeneratedCodecResolutionScope.Contract)
    {
        ArgumentNullException.ThrowIfNull(baseProvider);
        _owner = owner; _scope = scope;
        _header = owner.Storage.ReserveCollection(0, owner.RegistrationScope);
        try { _originHeader = owner.Storage.ReserveCollection(0, owner.RegistrationScope); }
        catch { _header.Dispose(); throw; }
    }
    public IRpcCodec<T> GetCodec<T>()
    {
        _owner.ThrowIfDisposed();
        if (_resolved.TryGetValue(typeof(T), out var cached) && cached.Codec is { } hit && !cached.Origin.IsClosed)
            return Cast<T>(hit, typeof(T));
        using var use = _owner.AcquireColdUse();
        var target = typeof(T);
        RpcGeneratedCodecRegistration? registration = null;
        if (_scope == RpcGeneratedCodecResolutionScope.Contract) _owner.ContractCodecs.TryGetValue(target, out registration);
        if (registration is null) _owner.Codecs.TryGetValue(target, out registration);
        if (registration is null) _owner.Dependencies.TryGetValue(target, out registration);
        if (registration is not null) return ResolveOwned<T>(target, registration);
        if (FindReferencedCodecDependency(_owner.Manifest, target) is { } referenced)
            throw new InvalidOperationException($"Contract assembly '{_owner.OwnerAssembly.FullName}' requires referenced generated Codec '{target.FullName}' with CodecHash '{referenced.ExpectedCodecHash}', but that exact generated registration is not available.");
        if (BuiltinRpcCodecs.TryGet(target, out var builtin)) return CacheBuiltin<T>(target, builtin);
        if (target.IsEnum) return CacheBuiltin<T>(target, EnumCodec<T>.Instance);
        if (target.IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new PlatformNotSupportedException($"UnsafeBlit Codec for '{target.FullName}' requires source-generated ABI metadata in its owner-local factory.");
        throw new NotSupportedException($"Codec for '{target.FullName}' is not part of the compile-time Codec graph owned by Contract assembly '{_owner.OwnerAssembly.FullName}'.");
    }
    private IRpcCodec<T> CacheBuiltin<T>(Type target, IRpcCodec codec)
    {
        ColdMetadataReservation? charge = _owner.Storage.ReserveCollectionSlot(_owner.RegistrationScope);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this); _owner.ThrowIfDisposed();
                var entry = new CachedCodec(codec, _owner.Identity, charge);
                if (_resolved.TryAdd(target, entry)) charge = null;
                return Cast<T>(_resolved[target].Codec!, target);
            }
        }
        finally { charge?.Dispose(); }
    }
    private IRpcCodec<T> ResolveOwned<T>(Type target, RpcGeneratedCodecRegistration registration)
    {
        using var dependency = registration.Owner.AcquireColdUse();
        var codec = registration.GetCodec();
        ColdMetadataReservation? slot = null;
        try
        {
            if (!ReferenceEquals(registration.Owner, _owner)) EnsureOrigin(registration.Owner);
            slot = _owner.Storage.ReserveCollectionSlot(_owner.RegistrationScope);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this); _owner.ThrowIfDisposed(); registration.Owner.ThrowIfDisposed();
                if (_resolved.TryGetValue(target, out var existing) && existing.Codec is { } ready) return Cast<T>(ready, target);
                var cached = new CachedCodec(codec, registration.Owner.Identity, slot);
                if (_resolved.TryAdd(target, cached)) slot = null;
                return Cast<T>(_resolved[target].Codec!, target);
            }
        }
        finally { slot?.Dispose(); }
    }
    private void EnsureOrigin(RpcGeneratedManifestRegistration origin)
    {
        ColdMetadataReservation? charge = _owner.Storage.ReserveCollection(2, _owner.RegistrationScope);
        IDisposable? lease = null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this); _owner.ThrowIfDisposed(); origin.ThrowIfDisposed();
                if (_origins.ContainsKey(origin.Identity)) return;
                lease = origin.RegisterCacheConsumer(this);
                _origins.Add(origin.Identity, new OriginLease(lease, charge));
                lease = null; charge = null;
            }
        }
        finally { lease?.Dispose(); charge?.Dispose(); }
    }
    private sealed class OriginLease(IDisposable lease, ColdMetadataReservation charge) : IDisposable
    {
        private IDisposable? _lease = lease;
        public void Dispose() { Interlocked.Exchange(ref _lease, null)?.Dispose(); charge.Dispose(); }
    }
    internal void RemoveOrigin(RpcCodecOwnerIdentity origin)
    {
        // A cached owner codec may embed a codec returned by a nested dependency lookup.
        // Invalidate the whole owner's scalar resolution identity, not just the direct leaf.
        _owner.InvalidateResolvedGraph();
    }
    internal void Clear()
    {
        IDisposable[] observers;
        lock (_gate)
        {
            if (_closed) return; _closed = true;
            foreach (var pair in _resolved) if (_resolved.TryRemove(pair.Key, out var entry)) entry.Dispose();
            observers = _origins.Values.ToArray(); _origins.Clear();
        }
        foreach (var observer in observers) observer.Dispose();
        Array.Clear(observers);
        Interlocked.Exchange(ref _header, null)?.Dispose();
        Interlocked.Exchange(ref _originHeader, null)?.Dispose();
    }
    private sealed class CachedCodec(IRpcCodec codec, RpcCodecOwnerIdentity origin, ColdMetadataReservation charge) : IDisposable
    {
        private IRpcCodec? _codec = codec;
        internal IRpcCodec? Codec => Volatile.Read(ref _codec);
        internal RpcCodecOwnerIdentity Origin { get; } = origin;
        public void Dispose() { Interlocked.Exchange(ref _codec, null); charge.Dispose(); }
    }
    public IRpcCodec<T> GetGeneratedUnsafeBlitCodec<T>(IRpcGeneratedUnsafeBlitCodecFactory factory,
        SharpLinkGeneratedUnsafeBlitRequirement requirement)
    {
        using var use = _owner.AcquireColdUse();
        ArgumentNullException.ThrowIfNull(factory);
        var target = typeof(T); RpcGeneratedCodecRegistration? registration = null;
        if (_scope == RpcGeneratedCodecResolutionScope.Contract) _owner.ContractCodecs.TryGetValue(target, out registration);
        if (registration is null) _owner.Codecs.TryGetValue(target, out registration);
        if (registration is null || !ReferenceEquals(registration.Owner, _owner) ||
            !ReferenceEquals(registration.FactoryIdentity, factory) || !ReferenceEquals(registration.TargetType, target) ||
            registration.UnsafeBlitRequirement is not { } admitted || admitted != requirement)
            throw new PlatformNotSupportedException($"UnsafeBlit Codec for '{target.FullName}' requires matching source-generated ABI metadata and its exact owner-local factory registration.");
        RpcUnsafeBlitPlatform.EnsureGeneratedSupported(target, admitted);
        _owner.ThrowIfDisposed(); return UnsafeBlitCodec<T>.Instance;
    }
    internal static bool IsAllowedDependency(RpcGeneratedManifestRegistration owner, RpcGeneratedCodecRegistration registration)
    {
        var target = registration.TargetType; var manifest = owner.Manifest;
        if (FindReferencedCodecDependency(manifest, target) is { } referenced)
            return ReferenceEquals(target.Assembly, registration.Owner.OwnerAssembly) && registration.Factory.CodecHash == referenced.ExpectedCodecHash;
        var assembly = registration.Owner.OwnerAssembly;
        if (!IsTargetOwnedByDependency(target, assembly)) return false;
        return manifest.ContractDependencies.Concat(manifest.Dependencies).Any(name =>
            SharpLinkGeneratedDependencyBinding.Matches(manifest.OwnerAssembly, name, assembly));
    }
    private static SharpLinkReferencedCodecDependency? FindReferencedCodecDependency(ISharpLinkGeneratedAssemblyManifest manifest, Type target)
    {
        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencies) return null;
        foreach (var dependency in dependencies.ReferencedCodecDependencies)
            if (ReferenceEquals(dependency.TargetType, target)) return dependency;
        return null;
    }
    internal static bool IsTargetOwnedByDependency(Type targetType, Assembly dependencyAssembly)
    {
        if (ReferenceEquals(targetType.Assembly, dependencyAssembly))
            return true;
        if (targetType.IsArray)
            return IsTargetOwnedByDependency(targetType.GetElementType()!, dependencyAssembly);
        if (!targetType.IsGenericType)
            return false;
        foreach (var argument in targetType.GetGenericArguments())
        {
            if (IsTargetOwnedByDependency(argument, dependencyAssembly))
                return true;
        }
        return false;
    }

    internal static IRpcCodec<T> Cast<T>(IRpcCodec codec, Type targetType)
        => codec as IRpcCodec<T> ?? throw new InvalidOperationException(
            $"The manifest-owned codec for '{targetType.FullName}' implements an incompatible codec interface.");
}
