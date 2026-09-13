using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

/// <summary>
/// Covers the two client-side deadline guarantees that do not depend on the server budget:
/// a plain OneWay still fails its caller at its own deadline, and a Request whose deadline won
/// while the payload was still being serialized never reaches the peer.
/// </summary>
public sealed class SharpLinkClientDeadlinePublicationTests
{
    [Test]
    public async Task TimedPlainOneWayShouldFailAtItsDeadlineWhileTheTransportIsStalled()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 301, TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        // Drain everything ConnectAsync published, then stall the pump inside the transport write
        // so the Request can be queued but never flushed while the deadline elapses.
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        using var stall = new ManualResetEventSlim(initialState: false);
        transport.Connection.RunOnNextOutputBufferRequest(() => stall.Wait(TimeSpan.FromSeconds(30)));

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Ensure(!invocation.IsCompleted,
            "the caller must still be waiting for emission before the deadline elapses");

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a timed plain OneWay must fail its caller at its own deadline even without a pending entry");

        // The Request was published before the deadline won, so the transport still owns it: the
        // local deadline stops the caller from blocking, it does not retract the frame.
        stall.Set();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(5),
            "the stalled Request must still carry the budget sampled when the frame was created");
    }

    [Test]
    public async Task TimedUnaryShouldNotPublishARequestAfterItsDeadlineWonDuringSerialization()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.Unary, methodId: 302, TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var codec = new DeadlineAdvancingCodec(timeProvider, TimeSpan.FromSeconds(5));

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        var requestValue = codec.Value;
        var invocation = channel.InvokeUnaryAsync(
            method,
            in requestValue,
            codec,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a deadline that elapses while the request is still being serialized must fail the call");

        // The deadline owned the call before the frame could be published, so neither half of the
        // pair may reach the peer: a Request after its cancel would be dispatched by the server,
        // and a cancel for an unpublished Request is discarded by it.
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(100)),
            "an expired request must never be published after its deadline won during serialization");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Cancel,
                TimeSpan.FromMilliseconds(100)),
            "no cancel may be emitted for a request that was never published");
    }

    private static RpcMethodDescriptor MethodWithTimeout(
        RpcMethodKind kind,
        long methodId,
        TimeSpan timeout)
        => new(
            ContractId: 1,
            MethodId: methodId,
            Kind: kind,
            HasResponsePayload: kind is not RpcMethodKind.OneWay,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: timeout);

    private static TimeSpan ReadTimeBudget(TestSentFrame sent)
        => TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(
            sent.Payload.AsSpan(ProtocolV2Constants.RequestPrefixBytes, sizeof(long))));

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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    /// <summary>
    /// Serializes a payload only after letting the call's own deadline elapse, which is what a slow
    /// user codec does when it competes with the pending deadline scheduler.
    /// </summary>
    private sealed class DeadlineAdvancingCodec : IRpcCodec<int>
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly TimeSpan _advance;

        internal DeadlineAdvancingCodec(ManualTimeProvider timeProvider, TimeSpan advance)
        {
            _timeProvider = timeProvider;
            _advance = advance;
        }

        internal int Value => 7;

        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.Advance(sizeof(int));
            _timeProvider.Advance(_advance);
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);
    }
}
