using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public class RpcSessionLifecycleTests
{
    [Test]
    public async Task FirstFaultShouldBePublishedOnceAndReusedByLaterSends()
    {
        var input = new Pipe();
        var output = new Pipe();
        var disconnectCount = 0;
        await using var session = new RpcSession(
            "first-fault",
            input.Reader,
            output.Writer,
            () => Interlocked.Increment(ref disconnectCount),
            static () => true);
        var publishedCount = 0;
        Exception? published = null;
        session.OnDisconnected += exception =>
        {
            published = exception;
            Interlocked.Increment(ref publishedCount);
        };
        var first = new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "bad frame");

        session.NotifyDisconnected(first);
        session.NotifyDisconnected(new IOException("later failure"));

        var thrown = CaptureSendException(session);
        Ensure(ReferenceEquals(first, published), "the first structured failure should be published");
        Ensure(ReferenceEquals(first, thrown), "later sends should receive the first failure instance");
        Ensure(publishedCount == 1, "disconnect should be published once");
        Ensure(disconnectCount == 1, "transport should be disconnected once");
    }

    [Test]
    public async Task ConcurrentReadAndWriteFaultsShouldConvergeToOneTerminalState()
    {
        var input = new Pipe();
        var output = new Pipe();
        var disconnectCount = 0;
        await using var session = new RpcSession(
            "concurrent-fault",
            input.Reader,
            output.Writer,
            () => Interlocked.Increment(ref disconnectCount),
            static () => true);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedCount = 0;
        session.OnDisconnected += exception =>
        {
            Interlocked.Increment(ref publishedCount);
            disconnected.TrySetResult(exception);
        };
        await output.Reader.CompleteAsync();
        var readFailure = new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "reader failed");

        var readFault = Task.Run(() => session.NotifyDisconnected(readFailure));
        var writeFault = Task.Run(() => session.SendPacket(CreatePacket(session)));
        try
        {
            await writeFault;
        }
        catch (SharpLinkException)
        {
        }
        await readFault;

        var terminal = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var laterSend = CaptureSendException(session);
        Ensure(terminal is SharpLinkException, "terminal failure should be structured");
        Ensure(ReferenceEquals(terminal, laterSend), "all waiters should observe the terminal instance");
        Ensure(publishedCount == 1, "competing faults should publish one disconnect");
        Ensure(disconnectCount == 1, "competing faults should close transport once");
    }

    [Test]
    public async Task ConcurrentSendAndDisposeShouldCompletePumpAndReturnCleanly()
    {
        var input = new Pipe();
        var output = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024));
        var disconnectCount = 0;
        var session = new RpcSession(
            "send-dispose",
            input.Reader,
            output.Writer,
            () => Interlocked.Increment(ref disconnectCount),
            static () => true);
        var failures = new ConcurrentBag<SharpLinkException>();
        var senders = new Task[4];
        for (var senderIndex = 0; senderIndex < senders.Length; senderIndex++)
        {
            senders[senderIndex] = Task.Run(() =>
            {
                for (var packetIndex = 0; packetIndex < 1000; packetIndex++)
                {
                    try
                    {
                        session.SendPacket(CreatePacket(session));
                    }
                    catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
                    {
                        failures.Add(exception);
                    }
                }
            });
        }

        await Task.Yield();
        var dispose1 = session.DisposeAsync().AsTask();
        var dispose2 = session.DisposeAsync().AsTask();
        await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(dispose1, dispose2).WaitAsync(TimeSpan.FromSeconds(5));

        Ensure(disconnectCount == 1, "concurrent disposal should close transport once");
        foreach (var failure in failures)
            Ensure(failure.Code == SharpLinkErrorCode.ConnectionClosed, "closed sends should be structured");
    }

    [Test]
    public async Task NotifyConnectedAfterDisposeShouldNotReopenConnectionMetric()
    {
        const string side = "late-notify-test";
        var balance = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.connections.active")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && Equals(tag.Value, side))
                {
                    Interlocked.Add(ref balance, measurement);
                    break;
                }
            }
        });
        listener.Start();

        var input = new Pipe();
        var output = new Pipe();
        var session = new RpcSession(
            "late-notify",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        session.SetTelemetrySide(side);

        await session.DisposeAsync();
        session.NotifyConnected();

        Ensure(Volatile.Read(ref balance) == 0, "a terminal session must not reopen its connection metric");
    }

    [Test]
    public async Task ConnectionThresholdShouldSendCreditForEveryContributingStream()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            "flow-credit-flush",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        session.BindRuntimeContext(new SharpLinkRuntimeContextBuilder().Build());
        session.NegotiatedCapabilities = ProtocolV2Capabilities.FlowControl;
        session.EnableStreamFlowControl(4, 4);
        session.StreamManager.Register(1, 1, new ImmediateConsumingDispatcher());
        session.StreamManager.Register(2, 1, new ImmediateConsumingDispatcher());

        await session.StreamManager.DispatchChunkAsync(1, 1, new ReadOnlySequence<byte>(new byte[1]));
        await session.StreamManager.DispatchChunkAsync(2, 1, new ReadOnlySequence<byte>(new byte[1]));
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var frames = read.Buffer;
        var updates = new List<(ulong RequestId, ProtocolV2WindowUpdate Update)>();
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref frames,
                   session.RuntimeContext.Protocol,
                   out var header,
                   out var payload))
        {
            Ensure(header.Type == ProtocolV2FrameType.WindowUpdate, "flow-control flush must only emit window updates");
            updates.Add((header.RequestId, ProtocolV2PayloadCodec.ReadWindowUpdate(payload)));
        }
        output.Reader.AdvanceTo(read.Buffer.End);

        Ensure(updates.Count == 2, "both contributing streams must receive one window update");
        Ensure(updates.Contains((1, new ProtocolV2WindowUpdate(1, 1))), "the first stream credit must be returned");
        Ensure(updates.Contains((2, new ProtocolV2WindowUpdate(1, 1))), "the triggering stream credit must be returned");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static SharpLinkException CaptureSendException(RpcSession session)
    {
        try
        {
            session.SendPacket(CreatePacket(session));
            throw new Exception("send should fail after the session terminates");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static IRpcByteBufferWriter CreatePacket(RpcSession session)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        writer.WritePacket(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1);
        return writer;
    }

    private sealed class ImmediateConsumingDispatcher : IStreamConsumptionAwareDispatcher
    {
        private Action<long, ushort, int>? _bytesConsumed;
        private long _requestId;
        private ushort _streamId;

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => DispatchAsync(payload, checked((int)payload.Length));

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            _ = payload;
            _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception) => _ = exception;

        public void SetBytesConsumedCallback(
            Action<long, ushort, int>? callback,
            long requestId,
            ushort streamId)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
