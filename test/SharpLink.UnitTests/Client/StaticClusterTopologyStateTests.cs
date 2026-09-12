using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class StaticClusterTopologyStateTests
{
    [Test]
    public async Task ReadyPublicationShouldOwnSelectionSnapshotMembership()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var firstConnection = CreateConnection(client, "first");
        await using var secondConnection = CreateConnection(client, "second");
        var first = CreateEndpointState("first", 0);
        var second = CreateEndpointState("second", 1);
        first.Connections.Add(firstConnection);
        second.Connections.Add(secondConnection);
        first.PublishReadyConnections();
        second.PublishReadyConnections();
        var topology = new SharpLinkClient.StaticClusterTopologyState(
            SharpLinkLoadBalancingStrategy.RoundRobin,
            selector: null);

        var publication = topology.PublishReadySnapshot([first, second]);
        var snapshot = topology.SelectionSnapshot;

        Ensure(publication.ReadyEndpoints == 2 && publication.ReadyConnections == 2,
            "ready publication must report every ready static endpoint and connection");
        Ensure(publication.ReadyEndpointDelta == 2 && publication.MembershipChanged,
            "the first publication must expose the complete ready-endpoint membership change");
        Ensure(snapshot.Endpoints.Length == 2 && snapshot.Candidates.Length == 2,
            "the selection snapshot must publish endpoint state and endpoint candidate arrays together");
        Ensure(ReferenceEquals(snapshot.Endpoints[0], first) && ReferenceEquals(snapshot.Endpoints[1], second),
            "selection membership must preserve static endpoint order");
        Ensure(snapshot.Candidates[0].Endpoint.Id == "first" && snapshot.Candidates[1].Endpoint.Id == "second",
            "candidate identity must stay aligned with the endpoint snapshot");

        var unchanged = topology.PublishReadySnapshot([first, second]);
        Ensure(!unchanged.MembershipChanged && unchanged.ReadyEndpointDelta == 0,
            "republishing identical membership must not replace the selection generation");
        Ensure(ReferenceEquals(snapshot, topology.SelectionSnapshot),
            "unchanged ready membership must retain the published immutable selection snapshot");
    }

    [Test]
    public async Task SelectionShouldRespectRetryExclusionWithoutAllocating()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var firstConnection = CreateConnection(client, "first");
        await using var secondConnection = CreateConnection(client, "second");
        var first = CreateEndpointState("first", 0);
        var second = CreateEndpointState("second", 1);
        first.Connections.Add(firstConnection);
        second.Connections.Add(secondConnection);
        first.PublishReadyConnections();
        second.PublishReadyConnections();
        var topology = new SharpLinkClient.StaticClusterTopologyState(
            SharpLinkLoadBalancingStrategy.RoundRobin,
            selector: null);
        _ = topology.PublishReadySnapshot([first, second]);
        var snapshot = topology.SelectionSnapshot;

        Ensure(topology.SelectEndpoint(snapshot, 1UL << 0) == 1,
            "retry exclusion must prevent the excluded static endpoint from being selected");
        Ensure(topology.SelectEndpoint(snapshot, 1UL << 1) == 0,
            "retry exclusion must preserve selection of the remaining static endpoint");

        for (var index = 0; index < 10_000; index++)
            _ = topology.SelectEndpoint(snapshot, excluded: 0);

        const int iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var index = 0; index < iterations; index++)
            checksum += topology.SelectEndpoint(snapshot, excluded: 0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);

        Ensure(allocated == 0,
            $"built-in static endpoint selection allocated {allocated} bytes over {iterations} calls");
    }

    [Test]
    public async Task LeastPendingShouldReadPublishedEndpointLoadWithoutTakingOwnershipOfConnections()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var busyConnection = CreateConnection(client, "busy");
        await using var idleConnection = CreateConnection(client, "idle");
        var busy = CreateEndpointState("busy", 0);
        var idle = CreateEndpointState("idle", 1);
        busy.Connections.Add(busyConnection);
        idle.Connections.Add(idleConnection);
        busy.PublishReadyConnections();
        idle.PublishReadyConnections();
        Ensure(busyConnection.TryBeginUntrackedCall(),
            "the busy connection must accept one call before least-pending selection");
        var callReleased = false;
        try
        {
            var topology = new SharpLinkClient.StaticClusterTopologyState(
                SharpLinkLoadBalancingStrategy.LeastPending,
                selector: null);
            _ = topology.PublishReadySnapshot([busy, idle]);

            var selected = topology.SelectEndpoint(topology.SelectionSnapshot, excluded: 0);

            Ensure(selected == 1,
                "least-pending selection must read the endpoint load providers from the published state");
            Ensure(busy.Connections.Contains(busyConnection) && idle.Connections.Contains(idleConnection),
                "topology selection must not take mutable connection ownership away from endpoint lifecycle state");
        }
        finally
        {
            if (!callReleased)
            {
                busyConnection.EndUntrackedCall();
                callReleased = true;
            }
        }
    }

    [Test]
    public async Task ClearShouldDropPublishedSelectionWithoutMutatingEndpointConnections()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var connection = CreateConnection(client, "node");
        var endpoint = CreateEndpointState("node", 0);
        endpoint.Connections.Add(connection);
        endpoint.PublishReadyConnections();
        var topology = new SharpLinkClient.StaticClusterTopologyState(
            SharpLinkLoadBalancingStrategy.PowerOfTwoChoices,
            selector: null);
        _ = topology.PublishReadySnapshot([endpoint]);

        var cleared = topology.Clear();

        Ensure(cleared == 1 && topology.ReadyEndpointCount == 0 && topology.ReadyConnectionCount == 0,
            "clear must remove the published ready topology and report its previous endpoint count");
        Ensure(topology.SelectionSnapshot.Endpoints.Length == 0,
            "clear must replace the selection snapshot with the empty generation");
        Ensure(endpoint.Connections.Contains(connection),
            "topology clear must not mutate connection lifecycle ownership");
    }

    private static StaticClientRuntimeEndpointState CreateEndpointState(string id, int index)
        => new(
            new StaticEndpointConfiguration(
                new SharpLinkEndpoint
                {
                    Id = id,
                    Address = new SharpLinkTcpAddress("127.0.0.1", index + 1)
                },
                DynamicClusterTransportPlaceholder.Instance),
            index);

    private static ClientConnection CreateConnection(SharpLinkClient client, string endpointId)
    {
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            $"static-{endpointId}",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        return new ClientConnection(
            client,
            session,
            new CancellationTokenSource(),
            8,
            context,
            endpointId);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
