using System.Collections.Generic;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed class StreamManagerRequestDrainTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RejectingRequestPublishesTerminalAfterAcquiredFirstFrameDebitsCredit()
    {
        const long requestId = 702;
        const ushort streamId = 1;
        const int window = 128;
        var flow = new StreamFlowController(window, window, 1024);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDebit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminals = 0;
        void ReturnCredit(long id, ushort stream, int credit)
        {
            if (credit != 0)
                flow.ApplyWindowUpdate(id, stream, credit);
        }
        var manager = new StreamManager(new RuntimeConcurrencyOptions(),
            (id, stream, bytes) =>
            {
                acquired.TrySetResult();
                allowDebit.Task.WaitAsync(CoordinationTimeout).GetAwaiter().GetResult();
                flow.AcceptReceived(id, stream, bytes);
            },
            (id, stream, bytes) => ReturnCredit(id, stream, flow.RecordConsumed(id, stream, bytes)),
            (id, stream) =>
            {
                Interlocked.Increment(ref terminals);
                ReturnCredit(id, stream, flow.FlushConsumed(id, stream));
            });
        manager.ReservePreAdmissionStreams(requestId, 1,
            new SharpLinkBufferWriterPool(new BufferWriterPoolOptions()),
            static _ => true, static _ => { }, static () => { });
        await flow.AcquireSendCreditAsync(requestId, streamId, 1, CancellationToken.None);
        var first = Task.Run(async () =>
            await manager.DispatchChunkAsync(requestId, streamId, new ReadOnlySequence<byte>(new byte[] { 1 })));
        try
        {
            await acquired.Task.WaitAsync(CoordinationTimeout);
            manager.CompleteRequestStreams(requestId, new OperationCanceledException("admission rejected"));
            flow.CompleteSendStream(requestId, streamId);
            Ensure(terminals == 0 && manager.ActiveStreamCount == 0,
                "rejection closes routing now but cannot publish receive terminal ahead of the acquired frame");
        }
        finally { allowDebit.TrySetResult(); }

        await first.WaitAsync(CoordinationTimeout);
        Ensure(terminals == 1 && flow.SendConnectionCredit == window &&
            flow.RetainedReceiveStreamCount == 0 && flow.RetainedSendStreamCount == 0,
            "the last lease returns the below-threshold byte and retires both flow states");
    }

    [Test]
    public async Task RequestWideDrainShouldWaitForAcquiredDispatchesBeforeFinalizingStreams()
    {
        const long requestId = 701;
        var events = new List<string>();
        var completedStreams = new List<ushort>();
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            acceptBytes: null,
            bytesConsumed: null,
            (_, streamId) =>
            {
                completedStreams.Add(streamId);
                events.Add($"stream-{streamId}-finalized");
            });
        var gated = new GatedDispatcher(events, "one");
        var idle = new RecordingDispatcher();
        manager.Register(requestId, 1, gated);
        manager.Register(requestId, 2, idle);

        var dispatch = manager.DispatchChunkAsync(
            requestId,
            1,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        await gated.Entered.WaitAsync(CoordinationTimeout);

        var completion = manager.CompleteRequestStreamsAfterDispatchesAsync(
            requestId,
            new OperationCanceledException("handler completed"));

        Ensure(!completion.IsCompleted,
            "request-wide completion must wait for a StreamData dispatch that acquired its entry before removal");
        Ensure(manager.ActiveStreamCount == 0,
            "request entries should stop accepting frames immediately while the acquired dispatch drains");
        Ensure(gated.CompleteCount == 1 && idle.CompleteCount == 1,
            "every request stream should receive terminal completion before the drain wait");
        Ensure(completedStreams.Count == 0,
            "stream finalization callbacks must wait until all acquired request dispatches drain");

        await manager.DispatchChunkAsync(
            requestId,
            1,
            new ReadOnlySequence<byte>(new byte[] { 2 }));
        Ensure(manager.DroppedStreamFrames == 1,
            "frames arriving after the request-wide barrier is installed must be dropped");

        gated.Release();
        await dispatch;
        await completion;

        Ensure(completedStreams.OrderBy(static id => id).SequenceEqual(new ushort[] { 1, 2 }),
            "both request streams must finalize after the dispatch barrier completes");
        Ensure(events.IndexOf("one-dispatch-released") < events.IndexOf("stream-1-finalized") &&
               events.IndexOf("one-dispatch-released") < events.IndexOf("stream-2-finalized"),
            "request stream finalization must follow the last acquired dispatch");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RecordingDispatcher : IStreamDispatcher
    {
        public int CompleteCount { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
            => Complete(isError ? new Exception(errorMessage) : null);

        public void Complete(Exception? exception)
        {
            _ = exception;
            CompleteCount++;
        }
    }

    private sealed class GatedDispatcher(List<string> events, string name) : IStreamDispatcher
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public int CompleteCount { get; private set; }

        public async ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            events.Add($"{name}-dispatch-entered");
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            events.Add($"{name}-dispatch-released");
        }

        public void Complete(bool isError, string? errorMessage)
            => Complete(isError ? new Exception(errorMessage) : null);

        public void Complete(Exception? exception)
        {
            _ = exception;
            CompleteCount++;
            events.Add($"{name}-completed");
        }

        public void Release() => _release.TrySetResult();
    }
}
