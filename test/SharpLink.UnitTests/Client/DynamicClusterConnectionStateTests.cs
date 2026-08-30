using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class DynamicClusterConnectionStateTests
{
    [Test]
    public async Task IdleDrainingShouldDetachConnectionImmediately()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var connection = CreateConnection(client, "node", 1);
        var endpoint = CreateEndpointState("node", 1);
        var state = new SharpLinkClient.DynamicClusterConnectionState();

        state.Add(endpoint, connection);
        state.PublishReadyConnections([endpoint]);
        Ensure(endpoint.ReadyConnections.Length == 1,
            "an owned ready connection must be published before retirement");

        Ensure(state.TryMarkDraining([endpoint], connection, out var owner, out var disposeNow),
            "the owner must accept retirement for its connection");
        state.PublishReadyConnections([endpoint]);

        Ensure(ReferenceEquals(owner, endpoint), "retirement must preserve endpoint ownership");
        Ensure(disposeNow, "an idle draining connection should be detached for immediate disposal");
        Ensure(connection.State == ClientConnectionState.Draining,
            "retirement must transition the physical connection to Draining");
        Ensure(state.CountConnections(static _ => 1) == 0,
            "an idle draining connection must leave the active ownership set immediately");
        Ensure(state.RetiringConnectionCount == 0,
            "an immediately detached connection must not consume retiring budget");
        Ensure(endpoint.ReadyConnections.Length == 0,
            "draining must remove the connection from the ready publication");
    }

    [Test]
    public async Task ActiveDrainingShouldRemainOwnedUntilAcceptedWorkCompletes()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var connection = CreateConnection(client, "node", 2);
        var endpoint = CreateEndpointState("node", 2);
        var state = new SharpLinkClient.DynamicClusterConnectionState();
        Ensure(connection.TryBeginUntrackedCall(), "the test call must be accepted while the connection is ready");
        var callReleased = false;
        try
        {
            state.Add(endpoint, connection);
            state.PublishReadyConnections([endpoint]);

            Ensure(state.TryMarkDraining([endpoint], connection, out var owner, out var disposeNow),
                "the owner must find the active connection during drain");
            state.PublishReadyConnections([endpoint]);

            Ensure(ReferenceEquals(owner, endpoint), "draining must retain the original endpoint owner");
            Ensure(!disposeNow, "accepted work must defer physical connection disposal");
            Ensure(state.RetiringConnectionCount == 1,
                "an active draining connection must consume retiring budget");
            Ensure(state.CountConnections(static _ => 1) == 1,
                "an active draining connection must remain lifecycle-owned");
            Ensure(endpoint.ReadyConnections.Length == 0,
                "draining must stop new selection before accepted work finishes");

            connection.EndUntrackedCall();
            callReleased = true;
            Ensure(state.TryRetireDrainingIfIdle([endpoint], connection, out owner),
                "the draining connection must detach once its accepted work becomes idle");
            Ensure(ReferenceEquals(owner, endpoint), "idle retirement must preserve the endpoint owner");
            Ensure(state.RetiringConnectionCount == 0,
                "completed drain must release retiring budget");
            Ensure(state.CountConnections(static _ => 1) == 0,
                "completed drain must release active connection ownership");
        }
        finally
        {
            if (!callReleased)
                connection.EndUntrackedCall();
        }
    }

    [Test]
    public async Task EndpointRetirementShouldDetachIdleAndDrainActiveConnections()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var idle = CreateConnection(client, "node", 3);
        await using var active = CreateConnection(client, "node", 3);
        var endpoint = CreateEndpointState("node", 3);
        var state = new SharpLinkClient.DynamicClusterConnectionState();
        Ensure(active.TryBeginUntrackedCall(), "the active test connection must accept one call");
        var callReleased = false;
        try
        {
            state.Add(endpoint, idle);
            state.Add(endpoint, active);
            state.PublishReadyConnections([endpoint]);
            var dispose = new List<ClientConnection>();

            Ensure(state.BeginEndpointRetirement(endpoint, dispose),
                "the first endpoint retirement transition must be accepted");
            state.PublishReadyConnections([endpoint]);

            Ensure(endpoint.Retiring, "endpoint retirement must publish the retiring generation state");
            Ensure(dispose.Count == 1 && ReferenceEquals(dispose[0], idle),
                "endpoint retirement must detach only the idle connection for immediate disposal");
            Ensure(state.RetiringConnectionCount == 1,
                "the active connection must remain in the retiring set while accepted work runs");
            Ensure(state.CountConnections(static _ => 1) == 1,
                "only the active draining connection should remain lifecycle-owned");
            Ensure(endpoint.ReadyConnections.Length == 0,
                "a retiring generation must publish no connection for new selection");
            Ensure(!state.CanRelease(endpoint),
                "a retiring endpoint cannot release its generation while a connection is draining");
            Ensure(!state.BeginEndpointRetirement(endpoint, dispose),
                "endpoint retirement must be idempotent");

            active.EndUntrackedCall();
            callReleased = true;
            Ensure(state.TryRetireDrainingIfIdle([endpoint], active, out var owner),
                "the active connection must detach after its accepted call completes");
            Ensure(ReferenceEquals(owner, endpoint), "drain completion must keep generation ownership stable");
            Ensure(state.CanRelease(endpoint),
                "the retiring endpoint may release after connections and connecting work reach zero");
            state.ReleaseEndpoint(endpoint);
            Ensure(state.RetiringConnectionCount == 0,
                "generation release must leave no retiring connection ownership behind");
        }
        finally
        {
            if (!callReleased)
                active.EndUntrackedCall();
        }
    }

    private static SharpLinkClient.DynamicEndpointState CreateEndpointState(string id, long generation)
        => new(
            new StaticEndpointConfiguration(
                new SharpLinkEndpoint
                {
                    Id = id,
                    Address = new SharpLinkTcpAddress("127.0.0.1", 1)
                },
                DynamicClusterTransportPlaceholder.Instance),
            generation);

    private static ClientConnection CreateConnection(SharpLinkClient client, string endpointId, long generation)
    {
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            $"dynamic-{generation}",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        return new ClientConnection(
            client,
            session,
            new CancellationTokenSource(),
            8,
            context,
            endpointId,
            generation);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
