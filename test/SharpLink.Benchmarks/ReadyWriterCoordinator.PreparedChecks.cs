#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Runtime;
namespace SharpLink.Benchmarks;
internal sealed partial class ReadyWriterCoordinator
{
    internal static Task<int> RunPreparedByteChecksAsync()
    {
        var count = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); count++; }
        var stream = new Stream(0, 16, 8192, preparedByteBudget: 8192);
        using var packet = new PooledByteBufferWriter(4096);
        packet.Advance(4096);
        stream.Frames.Enqueue(packet); stream.QueuedBytes = 4096;
        Check(stream.CanFit(4096) && !stream.CanFit(4097), "serialized byte bound independent of 16-slot count");
        stream.Frames.Enqueue(packet); stream.QueuedBytes = 8192;
        var wait = stream.WaitForSpace(CancellationToken.None, 4096);
        Check(!wait.IsCompleted, "byte-exhausted ring blocks before count exhaustion");
        stream.SignalSpace();
        Check(!wait.IsCompleted, "spurious notification cannot bypass byte budget");
        stream.Frames.Dequeue(); stream.QueuedBytes -= 4096; stream.SignalSpace();
        Check(wait.IsCompletedSuccessfully, "sufficient byte release wakes waiting producer");
        wait.GetAwaiter().GetResult();
        var large = stream.WaitForSpace(CancellationToken.None, 16384);
        Check(!large.IsCompleted, "oversized prepared packet cannot share a nonempty ring");
        stream.Frames.Clear(); stream.QueuedBytes = 0; stream.SignalSpace(); large.GetAwaiter().GetResult();
        Check(stream.CanFit(16384), "one oversized prepared packet may progress in an empty ring");
        stream.Frames.Enqueue(packet); stream.QueuedBytes = 16384;
        Check(!stream.CanFit(1), "oversized borrowed preparation slot excludes all further packets");
        var cancel = stream.WaitForSpace(CancellationToken.None, 1);
        stream.SignalSpace(new OperationCanceledException());
        var observed = false; try { cancel.GetAwaiter().GetResult(); } catch (OperationCanceledException) { observed = true; }
        Check(observed, "cancel releases byte-capacity waiter even without free bytes");
        stream.Frames.Clear(); stream.QueuedBytes = 0;
        for (var i = 0; i < 100000; i++)
        {
            stream.Frames.Enqueue(packet); stream.QueuedBytes = 8192;
            var next = stream.WaitForSpace(CancellationToken.None, 1);
            stream.Frames.Clear(); stream.QueuedBytes = 0; stream.SignalSpace(); next.GetAwaiter().GetResult();
        }
        Check(stream.CapacityWaits == 100003, "100000 byte-capacity completion reuses settle exactly once");
        return Task.FromResult(count);
    }
}
#endif
