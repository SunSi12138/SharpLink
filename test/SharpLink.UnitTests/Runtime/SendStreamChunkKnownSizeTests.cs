using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class SendStreamChunkKnownSizeTests
{
    [Test]
    public async Task SizedPathShouldProduceIdenticalFrameToFallbackPath()
    {
        const int value = 0x12345678;
        var sizedPayload = await PumpSinglePayloadAsync(new SizedIntCodec(), value);
        var fallbackPayload = await PumpSinglePayloadAsync(new NonSizedIntCodec(), value);

        Ensure(
            sizedPayload.AsSpan().SequenceEqual(fallbackPayload),
            "the sized path must emit a byte-for-byte identical StreamData frame");
    }

    [Test]
    public async Task SizeMismatchShouldFailSafelyAndRefundCreditExactlyOnce()
    {
        const int streamWindow = 16;
        const int connectionWindow = 64;
        const int predictedSize = sizeof(int) + 1;
        var (session, input, output) = CreateFlowControlledSession(streamWindow, connectionWindow);
        await using var _ = session;
        var controller = GetFlowController(session);
        var before = controller.SendConnectionCredit;
        var codec = new MismatchedSizedIntCodec();

        try
        {
            await session.SendStreamChunkKnownSizeAsync(
                73,
                0,
                42,
                codec,
                predictedSize,
                sizedSnapshot: null,
                CancellationToken.None);
            throw new Exception("expected size mismatch failure");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("size differed", StringComparison.Ordinal))
        {
        }

        Ensure(
            controller.SendConnectionCredit == before,
            "a predicted/actual size mismatch must return every unsent credit byte");
        try
        {
            session.ReturnUnsentStreamCredit(73, 0, predictedSize);
            throw new Exception("expected double refund failure");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("returned more than once", StringComparison.Ordinal))
        {
        }

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task SerializeSizedFailureAfterCreditShouldRefundExactlyOnce()
    {
        const int streamWindow = 16;
        const int connectionWindow = 64;
        var (session, input, output) = CreateFlowControlledSession(streamWindow, connectionWindow);
        await using var _ = session;
        var controller = GetFlowController(session);
        var before = controller.SendConnectionCredit;
        var codec = new ThrowingSizedIntCodec();

        try
        {
            await session.SendStreamChunkKnownSizeAsync(
                73,
                0,
                7,
                codec,
                sizeof(int),
                sizedSnapshot: null,
                CancellationToken.None);
            throw new Exception("expected sized serialization failure");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("serialization failed", StringComparison.Ordinal))
        {
        }

        Ensure(
            controller.SendConnectionCredit == before,
            "a sized serialization failure after credit must return every unsent credit byte");
        try
        {
            session.ReturnUnsentStreamCredit(73, 0, sizeof(int));
            throw new Exception("expected double refund failure");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("returned more than once", StringComparison.Ordinal))
        {
        }

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task CanceledBeforeCreditShouldNotSerializeOrDebitCredit()
    {
        const int streamWindow = 16;
        const int connectionWindow = 64;
        var (session, input, output) = CreateFlowControlledSession(streamWindow, connectionWindow);
        await using var _ = session;
        var controller = GetFlowController(session);
        var before = controller.SendConnectionCredit;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var serialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var codec = new SignalingSizedIntCodec(serialized);

        try
        {
            await session.SendStreamChunkKnownSizeAsync(
                73,
                0,
                9,
                codec,
                sizeof(int),
                sizedSnapshot: null,
                cancellation.Token);
            throw new Exception("expected cancellation failure");
        }
        catch (OperationCanceledException)
        {
        }

        Ensure(!serialized.Task.IsCompleted, "cancellation before credit must not serialize the item");
        Ensure(controller.SendConnectionCredit == before, "cancellation before credit must not debit credit");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task SerializationCancellationAfterCreditShouldRefundExactlyOnce()
    {
        const int streamWindow = 16;
        const int connectionWindow = 64;
        var (session, input, output) = CreateFlowControlledSession(streamWindow, connectionWindow);
        await using var _ = session;
        var controller = GetFlowController(session);
        var before = controller.SendConnectionCredit;
        var codec = new CancelThrowingSizedIntCodec();

        try
        {
            await session.SendStreamChunkKnownSizeAsync(
                73,
                0,
                11,
                codec,
                sizeof(int),
                sizedSnapshot: null,
                CancellationToken.None);
            throw new Exception("expected cancellation during sized serialization");
        }
        catch (OperationCanceledException)
        {
        }

        Ensure(
            controller.SendConnectionCredit == before,
            "cancellation after credit but before send must refund every unsent credit byte");
        try
        {
            session.ReturnUnsentStreamCredit(73, 0, sizeof(int));
            throw new Exception("expected double refund failure");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("returned more than once", StringComparison.Ordinal))
        {
        }

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task ZeroSizedPayloadShouldDebitExactlyOneCreditByte()
    {
        const int streamWindow = 16;
        const int connectionWindow = 64;
        var (session, input, output) = CreateFlowControlledSession(streamWindow, connectionWindow);
        await using var _ = session;
        var controller = GetFlowController(session);
        var before = controller.SendConnectionCredit;
        var codec = new ZeroSizedCodec();

        await session.SendStreamChunkKnownSizeAsync(
            73,
            0,
            Array.Empty<byte>(),
            codec,
            encodedBytes: 0,
            sizedSnapshot: null,
            CancellationToken.None);

        Ensure(
            controller.SendConnectionCredit == before - 1,
            "a zero-sized payload must debit exactly one flow-control byte");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task MaxFramePayloadBoundaryShouldSucceed()
    {
        const int maxFramePayloadBytes = 1024;
        const int itemBytes = maxFramePayloadBytes - sizeof(ushort);
        var (session, input, output) = CreateFlowControlledSession(
            streamWindow: itemBytes,
            connectionWindow: itemBytes * 2,
            maxFramePayloadBytes: maxFramePayloadBytes);
        await using var _ = session;
        var payload = new byte[itemBytes];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)(index + 1);
        var codec = new SizedByteArrayCodec();

        await session.SendStreamChunkKnownSizeAsync(
            73,
            0,
            payload,
            codec,
            encodedBytes: payload.Length,
            sizedSnapshot: null,
            CancellationToken.None);

        var frames = await ReadFramePayloadsAsync(session, output, expectedRequestId: 73);
        Ensure(frames.Count == 1, "the boundary-sized item must publish exactly one frame");
        Ensure(
            frames[0].Length == sizeof(ushort) + itemBytes,
            "the boundary frame body must include the stream id and the full item");
        Ensure(
            frames[0].AsSpan(sizeof(ushort)).SequenceEqual(payload),
            "the boundary-sized item bytes must round-trip unchanged");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task RepeatedSizedSendsShouldReturnCreditToFullWindow()
    {
        const int itemBytes = sizeof(int);
        const int streamWindow = itemBytes * 4;
        const int connectionWindow = 256;
        var (session, input, output) = CreateFlowControlledSession(streamWindow, connectionWindow);
        await using var _ = session;
        var controller = GetFlowController(session);
        var codec = new SizedIntCodec();

        for (var index = 0; index < 100; index++)
        {
            await session.SendStreamChunkKnownSizeAsync(
                73,
                0,
                index,
                codec,
                itemBytes,
                sizedSnapshot: null,
                CancellationToken.None);
            session.ApplyWindowUpdate(73, new ProtocolV2WindowUpdate(0, itemBytes));
        }

        Ensure(
            controller.SendConnectionCredit == connectionWindow,
            "repeated sized sends plus window updates must leave no credit leak");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static async Task<byte[]> PumpSinglePayloadAsync(IRpcCodec<int> codec, int value)
    {
        var (session, input, output) = CreateFlowControlledSession(streamWindow: 64, connectionWindow: 256);
        await using var _ = session;
        await new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
            73,
            0,
            SingleValue(value),
            codec,
            payloadNullable: false,
            contractId: 101,
            methodId: 202,
            CancellationToken.None);
        var frames = await ReadFramePayloadsAsync(session, output, expectedRequestId: 73);
        Ensure(frames.Count == 2, "one stream data frame and one terminal frame must be emitted");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
        return frames[0];
    }

    private static (RpcSession Session, Pipe Input, Pipe Output) CreateFlowControlledSession(
        int streamWindow,
        int connectionWindow,
        int maxFramePayloadBytes = 1024)
    {
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            Guid.NewGuid().ToString("N"),
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            maxFramePayloadBytes: maxFramePayloadBytes,
            streamReceiveWindowBytes: streamWindow,
            connectionReceiveWindowBytes: connectionWindow);
        return (session, input, output);
    }

    private static async Task<List<byte[]>> ReadFramePayloadsAsync(
        RpcSession session,
        Pipe output,
        ulong expectedRequestId)
    {
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var remaining = read.Buffer;
        var payloads = new List<byte[]>();
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref remaining,
                   session.RuntimeContext.Protocol,
                   out var header,
                   out var payload))
        {
            Ensure(header.RequestId == expectedRequestId, "every frame must retain the request id");
            payloads.Add(payload.ToArray());
        }
        Ensure(remaining.IsEmpty, "the output must contain only complete Protocol v2 frames");
        output.Reader.AdvanceTo(read.Buffer.End);
        return payloads;
    }

    private static StreamFlowController GetFlowController(RpcSession session)
    {
        var field = typeof(RpcSession).GetField(
            "_protocolState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RpcSession._protocolState field was not found.");
        var state = (RpcSessionProtocolState)field.GetValue(session)!;
        return state.FlowController
            ?? throw new InvalidOperationException("The test session did not negotiate flow control.");
    }

    private static async IAsyncEnumerable<int> SingleValue(int value)
    {
        yield return value;
        await Task.Yield();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void WriteInt(in int value, IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        buffer.Advance(sizeof(int));
    }

    private sealed class NonSizedIntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) => WriteInt(value, buffer);

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);
    }

    private class SizedIntCodec : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        public bool CanExactSize => true;

        public virtual void Serialize(in int value, IBufferWriter<byte> buffer) => WriteInt(value, buffer);

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);

        public bool TryGetEncodedSize(in int value, out int size)
        {
            size = sizeof(int);
            return true;
        }

        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        {
            snapshot = null;
            size = sizeof(int);
            return true;
        }

        public virtual void SerializeSized(
            in int value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => Serialize(value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class MismatchedSizedIntCodec : SizedIntCodec
    {
        public new bool TryGetEncodedSize(in int value, out int size)
        {
            size = sizeof(int) + 1;
            return true;
        }

        public new bool TryGetEncodedSize(
            in int value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            snapshot = null;
            size = sizeof(int) + 1;
            return true;
        }
    }

    private sealed class ThrowingSizedIntCodec : SizedIntCodec
    {
        public override void SerializeSized(
            in int value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => throw new InvalidOperationException("serialization failed");
    }

    private sealed class CancelThrowingSizedIntCodec : SizedIntCodec
    {
        public override void SerializeSized(
            in int value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => throw new OperationCanceledException("canceled during serialization");
    }

    private sealed class SignalingSizedIntCodec(TaskCompletionSource serialized) : SizedIntCodec
    {
        public override void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            base.Serialize(value, buffer);
            serialized.TrySetResult();
        }

        public override void SerializeSized(
            in int value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            base.SerializeSized(value, buffer, size, snapshot);
            serialized.TrySetResult();
        }
    }

    private sealed class ZeroSizedCodec : IRpcCodec<byte[]>, IRpcSizedCodec<byte[]>
    {
        public bool CanExactSize => true;

        public void Serialize(in byte[] value, IBufferWriter<byte> buffer)
        {
            _ = value;
            _ = buffer;
        }

        public byte[] Deserialize(in ReadOnlySequence<byte> buffer) => Array.Empty<byte>();

        public bool TryGetEncodedSize(in byte[] value, out int size)
        {
            size = 0;
            return true;
        }

        public bool TryGetEncodedSize(
            in byte[] value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            snapshot = null;
            size = 0;
            return true;
        }

        public void SerializeSized(
            in byte[] value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            _ = value;
            _ = buffer;
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class SizedByteArrayCodec : IRpcCodec<byte[]>, IRpcSizedCodec<byte[]>
    {
        public bool CanExactSize => true;

        public void Serialize(in byte[] value, IBufferWriter<byte> buffer) => buffer.Write(value);

        public byte[] Deserialize(in ReadOnlySequence<byte> buffer) => buffer.ToArray();

        public bool TryGetEncodedSize(in byte[] value, out int size)
        {
            size = value.Length;
            return true;
        }

        public bool TryGetEncodedSize(
            in byte[] value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            snapshot = null;
            size = value.Length;
            return true;
        }

        public void SerializeSized(
            in byte[] value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => buffer.Write(value);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }
}
