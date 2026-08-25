using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLink.Runtime;

/// <summary>Resolves generated codecs using the policy owned by one RPC Contract.</summary>
public static class RpcGeneratedCodecResolver
{
    private static readonly ConditionalWeakTable<RpcGeneratedManifestRegistration, RpcManifestCodecProvider> OwnerProviders = new();

    /// <summary>Gets the Codec provider bound to one generated Contract.</summary>
    public static IRpcCodecProvider GetProvider(
        IRpcRuntimeContext runtimeContext,
        Type contractType)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);
        ArgumentNullException.ThrowIfNull(contractType);
        if (runtimeContext is IRpcContractCodecProviderResolver resolver)
            return resolver.GetContractCodecProvider(contractType);
        throw new NotSupportedException(
            $"Runtime context '{runtimeContext.GetType().FullName}' must implement {nameof(IRpcContractCodecProviderResolver)} to construct generated Contract artifacts.");
    }

    // Compatibility bridge for API-4 generated source while the API-5 generator switches its
    // public proxy constructor to Contract Type lookup. SharpLink-owned contexts can still resolve
    // the assembly generation; custom runtimes must use the Contract-aware overload.
    public static IRpcCodecProvider GetProvider(
        IRpcRuntimeContext runtimeContext,
        Assembly ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        if (runtimeContext is SharpLinkRuntimeContext sharpLinkContext)
            return sharpLinkContext.GetManifestCodecProvider(ownerAssembly);
        throw new NotSupportedException(
            $"Runtime context '{runtimeContext.GetType().FullName}' must use Contract Type-aware generated Codec resolution.");
    }

    internal static IRpcCodecProvider GetProvider(RpcGeneratedManifestRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.ThrowIfDisposed();
        return OwnerProviders.GetValue(
            registration,
            static owner => new RpcManifestCodecProvider(owner, owner.BaseProvider));
    }
}

internal sealed class RpcManifestCodecProvider : IRpcCodecProvider
{
    private readonly RpcGeneratedManifestRegistration _owner;
    private readonly IRpcCodecProvider _fallback;
    private readonly RpcCodecProvider? _runtimeProvider;
    private readonly ConcurrentDictionary<Type, IRpcCodec> _resolved = new();

    internal RpcManifestCodecProvider(
        RpcGeneratedManifestRegistration owner,
        IRpcCodecProvider fallback)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _runtimeProvider = fallback as RpcCodecProvider;
    }

    public IRpcCodec<T> GetCodec<T>()
    {
        _owner.ThrowIfDisposed();
        var targetType = typeof(T);
        if (_owner.ContractCodecs.TryGetValue(targetType, out var policyRegistration))
            return ResolveOwned<T>(targetType, policyRegistration);

        if (_owner.HasContractCodecs)
        {
            if (_owner.Codecs.TryGetValue(targetType, out var ownerRegistration))
                return ResolveOwned<T>(targetType, ownerRegistration);

            if (_runtimeProvider is not null &&
                _runtimeProvider.CreateGeneratedRegistrationSnapshot().TryGetValue(targetType, out var dependency) &&
                IsGeneratedDependencyAllowed(targetType, dependency))
            {
                return ResolveOwned<T>(targetType, dependency);
            }

            if (BuiltinRpcCodecs.TryGet(targetType, out var builtin))
                return Cast<T>(builtin, targetType);
            if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                return UnsafeBlitCodec<T>.Instance;

            throw new NotSupportedException(
                $"Codec for '{targetType.FullName}' is not part of the compile-time Codec graph owned by '{_owner.Manifest.OwnerAssembly.FullName}'.");
        }

        if (_runtimeProvider is not null && _runtimeProvider.TryGetExplicitCodec<T>(out var explicitCodec))
            return explicitCodec;

        if (_owner.Codecs.TryGetValue(targetType, out var defaultRegistration))
            return ResolveOwned<T>(targetType, defaultRegistration);

        return _fallback.GetCodec<T>();
    }

    private IRpcCodec<T> ResolveOwned<T>(Type targetType, RpcGeneratedCodecRegistration registration)
    {
        _owner.ThrowIfDisposed();
        var codec = _resolved.GetOrAdd(
            targetType,
            _ => registration.GetCodec(this));
        _owner.ThrowIfDisposed();
        return Cast<T>(codec, targetType);
    }

    private bool IsGeneratedDependencyAllowed(
        Type targetType,
        RpcGeneratedCodecRegistration registration)
    {
        if (ReferenceEquals(registration.Owner, _owner))
            return true;

        var dependencyAssembly = registration.Owner.Manifest.OwnerAssembly;
        var dependencyIdentity = dependencyAssembly.FullName;
        if (dependencyIdentity is null || !IsTargetOwnedByDependency(targetType, dependencyAssembly))
            return false;

        var dependencies = _owner.Manifest.Dependencies;
        for (var index = 0; index < dependencies.Count; index++)
        {
            if (string.Equals(dependencies[index], dependencyIdentity, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsTargetOwnedByDependency(Type targetType, Assembly dependencyAssembly)
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

    private static IRpcCodec<T> Cast<T>(IRpcCodec codec, Type targetType)
        => codec as IRpcCodec<T> ?? throw new InvalidOperationException(
            $"The manifest-owned codec for '{targetType.FullName}' implements an incompatible codec interface.");
}
