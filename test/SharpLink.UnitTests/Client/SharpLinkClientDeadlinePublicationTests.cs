using System.Buffers.Binary;
using System.Buffers;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

/// <summary>
/// Covers the client-side deadline guarantees that do not depend on the server budget: a Request is
/// published only after the send queue accepted it, a plain OneWay still fails its caller at its own
/// deadline and tells the peer to stop, and a Request whose deadline won while the payload was still
/// being prepared never reaches the peer.
/// </summary>
public sealed class SharpLinkClientDeadlinePublicationTests
{
    [Test]
    public async Task TimedOneWayClientStreamShouldNotPublishARequestTheSendQueueRefused()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRuntime(static options => options.FlowControl.MaxSendQueueBytes = 32 * 1024));
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 303,
            Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(SilentClientStreams);
        await DrainSentFramesAsync(transport);

        // The frame is larger than the share of the send queue a normal frame may occupy even when
        // that queue is empty, so admission refuses it and the pump never owns it. The codec's clock
        // advance leaves the pending deadline elapsed without running its timer, so the terminal
        // decision is taken by the refusal path - the path that used to mark the Request published.
        var codec = new DeadlineAdvancingPayloadCodec(
            timeProvider,
            TimeSpan.FromSeconds(5),
            payloadBytes: 30 * 1024,
            runTimers: false);
        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            codec,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "the elapsed deadline must own the terminal reason of a call the send queue refused");

        // Neither half of the pair may reach the peer: the Request was refused, so the cancel that
        // the terminal transition wanted to publish belongs to no Request at all.
        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "a Request the send queue refused must not reach the peer");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "a Request that never entered the send queue must never produce a cancel");
    }

    [Test]
    public async Task TimedUnaryShouldRefusePublicationWhenTheDeadlineElapsesInsideTheCompressionProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var provider = new DeadlineAdvancingCompressionProvider(
            timeProvider,
            TimeSpan.FromSeconds(5));
        var transport = new TestClientTransportFactory(
            ProtocolV2Capabilities.CancellationReason | ProtocolV2Capabilities.Compression,
            compressionProfile: provider.WireProfile);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRuntime(options => options.Compression.Providers.Add(provider))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    Enabled = true,
                    MinimumPayloadBytes = 0,
                    MinimumSavingsBytes = 0,
                    MinimumSavingsRatio = 0
                }));
        await client.ConnectAsync();

        var channel = (IRpcChannel)client;
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);

        // The provider is user code that runs while the frame is being prepared. Its clock advance
        // is a slow compressor, and the deadline it lets elapse has to be able to win before the
        // frame is queued instead of being blocked behind a completion gate.
        var codec = new DeadlineAdvancingCodec(timeProvider, TimeSpan.Zero);
        var requestValue = codec.Value;
        var invocation = channel.InvokeUnaryAsync(
            MethodWithTimeout(RpcMethodKind.Unary, methodId: 305, TimeSpan.FromSeconds(5)),
            in requestValue,
            codec,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a deadline that elapses inside the compression provider must fail the call");

        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "a deadline that won during frame preparation must leave the Request unpublished");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "no cancel may be emitted for a request that was never published");
    }

    [Test]
    public async Task TimedOneWayClientStreamShouldFailAtItsDeadlineWhileTheTransportIsStalled()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 308,
            Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var probe = new ProducerProbe();
        var streams = new BlockingClientStreams(probe);
        try
        {
            var connection = GetOnlyReadyConnection(client);
            await connection.Session.FlushSendQueueAsync();
            await DrainSentFramesAsync(transport);
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
                "the caller must still be waiting for the emission of an accepted Request");

            // The Request was accepted, so the pending entry owns the call. Its deadline has to end
            // the invocation even though the transport never emitted the frame: waiting for emission
            // on the bare caller token would leave the call blocked until the transport recovered.
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a pending-owned OneWay must fail at its deadline while the transport is stalled");
            Ensure(!probe.Started.Task.IsCompleted,
                "the client-stream producer must not start for a call whose deadline already won");

            stall.Set();
            var published = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            var cancelled = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            Ensure(published?.Header.Type == ProtocolV2FrameType.Request,
                "the accepted Request still reaches the peer after the deadline stopped the caller");
            Ensure(cancelled?.Header.Type == ProtocolV2FrameType.Cancel &&
                   cancelled!.Value.Header.RequestId == published!.Value.Header.RequestId,
                "the deadline must still publish its cancel behind the Request it follows");
        }
        finally
        {
            probe.Release.TrySetResult();
        }
    }

    [Test]
    public async Task TimedClientStreamingShouldFailAtItsDeadlineWhileTheTransportIsStalled()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 309,
            Kind: RpcMethodKind.ClientStreaming,
            HasResponsePayload: true,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var probe = new ProducerProbe();
        var streams = new BlockingClientStreams(probe);
        try
        {
            var connection = GetOnlyReadyConnection(client);
            await connection.Session.FlushSendQueueAsync();
            await DrainSentFramesAsync(transport);
            using var stall = new ManualResetEventSlim(initialState: false);
            transport.Connection.RunOnNextOutputBufferRequest(() => stall.Wait(TimeSpan.FromSeconds(30)));

            var invocation = channel.InvokeClientStreamingAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(),
                in streams,
                metadata: null,
                cancellationToken: default).AsTask();

            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Ensure(!invocation.IsCompleted,
                "the caller must still be waiting for the emission of an accepted head Request");

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a client-streaming call must fail at its deadline while the transport is stalled");
            Ensure(!probe.Started.Task.IsCompleted,
                "the stream producer must not start for a call whose deadline already won");

            stall.Set();
            var published = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            var cancelled = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            Ensure(published?.Header.Type == ProtocolV2FrameType.Request,
                "the accepted head Request still reaches the peer after the deadline stopped the caller");
            Ensure(cancelled?.Header.Type == ProtocolV2FrameType.Cancel &&
                   cancelled!.Value.Header.RequestId == published!.Value.Header.RequestId,
                "the deadline must still publish its cancel behind the head Request it follows");
        }
        finally
        {
            probe.Release.TrySetResult();
        }
    }

    [Test]
    public async Task TimedDuplexStreamingShouldLeaveNoFrameworkTaskWhenTheDeadlineWinsAStalledWrite()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 310,
            Kind: RpcMethodKind.DuplexStreaming,
            HasResponsePayload: true,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var probe = new ProducerProbe();
        var streams = new BlockingClientStreams(probe);
        try
        {
            var connection = GetOnlyReadyConnection(client);
            await connection.Session.FlushSendQueueAsync();
            await DrainSentFramesAsync(transport);
            using var stall = new ManualResetEventSlim(initialState: false);
            transport.Connection.RunOnNextOutputBufferRequest(() => stall.Wait(TimeSpan.FromSeconds(30)));

            await using var enumerator = channel.InvokeDuplexStreamingAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(),
                in streams,
                metadata: null,
                cancellationToken: default).GetAsyncEnumerator();
            var moveNext = enumerator.MoveNextAsync().AsTask();

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(moveNext).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a duplex call must fail at its deadline while the transport is stalled");
            Ensure(!probe.Started.Task.IsCompleted,
                "the duplex producer must not start for a call whose deadline already won");

            Ensure(await WaitForFrameworkTaskToFinishAsync(client, "DuplexStreamingInvoker"),
                "the duplex start task must finish instead of leaking behind a stalled transport write");

            stall.Set();
            var published = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            var cancelled = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            Ensure(published?.Header.Type == ProtocolV2FrameType.Request,
                "the accepted head Request still reaches the peer after the deadline stopped the caller");
            Ensure(cancelled?.Header.Type == ProtocolV2FrameType.Cancel &&
                   cancelled!.Value.Header.RequestId == published!.Value.Header.RequestId,
                "the deadline must still publish its cancel behind the head Request it follows");
        }
        finally
        {
            probe.Release.TrySetResult();
        }
    }

    [Test]
    public async Task TimedPlainOneWayShouldNotPublishARequestWhoseDeadlineElapsesInsideTheCompressionProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var provider = new DeadlineAdvancingCompressionProvider(
            timeProvider,
            TimeSpan.FromSeconds(5));
        var transport = new TestClientTransportFactory(
            ProtocolV2Capabilities.CancellationReason | ProtocolV2Capabilities.Compression,
            compressionProfile: provider.WireProfile);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRuntime(options => options.Compression.Providers.Add(provider))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    Enabled = true,
                    MinimumPayloadBytes = 0,
                    MinimumSavingsBytes = 0,
                    MinimumSavingsRatio = 0
                }));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 311, TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);

        // The codec is fast, so the pre-compression check still sees a live budget. The provider -
        // user code that runs while the frame is being prepared - is what consumes the budget, which
        // makes the linearization point the only place that can still refuse this Request. The codec
        // still writes a payload: an empty one is skipped by the compression policy before the
        // provider is ever asked.
        var codec = new DeadlineAdvancingPayloadCodec(timeProvider, TimeSpan.Zero, payloadBytes: 8);
        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            codec,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a plain OneWay whose budget elapsed during preparation must fail at its deadline");

        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "a Request that expired before admission must never be published");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "no cancel may be emitted for a request that was never published");
    }

    [Test]
    public async Task TimedPlainOneWayShouldCancelTheRemoteCallWhenItsDeadlineWinsTheEmissionRace()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 306, TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);
        using var stall = new ManualResetEventSlim(initialState: false);
        transport.Connection.RunOnNextOutputBufferRequest(() => stall.Wait(TimeSpan.FromSeconds(30)));

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a timed plain OneWay must fail its caller at its own deadline");

        // Nothing else tracks this shape, so the caller owns telling the peer to stop. The Request is
        // already in the send queue ahead of the cancel, so the peer reads them in that order.
        stall.Set();
        var published = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
        var cancelled = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
        Ensure(published?.Header.Type == ProtocolV2FrameType.Request,
            "the Request must be published before its cancel");
        Ensure(ReadTimeBudget(published!.Value) == TimeSpan.FromSeconds(5),
            "the published Request must still carry the budget sampled when the frame was created");
        Ensure(cancelled?.Header.Type == ProtocolV2FrameType.Cancel,
            "the deadline must be published as a cancel behind its own Request");
        Ensure(cancelled!.Value.Header.RequestId == published.Value.Header.RequestId,
            "the cancel must target the Request it follows");
        Ensure(ProtocolV2PayloadCodec.ReadCancelReason(new ReadOnlySequence<byte>(cancelled.Value.Payload))
                == ProtocolV2CancelReason.DeadlineExceeded,
            "the cancel must carry the deadline reason the peer negotiated");
    }

    [Test]
    public async Task TimedPlainOneWayShouldNotPublishARequestWhoseDeadlineElapsedDuringSerialization()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 307, TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);
        var codec = new DeadlineAdvancingPayloadCodec(timeProvider, TimeSpan.FromSeconds(5));

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            codec,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a deadline that elapses while a plain OneWay is serialized must fail the call");

        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "an already expired plain OneWay must not be published at all");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "no cancel may follow a Request that was never published");
    }

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

    private static Task<int> InvokeTimedUnaryAsync(IRpcChannel channel, RpcMethodDescriptor method)
    {
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
    }

    /// <summary>Reads every frame the client writes until the transport stays quiet for one period.</summary>
    private static async Task<List<TestSentFrame>> ReadSentFramesAsync(
        TestClientTransportFactory transport,
        TimeSpan quietPeriod)
    {
        var frames = new List<TestSentFrame>();
        while (await transport.Connection.TryReadNextSentFrameAsync(quietPeriod) is { } frame)
            frames.Add(frame);
        return frames;
    }

    private static async Task DrainSentFramesAsync(TestClientTransportFactory transport)
        => _ = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(20));

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

    /// <summary>
    /// Writes the requested payload, but only after letting the call's own deadline elapse - what a
    /// slow user codec does while a Request is still being built.
    /// </summary>
    private sealed class DeadlineAdvancingPayloadCodec : IRpcCodec<RpcEmptyRequest>
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly TimeSpan _advance;
        private readonly int _payloadBytes;
        private readonly bool _runTimers;

        internal DeadlineAdvancingPayloadCodec(
            ManualTimeProvider timeProvider,
            TimeSpan advance,
            int payloadBytes = 0,
            bool runTimers = true)
        {
            _timeProvider = timeProvider;
            _advance = advance;
            _payloadBytes = payloadBytes;
            _runTimers = runTimers;
        }

        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
        {
            if (_payloadBytes > 0)
            {
                var span = buffer.GetSpan(_payloadBytes);
                span[.._payloadBytes].Clear();
                buffer.Advance(_payloadBytes);
            }

            if (_runTimers)
                _timeProvider.Advance(_advance);
            else
                _timeProvider.AdvanceWithoutRunningTimers(_advance);
        }

        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer)
            => default;
    }

    /// <summary>Never writes client stream items: the tests it serves stop before the producer runs.</summary>
    private readonly struct SilentClientStreams : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
            => new(new TaskCompletionSource().Task);
    }

    /// <summary>Records whether the runtime ever started producing client stream items.</summary>
    private sealed class ProducerProbe
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Only signals that the producer ran, then waits for the test to release it. None of the calls
    /// these tests cover may start their producer, and waiting on the call's own token keeps an
    /// unexpected start from hanging the test run instead of failing the assertion.
    /// </summary>
    private readonly struct BlockingClientStreams(ProducerProbe probe) : IRpcClientStreamWriter
    {
        public async ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for a named framework task to leave the supervisor's active set: a call that failed at
    /// its deadline must not leave its start task parked behind a stalled transport write.
    /// </summary>
    private static async Task<bool> WaitForFrameworkTaskToFinishAsync(
        SharpLinkClient client,
        string operation)
    {
        var supervisor = (FrameworkTaskSupervisor)(typeof(SharpLinkClient).GetField(
                "_frameworkTasks",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find the framework task supervisor"));
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var active = false;
            foreach (var entry in supervisor.CaptureSnapshot().Operations)
            {
                if (entry.Operation == operation)
                {
                    active = true;
                    break;
                }
            }

            if (!active)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        return false;
    }

    /// <summary>
    /// Reports that it cannot compress, but only after letting the call's deadline elapse - what a
    /// slow user-provided compression provider does while a frame is being prepared.
    /// </summary>
    private sealed class DeadlineAdvancingCompressionProvider : ISharpLinkCompressionProvider
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly TimeSpan _advance;

        internal DeadlineAdvancingCompressionProvider(ManualTimeProvider timeProvider, TimeSpan advance)
        {
            _timeProvider = timeProvider;
            _advance = advance;
        }

        public string WireProfile => "test.deadline/v1";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            _timeProvider.Advance(_advance);
            return false;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the deadline provider never compresses");
    }
}
