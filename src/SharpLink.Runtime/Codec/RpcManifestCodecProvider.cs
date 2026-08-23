using System.Reflection;

namespace SharpLink.Runtime;

/// <summary>Resolves generated codecs using the policy owned by one contract assembly.</summary>
public static class RpcGeneratedCodecResolver
{
    /// <summary>
    /// Gets the codec provider bound to <paramref name="ownerAssembly"/> when the runtime is
    /// SharpLink-owned, otherwise falls back to the context provider.
    /// </summary>
    public static IRpcCodecProvider GetProvider(
        IRpcRuntimeContext runtimeContext,
        Assembly ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        return runtimeContext is SharpLinkRuntimeContext sharpLinkContext
            ? sharpLinkContext.GetManifestCodecProvider(ownerAssembly)
            : runtimeContext.Codecs;
    }
}

internal sealed class RpcManifestCodecProvider : IRpcCodecProvider
{
    private readonly RpcGeneratedManifestRegistration _owner;
    private readonly IRpcCodecProvider _fallback;
    private readonly ConcurrentDictionary<Type, IRpcCodec> _resolved = new();

    internal RpcManifestCodecProvider(
        RpcGeneratedManifestRegistration owner,
        IRpcCodecProvider fallback)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public IRpcCodec<T> GetCodec<T>()
    {
        var targetType = typeof(T);
        if (!_owner.AllCodecs.TryGetValue(targetType, out var registration))
            return _fallback.GetCodec<T>();

        var codec = _resolved.GetOrAdd(
            targetType,
            _ => registration.GetCodec(this));
        return codec as IRpcCodec<T> ?? throw new InvalidOperationException(
            $"The manifest-scoped codec for '{targetType.FullName}' implements an incompatible codec interface.");
    }
}
