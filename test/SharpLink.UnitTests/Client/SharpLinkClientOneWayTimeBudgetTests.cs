using System.Threading;
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
        var emissionBlocked = transport.Connection.BlockNextOutputBufferRequest();
        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        try
        {
            await emissionBlocked.WaitAsync(TimeSpan.FromSeconds(2));
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        }
        finally
        {
            transport.Connection.ReleaseBlockedOutputBufferRequest();
        }

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an initial OneWay client-stream Request that expires at the emission boundary must fail locally");
        Ensure(!probe.Started,
            "the OneWay client-stream producer must not start until its owning Request survives emission");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.StreamData,
                TimeSpan.FromMilliseconds(50)),
            "no orphan OneWay StreamData may be emitted after the owning Request is dropped");
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
