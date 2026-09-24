#if SHARPLINK_READY_WRITER_EXPERIMENT
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunQueueBudgetChecksAsync()
    {
        var passed = 0;
        var failures = new List<Exception>();
        foreach (var reference in new[] { true, false })
        foreach (var setup in new[] { (Queue: 32768, Bytes: 4096, Items: 8), (Queue: 8192, Bytes: 16384, Items: 2) })
        {
            try
            {
                await CheckQueuedBatchAsync(reference, setup.Queue, setup.Bytes, setup.Items);
                Console.WriteLine($"PASS pump-budget/{reference}: queue={setup.Queue}, item={setup.Bytes}, preload={2 * setup.Items}");
                passed++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL pump-budget/{reference}: queue={setup.Queue}, item={setup.Bytes}: {error}");
                failures.Add(error);
            }
        }
        foreach (var reference in new[] { true, false })
        {
            await CheckDeniedAdmissionAsync(reference);
            passed++;
            await CheckImpossibleFrameAsync(reference);
            passed++;
        }
        if (failures.Count != 0) throw new AggregateException("Ready frames must wait for a transient pump byte limit.", failures);
        return passed;
    }

    private sealed class AdmissionProbe : IReadyFrameAdmission
    {
        internal bool Allow;
        internal int Accepted;
        internal int LastLength;
        public bool TryReserve(int frameBytes)
        {
            LastLength = frameBytes;
            if (Allow) Accepted++;
            return Allow;
        }
    }

    private static async Task CheckDeniedAdmissionAsync(bool reference)
    {
        var (transport, peer) = await PhaseBTransportPair.CreateAsync("pipe");
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(transport, new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        using var cancel = new CancellationTokenSource();
        var owner = new ReadyWriterCoordinator(session, context, cancel, reference, 2, 1, 16, 8192, 16384, 1);
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var packet = context.Buffers.Rent();
                using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(i + 1)))
                { packet.GetSpan(18)[..18].Clear(); packet.Advance(18); }
                await owner.EnqueueAsync(i, packet);
            }
            var head = owner._streams[0].Frames.Peek();
            var gate = new AdmissionProbe();
            if (owner.TryTake(gate, out _) || gate.LastLength != head.WrittenCount || gate.Accepted != 0 ||
                owner._creditDebits != 0 || owner._streams[0].Taken != 0 || owner._streams[1].Taken != 0 ||
                !ReferenceEquals(owner._streams[0].Frames.Peek(), head))
                throw new InvalidOperationException("A denied budget must not debit, dequeue or bypass its ready head.");
            gate.Allow = true;
            if (!owner.TryTake(gate, out var ready) || ready.Slot != 0 || !ReferenceEquals(ready.Packet, head) ||
                gate.Accepted != 1 || owner._creditDebits != (reference ? 0 : 1))
                throw new InvalidOperationException("A released budget must admit the same head exactly once.");
            context.Buffers.Return(ready.Packet);
            owner.Released(ready.Slot, ready.CreditBytes, true, null);
            Console.WriteLine($"PASS pump-budget/{reference}: denial preserves full length, ownership, credit and ready order");
        }
        finally
        {
            owner.Stopped(new OperationCanceledException("admission check cleanup"));
            try { await owner.Completion; } catch (Exception) { }
            await session.DisposeAsync();
            await peer.DisposeAsync();
        }
    }

    private static async Task CheckImpossibleFrameAsync(bool reference)
    {
        var (transport, peer) = await PhaseBTransportPair.CreateAsync("pipe");
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxSendQueueBytes = 32768)
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(transport, new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        using var cancel = new CancellationTokenSource();
        var owner = new ReadyWriterCoordinator(session, context, cancel, reference, 1, 1, 30720, 65536, 65536, 1);
        try
        {
            var packet = context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, 1))
            { packet.GetSpan(30722)[..30722].Clear(); packet.Advance(30722); }
            await owner.EnqueueAsync(0, packet);
            owner.Attach();
            try
            {
                await owner.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("A frame that consumes progress reserve must be rejected.");
            }
            catch (SharpLinkException failure) when (failure.Message.Contains("normal send-queue allowance", StringComparison.Ordinal)) { }
            if (owner._streams[0].Taken != 0 || owner._discarded != 1 || owner._releases != 0 ||
                owner._streams[0].QueuedBytes != 0 || owner._creditDebits != 0)
                throw new InvalidOperationException("Impossible-frame rejection changed ownership or leaked its buffer.");
            Console.WriteLine($"PASS pump-budget/{reference}: impossible frame preserves protocol progress reserve");
        }
        finally
        {
            owner.Stopped(new OperationCanceledException("impossible frame cleanup"));
            try { await owner.Completion; } catch (Exception) { }
            await session.DisposeAsync();
            await peer.DisposeAsync();
        }
    }

    private static async Task CheckQueuedBatchAsync(bool reference, int queueBytes, int bytes, int items)
    {
        var (transport, peer) = await PhaseBTransportPair.CreateAsync("pipe");
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxSendQueueBytes = queueBytes)
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(transport,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context,
                new RpcSessionFlushOptions(131072, TimeSpan.MaxValue)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var owner = new ReadyWriterCoordinator(session, context, cancel, reference,
            2, items, bytes, 65536, 131072, items, 16);
        Task? receiving = null;
        try
        {
            // Preload before attaching: every DATA frame is already eligible when
            // the pump starts. No sleep, scheduler luck or concurrent producer is needed.
            for (var stream = 0; stream < 2; stream++)
            for (var item = 0; item < items; item++)
            {
                var packet = context.Buffers.Rent();
                using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(stream + 1)))
                {
                    var payload = packet.GetSpan(bytes + 2)[..(bytes + 2)];
                    payload.Clear();
                    BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
                    BinaryPrimitives.WriteInt32LittleEndian(payload[2..], item);
                    packet.Advance(bytes + 2);
                }
                await owner.EnqueueAsync(stream, packet);
            }
            receiving = ReadPreparedAsync();
            owner.Attach();
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await receiving.WaitAsync(cancel.Token);
            await session.FlushSendQueueAsync();
            var metrics = owner.Metrics();
            if (!session.IsConnected || metrics["NormalQueueRejections"] != 0 ||
                metrics["FramesReleased"] != 2L * items || metrics["RemainingQueuedBytes"] != 0 ||
                metrics["CreditBytesApplied"] != 2L * items * bytes)
                throw new InvalidOperationException("Transient queue pressure lost data, credit or ownership.");

            async Task ReadPreparedAsync()
            {
                var counts = new int[2];
                while (counts[0] + counts[1] < 2 * items)
                {
                    var read = await peer.Input.ReadAsync(cancel.Token);
                    var remaining = read.Buffer;
                    var credits = new List<int>();
                    try
                    {
                        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, context.Protocol, out var header, out var payload))
                        {
                            var slot = checked((int)header.RequestId - 1);
                            var body = payload.ToArray();
                            if (header.Type != ProtocolV2FrameType.StreamData || slot < 0 || slot > 1 ||
                                body.Length != bytes + 2 || BinaryPrimitives.ReadUInt16LittleEndian(body) != 1 ||
                                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2)) != counts[slot]++)
                                throw new InvalidOperationException("Prepared DATA was lost, duplicated or reordered.");
                            credits.Add(slot);
                        }
                        if (read.IsCompleted && counts[0] + counts[1] != 2 * items)
                            throw new InvalidOperationException("Transport completed before all prepared frames.");
                    }
                    finally { peer.Input.AdvanceTo(remaining.Start, remaining.End); }
                    // Release the read before awaiting the bounded return mailbox:
                    // the output Flush can need this very AdvanceTo to release its batch.
                    foreach (var slot in credits) await owner.EnqueueUpdateAsync(slot + 1, 1, bytes);
                }
            }
        }
        finally
        {
            owner.Stopped(new OperationCanceledException("budget check cleanup"));
            if (receiving is not null) { try { await receiving; } catch (Exception) { } }
            try { await owner.Completion; } catch (Exception) { }
            await session.DisposeAsync();
            await peer.DisposeAsync();
        }
    }
}
#endif
