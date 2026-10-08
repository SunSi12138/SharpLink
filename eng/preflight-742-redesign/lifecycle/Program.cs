using System.Buffers;
using SharpLink.Abstractions;
using SharpLink.Runtime;

const int Warmup = 2048;
const int Iterations = 10000;
Console.WriteLine("scenario,cleanup,cold,repeat,routes,bytes_per_request,bytes_per_route");
foreach (var scenario in new[] { "default", "named", "mixed-two", "mailbox", "mailbox-two" })
foreach (var cleanup in new[] { "unregister", "complete-stream", "request-sync", "request-async" })
foreach (var cold in new[] { false, true })
{
    var count = scenario.EndsWith("-two", StringComparison.Ordinal) ? 2 : 1;
    var mailbox = scenario.StartsWith("mailbox", StringComparison.Ordinal);
    var ids = scenario == "mixed-two" ? new ushort[] { 0, 1 } :
        count == 2 ? new ushort[] { 1, 2 } : new ushort[] { scenario == "default" ? (ushort)0 : (ushort)1 };
    PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    var flow = new StreamFlowController(4, 4 * count, 1024, count);
    long consumed = 0;
    void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes) => flow.AcceptReceived(in lease, bytes);
    void Consume(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes) => consumed += flow.RecordConsumed(in lease, bytes);
    void Terminal(StreamFlowController.ResolvedReceiveCreditLease lease) => consumed += flow.FlushConsumed(in lease);
    var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, count, null,
        flow.ResolveReceiveCreditLease, Accept, Consume, Terminal);
    var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
    var payload = new ReadOnlySequence<byte>(new byte[4]);
    var dispatchers = new PooledAsyncStreamDispatcher<int>[count];
    var enumerators = new IAsyncEnumerator<int>[count];
    void Round()
    {
        if (cold) PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        if (mailbox) manager.ReservePreAdmissionStreams(1, count, buffers, static _ => true, static _ => { }, static () => { });
        for (var i = 0; i < count; i++)
        {
            var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
            dispatchers[i] = dispatcher;
            manager.Register(1, ids[i], dispatcher);
            var dispatch = manager.DispatchChunkAsync(1, ids[i], payload);
            if (!dispatch.IsCompletedSuccessfully) throw new Exception("Unexpected suspended dispatch");
            dispatch.GetAwaiter().GetResult();
            var enumerator = dispatcher.GetAsyncEnumerator();
            enumerators[i] = enumerator;
            var move = enumerator.MoveNextAsync();
            if (!move.IsCompletedSuccessfully || !move.GetAwaiter().GetResult()) throw new Exception("Missing synchronous item");
        }
        switch (cleanup)
        {
            case "unregister":
                for (var i = 0; i < count; i++) manager.Unregister(1, ids[i]);
                break;
            case "complete-stream":
                for (var i = 0; i < count; i++) manager.CompleteStream(1, ids[i], exception: null);
                break;
            case "request-sync":
                manager.CompleteRequestStreams(1, exception: null);
                break;
            case "request-async":
                var completion = manager.CompleteRequestStreamsAfterDispatchesAsync(1, exception: null);
                if (!completion.IsCompletedSuccessfully) throw new Exception("Zero-active request cleanup must complete synchronously");
                completion.GetAwaiter().GetResult();
                break;
        }
        for (var i = 0; i < count; i++)
        {
            var dispose = enumerators[i].DisposeAsync();
            if (!dispose.IsCompletedSuccessfully) throw new Exception("Unexpected suspended disposal");
            dispose.GetAwaiter().GetResult();
            enumerators[i] = null!;
            dispatchers[i] = null!;
        }
    }
    for (var i = 0; i < Warmup; i++) Round();
    for (var repeat = 0; repeat < 3; repeat++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var consumedBefore = consumed;
        for (var i = 0; i < Iterations; i++) Round();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (manager.ActiveStreamCount != 0 || consumed - consumedBefore != 4L * count * Iterations)
            throw new Exception("Lifecycle/credit accounting mismatch");
        Console.WriteLine($"{scenario},{cleanup},{cold},{repeat},{count},{bytes / (double)Iterations:F6},{bytes / (double)(Iterations * count):F6}");
    }
    manager.CompleteAll(exception: null);
    PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
}
