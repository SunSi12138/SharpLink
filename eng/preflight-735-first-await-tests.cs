namespace SharpLink.UnitTests.Runtime;

[NotInParallel("first-receive-allocation")]
public sealed class StreamManagerFirstReceiveAwaitAllocationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments((ushort)0)]
    [Arguments((ushort)7)]
    public async Task SuspendedFirstDeliveryMustNotAllocateASecondCompletionStateMachine(ushort streamId)
    {
        const int warmups = 64;
        const int measurements = 64;
        const long requestId = 9860;
        var controller = new StreamFlowController(8, 8, 1024, 1);
        long consumed = 0;
        long returned = 0;
        void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int count)
            => controller.AcceptReceived(in lease, count);
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int count)
        {
            consumed += count;
            returned += controller.RecordConsumed(in lease, count);
        }
        void Terminal(StreamFlowController.ResolvedReceiveCreditLease lease)
            => returned += controller.FlushConsumed(in lease);
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            controller.ResolveReceiveCreditLease, Accept, Consumed, Terminal,
            controller.AcceptReceivedCreditLease);
        var dispatcher = new PendingConsumer();
        var payload = new ReadOnlySequence<byte>(new byte[] { 1 });
        long firstBytes = 0;
        long followingBytes = 0;
        try
        {
            for (var iteration = 0; iteration < warmups + measurements; iteration++)
            {
                manager.Register(requestId, streamId, dispatcher);
                dispatcher.Prepare(); // Allocate the controlled pending task outside the measured interval.
                var before = GC.GetAllocatedBytesForCurrentThread();
                var first = manager.DispatchChunkAsync(requestId, streamId, payload);
                var firstAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(!first.IsCompleted, "the first DATA must really suspend before completion");
                dispatcher.Resume();
                await first.AsTask().WaitAsync(Bound);

                dispatcher.Prepare();
                before = GC.GetAllocatedBytesForCurrentThread();
                var following = manager.DispatchChunkAsync(requestId, streamId, payload);
                var followingAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(!following.IsCompleted, "the ordinary DATA control must also suspend");
                dispatcher.Resume();
                await following.AsTask().WaitAsync(Bound);
                manager.Unregister(requestId, streamId);
                if (iteration >= warmups)
                {
                    firstBytes += firstAllocated;
                    followingBytes += followingAllocated;
                }
            }
            var expected = 2L * (warmups + measurements);
            Require(consumed == expected && returned == expected && manager.ActiveStreamCount == 0,
                "both pending paths must settle their exact credit and route ownership");
            Console.WriteLine($"FIRST_AWAIT_ALLOCATION stream={streamId} count={measurements} first={firstBytes} following={followingBytes}");
            var directory = Environment.GetEnvironmentVariable("SHARPLINK_FIRST_AWAIT_ALLOCATION_DIRECTORY");
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, $"stream-{streamId}.csv"),
                    $"count,first_bytes,following_bytes\n{measurements},{firstBytes},{followingBytes}\n");
            }
            Require(firstBytes <= followingBytes,
                "pending first DATA adds a redundant completion state machine");
        }
        finally
        {
            dispatcher.ReleaseForCleanup();
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
        }
    }

    private sealed class PendingConsumer : IStreamConsumptionAwareDispatcher
    {
        private TaskCompletionSource? _pending;
        private ResolvedStreamBytesCallback? _consumed;
        private StreamFlowController.ResolvedReceiveCreditLease _lease;
        private int _pendingBytes;

        internal void Prepare()
        {
            _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingBytes = 0;
        }

        internal void Resume()
        {
            var count = _pendingBytes;
            _pendingBytes = 0;
            _consumed?.Invoke(in _lease, count);
            _pending!.SetResult();
        }

        internal void ReleaseForCleanup() => _pending?.TrySetResult();
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => DispatchAsync(payload, checked((int)payload.Length));
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            _pendingBytes = encodedByteCount;
            return new ValueTask(_pending!.Task);
        }
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId) { }
        public bool TrySetResolvedBytesConsumedCallback(ResolvedStreamBytesCallback? callback,
            in StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            _consumed = callback;
            _lease = lease;
            return true;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
