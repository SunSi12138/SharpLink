using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientOneWayShapeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DescriptorShapeShouldSelectProducerAndCancellationOwnership(bool hasClientStreams)
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var connections = (ClientConnection[])(typeof(SharpLinkClient).GetField(
            "_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!);
        Ensure(connections.Length == 1, "expected one ready connection");
        var connection = connections[0];
        using var cancellation = new CancellationTokenSource();
        var probe = new ProducerProbe();
        var streams = new RecordingWriter(probe);
        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: hasClientStreams ? 296 : 295,
            Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false,
            HasClientStreams: hasClientStreams,
            HasMethodTimeout: false,
            MethodTimeout: null,
            ClientStreamCount: hasClientStreams ? 1 : 0);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        // Use the same concrete writer in both cases: the descriptor's method shape,
        // not a guess about the writer type, determines pending/producer ownership.
        await channel.InvokeOneWayAsync(
            method, in request, RpcEmptyRequestCodec.Instance, in streams,
            metadata: null, cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);

        Ensure(probe.Calls == (hasClientStreams ? 1 : 0),
            "only the client-stream shape may invoke the producer");
        Ensure(sent.Header.Flags.HasFlag(ProtocolV2FrameFlags.OneWay), "expected OneWay request");
        Ensure(sent.Header.Flags.HasFlag(ProtocolV2FrameFlags.Cancellable) == hasClientStreams,
            "a caller token makes only the pending-owned client-stream shape wire-cancellable");
        Ensure(connection.ActiveCallCount == 0, "completed OneWay must release active call ownership");
        Ensure(connection.CallAdmissionReservationCount == 0, "completed OneWay must release admission reservation");
        if (hasClientStreams)
        {
            Ensure(probe.OwnedWhileWriting, "the pending call must own the producer before it starts");
            Ensure(probe.Token.CanBeCanceled, "the producer must receive a cancellable token");
            Ensure(probe.Token != cancellation.Token, "the pending owner must not forward the bare caller token");
            Ensure(probe.RequestId == unchecked((long)sent.Header.RequestId),
                "the producer must belong to the published request");
            Ensure(!probe.Connection!.PendingCalls.Contains(probe.RequestId),
                "the completed producer must release its pending call");
        }
    }

    private sealed class ProducerProbe
    {
        internal int Calls;
        internal long RequestId;
        internal bool OwnedWhileWriting;
        internal CancellationToken Token;
        internal ClientConnection? Connection;
    }

    private readonly struct RecordingWriter(ProducerProbe probe) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        {
            probe.Calls++;
            probe.RequestId = requestId;
            probe.Connection = (ClientConnection)sink;
            probe.OwnedWhileWriting = probe.Connection.PendingCalls.Contains(requestId);
            probe.Token = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
