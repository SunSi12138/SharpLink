using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLink.Runtime;

/// <summary>Resolves generated codecs using the policy owned by one contract assembly.</summary>
public static class RpcGeneratedCodecResolver
{
    private static readonly ConditionalWeakTable<RpcGeneratedManifestRegistration, RpcManifestCodecProvider> OwnerProviders = new();

    /// <summary>
    /// Gets the codec provider bound to <paramref name="ownerAssembly"/> when the runtime is
    /// SharpLink-owned. Custom runtimes must provide the same owner-aware contract explicitly.
    /// </summary>
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

    internal static IRpcCodecProvider GetProvider(RpcGeneratedManifestRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
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
        var targetType = typeof(T);
        if (_owner.ContractCodecs.TryGetValue(targetType, out var policyRegistration))
            return ResolveOwned<T>(targetType, policyRegistration);

        if (_owner.HasContractCodecs)
        {
            // Once a Contract generation owns any compile-time policy, the complete generation is
            // immutable: policy first, then this owner's generated/default graph, then deterministic
            // generated dependencies and builtins. Runtime UseCodec/resolver state must not rewrite
            // the unrouted remainder of the same published Contract.
            if (_owner.Codecs.TryGetValue(targetType, out var ownerRegistration))
                return ResolveOwned<T>(targetType, ownerRegistration);

            if (_runtimeProvider is not null &&
                _runtimeProvider.CreateGeneratedRegistrationSnapshot().TryGetValue(targetType, out var dependency) &&
                IsGeneratedDependencyAllowed(dependency))
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

        // No-policy Contracts preserve the established explicit runtime UseCodec<T> precedence,
        // but their generated/default Codec instances still belong to this manifest generation.
        // Do not borrow an equivalent generated Adapter/native instance from an unrelated module.
        if (!_owner.Codecs.TryGetValue(targetType, out var defaultRegistration))
            return _fallback.GetCodec<T>();

        if (_runtimeProvider is null)
        {
            try
            {
                return _fallback.GetCodec<T>();
            }
            catch (NotSupportedException)
            {
                return ResolveOwned<T>(targetType, defaultRegistration);
            }
        }

        var snapshot = _runtimeProvider.CreateGeneratedRegistrationSnapshot();
        if (!snapshot.TryGetValue(targetType, out var publishedRegistration))
        {
            try
            {
                return _fallback.GetCodec<T>();
            }
            catch (NotSupportedException)
            {
                return ResolveOwned<T>(targetType, defaultRegistration);
            }
        }

        var candidate = _fallback.GetCodec<T>();
        if (ReferenceEquals(publishedRegistration.Owner, _owner))
            return candidate;
        if (!IsPublishedGeneratedCandidate(publishedRegistration, candidate))
            return candidate; // explicit runtime Codec won over the published generated registration.

        return ResolveOwned<T>(targetType, defaultRegistration);
    }

    private IRpcCodec<T> ResolveOwned<T>(Type targetType, RpcGeneratedCodecRegistration registration)
    {
        var codec = _resolved.GetOrAdd(
            targetType,
            _ => registration.GetCodec(this));
        return Cast<T>(codec, targetType);
    }

    private bool IsGeneratedDependencyAllowed(RpcGeneratedCodecRegistration registration)
    {
        if (ReferenceEquals(registration.Owner, _owner))
            return true;
        var dependencyIdentity = registration.Owner.Manifest.OwnerAssembly.FullName;
        return dependencyIdentity is not null &&
               _owner.Manifest.Dependencies.Contains(dependencyIdentity, StringComparer.Ordinal);
    }

    private bool IsPublishedGeneratedCandidate<T>(
        RpcGeneratedCodecRegistration registration,
        IRpcCodec<T> candidate)
    {
        var generated = registration.GetCodec(_fallback);
        return registration.Factory.Kind == RpcGeneratedCodecFactoryKind.Adapter
            ? ReferenceEquals(generated, candidate)
            : generated.GetType() == candidate.GetType();
    }

    private static IRpcCodec<T> Cast<T>(IRpcCodec codec, Type targetType)
        => codec as IRpcCodec<T> ?? throw new InvalidOperationException(
            $"The manifest-owned codec for '{targetType.FullName}' implements an incompatible codec interface.");
}
