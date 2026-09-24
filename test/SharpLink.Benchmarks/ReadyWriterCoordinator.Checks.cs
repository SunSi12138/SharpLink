#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;
namespace SharpLink.Benchmarks;
internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunDeterministicChecksAsync()
    {
        var passed = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); passed++; }
        static bool Throws(Action action)
        { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var slot = new Stream(0, 1, 8192);
        slot.Frames.Enqueue(null!); // Capacity-only checks, not a serialized data fixture.
        var held = slot.WaitForSpace(CancellationToken.None);
        Check(!held.IsCompleted && Throws(() => held.GetAwaiter().GetResult()), "pending capacity result cannot be consumed/reset");
        slot.SignalSpace(); slot.SignalSpace();
        Check(held.IsCompletedSuccessfully && Throws(() => slot.WaitForSpace(CancellationToken.None)), "signaled capacity remains held until consumed");
        held.GetAwaiter().GetResult();
        var next = slot.WaitForSpace(CancellationToken.None);
        Check(Throws(() => held.GetAwaiter().GetResult()) && !next.IsCompleted, "old capacity token cannot consume current wait");
        slot.SignalSpace(new OperationCanceledException());
        var canceled = false; try { next.GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "capacity cancellation observed once");
        for (var i = 0; i < 100000; i++)
        { var wait = slot.WaitForSpace(CancellationToken.None); slot.SignalSpace(); wait.GetAwaiter().GetResult(); }
        Check(slot.CapacityWaits == 100002, "100000 single-consumption capacity-slot reuses");
        Check(!Available(-1, 8192, 1) && Available(8192, 8192, 16384) && !Available(8191, 8192, 16384), "oversized admission requires full window");

        var (transport, peer) = await PhaseBTransportPair.CreateAsync("pipe");
        var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(transport, new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        using var cancel = new CancellationTokenSource();
        var source = new ReadyWriterCoordinator(session, context, cancel, false, 2, 100, 4096, 8192, 16384, 4);
        IRpcByteBufferWriter Packet(int index)
        {
            var p = context.Buffers.Rent();
            using (p.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(index + 1)))
            { p.GetSpan(4098)[..4098].Clear(); p.Advance(4098); }
            return p;
        }
        void Release(ReadyStreamFrame frame, bool admitted = true)
        { context.Buffers.Return(frame.Packet); source.Released(frame.Slot, frame.CreditBytes, admitted, null); }
        try
        {
            await source.EnqueueAsync(0, Packet(0)); await source.EnqueueAsync(1, Packet(1));
            Check(source.TryTake(out var a) && a.Slot == 0, "ready arrival FIFO first stream"); Release(a);
            Check(source.TryTake(out var b) && b.Slot == 1, "ready arrival FIFO second stream"); Release(b);
            await source.EnqueueAsync(0, Packet(0));
            Check(source.TryTake(out a) && a.Slot == 0, "second stream-zero debit"); Release(a);
            await source.EnqueueAsync(0, Packet(0)); await source.EnqueueAsync(1, Packet(1));
            Check(source.TryTake(out b) && b.Slot == 1, "stream-credit-blocked head may be skipped"); Release(b);
            Check(!source.TryTake(out _), "all exhausted streams stay blocked without requeue spin");
            await source.EnqueueUpdateAsync(1, 1, 8192);
            Check(source.TryTake(out a) && a.Slot == 0, "real update event reactivates retained blocked stream"); Release(a);
            var beforeRefund = source._connectionCredit;
            await source.EnqueueAsync(0, Packet(0));
            Check(source.TryTake(out a), "admit a known-unsent normal-capacity rejection"); Release(a, admitted: false);
            Check(source._connectionCredit == beforeRefund && source._normalQueueRejected == 1, "known-unsent rejection refunds exactly its own debit");
            await source.EnqueueAsync(0, Packet(0));
            Check(source.TryTake(out a), "admit writer-visible failure control");
            var afterDebit = source._connectionCredit;
            context.Buffers.Return(a.Packet);
            source.Released(a.Slot, a.CreditBytes, admitted: true, new InvalidOperationException("injected visible-byte failure"));
            Check(source._connectionCredit == afterDebit, "accepted emission failure never invents unsent credit");
            await source.EnqueueAsync(1, Packet(1));
            source.Stopped(new OperationCanceledException());
            Check(source._discarded == 1 && !source.TryTake(out _), "stop discards only unadmitted buffered frames and blocks new admissions");
        }
        finally
        {
            source.Stopped(new OperationCanceledException());
            try { await source.Completion; } catch (Exception) { }
            await session.DisposeAsync();
            await peer.DisposeAsync();
            context.Dispose();
        }
        var (qt, qp) = await PhaseBTransportPair.CreateAsync("pipe");
        var qc = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        await using var qs = new RpcSession(qt, new RpcSessionCreationOptions(RpcSessionRole.Client, qc));
        using var qcancel = new CancellationTokenSource();
        var owner = new ReadyWriterCoordinator(qs, qc, qcancel, false, 2, 100, 16, 8192, 16384, 32, 16);
        try
        {
            for (var i = 0; i < 32; i++)
                for (var stream = 0; stream < 2; stream++)
                {
                    var p = qc.Buffers.Rent();
                    using (p.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(stream + 1)))
                    { p.GetSpan(18)[..18].Clear(); p.Advance(18); }
                    await owner.EnqueueAsync(stream, p);
                }
            for (var i = 0; i < 64; i++)
            {
                if (!owner.TryTake(out var frame) || frame.Slot != (i / 16) % 2) throw new Exception("Quantum exceeded or stream starved.");
                qc.Buffers.Return(frame.Packet); owner.Released(frame.Slot, frame.CreditBytes, true, null);
            }
            Check(owner._writerTurns == 4 && owner._maxConsecutive == 16, "two continuously ready streams obey bounded 16-frame round robin");
        }
        finally
        {
            owner.Stopped(new OperationCanceledException());
            try { await owner.Completion; } catch (Exception) { }
            await qs.DisposeAsync(); await qp.DisposeAsync(); qc.Dispose();
        }
        return passed;
    }
}
#endif
