using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class DynamicClusterConnectionOwnershipInvariantTests
{
    [Test]
    public async Task AddShouldRejectConnectionOwnedByDifferentEndpoint()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var connection = CreateConnection(client, "node-a", 7);
        var first = CreateEndpointState("node-a", 7);
        var second = CreateEndpointState("node-b", 8);
        var state = new SharpLinkClient.DynamicClusterConnectionState();

        state.Add(first, connection);
        state.Add(first, connection);

        var threw = false;
        try
        {
            state.Add(second, connection);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Ensure(threw, "a connection must not be accepted by a second endpoint owner");
        Ensure(ReferenceEquals(state.FindEndpoint(connection), first),
            "failed cross-endpoint ownership must preserve the original owner");
        Ensure(state.CountConnections(static _ => 1) == 1,
            "duplicate ownership attempts must not double-count the connection");

        var detached = state.DetachAll();
        Ensure(detached.Length == 1 && ReferenceEquals(detached[0], connection),
            "authoritative detach must return a uniquely-owned connection exactly once");
    }

    [Test]
    public async Task AddShouldRejectConnectionWithMismatchedEndpointIdentity()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var wrongId = CreateConnection(client, "node-b", 7);
        await using var wrongGeneration = CreateConnection(client, "node-a", 8);
        var endpoint = CreateEndpointState("node-a", 7);
        var state = new SharpLinkClient.DynamicClusterConnectionState();

        var wrongIdRejected = false;
        try
        {
            state.Add(endpoint, wrongId);
        }
        catch (InvalidOperationException)
        {
            wrongIdRejected = true;
        }

        var wrongGenerationRejected = false;
        try
        {
            state.Add(endpoint, wrongGeneration);
        }
        catch (InvalidOperationException)
        {
            wrongGenerationRejected = true;
        }

        Ensure(wrongIdRejected, "endpoint ownership must reject a connection with a different endpoint id");
        Ensure(wrongGenerationRejected,
            "endpoint ownership must reject a connection from a different endpoint generation");
        Ensure(state.FindEndpoint(wrongId) is null,
            "a rejected endpoint-id mismatch must not create ownership");
        Ensure(state.FindEndpoint(wrongGeneration) is null,
            "a rejected generation mismatch must not create ownership");
        Ensure(state.CountConnections(static _ => 1) == 0,
            "identity mismatches must leave authoritative ownership unchanged");
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
