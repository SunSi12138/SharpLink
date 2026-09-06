using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
        => _assemblyRegistry.UnregisterAssemblyAsync(
            assembly,
            gracefulTimeout,
            cancellationToken);
}
