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

        var referencedDependency = FindReferencedCodecDependency(targetType);
        if (_runtimeProvider is not null &&
            _runtimeProvider.CreateGeneratedRegistrationSnapshot().TryGetValue(targetType, out var dependency) &&
            IsGeneratedDependencyAllowed(targetType, dependency, referencedDependency))
        {
            return ResolveOwned<T>(targetType, dependency);
        }
        if (referencedDependency is not null)
        {
            throw new InvalidOperationException(
                $"Contract assembly '{_owner.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec " +
                $"'{targetType.FullName}' from the exact bound runtime Type/assembly generation with CodecHash " +
                $"'{referencedDependency.ExpectedCodecHash}', but that exact generated registration is not available.");
        }

        if (BuiltinRpcCodecs.TryGet(targetType, out var builtin))
            return Cast<T>(builtin, targetType);
        if (targetType.IsEnum)
            return EnumCodec<T>.Instance;
        if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            RpcUnsafeBlitPlatform.EnsureSupported(targetType);
            return UnsafeBlitCodec<T>.Instance;
        }

        throw new NotSupportedException(
            $"Codec for '{targetType.FullName}' is not part of the compile-time Codec graph owned by Contract assembly '{_owner.Manifest.OwnerAssembly.FullName}'.");
    }

    private IRpcCodec<T> ResolveOwned<T>(Type targetType, RpcGeneratedCodecRegistration registration)
    {
        _owner.ThrowIfDisposed();
        var codec = _resolved.GetOrAdd(
            targetType,
            _ => registration.GetCodec(GetRegistrationProvider(registration)));
        _owner.ThrowIfDisposed();
        return Cast<T>(codec, targetType);
    }

    private static IRpcCodecProvider GetRegistrationProvider(RpcGeneratedCodecRegistration registration)
    {
        var owner = registration.Owner;
        owner.ThrowIfDisposed();
        var targetType = registration.Factory.TargetType;
        if (owner.ContractCodecs.TryGetValue(targetType, out var contractRegistration) &&
            ReferenceEquals(contractRegistration, registration))
        {
            return RpcGeneratedCodecResolver.GetProvider(owner);
        }

        // A context-global registration is part of the provider manifest's global generated graph.
        // In particular, a referenced codec selected by another Contract must never receive that
        // consumer Contract's policy provider while constructing its own nested dependencies.
        return owner.BaseProvider;
    }

    private bool IsGeneratedDependencyAllowed(
        Type targetType,
        RpcGeneratedCodecRegistration registration,
        SharpLinkReferencedCodecDependency? referencedDependency)
    {
        if (referencedDependency is not null)
        {
            return ReferenceEquals(referencedDependency.TargetType, targetType) &&
                   ReferenceEquals(registration.Owner.Manifest.OwnerAssembly, targetType.Assembly) &&
                   registration.Factory.CodecHash == referencedDependency.ExpectedCodecHash;
        }

        if (ReferenceEquals(registration.Owner, _owner))
            return true;

        var dependencyAssembly = registration.Owner.Manifest.OwnerAssembly;
        if (!IsTargetOwnedByDependency(targetType, dependencyAssembly))
            return false;

        if (ContainsBoundDependency(_owner.Manifest.ContractDependencies, dependencyAssembly))
            return true;

        // Compatibility for custom manifests that predate ContractDependencies and publish their
        // whole generated-module closure through Dependencies. The string is only a CLR AssemblyRef
        // locator; the actual permission is bound to the resolved Assembly object/generation.
        return ContainsBoundDependency(_owner.Manifest.Dependencies, dependencyAssembly);
    }

    private SharpLinkReferencedCodecDependency? FindReferencedCodecDependency(Type targetType)
    {
        if (_owner.Manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest)
            return null;
        var dependencies = dependencyManifest.ReferencedCodecDependencies;
        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            if (dependency is not null && ReferenceEquals(dependency.TargetType, targetType))
                return dependency;
        }
        return null;
    }

    private bool ContainsBoundDependency(
        IReadOnlyList<string> dependencies,
        Assembly dependencyAssembly)
    {
        for (var index = 0; index < dependencies.Count; index++)
        {
            if (SharpLinkGeneratedDependencyBinding.Matches(
                    _owner.Manifest.OwnerAssembly,
                    dependencies[index],
                    dependencyAssembly))
            {
                return true;
            }
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
