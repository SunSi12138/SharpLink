using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientOneWayTimeBudgetTests
{
    [Test]
    public async Task TimedOneWayClientStreamShouldNotStartProducerUntilRequestSurvivesEmission()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.UseRpcSessionFlush(1024 * 1024, TimeSpan.FromSeconds(10));
            });
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 290,
            Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var probe = new ProducerProbe();
        var streams = new ProbeClientStreams(probe);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        var failure = await CaptureSharpLinkExceptionAsync(invocation);
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an initial OneWay client-stream Request that expires in the send queue must fail locally");
        Ensure(!probe.Started,
            "the OneWay client-stream producer must not start until its owning Request survives emission");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.StreamData,
                TimeSpan.FromMilliseconds(50)),
            "no orphan OneWay StreamData may be emitted after the owning Request is dropped");
    }

    private static ClientConnection GetOnlyReadyConnection(SharpLinkClient client)
    {
        var connections = (ClientConnection[])(typeof(SharpLinkClient).GetField(
                "_readyConnections",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find ready connection selection snapshot"));
        Ensure(connections.Length == 1, "expected exactly one ready connection");
        return connections[0];
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }

        throw new Exception("expected SharpLinkException");
    }

    private sealed class ProducerProbe
    {
        internal bool Started;
    }

    private readonly struct ProbeClientStreams(ProducerProbe probe) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
        {
            probe.Started = true;
            return ValueTask.CompletedTask;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
