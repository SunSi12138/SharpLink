using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientStreamAdmissionTests
{
    [Test]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task ProducerMustWaitForAdmissionOnTheSelectedSession(RpcMethodKind kind)
    {
        var transport = new TestClientTransportFactory();
        transport.Connection.AutoAcceptClientStreams = false;
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var producer = new ProducerProbe();
        var invocation = InvokeAsync(client, kind, producer, default);
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(producer.Calls == 0 && !invocation.IsCompleted,
            "local head publication alone cannot start a bidirectional stream producer");

        await transport.Connection.InjectPacketAsync(ProtocolV2FrameType.RequestAccepted,
            ProtocolV2FrameFlags.None, unchecked((long)request.RequestId));
        await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(producer.Calls == 1 && producer.RequestId == unchecked((long)request.RequestId),
            "admission starts exactly the original request's producer");
        if (kind == RpcMethodKind.ClientStreaming)
            await transport.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId), 42);
        else
            await transport.Connection.InjectFrameAsync(ProtocolV2FrameType.StreamComplete,
                ProtocolV2FrameFlags.None, request.RequestId, new byte[sizeof(ushort)]);
        await invocation.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.PendingCallCount == 0, "completion releases the admission and pending owners");
    }

    [Test]
    [Arguments(RpcMethodKind.ClientStreaming, "reject")]
    [Arguments(RpcMethodKind.ClientStreaming, "cancel")]
    [Arguments(RpcMethodKind.ClientStreaming, "deadline")]
    [Arguments(RpcMethodKind.ClientStreaming, "stop")]
    [Arguments(RpcMethodKind.DuplexStreaming, "reject")]
    [Arguments(RpcMethodKind.DuplexStreaming, "cancel")]
    [Arguments(RpcMethodKind.DuplexStreaming, "deadline")]
    [Arguments(RpcMethodKind.DuplexStreaming, "stop")]
    public async Task TerminalBeforeAdmissionMustReleaseWaiterWithoutStartingProducer(RpcMethodKind kind, string terminal)
    {
        var transport = new TestClientTransportFactory();
        transport.Connection.AutoAcceptClientStreams = false;
        var clock = new ManualTimeProvider();
        await using var client = ClientBuilderTestHelper.Build(transport, builder => builder.UseTimeProvider(clock));
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var producer = new ProducerProbe();
        var invocation = InvokeAsync(client, kind, producer, cancellation.Token);
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(producer.Calls == 0, "the admission gate remains closed before the terminal event");
        switch (terminal)
        {
            case "reject":
                using (var error = new PooledByteBufferWriter())
                {
                    ProtocolV2PayloadCodec.WriteError(error, SharpLinkErrorCode.FailedPrecondition,
                        "incompatible registration", 1024, out _);
                    await transport.Connection.InjectFrameAsync(ProtocolV2FrameType.Response,
                        ProtocolV2FrameFlags.Error, request.RequestId, error.WrittenMemory);
                }
                break;
            case "cancel":
                cancellation.Cancel();
                break;
            case "deadline":
                clock.Advance(TimeSpan.FromSeconds(5));
                break;
            default:
                await client.StopAsync();
                break;
        }
        var failure = await CaptureFailureAsync(invocation.WaitAsync(TimeSpan.FromSeconds(2)));
        Ensure(terminal switch
        {
            "reject" => failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "cancel" => failure is OperationCanceledException,
            "deadline" => failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            _ => failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed }
        }, "the original pending owner, not an independent admission budget, selects the terminal reason");
        Ensure(producer.Calls == 0 && client.PendingCallCount == 0,
            "every rejected admission releases its bounded waiter without invoking user stream code");
    }

    [Test]
    public async Task OneWayClientStreamMustKeepItsUnacknowledgedCompletionSemantics()
    {
        var transport = new TestClientTransportFactory();
        transport.Connection.AutoAcceptClientStreams = false;
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var producer = new ProducerProbe();
        var method = new RpcMethodDescriptor(1, 77, RpcMethodKind.OneWay, false, true, false, null, ClientStreamCount: 1);
        await ((IRpcChannel)client).InvokeOneWayAsync(method, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
            new ProbeStreams(producer), null).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(producer.Calls == 1 && client.PendingCallCount == 0,
            "OneWay has no per-request acceptance acknowledgement and only completes local stream ownership");
    }

    private static async Task InvokeAsync(SharpLinkClient client, RpcMethodKind kind,
        ProducerProbe probe, CancellationToken cancellationToken)
    {
        var channel = (IRpcChannel)client;
        var method = new RpcMethodDescriptor(1, 77, kind, true, true, true, TimeSpan.FromSeconds(5), ClientStreamCount: 1);
        var streams = new ProbeStreams(probe);
        var codec = channel.RuntimeContext.Codecs.GetCodec<int>();
        if (kind == RpcMethodKind.ClientStreaming)
        {
            _ = await channel.InvokeClientStreamingAsync(method, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                codec, streams, null, cancellationToken);
        }
        else
        {
            var stream = channel.InvokeDuplexStreamingAsync(method, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                codec, streams, null, cancellationToken);
            await using var enumerator = stream.GetAsyncEnumerator();
            _ = await enumerator.MoveNextAsync();
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

    private sealed class ProducerProbe
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal long RequestId;
    }

    private readonly struct ProbeStreams(ProducerProbe probe) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        {
            probe.RequestId = requestId;
            Interlocked.Increment(ref probe.Calls);
            probe.Started.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
