using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class EndpointSelectionPolicyStateTests
{
    [Test]
    public async Task StaticSelectionShouldKeepCapturedCustomGenerationAcrossPublication()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var firstConnection = CreateConnection(client, "first");
        await using var secondConnection = CreateConnection(client, "second");
        var first = CreateEndpointState("first", 0, firstConnection);
        var second = CreateEndpointState("second", 1, secondConnection);
        var topology = new SharpLinkClient.StaticClusterTopologyState(
            SharpLinkLoadBalancingStrategy.PowerOfTwoChoices,
            new FixedIndexSelector(0));
        _ = topology.PublishReadySnapshot([first, second]);
        var snapshot = topology.SelectionSnapshot;
        var captured = topology.CaptureSelectionPolicy();

        topology.UpdateEndpointSelector(new FixedIndexSelector(1));

        Ensure(topology.SelectEndpoint(snapshot, captured, excluded: 0) == 0,
            "an already-started physical attempt must keep its captured selector generation");
        Ensure(topology.SelectEndpoint(snapshot, topology.CaptureSelectionPolicy(), excluded: 0) == 1,
            "a later physical attempt must observe the newly published selector generation");
    }

    [Test]
    public async Task BuiltInSelectionShouldRemainAllocationFreeAcrossRuntimePublication()
    {
        await using var client = ClientBuilderTestHelper.Build(DynamicClusterTransportPlaceholder.Instance);
        await using var firstConnection = CreateConnection(client, "first");
        await using var secondConnection = CreateConnection(client, "second");
        var first = CreateEndpointState("first", 0, firstConnection);
        var second = CreateEndpointState("second", 1, secondConnection);
        var topology = new SharpLinkClient.StaticClusterTopologyState(
            SharpLinkLoadBalancingStrategy.PowerOfTwoChoices,
            selector: null);
        _ = topology.PublishReadySnapshot([first, second]);
        var snapshot = topology.SelectionSnapshot;

        for (var index = 0; index < 1_000; index++)
        {
            topology.UpdateLoadBalancing((SharpLinkLoadBalancingStrategy)(index & 3));
            var policy = topology.CaptureSelectionPolicy();
            _ = topology.SelectEndpoint(snapshot, policy, excluded: 0);
        }

        for (var index = 0; index < 20_000; index++)
        {
            var policy = topology.CaptureSelectionPolicy();
            _ = topology.SelectEndpoint(snapshot, policy, excluded: 0);
        }
        _ = GC.GetAllocatedBytesForCurrentThread();

        const int iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            var policy = topology.CaptureSelectionPolicy();
            checksum += topology.SelectEndpoint(snapshot, policy, excluded: 0);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);

        Ensure(allocated == 0,
            $"capturing and using the runtime built-in selection policy allocated {allocated} bytes over {iterations} attempts");
    }

    [Test]
    public void PolicyPublicationShouldPreserveCursorStateAndAvoidNoOpGenerations()
    {
        var state = new EndpointSelectionPolicyState(
            SharpLinkLoadBalancingStrategy.RoundRobin,
            selector: null);
        var initial = state.GetSnapshot();

        state.PublishBuiltIn(SharpLinkLoadBalancingStrategy.RoundRobin);
        Ensure(state.GetSnapshot().Generation == initial.Generation,
            "publishing the same built-in strategy must be a generation no-op");

        var selector = new FixedIndexSelector(0);
        state.PublishCustom(selector);
        var custom = state.GetSnapshot();
        Ensure(custom.Generation == initial.Generation + 1 &&
               custom.Kind == SharpLinkEndpointSelectionPolicyKind.Custom &&
               custom.BuiltInStrategy is null,
            "custom publication must atomically replace the complete policy generation");

        state.PublishCustom(selector);
        Ensure(state.GetSnapshot().Generation == custom.Generation,
            "publishing the same custom selector instance must be a generation no-op");

        state.PublishBuiltIn(SharpLinkLoadBalancingStrategy.LeastPending);
        var builtIn = state.GetSnapshot();
        Ensure(builtIn.Generation == custom.Generation + 1 &&
               builtIn.Kind == SharpLinkEndpointSelectionPolicyKind.BuiltIn &&
               builtIn.BuiltInStrategy == SharpLinkLoadBalancingStrategy.LeastPending,
            "custom-to-built-in publication must expose one complete replacement generation");
    }

    private static StaticClientRuntimeEndpointState CreateEndpointState(
        string id,
        int index,
        ClientConnection connection)
    {
        var state = new StaticClientRuntimeEndpointState(
            new StaticEndpointConfiguration(
                new SharpLinkEndpoint
                {
                    Id = id,
                    Address = new SharpLinkTcpAddress("127.0.0.1", index + 1)
                },
                DynamicClusterTransportPlaceholder.Instance),
            index);
        state.Connections.Add(connection);
        state.PublishReadyConnections();
        return state;
    }

    private static ClientConnection CreateConnection(SharpLinkClient client, string endpointId)
    {
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            $"selection-{endpointId}",
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

    private sealed class FixedIndexSelector(int index) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => index;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
