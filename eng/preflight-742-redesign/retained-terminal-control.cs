namespace SharpLink.UnitTests.Runtime;

public sealed class StreamManagerRetainedPeerTerminalTests
{
    [Test]
    public async Task RetainedPeerTerminalMustWaitForAlreadyAcquiredDataBeforeReceiveFlush()
    {
        var timeout = TimeSpan.FromSeconds(10);
        var controller = new StreamFlowController(4, 4, 1024, 1);
        using var beforeDebit = new ManualResetEventSlim();
        using var releaseDebit = new ManualResetEventSlim();
        var terminalCalls = 0;
        var returned = 0;
        void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int count)
        {
            beforeDebit.Set();
            if (!releaseDebit.Wait(timeout)) throw new TimeoutException("Missing debit release.");
            controller.AcceptReceived(in lease, count);
        }
        void Consume(in StreamFlowController.ResolvedReceiveCreditLease lease, int count)
            => Interlocked.Add(ref returned, controller.RecordConsumed(in lease, count));
        void Terminal(StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            Interlocked.Increment(ref terminalCalls);
            Interlocked.Add(ref returned, controller.FlushConsumed(in lease));
        }
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            controller.ResolveReceiveCreditLease, Accept, Consume, Terminal);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(9620, 1, buffers, _ => true, _ => { },
            () => throw new InvalidOperationException("Unexpected capacity exhaustion."),
            retainUntilLocalCompletion: true);
        var dispatch = Task.Run(async () => await manager.DispatchChunkAsync(9620, 1,
            new ReadOnlySequence<byte>(new byte[] { 1 })));
        var prematurelyPublished = false;
        try
        {
            if (!beforeDebit.Wait(timeout)) throw new TimeoutException("Missing acquired DATA.");
            manager.CompletePeerStream(9620, 1, exception: null);
            prematurelyPublished = Volatile.Read(ref terminalCalls) != 0;
        }
        finally { releaseDebit.Set(); }
        await dispatch.WaitAsync(timeout);
        if (prematurelyPublished || terminalCalls != 1 || returned != 1)
            throw new InvalidOperationException(
                "Retained peer terminal must wait for acquired DATA, then return its exact credit once.");
        manager.AbandonExistingRequestStreams(9620, 1);
        if (manager.ActiveStreamCount != 0)
            throw new InvalidOperationException("Local completion must retire the terminal retained route.");
    }
}
