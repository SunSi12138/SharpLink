#if SHARPLINK_READY_WRITER_EXPERIMENT
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunAdmissionPipeChecksAsync()
    {
        foreach (var bytes in new[] { 16, 4096 })
            foreach (var blocked in new[] { false, true })
            {
                await CheckAdmissionPipeAsync(bytes, blocked).WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine($"PASS admission/real-pump/{bytes}/blocked={blocked}");
            }
        return 4;
    }

    private static async Task CheckAdmissionPipeAsync(int bytes, bool blocked)
    {
        var outgoing = new Pipe(); var incoming = new Pipe();
        var output = new PausedCancellationWriter(outgoing.Writer);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(new CancellationTransport(incoming.Reader, output),
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        await using var peer = new RpcSession(new CancellationTransport(outgoing.Reader, incoming.Writer),
            new RpcSessionCreationOptions(RpcSessionRole.Server, context));
        foreach (var endpoint in new[] { session, peer })
            RequireWire(endpoint.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None, context.Protocol.MaxFramePayloadBytes, bytes, bytes, null)),
                "Actual traffic requires a negotiated session.");
        using var stop = new CancellationTokenSource();
        using var cancel = new CancellationTokenSource();
        var owner = new ReadyWriterCoordinator(session, context, stop, false, 1, 1, bytes,
            bytes, bytes, 1, dynamicLifetimes: true);
        var state = owner._streams[0];
        IRpcByteBufferWriter Packet(long requestId, int marker)
        {
            var packet = context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)requestId))
            {
                var span = packet.GetSpan(bytes + 2)[..(bytes + 2)]; span.Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(span, 1);
                BinaryPrimitives.WriteInt32LittleEndian(span[2..], marker); packet.Advance(bytes + 2);
            }
            return packet;
        }
        async Task ReadData(long requestId, int marker)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var read = await outgoing.Reader.ReadAsync(timeout.Token); var rest = read.Buffer; var parsed = false;
                try
                {
                    if (ProtocolV2FrameParser.TryReadFrame(ref rest, context.Protocol, out var header, out var payload))
                    {
                        parsed = true;
                        var reader = new SequenceReader<byte>(payload);
                        RequireWire(header.Type == ProtocolV2FrameType.StreamData && header.RequestId == (ulong)requestId &&
                            reader.TryReadLittleEndian(out short streamId) && streamId == 1 &&
                            reader.TryReadLittleEndian(out int actual) && actual == marker,
                            "DATA must belong to the intended admitted generation.");
                        return;
                    }
                    if (read.IsCompleted) throw new System.IO.EndOfStreamException();
                }
                finally { outgoing.Reader.AdvanceTo(rest.Start, parsed ? rest.Start : read.Buffer.End); }
            }
        }
        async Task ReturnCredit(long requestId)
        {
            peer.SendWindowUpdate(requestId, 1, bytes);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var read = await session.Input.ReadAsync(timeout.Token); var rest = read.Buffer; var parsed = false;
                try
                {
                    if (ProtocolV2FrameParser.TryReadFrame(ref rest, context.Protocol, out var header, out var payload))
                    {
                        parsed = true;
                        var update = ProtocolV2PayloadCodec.ReadWindowUpdate(payload);
                        RequireWire(header.Type == ProtocolV2FrameType.WindowUpdate && header.RequestId == (ulong)requestId &&
                            update.StreamId == 1 && update.Credit == bytes, "Expected the actual peer credit frame.");
                        await owner.EnqueueUpdateAsync((long)header.RequestId, update.StreamId, checked((int)update.Credit));
                        return;
                    }
                    if (read.IsCompleted) throw new System.IO.EndOfStreamException();
                }
                finally { session.Input.AdvanceTo(rest.Start, parsed ? rest.Start : read.Buffer.End); }
            }
        }
        try
        {
            owner.Attach();
            var first = await owner.AcquireStreamAsync(1, 1).WaitAsync(TimeSpan.FromSeconds(5));
            if (!blocked) output.Resume.TrySetResult();
            await owner.EnqueueAsync(first, Packet(1, 10));
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await ReadData(1, 10);
            if (blocked)
            {
                // All four mailbox entries are published before starting the waiter.
                // Cancellation must not need another mailbox slot or another owner.
                Task<int>[] fillers = [owner.OnWriterAsync(() => 1), owner.OnWriterAsync(() => 2),
                    owner.OnWriterAsync(() => 3), owner.OnWriterAsync(() => 4)];
                RequireWire(owner._updatesPending == 4 && !fillers[0].IsCompleted && state.Released == 0,
                    "The fixture must actually block Flush with a full owner inbox.");
                var canceled = owner.AcquireStreamAsync(99, 1, cancel.Token);
                cancel.Cancel();
                await ExpectAdmissionCanceledAsync(canceled, cancel.Token);
                RequireWire(state.Generation == 1 && state.Released == 0 && state.Outstanding == bytes && owner._updatesPending == 4,
                    "Canceled inbox admission must not touch writer ownership or publish a phantom message count.");
                output.Resume.TrySetResult();
                RequireWire((await Task.WhenAll(fillers)).SequenceEqual(new[] { 1, 2, 3, 4 }), "Owner commands must remain intact.");
            }
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            RequireWire(await owner.CloseStreamAsync(first), "Expected a retained completed state.");
            var pending = owner.AcquireStreamAsync(2, 1);
            var parked = await owner.OnWriterAsync(() => owner._pendingAdmission is not null && state.Closed && !state.Retired);
            RequireWire(parked && !pending.IsCompleted, "The real writer must park registration behind retained DATA.");
            await ReturnCredit(1);
            var second = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            RequireWire(second.Slot == first.Slot && second.Generation == 2 && ReferenceEquals(state, owner._streams[0]),
                "Wire credit alone must retire and admit into the same state, without another producer notification.");
            await owner.EnqueueAsync(second, Packet(2, 20)); await ReadData(2, 20);
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await ReturnCredit(2); await owner.CloseStreamAsync(second);
            RequireWire(await owner.OnWriterAsync(() => owner._pendingAdmission is null && owner._identities.Count == 0 &&
                owner._retiredStreams.Count == 1 && owner._connectionCredit == bytes && owner._releases == 2),
                "Both generations must finish with no waiter, state, DATA or credit leak.");
        }
        finally
        {
            output.Resume.TrySetResult(); await session.DisposeAsync();
            try { await owner.Completion; } catch (Exception) { }
        }
    }
}
#endif
