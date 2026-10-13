using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientContractPublicationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TopologyPublicationMustKeepCompatibleSurvivorAndRejectRetiredSession(bool dynamicTopology)
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory();
        var firstEndpoint = Endpoint("first", 5001);
        var secondEndpoint = Endpoint("second", 5002);
        var resolver = dynamicTopology
            ? new SharpLinkClientLifecycleReconnectSupport.ChannelSnapshotResolver(
                new SharpLinkEndpointSnapshot(1, [firstEndpoint, secondEndpoint]))
            : null;
        await using var client = dynamicTopology
            ? ClientBuilderTestHelper.BuildDynamic(resolver!, endpoint => endpoint.Id == "first" ? first : second, Configure)
            : ClientBuilderTestHelper.BuildStatic(
                [new(firstEndpoint, first), new(secondEndpoint, second)], Configure);
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var retired = SharpLinkClientLifecycleReconnectSupport.GetClusterReadyConnection(client, "first");
        var survivor = SharpLinkClientLifecycleReconnectSupport.GetClusterReadyConnection(client, "second");
        var cutEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCut = new ManualResetEventSlim(false);
        ClientConnection? admitted = null;
        client._beforeContractRoutePublicationTestHook = () =>
        {
            if (client.ReadyConnectionCount != 1 || retired.CanAcceptCalls)
                return;
            cutEntered.TrySetResult();
            if (!releaseCut.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("contract route publication was not released");
        };
        client._callAdmissionReservedTestHook = connection => Volatile.Write(ref admitted, connection);
        Task? publication = null;

        try
        {
            publication = LongRunningTestWorker.Run(
                () => PublishRemoval(client, dynamicTopology, retired, secondEndpoint));
            await cutEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(client.ReadyConnectionCount == 1 && !retired.CanAcceptCalls,
                "the topology has removed the first endpoint before the new contract routes are published");

            var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
            var request = await second.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(ReferenceEquals(Volatile.Read(ref admitted), survivor),
                "a compatible survivor must be selected and reserved from one coherent route publication");
            await second.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId), 42);
            Ensure(await invocation.WaitAsync(TimeSpan.FromSeconds(2)) == 42,
                "an unrelated topology publication must not interrupt a legal call to the survivor");
            Ensure(retired.CallAdmissionReservationCount == 0 &&
                   !await first.Connection.TryWaitForSentPacket(ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(20)),
                "a retained older publication must not admit or send through the removed or draining session");
        }
        finally
        {
            client._beforeContractRoutePublicationTestHook = null;
            client._callAdmissionReservedTestHook = null;
            releaseCut.Set();
            if (publication is not null)
                await LongRunningTestWorker.JoinAsync(publication, TimeSpan.FromSeconds(3));
        }
        await publication!;
    }

    // Drive the existing lifecycle owner on a dedicated thread so the deliberate synchronous
    // publication cut cannot starve the ThreadPool used by the invocation and response pumps.
    private static void PublishRemoval(SharpLinkClient client, bool dynamicTopology,
        ClientConnection retired, SharpLinkEndpoint survivor)
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        if (!dynamicTopology)
        {
            var markDraining = typeof(SharpLinkClient).GetMethod("MarkConnectionDraining", PrivateInstance)!;
            markDraining.Invoke(client, [retired]);
            return;
        }
        var cluster = typeof(SharpLinkClient).GetField("_cluster", PrivateInstance)!.GetValue(client)!;
        var applySnapshot = cluster.GetType().GetMethod("ApplySnapshotAsync", PrivateInstance)!;
        var applied = (Task<bool>)applySnapshot.Invoke(cluster,
            [new SharpLinkEndpointSnapshot(2, [survivor]), false])!;
        Ensure(applied.GetAwaiter().GetResult(), "the controlled endpoint removal must be accepted");
    }

    private static void Configure(SharpClientBuilder builder)
        => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 2;
            options.MaxConnections = 2;
            options.MaxConnectionsPerEndpoint = 1;
        }).UseEndpointSelector(new FirstAvailableSelector());

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new() { Id = id, Address = new SharpLinkTcpAddress("127.0.0.1", port) };

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FirstAvailableSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var index = 0; index < context.Count; index++)
                if ((context.ExcludedMask & (1UL << index)) == 0)
                    return index;
            return -1;
        }
    }
}
