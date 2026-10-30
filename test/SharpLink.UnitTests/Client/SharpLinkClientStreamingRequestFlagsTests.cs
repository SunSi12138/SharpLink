using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientStreamingRequestFlagsTests
{
    [Test]
    [Arguments(RpcMethodKind.ServerStreaming, false, false, true)]
    [Arguments(RpcMethodKind.ServerStreaming, true, false, true)]
    [Arguments(RpcMethodKind.ServerStreaming, false, true, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, false, false, true)]
    [Arguments(RpcMethodKind.ClientStreaming, false, false, true)]
    [Arguments(RpcMethodKind.Unary, false, false, false)]
    public async Task RequestShouldAdvertiseCancellationForOwnedStreams(
        RpcMethodKind kind,
        bool hasCallerToken,
        bool hasMethodTimeout,
        bool expectedCancellable)
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.DisableRequestTimeout());
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var token = hasCallerToken ? cancellation.Token : CancellationToken.None;
        var hasClientStreams = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming;
        // Streaming calls do not inherit the client-default timeout. Use a method timeout to
        // exercise the actual deadline-enabled control case, without waiting for it to expire.
        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 933,
            Kind: kind,
            HasResponsePayload: true,
            HasClientStreams: hasClientStreams,
            HasMethodTimeout: hasMethodTimeout,
            MethodTimeout: hasMethodTimeout ? TimeSpan.FromMinutes(1) : null,
            ClientStreamCount: hasClientStreams ? 1 : 0);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var codec = channel.RuntimeContext.Codecs.GetCodec<int>();
        var streams = default(EmptyClientStreams);

        if (kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming)
        {
            var response = kind == RpcMethodKind.ServerStreaming
                ? channel.InvokeServerStreamingAsync(
                    method, in request, RpcEmptyRequestCodec.Instance, codec, null, token)
                : channel.InvokeDuplexStreamingAsync(
                    method, in request, RpcEmptyRequestCodec.Instance, codec, in streams, null, token);
            var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await using (var enumerator = response.GetAsyncEnumerator())
            {
                EnsureRequestFlags(sent.Header.Flags, expectedCancellable, hasMethodTimeout, hasReturn: false);
            }

            // Even with neither caller cancellation nor a deadline, disposing the response is
            // a terminal event. Its peer must have been given a per-call cancellation owner.
            var cancel = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(cancel.Header.RequestId == sent.Header.RequestId,
                "consumer abandonment must target the published request");
            Ensure(cancel.Payload is [(byte)ProtocolV2CancelReason.ConsumerAbandoned],
                "early response disposal must send ConsumerAbandoned");
        }
        else
        {
            var response = kind == RpcMethodKind.ClientStreaming
                ? channel.InvokeClientStreamingAsync(
                    method, in request, RpcEmptyRequestCodec.Instance, codec, in streams, null, token)
                : channel.InvokeUnaryAsync(
                    method, in request, RpcEmptyRequestCodec.Instance, codec, null, token);
            var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId), 42);
            Ensure(await response.AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 42,
                "the request must still complete with its response");
            EnsureRequestFlags(sent.Header.Flags, expectedCancellable, hasMethodTimeout, hasReturn: true);
        }
    }

    private static void EnsureRequestFlags(
        ProtocolV2FrameFlags flags, bool expectedCancellable, bool hasMethodTimeout, bool hasReturn)
    {
        Ensure(flags.HasFlag(ProtocolV2FrameFlags.Cancellable) == expectedCancellable,
            "stream ownership, not only a caller token or deadline, must enable wire cancellation");
        Ensure(flags.HasFlag(ProtocolV2FrameFlags.HasReturn) == hasReturn,
            "the existing response shape must be preserved");
        Ensure(flags.HasFlag(ProtocolV2FrameFlags.HasTimeBudget) == hasMethodTimeout,
            "only the method-timeout control case may publish a deadline");
        Ensure(!flags.HasFlag(ProtocolV2FrameFlags.OneWay), "these calls must not become OneWay");
    }

    private readonly struct EmptyClientStreams : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
