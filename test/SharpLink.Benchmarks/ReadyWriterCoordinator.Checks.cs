#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Threading.Tasks;
using System.Linq;
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
        slot.Frames.Enqueue(new PreparedFrame(null!, 1)); // Capacity-only checks, not a serialized data fixture.
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

        // Mixed item sizes are a correctness contract, not an internal-queue equivalence test.
        // The frozen controller is used only as an oracle for externally observable admission:
        // connection-credit blockage is FIFO, stream-local blockage may be skipped, and an
        // oversized item may borrow only from a completely restored window.
        var oracle = new StreamFlowController(8, 16, 4096, 3);
        await oracle.AcquireSendCreditAsync(1, 1, 4, CancellationToken.None);
        await oracle.AcquireSendCreditAsync(2, 1, 8, CancellationToken.None);
        var oracleOlder = oracle.AcquireSendCreditAsync(3, 1, 8, CancellationToken.None).AsTask();
        var oracleYounger = oracle.AcquireSendCreditAsync(1, 1, 4, CancellationToken.None).AsTask();
        Check(!oracleOlder.IsCompleted && !oracleYounger.IsCompleted,
            "frozen mixed-size connection credit does not let a younger waiter bypass the blocked head");
        oracle.ApplyWindowUpdate(2, 1, 8);
        await Task.WhenAll(oracleOlder, oracleYounger).WaitAsync(TimeSpan.FromSeconds(2));

        var streamOracle = new StreamFlowController(8, 16, 4096, 3);
        await streamOracle.AcquireSendCreditAsync(1, 1, 4, CancellationToken.None);
        var streamBlockedOracle = streamOracle.AcquireSendCreditAsync(1, 1, 8, CancellationToken.None).AsTask();
        var independentOracle = streamOracle.AcquireSendCreditAsync(2, 1, 8, CancellationToken.None).AsTask();
        Check(independentOracle.IsCompletedSuccessfully && !streamBlockedOracle.IsCompleted,
            "frozen mixed-size stream-local blockage permits another stream to progress");
        streamOracle.ApplyWindowUpdate(1, 1, 4);
        await streamBlockedOracle.WaitAsync(TimeSpan.FromSeconds(2));

        var oversizedOracle = new StreamFlowController(8, 16, 4096, 1);
        await oversizedOracle.AcquireSendCreditAsync(1, 1, 20, CancellationToken.None);
        Check(oversizedOracle.SendConnectionCredit == -4,
            "frozen oversized item borrows only after a fully restored connection window");
        oversizedOracle.ApplyWindowUpdate(1, 1, 20);
        Check(oversizedOracle.SendConnectionCredit == 16, "frozen oversized repayment restores the connection window");

        var (mt, mp) = await PhaseBTransportPair.CreateAsync("pipe");
        var mc = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        await using var ms = new RpcSession(mt, new RpcSessionCreationOptions(RpcSessionRole.Client, mc));
        using var mcancel = new CancellationTokenSource();
        var mixed = new ReadyWriterCoordinator(ms, mc, mcancel, false, 3, 100, 1, 8, 16, 4, 1);
        IRpcByteBufferWriter MixedPacket(int stream, int creditBytes)
        {
            var packet = mc.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(stream + 1)))
            {
                var span = packet.GetSpan(creditBytes + 2)[..(creditBytes + 2)]; span.Clear();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span, 1);
                packet.Advance(creditBytes + 2);
            }
            return packet;
        }
        void ReleaseMixed(ReadyStreamFrame frame)
        {
            mc.Buffers.Return(frame.Packet);
            mixed.Released(frame.Slot, frame.CreditBytes, true, null);
        }
        try
        {
            await mixed.EnqueueAsync(0, MixedPacket(0, 4), 4);
            Check(mixed.TryTake(out var m0) && m0.CreditBytes == 4 && m0.Slot == 0,
                "B3 debits the first frame's own credit size"); ReleaseMixed(m0);
            await mixed.EnqueueAsync(1, MixedPacket(1, 8), 8);
            Check(mixed.TryTake(out var m1) && m1.CreditBytes == 8 && m1.Slot == 1,
                "B3 debits a different frame size without coordinator-wide byte assumptions"); ReleaseMixed(m1);
            Check(mixed._connectionCredit == 4 && mixed._streams[0].Credit == 4 && mixed._streams[1].Credit == 0,
                "mixed-size debits conserve stream and connection permission");

            await mixed.EnqueueAsync(2, MixedPacket(2, 8), 8);
            await mixed.EnqueueAsync(0, MixedPacket(0, 4), 4);
            Check(!mixed.TryTake(out _),
                "connection-credit blocked ready head prevents a younger smaller frame from bypassing");
            await mixed.EnqueueUpdateAsync(2, 1, 8);
            Check(mixed.TryTake(out var m2) && m2.Slot == 2 && m2.CreditBytes == 8,
                "credit return admits the older connection-blocked frame first"); ReleaseMixed(m2);
            Check(mixed.TryTake(out m0) && m0.Slot == 0 && m0.CreditBytes == 4,
                "younger fitting frame follows after the older connection waiter"); ReleaseMixed(m0);
            await mixed.EnqueueUpdateAsync(3, 1, 8);
            await mixed.EnqueueUpdateAsync(1, 1, 8);
            Check(!mixed.TryTake(out _) && mixed._connectionCredit == 16 &&
                mixed._streams.All(stream => stream.Credit == 8 && stream.Outstanding == 0),
                "mixed-size connection-blocked sequence repays exactly");

            await mixed.EnqueueAsync(0, MixedPacket(0, 4), 4);
            Check(mixed.TryTake(out m0), "prepare stream-local blocked control"); ReleaseMixed(m0);
            await mixed.EnqueueAsync(0, MixedPacket(0, 8), 8);
            await mixed.EnqueueAsync(1, MixedPacket(1, 8), 8);
            Check(mixed.TryTake(out m1) && m1.Slot == 1,
                "stream-local blocked head may be skipped while another stream has permission"); ReleaseMixed(m1);
            await mixed.EnqueueUpdateAsync(1, 1, 4);
            Check(mixed.TryTake(out m0) && m0.Slot == 0 && m0.CreditBytes == 8,
                "stream-local credit return reactivates the retained frame"); ReleaseMixed(m0);
            await mixed.EnqueueUpdateAsync(2, 1, 8);
            await mixed.EnqueueUpdateAsync(1, 1, 8);
            Check(!mixed.TryTake(out _) && mixed._connectionCredit == 16,
                "stream-local skip path repays connection permission exactly");

            await mixed.EnqueueAsync(2, MixedPacket(2, 20), 20);
            Check(mixed.TryTake(out m2) && m2.CreditBytes == 20 && mixed._connectionCredit == -4 &&
                mixed._streams[2].Credit == -12, "B3 oversized item borrows only from fully restored windows"); ReleaseMixed(m2);
            await mixed.EnqueueAsync(1, MixedPacket(1, 1), 1);
            Check(!mixed.TryTake(out _), "negative oversized debt blocks later connection admission");
            await mixed.EnqueueUpdateAsync(3, 1, 20);
            Check(mixed.TryTake(out m1) && m1.CreditBytes == 1,
                "oversized repayment restores progress without changing wire semantics"); ReleaseMixed(m1);
            await mixed.EnqueueUpdateAsync(2, 1, 1);
            Check(!mixed.TryTake(out _) && mixed._connectionCredit == 16 &&
                mixed._streams.All(stream => stream.Credit == 8 && stream.Outstanding == 0),
                "mixed-size and oversized ledger finishes balanced");
        }
        finally
        {
            mixed.Stopped(new OperationCanceledException());
            try { await mixed.Completion; } catch (Exception) { }
            await ms.DisposeAsync(); await mp.DisposeAsync(); mc.Dispose();
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
