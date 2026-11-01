using System.Reflection;

namespace SharpLink.IntegrationTests;

// Test-only observation of a real handle's exact physical binding cohort. This does
// not create proxies, issue RPCs, choose endpoints, or substitute for PrepareAsync.
internal static class ContractBindingTestBarrier
{
    internal static async Task WaitForAllReadyTargetsAsync<TContract>(
        ISharpLinkClient client, RpcProxy<TContract> proxy, int minimumConnections, TimeSpan timeout)
        where TContract : IService
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumConnections, 1);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var channel = typeof(RpcProxy<TContract>).GetField("_channel", flags)!.GetValue(proxy);
        if (channel is not SharpLinkClient.ContractCallSite site ||
            !ReferenceEquals(typeof(SharpLinkClient.ContractCallSite).GetField("_owner", flags)!.GetValue(site), client))
            throw new InvalidOperationException("This cohort probe requires the supplied client's actual static contract site.");
        var gate = (Lock)typeof(SharpLinkClient.ContractCallSite).GetField("_bindingGate", flags)!.GetValue(site)!;
        var targetField = typeof(SharpLinkClient.ContractCallSite).GetField("_boundTargets", flags)!;
        var implementation = (SharpLinkClient)client;
        var cluster = typeof(SharpLinkClient).GetField("_cluster", flags)!.GetValue(implementation);
        var capture = cluster?.GetType().GetMethod("CaptureReadyConnections", flags);
        var fixedReady = typeof(SharpLinkClient).GetField("_readyConnections", flags)!;

        ClientConnection[] CaptureReady()
            => ((ClientConnection[])(cluster is null
                    ? fixedReady.GetValue(implementation)!
                    : capture!.Invoke(cluster, null)!))
                .Where(static connection => connection.CanAcceptCalls).ToArray();

        bool AllCurrentTargetsAreBound()
        {
            var ready = CaptureReady();
            if (ready.Length < minimumConnections)
                return false;
            lock (gate)
            {
                var targets = (SharpLinkClient.ContractCallSite.BoundTarget[])targetField.GetValue(site)!;
                if (site.LocalControl.IsRetired || ready.Any(connection => !targets.Any(target =>
                        ReferenceEquals(target.Connection, connection) && target.IsEligible)))
                    return false;
            }
            // Never accept a cohort that changed while its binding table was inspected.
            var current = CaptureReady();
            return current.Length == ready.Length && current.All(connection => ready.Contains(connection));
        }

        using var cancellation = new CancellationTokenSource(timeout);
        while (!AllCurrentTargetsAreBound())
            await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
    }
}
