using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractShapeTests : ContractAdmissionTestSupport
{
    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task RawClientChannelMustFailClosedDespiteMatchingLocalAndRemoteContract(RpcMethodKind kind)
    {
        var transport = new TestClientTransportFactory();
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        _ = client.Get<IOrdersContract>();
        var codec = new CountingRequestCodec();
        var failure = await CaptureExceptionAsync(InvokeShape((IRpcChannel)client, ShapeMethod(kind), codec, default).WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            $"{kind}: raw channel must not infer mutable registered identity");
        Ensure(codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0, "raw rejection precedes business work");
    }

    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task EveryShapeMustFailClosedBeforeSerialization(RpcMethodKind kind)
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var codec = new CountingRequestCodec();
        var channel = ((OrdersProxy)client.Get<IOrdersContract>()).Channel;
        var failure = await CaptureExceptionAsync(InvokeShape(channel, ShapeMethod(kind), codec, default).WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkContractAdmissionException { Code: SharpLinkErrorCode.Unavailable }, $"{kind} must fail admission");
        Ensure(codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0, $"{kind} must reject before serialization");
    }

    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task EveryShapeMustCleanUpCanceledPreSendWait(RpcMethodKind kind)
    {
        var transport = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var codec = new CountingRequestCodec();
        var channel = ((OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Availability = RpcAvailability.WaitForCompatible })).Channel;
        var call = InvokeShape(channel, ShapeMethod(kind), codec, cancellation.Token);
        Ensure(!call.IsCompleted && ((SharpLinkClient)client).ContractWaiterCountForTesting == 1, $"{kind} must wait before sending");
        cancellation.Cancel();
        var failure = await CaptureExceptionAsync(call.WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is OperationCanceledException or SharpLinkException { Code: SharpLinkErrorCode.Cancelled }, $"{kind} preserves cancellation");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0 && codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0,
            $"{kind} must release the waiter without business work");
    }

    [Test]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task AdmittedStreamMustRemainPinnedWhenCompatibilityMoves(RpcMethodKind kind)
    {
        var original = new TestClientTransportFactory();
        var replacement = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseEndpoints([Endpoint("original", 7601), Endpoint("replacement", 7602)],
            endpoint => endpoint.Id == "original" ? original : replacement).Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);
        var channel = ((OrdersProxy)client.Get<IOrdersContract>()).Channel;
        var codec = new CountingRequestCodec();
        var response = channel.RuntimeContext.Codecs.GetCodec<int>();
        var method = ShapeMethod(kind);
        var stream = kind == RpcMethodKind.ServerStreaming
            ? channel.InvokeServerStreamingAsync(method, 42, codec, response, null)
            : channel.InvokeDuplexStreamingAsync(method, 42, codec, response, default(RpcNoClientStreams), null);
        await using var enumerator = stream.GetAsyncEnumerator();
        var first = enumerator.MoveNextAsync().AsTask();
        var request = await original.Connection.WaitForSentFrame(ProtocolV2FrameType.Request).WaitAsync(RaceCoordinationTimeout);
        await InjectStreamValue(original.Connection, request.Header.RequestId, 7);
        Ensure(await first.WaitAsync(RaceCoordinationTimeout) && enumerator.Current == 7, "original stream starts");
        await PublishManifest(original.Connection, 1, new RpcHash128(1, 2));
        await PublishManifest(replacement.Connection, 1, Manifest.Instance.RpcAssemblyHash);
        var next = enumerator.MoveNextAsync().AsTask();
        await InjectStreamValue(original.Connection, request.Header.RequestId, 9);
        Ensure(await next.WaitAsync(RaceCoordinationTimeout) && enumerator.Current == 9,
            "a manifest update must not migrate or reject an admitted stream");
        await original.Connection.InjectFrameAsync(ProtocolV2FrameType.StreamComplete, ProtocolV2FrameFlags.None,
            request.Header.RequestId, new byte[sizeof(ushort)]);
        Ensure(!await enumerator.MoveNextAsync().AsTask().WaitAsync(RaceCoordinationTimeout), "original stream completes");
        Ensure(codec.SerializeCount == 1 && original.Connection.SentRequestCount == 1 && replacement.Connection.SentRequestCount == 0,
            "stream continuation remains on its physical session without another Request");
    }

    [Test]
    public async Task RetryMustReadmitFrozenRequirementAgainstNextSelectedSession()
    {
        var first = new TestClientTransportFactory();
        var second = new TestClientTransportFactory(contractManifest: []);
        await using var client = Builder().UseEndpoints([Endpoint("first", 7701), Endpoint("second", 7702)],
            endpoint => endpoint.Id == "first" ? first : second).UseRetry(static options =>
            {
                options.MaxAttempts = 2;
                options.InitialBackoff = TimeSpan.FromMilliseconds(1);
                options.MaxBackoff = TimeSpan.FromMilliseconds(1);
                options.JitterRatio = 0;
            }).Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);
        var channel = ((OrdersProxy)client.Get<IOrdersContract>()).Channel;
        var codec = new CountingRequestCodec();
        var method = new RpcMethodDescriptor(OrdersContractId, 1, RpcMethodKind.Unary, true, false, false, null, IsIdempotent: true);
        var call = channel.InvokeUnaryAsync(method, 42, codec, channel.RuntimeContext.Codecs.GetCodec<int>(), null).AsTask();
        var request = await first.Connection.WaitForSentFrame(ProtocolV2FrameType.Request).WaitAsync(RaceCoordinationTimeout);
        await PublishManifest(first.Connection, 1, new RpcHash128(1, 2));
        await PublishManifest(second.Connection, 1, Manifest.Instance.RpcAssemblyHash);
        using var error = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteError(error, SharpLinkErrorCode.Unavailable, "retry", 1024, out _);
        await first.Connection.InjectFrameAsync(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.Error, request.Header.RequestId, error.WrittenMemory);
        var retried = await second.Connection.WaitForSentFrame(ProtocolV2FrameType.Request).WaitAsync(RaceCoordinationTimeout);
        await second.Connection.InjectInt32ResponseAsync(unchecked((long)retried.Header.RequestId), 7);
        Ensure(await call.WaitAsync(RaceCoordinationTimeout) == 7, "retry selects the currently compatible session");
        Ensure(first.Connection.SentRequestCount == 1 && second.Connection.SentRequestCount == 1, "incompatible original must not receive retry");
    }

    private static RpcMethodDescriptor ShapeMethod(RpcMethodKind kind)
        => new(OrdersContractId, 1, kind, kind != RpcMethodKind.OneWay,
            kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming, false, null,
            ClientStreamCount: kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming ? 1 : 0);

    private static async Task InvokeShape(IRpcChannel channel, RpcMethodDescriptor method, CountingRequestCodec codec, CancellationToken cancellationToken)
    {
        var response = channel.RuntimeContext.Codecs.GetCodec<int>();
        switch (method.Kind)
        {
            case RpcMethodKind.Unary:
                _ = await channel.InvokeUnaryAsync(method, 42, codec, response, null, cancellationToken); return;
            case RpcMethodKind.OneWay:
                await channel.InvokeOneWayAsync(method, 42, codec, default(RpcNoClientStreams), null, cancellationToken); return;
            case RpcMethodKind.ClientStreaming:
                _ = await channel.InvokeClientStreamingAsync(method, 42, codec, response, default(RpcNoClientStreams), null, cancellationToken); return;
            default:
                var stream = method.Kind == RpcMethodKind.ServerStreaming
                    ? channel.InvokeServerStreamingAsync(method, 42, codec, response, null, cancellationToken)
                    : channel.InvokeDuplexStreamingAsync(method, 42, codec, response, default(RpcNoClientStreams), null, cancellationToken);
                await using (var enumerator = stream.GetAsyncEnumerator(cancellationToken)) _ = await enumerator.MoveNextAsync();
                return;
        }
    }

    private static Task InjectStreamValue(TestTransportConnection connection, ulong requestId, int value)
    {
        var payload = new byte[sizeof(ushort) + sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(ushort)), value);
        return connection.InjectFrameAsync(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, requestId, payload);
    }
}
