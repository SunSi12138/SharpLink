using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static void PrepareReplacementControl(ISharpLinkServer server, ISharpLinkClient client)
    {
        server.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
        client.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
    }

    private static async Task<(object A, IShutdownCleanupProbe B)> OpenReplacementServicesAsync(
        ISharpLinkClient client, PluginBundle contracts, CancellationToken ct)
    {
        var dynamicProxy = typeof(RuntimeAssemblyIntegrationTests).GetMethod(nameof(OpenReplacementProxyAsync),
            BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(contracts.ContractType);
        // Prepare the two contracts serially before the workload. One physical
        // Session admits one binding control operation at a time.
        var a = await (Task<object>)dynamicProxy.Invoke(null, [client, ct])!;
        var sibling = client.CreateProxy<IShutdownCleanupProbe>();
        await sibling.PrepareAsync(s_replaceEvidenceBudget, ct);
        return (a, sibling.Service);
    }

    private static async Task<object> OpenReplacementProxyAsync<T>(ISharpLinkClient client, CancellationToken ct)
        where T : IService
    {
        var proxy = client.CreateProxy<T>();
        await proxy.PrepareAsync(s_replaceEvidenceBudget, ct);
        return proxy.Service;
    }

    private static async Task ReplaceEvidenceAsync(ISharpLinkServer server, ISharpLinkClient client,
        PluginBundle contracts, long contractId, Assembly oldAssembly, Assembly next, CancellationToken ct)
    {
        var peer = server.GetVersionPeers().Single();
        var participant = new SharpLinkVersionParticipant(peer, SharpLinkVersionInitiator.Server);
        var local = client.GetLocalContract(contracts.ContractAssembly, contractId);
        var joining = client.JoinVersionChangeAsync(local, participant, cancellationToken: ct).AsTask();
        var replacing = server.ReplaceAssemblyCoordinatedAsync(oldAssembly, next, contractId, [participant],
            s_replaceEvidenceBudget, cancellationToken: ct).AsTask();
        await Task.WhenAll(joining, replacing);
        var joined = await joining;
        var replaced = await replacing;
        var finalPeer = replaced.Peers.Single();
        Ensure(replaced.Replacement.Succeeded && replaced.Replacement.ReferencesReleased && joined.Resumed &&
               finalPeer.ActivationSent && finalPeer.Change == joined.Change && server.GetVersionPeers().Single() == peer,
            "coordinated replacement must resume the prepared route on exactly the original physical connection");
    }
}
