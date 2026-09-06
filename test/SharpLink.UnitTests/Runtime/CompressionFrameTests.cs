using System.IO.Pipelines;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class CompressionFrameTests
{
    [Test]
    public async Task RequestAndStreamPayloadPrefixesShouldRemainUncompressed()
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider);
        var source = Enumerable.Repeat((byte)0x4c, 4096).ToArray();
        var compressed = Compress(provider, source);

        var requestWire = new PooledByteBufferWriter();
        requestWire.Write(new byte[ProtocolV2Constants.RequestPrefixBytes]);
        WriteOriginalLength(requestWire, source.Length);
        requestWire.Write(compressed);
        var decodedRequest = session.DecodeInboundPayload(
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.Compressed,
            CreateSegmented(requestWire.WrittenMemory.ToArray(), 19),
            CancellationToken.None,
            out var requestOwner);
        try
        {
            Ensure(decodedRequest.Length == ProtocolV2Constants.RequestPrefixBytes + source.Length,
                "request decoded length");
            Ensure(decodedRequest.Slice(ProtocolV2Constants.RequestPrefixBytes).ToArray().SequenceEqual(source),
                "request business payload");
        }
        finally
        {
            session.ReturnDecodedPayload(requestOwner);
        }

        var streamWire = new PooledByteBufferWriter();
        var streamId = streamWire.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(streamId, 7);
        streamWire.Advance(sizeof(ushort));
        WriteOriginalLength(streamWire, source.Length);
        streamWire.Write(compressed);
        var decodedStream = session.DecodeInboundPayload(
            ProtocolV2FrameType.StreamData,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(streamWire.WrittenMemory),
            CancellationToken.None,
            out var streamOwner);
        try
        {
            Ensure(BinaryPrimitives.ReadUInt16LittleEndian(decodedStream.FirstSpan) == 7,
                "stream ID prefix");
            Ensure(decodedStream.Slice(sizeof(ushort)).ToArray().SequenceEqual(source),
                "stream business payload");
        }
        finally
        {
            session.ReturnDecodedPayload(streamOwner);
        }
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("corrupt")]
    [Arguments("trailing")]
    public async Task InvalidCompressedBodyShouldMapToDataLoss(string mutation)
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider);
        var source = Enumerable.Repeat((byte)0x6d, 4096).ToArray();
        var compressed = Compress(provider, source).ToList();
        switch (mutation)
        {
            case "truncated":
                compressed.RemoveAt(compressed.Count - 1);
                break;
            case "corrupt":
                compressed[compressed.Count / 2] ^= 0x80;
                break;
            case "trailing":
                compressed.Add(0xff);
                break;
        }

        using var wire = new PooledByteBufferWriter();
        WriteOriginalLength(wire, source.Length);
        wire.Write(compressed.ToArray());
        var exception = CaptureSharpLinkException(() => session.DecodeInboundPayload(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(wire.WrittenMemory),
            CancellationToken.None,
            out _));
        Ensure(exception.Code == SharpLinkErrorCode.DataLoss, $"{mutation} maps to DataLoss");
    }

    [Test]
    public async Task OriginalLengthShouldBeValidatedBeforeRentingOutput()
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider);
        using var wire = new PooledByteBufferWriter();
        var length = wire.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(length, uint.MaxValue);
        wire.Advance(sizeof(uint));
        wire.Write(new byte[] { 1 });

        var exception = CaptureSharpLinkException(() => session.DecodeInboundPayload(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(wire.WrittenMemory),
            CancellationToken.None,
            out _));
        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation, "uint32 original length bound");
    }

    [Test]
    public async Task UnnegotiatedCompressedFrameShouldBeProtocolViolation()
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider, enableCompression: false);
        var exception = CaptureSharpLinkException(() => session.DecodeInboundPayload(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(new byte[5]),
            CancellationToken.None,
            out _));
        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation, "unnegotiated compressed frame");
    }

    [Test]
    public async Task OversizedOriginalPayloadShouldBeRejectedBeforeCompression()
    {
        var provider = new ThrowIfCompressedProvider();
        await using var session = CreateSession(
            provider,
            maxFramePayloadBytes: SharpLinkProtocolOptions.MinMaxFramePayloadBytes);
        var writer = session.RuntimeContext.Buffers.Rent(
            ProtocolV2Constants.HeaderBytes + SharpLinkProtocolOptions.MinMaxFramePayloadBytes + 1);
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId: 1))
        {
            writer.Write(new byte[SharpLinkProtocolOptions.MinMaxFramePayloadBytes + 1]);
        }

        var exception = CaptureSharpLinkException(() => session.SendPacket(writer));

        Ensure(exception.Code == SharpLinkErrorCode.ResourceExhausted,
            "oversized original payload should fail locally");
        Ensure(provider.CompressCount == 0, "provider must not receive oversized original payload");
    }

    [Test]
    public async Task CompressionFailureShouldReturnAcquiredStreamCredit()
    {
        var provider = new ThrowIfCompressedProvider();
        await using var session = CreateSession(provider, enableFlowControl: true);

        var exception = await CaptureSharpLinkExceptionAsync(
            session.SendStreamChunkAsync(
                requestId: 1,
                streamId: 1,
                Enumerable.Repeat((byte)0x2a, 2048).ToArray()).AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.Internal,
            "provider failure should remain a call-level Internal error");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await session.AcquireStreamSendCreditAsync(2, 1, 4096, timeout.Token);
    }

    private static RpcSession CreateSession(
        ISharpLinkCompressionProvider provider,
        bool enableCompression = true,
        bool enableFlowControl = false,
        int? maxFramePayloadBytes = null)
    {
        var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "compression-frame-test",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        var capabilities =
            (enableCompression ? ProtocolV2Capabilities.Compression : ProtocolV2Capabilities.None) |
            (enableFlowControl ? ProtocolV2Capabilities.FlowControl : ProtocolV2Capabilities.None);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            capabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes: enableFlowControl ? 4096 : null,
            connectionReceiveWindowBytes: enableFlowControl ? 4096 : null,
            compressionBinding: enableCompression ? context.Compression.ProviderBindings[0] : null);
        return session;
    }

    private static byte[] Compress(
        ISharpLinkCompressionProvider provider,
        byte[] source)
    {
        using var writer = new PooledByteBufferWriter(source.Length);
        provider.TryCompress(new ReadOnlySequence<byte>(source), writer, source.Length);
        return writer.WrittenMemory.ToArray();
    }

    private static void WriteOriginalLength(IBufferWriter<byte> writer, int length)
    {
        var span = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, checked((uint)length));
        writer.Advance(sizeof(uint));
    }

    private static SharpLinkException CaptureSharpLinkException(Action action)
    {
        try
        {
            action();
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected SharpLinkException.");
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task action)
    {
        try
        {
            await action;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected SharpLinkException.");
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            var segment = new Segment(bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset)));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Compression frame assertion failed: {scenario}.");
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal void SetNext(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    private sealed class ThrowIfCompressedProvider : ISharpLinkCompressionProvider
    {
        internal int CompressCount { get; private set; }
        public string WireProfile => "test-oversized";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            CompressCount++;
            throw new InvalidOperationException("Oversized payload reached the provider.");
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
