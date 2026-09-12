using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientReadinessPublicationSupport
{
    internal static async Task<SharpLinkClientReadinessSnapshot> AwaitNextPublicationAsync(
        SharpLinkClient client,
        ClientReadinessPublication publication,
        TaskCompletionSource entered)
    {
        entered.TrySetResult();
        await publication.Changed.Task;
        return client.GetReadinessSnapshot();
    }

    internal static void AssertStressSnapshot(SharpLinkClientReadinessSnapshot snapshot)
    {
        SharpLinkClientReadinessSharedSupport.Ensure(snapshot.State == SharpLinkConnectionState.Created,
            "fact-only stress publication must preserve the client lifecycle state");
        SharpLinkClientReadinessSharedSupport.Ensure(snapshot.ActiveEndpoints == 1 && snapshot.TargetReadyEndpoints == 1,
            "stress publication must preserve fixed-topology configuration");
        SharpLinkClientReadinessSharedSupport.Ensure(snapshot.ReadyEndpoints is 0 or 1 &&
               snapshot.ReadyConnections == snapshot.ReadyEndpoints,
            "stress publication must expose one complete valid fact set");
    }
}
