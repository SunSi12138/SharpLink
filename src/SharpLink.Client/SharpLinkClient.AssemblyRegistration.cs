using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
        => _assemblyRegistry.RegisterAssembly(assembly);

    public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        Assembly oldAssembly,
        Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
        => _assemblyRegistry.ReplaceAssemblyAsync(
            oldAssembly,
            newAssembly,
            gracefulTimeout,
            cancellationToken);

    internal static FrozenDictionary<Type, ClientProxyRegistration> BuildStaticProxySnapshot(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        SharpLinkRuntimeContext runtimeContext)
        => ClientAssemblyRegistry.BuildStaticProxySnapshot(manifests, runtimeContext);

    internal static void ValidateStaticManifestCompatibility(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => ClientAssemblyRegistry.ValidateStaticManifestCompatibility(manifest);

    internal bool IsDynamicAssemblyRegistered(Assembly assembly)
        => _assemblyRegistry.IsDynamicAssemblyRegistered(assembly);

    bool IDynamicAssemblyRegistrationInspector.IsDynamicAssemblyRegistered(Assembly assembly)
        => IsDynamicAssemblyRegistered(assembly);

    internal sealed class ClientProxyRegistration
    {
        internal ClientProxyRegistration(
            SharpLinkGeneratedContractDescriptor descriptor,
            SharpLinkDynamicModule? module,
            IRpcCodecProvider codecs,
            RpcHash128 rpcAssemblyHash)
        {
            Descriptor = descriptor;
            Module = module;
            Codecs = codecs;
            Binding = new ClientContractBinding(descriptor.ContractId, rpcAssemblyHash);
        }

        internal SharpLinkGeneratedContractDescriptor Descriptor { get; }

        internal SharpLinkDynamicModule? Module { get; }

        internal IRpcCodecProvider Codecs { get; }

        internal ClientContractBinding Binding { get; }

        internal object? Proxy;
    }
}
