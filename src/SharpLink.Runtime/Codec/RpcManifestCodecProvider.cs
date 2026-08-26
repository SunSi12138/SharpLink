using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLink.Runtime;

/// <summary>Resolves generated codecs using the immutable policy owned by one Contract assembly generation.</summary>
public static class RpcGeneratedCodecResolver
{
    private static readonly ConditionalWeakTable<RpcGeneratedManifestRegistration, RpcManifestCodecProvider> OwnerProviders = new();

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
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.ThrowIfDisposed();
        return OwnerProviders.GetValue(
            registration,
            static owner => new RpcManifestCodecProvider(owner, owner.BaseProvider));
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

internal sealed class RpcManifestCodecProvider : IRpcCodecProvider
{
    private readonly RpcGeneratedManifestRegistration _owner;
    private readonly RpcCodecProvider? _runtimeProvider;
    private readonly ConcurrentDictionary<Type, IRpcCodec> _resolved = new();

    internal RpcManifestCodecProvider(
        RpcGeneratedManifestRegistration owner,
        IRpcCodecProvider baseProvider)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(baseProvider);
        _runtimeProvider = baseProvider as RpcCodecProvider;
    }

    public IRpcCodec<T> GetCodec<T>()
    {
        _owner.ThrowIfDisposed();
        var targetType = typeof(T);

        // The Contract assembly compilation is the only serializer-selection authority.
        // Endpoint runtime UseCodec/resolver state is intentionally not consulted here.
        if (_owner.ContractCodecs.TryGetValue(targetType, out var contractRegistration))
            return ResolveOwned<T>(targetType, contractRegistration);

        // Compatibility for hand-authored/older manifests whose generated defaults are published
        // only in the owner-local global table. New generated manifests publish the complete RPC
        // graph through ContractCodecs.
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
        if (targetType.IsEnum)
            return EnumCodec<T>.Instance;
        if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return UnsafeBlitCodec<T>.Instance;

        throw new NotSupportedException(
            $"Codec for '{targetType.FullName}' is not part of the compile-time Codec graph owned by Contract assembly '{_owner.Manifest.OwnerAssembly.FullName}'.");
    }

    private IRpcCodec<T> ResolveOwned<T>(Type targetType, RpcGeneratedCodecRegistration registration)
    {
        _owner.ThrowIfDisposed();
        var codec = _resolved.GetOrAdd(targetType, _ => registration.GetCodec(this));
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

        if (ContainsIdentity(_owner.Manifest.ContractDependencies, dependencyIdentity))
            return true;

        // Compatibility for custom manifests that predate ContractDependencies and publish their
        // whole generated-module closure through Dependencies.
        return ContainsIdentity(_owner.Manifest.Dependencies, dependencyIdentity);
    }

    private static bool ContainsIdentity(IReadOnlyList<string> dependencies, string identity)
    {
        for (var index = 0; index < dependencies.Count; index++)
        {
            if (string.Equals(dependencies[index], identity, StringComparison.Ordinal))
                return true;
        }
        return false;
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
