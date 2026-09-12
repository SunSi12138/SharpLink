using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterAddReadinessTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task OnlyPublishedNotReadyAddedClusterShouldProjectNotReady()
    {
        var candidateTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = CreateDynamicBuilder()
            .AddCluster(
                "bootstrap",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("bootstrap").AsTask().WaitAsync(RaceCoordinationTimeout);

        var removal = await client.RemoveClusterAsync("bootstrap", TimeSpan.FromSeconds(2));
        Ensure(removal.Succeeded && removal.ReferencesReleased,
            "the bootstrap cluster must be fully retired before the added-only readiness assertion");

        var add = AddClusterWithFixedDiscoveryAsync(
            client,
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await candidateTransport.ConnectStarted.Task.WaitAsync(RaceCoordinationTimeout);
        await add.WaitAsync(RaceCoordinationTimeout);

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "publishing a not-ready added cluster must keep the local coordinator Running");
        Ensure(client.GetClusterReadiness("candidate") == SharpLinkReadinessState.NotReady,
            "the added cluster must remain NotReady while its remote connect is pending");
        Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
            "when the added not-ready cluster is the only published slot, aggregate readiness must be NotReady");

        candidateTransport.ReleaseConnect();
        await client.WaitForReadyAsync("candidate").AsTask().WaitAsync(RaceCoordinationTimeout);
        Ensure(client.Readiness == SharpLinkReadinessState.Ready,
            "the published child supervisor must converge aggregate readiness without another ConnectAsync call");
    }
}
