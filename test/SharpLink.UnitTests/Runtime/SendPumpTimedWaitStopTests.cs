using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class SendPumpTimedWaitStopTests
{
    [Test]
    public async Task StopLatchedBeforeTimedWaitMustNotBeConsumedAsObservedData()
    {
        var input = new Pipe();
        var output = new Pipe();
        var blockingWriter = new BlockingFirstBufferPipeWriter(output.Writer);
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stop-before-timed-wait-arm",
            input.Reader,
            blockingWriter,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.MaxValue)));
        var frame = CreateFrame(session, 32, requestId: 1);

        try
        {
            session.SendPacket(frame);
            await blockingWriter.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            // The pump has dequeued the frame but is blocked inside WriteFrame. Stop now:
            // its wake is latched before the timed wait can arm. Once the write resumes,
            // stale data-wake cleanup must not swallow this stop and park the pump on the
            // effectively infinite MaxLatency timer.
            var dispose = session.DisposeAsync().AsTask();
            blockingWriter.Release();

            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            EnsureReturned(frame, "shutdown must return the staged frame owner");
            Ensure(session.QueuedSendBytes == 0,
                "shutdown crossing the timed-wait arm must release all queued bytes");
        }
        finally
        {
            blockingWriter.Release();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private static IRpcByteBufferWriter CreateFrame(RpcSession session, int payloadBytes, ulong requestId)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId))
        {
            writer.Write(new byte[payloadBytes]);
        }
        return writer;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void EnsureReturned(IRpcByteBufferWriter writer, string message)
    {
        try
        {
            _ = writer.WrittenCount;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new Exception(message);
    }

    private sealed class BlockingFirstBufferPipeWriter(PipeWriter inner) : PipeWriter
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNext = 1;

        internal Task Entered => _entered.Task;

        internal void Release() => _release.Set();

        public override void Advance(int bytes) => inner.Advance(bytes);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null)
            => inner.CompleteAsync(exception);

        public override ValueTask<FlushResult> FlushAsync(
            CancellationToken cancellationToken = default)
            => inner.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            BlockOnce();
            return inner.GetMemory(sizeHint);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            BlockOnce();
            return inner.GetSpan(sizeHint);
        }

        private void BlockOnce()
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 0)
                return;

            _entered.TrySetResult(true);
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("test writer was not released");
        }
    }
}
