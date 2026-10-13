using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractRoutingTests
{
    private static readonly RpcHash128 OtherHash = new(21, 34);

    [Test]
    public async Task LocalProxyCreationShouldWorkBeforeConnectWithExplicitReceipt()
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = ClientBuilderTestHelper.Build(transport);
        var ordinary = client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>();
        var receipt = Receipt(ClientInvocationTestManifest.ServerInstanceId, ClientInvocationTestManifest.RegistryEpoch);
        var constrained = client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>(new RpcProxyOptions
        {
            Receipt = receipt,
            Requirement = RpcRegistrationRequirement.ExactRegistration
        });
        Ensure(ordinary is ClientInvocationTestManifest.InvocationProxy &&
               constrained is ClientInvocationTestManifest.InvocationProxy && transport.ConnectCount == 0,
            "both proxy paths are synchronous local construction, including static generation zero receipts");
        await client.ConnectAsync();
        Ensure(ReferenceEquals(ordinary, client.Get<ClientInvocationTestManifest.IInvocationContract>()),
            "an empty remote manifest cannot invalidate local acquisition");
        await client.StopAsync();
        try
        {
            client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>();
            throw new Exception("a stopped client must reject proxy creation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed) { }
    }

    [Test]
    [Arguments(RpcMethodKind.Unary, false)]
    [Arguments(RpcMethodKind.OneWay, false)]
    [Arguments(RpcMethodKind.OneWay, true)]
    [Arguments(RpcMethodKind.ClientStreaming, true)]
    [Arguments(RpcMethodKind.ServerStreaming, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, true)]
    public async Task NoRouteShouldFailBeforePayloadSerializationOrProducerStart(RpcMethodKind kind, bool clientStreams)
    {
        var transport = new TestClientTransportFactory(contractManifest: [new(1, OtherHash)]);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var channel = ((ClientInvocationTestManifest.InvocationProxy)
            client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>()).Channel;
        var probe = new PayloadProbe();
        var method = new RpcMethodDescriptor(1, 77, kind, kind != RpcMethodKind.OneWay,
            clientStreams, false, null, ClientStreamCount: clientStreams ? 1 : 0);
        var failure = await CaptureFailureAsync(InvokeShapeAsync(channel, method, probe));
        Ensure(failure is SharpLinkException
        { Code: SharpLinkErrorCode.Unavailable, DetailCode: SharpLinkErrorDetails.Unavailable.NoRoutableTarget },
            "FailFast reports current absence of a legal route with a stable detail code");
        Ensure(probe.SerializationCount == 0 && probe.ProducerCount == 0 && client.PendingCallCount == 0,
            "no-route failures precede payload and producer work and release pending ownership");
    }

    [Test]
    public async Task MixedEndpointPickerShouldOnlySendThroughCompatibleConnections()
    {
        var rejected = new TestClientTransportFactory(contractManifest: [new(1, OtherHash)]);
        var accepted = new TestClientTransportFactory(contractManifest: ClientInvocationTestManifest.ContractsOnWire);
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(Endpoint("old", 5001), rejected),
            new StaticEndpointConfiguration(Endpoint("current", 5002), accepted)
        ], builder => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 2;
            options.MaxConnections = 2;
            options.MaxConnectionsPerEndpoint = 1;
        }).UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin));
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var proxy = (ClientInvocationTestManifest.InvocationProxy)
            client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>();
        for (var index = 0; index < 4; index++)
        {
            await InvokeOneWayAsync(proxy.Channel);
            var frame = await accepted.Connection.WaitForSentFrame(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var identity = ReadIdentity(frame);
            Ensure(identity.ContractId == 1 && identity.RpcAssemblyHash == ClientInvocationTestManifest.Hash,
                "the eligible endpoint receives the frozen local wire identity");
        }
        Ensure(client.ReadyConnectionCount == 2, "an incompatible contract cannot poison an otherwise Ready endpoint");
    }

    [Test]
    public async Task ExplicitReceiptShouldRouteThroughAStaleManifestOnlyOnItsServerAndEpoch()
    {
        var server = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var transport = new TestClientTransportFactory(contractManifest: [new(1, OtherHash)],
            serverInstanceId: server, registryEpoch: epoch);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var ordinary = (ClientInvocationTestManifest.InvocationProxy)
            client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>();
        Ensure(await CaptureFailureAsync(InvokeOneWayAsync(ordinary.Channel).AsTask()) is SharpLinkException
        { DetailCode: SharpLinkErrorDetails.Unavailable.NoRoutableTarget }, "ordinary routing still requires a compatible manifest");

        var receipt = Receipt(server, epoch);
        var constrained = (ClientInvocationTestManifest.InvocationProxy)
            client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>(new RpcProxyOptions
            { Receipt = receipt, Requirement = RpcRegistrationRequirement.ExactRegistration });
        await InvokeOneWayAsync(constrained.Channel);
        var request = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));
        var identity = ReadIdentity(request);
        Ensure(identity.RpcAssemblyHash == ClientInvocationTestManifest.Hash && identity.Receipt == receipt &&
               identity.Requirement == RpcRegistrationRequirement.ExactRegistration,
            "the receipt route bypasses only stale observations and sends the local hash plus exact constraint");

        foreach (var invalid in new[] { receipt with { ServerInstanceId = Guid.NewGuid() }, receipt with { RegistryEpoch = Guid.NewGuid() } })
        {
            var wrongTarget = (ClientInvocationTestManifest.InvocationProxy)
                client.CreateProxy<ClientInvocationTestManifest.IInvocationContract>(new RpcProxyOptions { Receipt = invalid });
            Ensure(await CaptureFailureAsync(InvokeOneWayAsync(wrongTarget.Channel).AsTask()) is SharpLinkException
            { DetailCode: SharpLinkErrorDetails.Unavailable.NoRoutableTarget },
                "receipt versions are incomparable across server instances and epochs");
        }
    }

    [Test]
    public async Task PublishedPickerMustNeverFollowAnUnselectedSessionRefreshRedirect()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await using var source = new ConnectionFixture(client, "source");
        await using var replacement = new ConnectionFixture(client, "replacement");
        var routes = new ClientContractRouteTable();
        var topology = new object();
        var binding = new ClientContractBinding(1, ClientInvocationTestManifest.Hash);
        routes.PublishManifest(source.Connection.Session, new ProtocolV2ContractManifest(0, ClientInvocationTestManifest.ContractsOnWire));
        routes.PublishTopology(topology, [[source.Connection]]);
        Ensure(routes.TryGetRoute(binding, out var route) && ReferenceEquals(route.Topology, topology),
            "the source has a published compatible route with its matching topology");

        Ensure(replacement.Connection.TryReserveSessionRefreshCommit(), "replacement is locally Ready");
        try
        {
            Ensure(replacement.Connection.TryCommitSessionRefreshRetirement(source.Connection), "the refresh cut is published");
        }
        finally { replacement.Connection.ReleaseCallAdmissionReservation(); }

        Ensure(route.SelectConnection(0) is null && replacement.Connection.CallAdmissionReservationCount == 0,
            "a stale route for A must fail instead of reserving the unselected replacement B");
        var nextTopology = new object();
        routes.PublishTopology(nextTopology, [[replacement.Connection]]);
        Ensure(!routes.TryGetRoute(binding, out _), "replacement without observed compatibility is not eligible");
        routes.PublishManifest(replacement.Connection.Session,
            new ProtocolV2ContractManifest(0, ClientInvocationTestManifest.ContractsOnWire));
        Ensure(routes.TryGetRoute(binding, out var next) && ReferenceEquals(next.Topology, nextTopology),
            "the control-plane event publishes replacement eligibility with its matching topology");
        var selected = next.SelectConnection(0);
        Ensure(ReferenceEquals(selected, replacement.Connection), "selection and reservation name the same physical session");
        selected!.ReleaseCallAdmissionReservation();
    }

    [Test]
    public async Task ReceiptIndexesMustRemainWithinTheirClientTargetScope()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await using var otherClient = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await using var first = new ConnectionFixture(client, "tenant-a");
        await using var second = new ConnectionFixture(otherClient, "tenant-b");
        var server = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        first.Connection.Session.SetRemoteRegistryIdentity(server, epoch);
        second.Connection.Session.SetRemoteRegistryIdentity(server, epoch);
        var binding = new ClientContractBinding(1, ClientInvocationTestManifest.Hash, Receipt(server, epoch));
        var firstRoutes = new ClientContractRouteTable();
        var secondRoutes = new ClientContractRouteTable();
        var firstTopology = new object();
        var secondTopology = new object();
        firstRoutes.PublishTopology(firstTopology, [[first.Connection]]);
        secondRoutes.PublishTopology(secondTopology, []);
        Ensure(firstRoutes.TryGetRoute(binding, out var route) && ReferenceEquals(route.Topology, firstTopology),
            "the verified receipt route exists in the first scope");
        var selected = route.SelectConnection(0);
        Ensure(ReferenceEquals(selected, first.Connection), "equal identity cannot select another client's tenant or cluster scope");
        selected!.ReleaseCallAdmissionReservation();
        Ensure(!secondRoutes.TryGetRoute(binding, out _),
            "a second target scope cannot borrow the first scope's matching server identity");
        secondRoutes.PublishTopology(secondTopology, [[second.Connection]]);
        Ensure(secondRoutes.TryGetRoute(binding, out var secondRoute) && ReferenceEquals(secondRoute.Topology, secondTopology),
            "the second scope becomes routable only after its own verified connection is published");
        var secondSelected = secondRoute.SelectConnection(0);
        Ensure(ReferenceEquals(secondSelected, second.Connection), "the second scope can reserve only its own physical session");
        secondSelected!.ReleaseCallAdmissionReservation();
    }

    private static ContractRegistrationReceipt Receipt(Guid server, Guid epoch)
        => new(server, epoch, 0, 1, ClientInvocationTestManifest.Hash, Guid.NewGuid());

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new() { Id = id, Address = new SharpLinkTcpAddress("127.0.0.1", port) };

    private static ProtocolV2RequestIdentity ReadIdentity(TestSentFrame frame)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame.Payload));
        return ProtocolV2RequestIdentityCodec.Read(ref reader);
    }

    private static ValueTask InvokeOneWayAsync(IRpcChannel channel)
        => channel.InvokeOneWayAsync(new RpcMethodDescriptor(1, 77, RpcMethodKind.OneWay, false, false, false, null),
            default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, default(RpcNoClientStreams), null);

    private static async Task InvokeShapeAsync(IRpcChannel channel, RpcMethodDescriptor method, PayloadProbe probe)
    {
        var request = default(RpcEmptyRequest);
        var streams = new ProbeStreams(probe);
        var response = channel.RuntimeContext.Codecs.GetCodec<int>();
        switch (method.Kind)
        {
            case RpcMethodKind.Unary:
                await channel.InvokeUnaryAsync(method, request, probe, response, null);
                break;
            case RpcMethodKind.OneWay:
                await channel.InvokeOneWayAsync(method, request, probe, streams, null);
                break;
            case RpcMethodKind.ClientStreaming:
                await channel.InvokeClientStreamingAsync(method, request, probe, response, streams, null);
                break;
            default:
                var stream = method.Kind == RpcMethodKind.ServerStreaming
                    ? channel.InvokeServerStreamingAsync(method, request, probe, response, null)
                    : channel.InvokeDuplexStreamingAsync(method, request, probe, response, streams, null);
                await using (var enumerator = stream.GetAsyncEnumerator())
                    _ = await enumerator.MoveNextAsync();
                break;
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class PayloadProbe : IRpcCodec<RpcEmptyRequest>
    {
        internal int SerializationCount;
        internal int ProducerCount;
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer) => SerializationCount++;
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private readonly struct ProbeStreams(PayloadProbe probe) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        {
            probe.ProducerCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConnectionFixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        internal ConnectionFixture(SharpLinkClient client, string id)
        {
            var context = (SharpLinkRuntimeContext)client.RuntimeContext;
            var session = RpcSessionTestFixture.CreateSessionOverTestTransport(id, _input.Reader, _output.Writer,
                RpcSessionTestFixture.ClientOptions(context));
            Connection = new ClientConnection(client, session, new CancellationTokenSource(), 8, context);
        }
        internal ClientConnection Connection { get; }
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
        }
    }
}
