using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientLifecycleHeartbeatSupport
{
    internal static Task GetSessionStoppedTask(RpcSession session)
        => ((TaskCompletionSource<bool>)(typeof(RpcSession).GetField(
            "_stoppedTcs",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(session) ?? throw new Exception("cannot find session stop owner"))).Task;

    internal static async Task YieldUntilAsync(Func<bool> condition, string failureMessage)
    {
        for (var attempt = 0; attempt < 128 && !condition(); attempt++)
            await Task.Yield();
        Ensure(condition(), failureMessage);
    }

    internal static void EnsureTimestampFrame(
        ReadOnlyMemory<byte> bytes,
        SharpLinkProtocolOptions limits,
        ProtocolV2FrameType expectedType,
        long? expectedTimestamp)
        => EnsureTimestampFrame(
            new ReadOnlySequence<byte>(bytes),
            limits,
            expectedType,
            expectedTimestamp);

    internal static void EnsureTimestampFrame(
        ReadOnlySequence<byte> bytes,
        SharpLinkProtocolOptions limits,
        ProtocolV2FrameType expectedType,
        long? expectedTimestamp)
    {
        var remaining = bytes;
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, limits, out var header, out var payload))
        {
            if (header.Type != expectedType)
                continue;

            Ensure(header.RequestId == 0 && header.Flags == ProtocolV2FrameFlags.None,
                $"{expectedType} must retain its control-frame header");
            Ensure(payload.Length == sizeof(long), $"{expectedType} must retain its timestamp payload");
            var timestamp = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload.ToArray());
            Ensure(expectedTimestamp is { } expected
                    ? timestamp == expected
                    : timestamp > 0,
                $"{expectedType} must retain the expected monotonic timestamp");
            return;
        }

        throw new Exception($"{expectedType} frame was not emitted");
    }

    internal static void EnsureHealthResponseFrame(
        ReadOnlySequence<byte> bytes,
        SharpLinkProtocolOptions limits,
        ulong expectedRequestId,
        SharpLinkHealthStatus expectedStatus)
    {
        var remaining = bytes;
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, limits, out var header, out var payload))
        {
            if (header.Type != ProtocolV2FrameType.HealthResponse)
                continue;

            Ensure(header.RequestId == expectedRequestId && header.Flags == ProtocolV2FrameFlags.None,
                "HealthResponse must retain its request identity and control-frame flags");
            Ensure(ProtocolV2PayloadCodec.ReadHealthResponse(payload).Status == expectedStatus,
                "HealthResponse must retain its exact status payload");
            return;
        }

        throw new Exception($"HealthResponse frame {expectedRequestId} was not emitted");
    }

    internal sealed class BlockingFlushPipeWriter : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly TaskCompletionSource<FlushResult> _flush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushCount;

        internal TaskCompletionSource FirstFlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondFlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override void CancelPendingFlush() => _flush.TrySetResult(new FlushResult(true, false));
        public override void Complete(Exception? exception = null) => ReleaseFlush();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _flushCount) == 1)
                FirstFlushStarted.TrySetResult();
            else
                SecondFlushStarted.TrySetResult();
            return new ValueTask<FlushResult>(_flush.Task.WaitAsync(cancellationToken));
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

        internal void ReleaseFlush()
            => _flush.TrySetResult(new FlushResult(isCanceled: false, isCompleted: false));
    }
}
