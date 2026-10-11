using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public abstract class ContractAdmissionTestSupport : SharpLinkMultiClusterClientTestBase
{
    private protected const long OrdersContractId = 8_101;
    private protected static readonly RpcMethodDescriptor Method = new(
        OrdersContractId, 1, RpcMethodKind.Unary, true, false, false, null);

    private protected static SharpClientBuilder Builder()
        => SharpClientBuilder.Create().DisableRequestTimeout()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([Manifest.Instance]));

    private protected static TestClientTransportFactory Transport(RpcHash128 hash)
        => new(contractManifest: [new KeyValuePair<long, RpcHash128>(OrdersContractId, hash)]);

    private protected static Task<int> Invoke(OrdersProxy proxy, CountingRequestCodec codec, CancellationToken cancellationToken = default)
        => proxy.Channel.InvokeUnaryAsync(Method, 42, codec,
            proxy.Channel.RuntimeContext.Codecs.GetCodec<int>(), null, cancellationToken).AsTask();

    private protected static async Task CompleteCall(OrdersProxy proxy, CountingRequestCodec codec, TestTransportConnection connection)
    {
        var call = Invoke(proxy, codec);
        var request = await connection.WaitForSentFrame(ProtocolV2FrameType.Request).WaitAsync(RaceCoordinationTimeout);
        Ensure((request.Header.Flags & ProtocolV2FrameFlags.HasContractIdentity) != 0,
            "bound proxy requests must carry the frozen contract identity");
        Ensure(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(request.Payload.AsSpan(16)) ==
                   Manifest.Instance.RpcAssemblyHash.High &&
               System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(request.Payload.AsSpan(24)) ==
                   Manifest.Instance.RpcAssemblyHash.Low,
            "the request must carry its locally frozen hash, High then Low in little-endian order");
        Ensure(request.Payload[32] == 1 && request.Payload[33] == 0 &&
               System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(request.Payload.AsSpan(34)) == 42,
            "compatible admission must encode version one and mode zero before the business payload");
        await connection.InjectInt32ResponseAsync(unchecked((long)request.Header.RequestId), 7);
        Ensure(await call.WaitAsync(RaceCoordinationTimeout) == 7, "the admitted call must complete");
    }

    private protected static async Task PublishManifest(TestTransportConnection connection, long generation,
        RpcHash128 hash, bool synchronize = true)
    {
        using var payload = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(payload,
            new ProtocolV2ContractManifest(generation,
                [new KeyValuePair<long, RpcHash128>(OrdersContractId, hash)]), new SharpLinkProtocolOptions());
        await connection.InjectFrameAsync(ProtocolV2FrameType.ContractManifest, ProtocolV2FrameFlags.None, 0, payload.WrittenMemory);
        if (!synchronize)
            return;
        // The ordered Ping/Pong exchange acknowledges manifest processing without timing guesses.
        await connection.InjectFrameAsync(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0, new byte[sizeof(long)]);
        await connection.WaitForSentFrame(ProtocolV2FrameType.Pong).WaitAsync(RaceCoordinationTimeout);
    }

    private protected sealed class CountingRequestCodec : IRpcCodec<int>
    {
        private int _serializeCount;
        public int SerializeCount => Volatile.Read(ref _serializeCount);
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            Interlocked.Increment(ref _serializeCount);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value);
            buffer.Advance(sizeof(int));
        }
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new NotSupportedException();
    }
}
