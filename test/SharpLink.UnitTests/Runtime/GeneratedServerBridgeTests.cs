using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public class GeneratedServerBridgeTests
{
    [Test]
    public async Task DuplicateInboundRegistrationShouldReturnDispatcherWithoutPublishingPartialState()
    {
        PooledAsyncStreamDispatcher<BridgeItem>.ClearPoolForTests();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            "bridge-register-rollback",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        session.BindRuntimeContext(new SharpLinkRuntimeContextBuilder().Build());
        var existing = new TrackingDispatcher();
        session.StreamManager.Register(41, 1, existing);

        Exception? failure = null;
        try
        {
            _ = ((IRpcGeneratedServerBridge)session).CreateInboundStream(
                41,
                1,
                new BridgeItemCodec(),
                payloadNullable: false,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is InvalidOperationException,
            "duplicate registration must preserve the StreamManager conflict");
        Ensure(((StreamManager)session.StreamManager).ActiveStreamCount == 1,
            "failed registration must not publish a second stream");
        Ensure(existing.CompletionCount == 0,
            "candidate rollback must not terminate the previously published stream");
        Ensure(PooledAsyncStreamDispatcher<BridgeItem>.RetainedCountForTests == 1,
            "the unpublished dispatcher must be returned to its pool exactly once");

        session.StreamManager.CompleteStream(41, 1, exception: null);
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
        PooledAsyncStreamDispatcher<BridgeItem>.ClearPoolForTests();
    }

    [Test]
    public async Task SuccessfulOutboundPumpShouldEmitOneSuccessTerminal()
    {
        var frames = await PumpAndReadFramesAsync(Values(1, 2, 3));

        Ensure(frames.Count == 4, "three items and one terminal frame must be emitted");
        Ensure(frames.Count(static frame => frame.Type == ProtocolV2FrameType.StreamData) == 3,
            "every service item must produce one data frame");
        Ensure(frames.Count(static frame => frame.Type == ProtocolV2FrameType.StreamComplete) == 1,
            "successful pumping must emit exactly one terminal frame");
        Ensure(frames[^1] == (ProtocolV2FrameType.StreamComplete, ProtocolV2FrameFlags.None),
            "the final frame must be a non-error completion");
    }

    [Test]
    public async Task ThrowingOutboundPumpShouldEmitOneErrorTerminalAndNoSuccessTerminal()
    {
        var frames = await PumpAndReadFramesAsync(ValueThenFailure());

        Ensure(frames.Count == 2, "one item and one terminal frame must be emitted");
        Ensure(frames[0].Type == ProtocolV2FrameType.StreamData,
            "the item accepted before the service failure must remain ordered first");
        Ensure(frames.Count(static frame => frame.Type == ProtocolV2FrameType.StreamComplete) == 1,
            "a service failure must emit exactly one terminal frame");
        Ensure(frames[^1].Type == ProtocolV2FrameType.StreamComplete &&
               (frames[^1].Flags & ProtocolV2FrameFlags.Error) != 0,
            "the unique terminal frame must carry the error flag");
    }

    [Test]
    public async Task ThrowingOutboundCodecShouldEmitOneErrorTerminalAndNoDataOrSuccessTerminal()
    {
        var frames = await PumpAndReadFramesAsync(Values(1), new ThrowingIntCodec());

        Ensure(frames.Count == 1,
            "a serialization failure before publication must emit only its terminal frame");
        Ensure(frames.Count(static frame => frame.Type == ProtocolV2FrameType.StreamData) == 0,
            "a failed serialization must not publish a partial data frame");
        Ensure(frames.Count(static frame => frame.Type == ProtocolV2FrameType.StreamComplete) == 1,
            "a failed serialization must emit exactly one terminal frame");
        Ensure(frames.Count(static frame =>
                   frame.Type == ProtocolV2FrameType.StreamComplete &&
                   (frame.Flags & ProtocolV2FrameFlags.Error) == 0) == 0,
            "a failed serialization must not emit a success terminal");
        Ensure((frames[0].Flags & ProtocolV2FrameFlags.Error) != 0,
            "the serialization failure terminal must carry the error flag");
    }

    [Test]
    public async Task BackpressuredOutboundPumpShouldResumeWithOneDataAndOneSuccessTerminal()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            "bridge-outbound-backpressure",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        session.BindRuntimeContext(new SharpLinkRuntimeContextBuilder().Build());
        session.NegotiatedCapabilities = ProtocolV2Capabilities.FlowControl;
        session.EnableStreamFlowControl(streamWindowBytes: 4, connectionWindowBytes: 4);
        await session.AcquireStreamSendCreditAsync(72, 0, 4, CancellationToken.None);
        var serialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pump = ((IRpcGeneratedServerBridge)session).PumpOutboundStreamAsync(
            73,
            0,
            Values(1),
            new SignalingIntCodec(serialized),
            payloadNullable: false,
            contractId: 101,
            methodId: 202,
            CancellationToken.None);
        await serialized.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(!pump.IsCompleted,
            "the generated bridge must await exhausted connection credit before publishing data");
        session.ApplyWindowUpdate(72, new ProtocolV2WindowUpdate(0, 4));
        await pump.AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var frames = await FlushAndReadFramesAsync(session, output, expectedRequestId: 73);
        Ensure(frames.Count == 2, "one resumed item and one terminal frame must be emitted");
        Ensure(frames[0] == (ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None),
            "the resumed item must be published exactly once before the terminal");
        Ensure(frames[1] == (ProtocolV2FrameType.StreamComplete, ProtocolV2FrameFlags.None),
            "the resumed stream must end with exactly one success terminal");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static async Task<List<(ProtocolV2FrameType Type, ProtocolV2FrameFlags Flags)>>
        PumpAndReadFramesAsync(IAsyncEnumerable<int> stream, IRpcCodec<int>? codec = null)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            "bridge-outbound-pump",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        session.BindRuntimeContext(new SharpLinkRuntimeContextBuilder().Build());

        await ((IRpcGeneratedServerBridge)session).PumpOutboundStreamAsync(
            73,
            0,
            stream,
            codec ?? session.RuntimeContext.Codecs.GetCodec<int>(),
            payloadNullable: false,
            contractId: 101,
            methodId: 202,
            CancellationToken.None);
        var frames = await FlushAndReadFramesAsync(session, output, expectedRequestId: 73);
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
        return frames;
    }

    private static async Task<List<(ProtocolV2FrameType Type, ProtocolV2FrameFlags Flags)>>
        FlushAndReadFramesAsync(RpcSession session, Pipe output, ulong expectedRequestId)
    {
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var remaining = read.Buffer;
        var frames = new List<(ProtocolV2FrameType, ProtocolV2FrameFlags)>();
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref remaining,
                   session.RuntimeContext.Protocol,
                   out var header,
                   out _))
        {
            Ensure(header.RequestId == expectedRequestId,
                "every bridge frame must retain the request ID");
            frames.Add((header.Type, header.Flags));
        }
        Ensure(remaining.IsEmpty, "the bridge output must contain only complete Protocol v2 frames");
        output.Reader.AdvanceTo(read.Buffer.End);
        return frames;
    }

    private static async IAsyncEnumerable<int> Values(params int[] values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> ValueThenFailure()
    {
        yield return 7;
        await Task.Yield();
        throw new InvalidOperationException("service stream failed");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed record BridgeItem(int Value);

    private sealed class BridgeItemCodec : IRpcCodec<BridgeItem>
    {
        public void Serialize(in BridgeItem value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BitConverter.TryWriteBytes(span, value.Value);
            buffer.Advance(sizeof(int));
        }

        public BridgeItem Deserialize(in ReadOnlySequence<byte> buffer)
            => new(BitConverter.ToInt32(buffer.FirstSpan));
    }

    private sealed class ThrowingIntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            _ = value;
            _ = buffer;
            throw new InvalidOperationException("codec serialization failed");
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
        {
            _ = buffer;
            throw new NotSupportedException();
        }
    }

    private sealed class SignalingIntCodec(TaskCompletionSource serialized) : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BitConverter.TryWriteBytes(span, value);
            buffer.Advance(sizeof(int));
            serialized.TrySetResult();
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.FirstSpan);
    }

    private sealed class TrackingDispatcher : IStreamDispatcher
    {
        private int _completionCount;
        internal int CompletionCount => Volatile.Read(ref _completionCount);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage)
            => Interlocked.Increment(ref _completionCount);
        public void Complete(Exception? exception)
            => Interlocked.Increment(ref _completionCount);
    }
}
