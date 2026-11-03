using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient : ISharpLinkPreparedAssemblyClient
{
    SharpLinkAssemblyRegistrationResult ISharpLinkPreparedAssemblyClient.RegisterPreparedAssembly(
        PreparedGeneratedManifest manifest,
        Func<Func<SharpLinkAssemblyRegistrationResult>, SharpLinkAssemblyRegistrationResult>? publication)
        => _assemblyRegistry.RegisterPreparedAssembly(manifest, publication);

    ValueTask<SharpLinkAssemblyReplacementResult> ISharpLinkPreparedAssemblyClient.ReplacePreparedAssemblyAsync(
        Assembly oldAssembly, PreparedGeneratedManifest manifest, ClientRetirementBudget budget,
        ChildAssemblySelectionMailbox? mailbox)
        => _assemblyRegistry.ReplacePreparedAssemblyAsync(oldAssembly, manifest, budget, mailbox);

    ClientRetirementBudget ISharpLinkPreparedAssemblyClient.CaptureRetirementBudget(TimeSpan gracefulTimeout)
        => _assemblyRegistry.CaptureRetirementBudget(gracefulTimeout);

    ValueTask<SharpLinkAssemblyUnregisterResult> ISharpLinkPreparedAssemblyClient.UnregisterAssemblyAsync(
        Assembly assembly, ClientRetirementBudget budget, ChildAssemblySelectionMailbox? mailbox)
        => _assemblyRegistry.UnregisterAssemblyAsync(assembly, budget, mailbox: mailbox);

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

    internal static ClientProxySnapshot BuildStaticProxySnapshot(
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
            IRpcCodecProvider codecs)
        {
            Descriptor = descriptor;
            Module = module;
            Codecs = codecs;
        }

        internal SharpLinkGeneratedContractDescriptor Descriptor { get; }

        internal SharpLinkDynamicModule? Module { get; }

        internal IRpcCodecProvider Codecs { get; }


        internal ClientLocalContractControl LocalControl { get; set; } = null!;
    }
}

// Internal injected child implementations must participate in the same prepared transaction.
// The public builders always materialize SharpLinkClient; this is not a public capability fallback.
internal interface ISharpLinkPreparedAssemblyClient
{
    SharpLinkAssemblyRegistrationResult RegisterPreparedAssembly(PreparedGeneratedManifest manifest,
        Func<Func<SharpLinkAssemblyRegistrationResult>, SharpLinkAssemblyRegistrationResult>? publication = null);
    ValueTask<SharpLinkAssemblyReplacementResult> ReplacePreparedAssemblyAsync(
        Assembly oldAssembly, PreparedGeneratedManifest manifest, ClientRetirementBudget budget,
        ChildAssemblySelectionMailbox? mailbox = null);
    ClientRetirementBudget CaptureRetirementBudget(TimeSpan gracefulTimeout);
    ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        Assembly assembly, ClientRetirementBudget budget, ChildAssemblySelectionMailbox? mailbox = null);
}
