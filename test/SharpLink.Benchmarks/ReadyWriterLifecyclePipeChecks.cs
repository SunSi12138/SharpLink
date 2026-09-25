#if SHARPLINK_READY_WRITER_EXPERIMENT
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunLifecyclePipeChecksAsync()
    {
        foreach (var bytes in new[] { 16, 4096 })
        {
            await CheckLifecyclePipeAsync(bytes);
            Console.WriteLine($"PASS lifecycle/real-pump/{bytes}: ordered close, key-only credit and same-state reopen");
        }
        return 2;
    }

    private static async Task CheckLifecyclePipeAsync(int bytes)
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
                "The fixture must enter the negotiated phase before actual wire traffic.");
        using var cancel = new CancellationTokenSource();
        var source = new ReadyWriterCoordinator(session, context, cancel, false, 1, 1, bytes,
            bytes, bytes, 1, dynamicLifetimes: true);
        var old = new StreamHandle(source, 0, 1);
        var oldState = source._streams[0];
        IRpcByteBufferWriter Packet(int marker)
        {
            var packet = context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, 1))
            {
                var span = packet.GetSpan(bytes + 2)[..(bytes + 2)]; span.Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(span, 1);
                BinaryPrimitives.WriteInt32LittleEndian(span[2..], marker); packet.Advance(bytes + 2);
            }
            return packet;
        }
        async Task ReadData(int marker)
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
                        var reader = new System.Buffers.SequenceReader<byte>(payload);
                        RequireWire(header.Type == ProtocolV2FrameType.StreamData && header.RequestId == 1 &&
                            reader.TryReadLittleEndian(out short streamId) && streamId == 1 &&
                            reader.TryReadLittleEndian(out int actual) && actual == marker,
                            "Actual DATA must retain the intended lifecycle's payload.");
                        return;
                    }
                    if (read.IsCompleted) throw new System.IO.EndOfStreamException();
                }
                finally { outgoing.Reader.AdvanceTo(rest.Start, parsed ? rest.Start : read.Buffer.End); }
            }
        }
        async Task ReturnWireCredit()
        {
            peer.SendWindowUpdate(1, 1, bytes);
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
                        RequireWire(header.Type == ProtocolV2FrameType.WindowUpdate && header.RequestId == 1 &&
                            update.StreamId == 1 && update.Credit == bytes, "Expected real peer WindowUpdate bits.");
                        await source.EnqueueUpdateAsync((long)header.RequestId, update.StreamId, checked((int)update.Credit));
                        return;
                    }
                    if (read.IsCompleted) throw new System.IO.EndOfStreamException();
                }
                finally { session.Input.AdvanceTo(rest.Start, parsed ? rest.Start : read.Buffer.End); }
            }
        }
        try
        {
            source.Attach(); await source.EnqueueAsync(old, Packet(10));
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await ReadData(10);
            var close = source.CloseStreamAsync(old);
            await ReturnWireCredit();
            var reopen = source.OpenStreamAsync(1, 1);
            RequireWire(!close.IsCompleted && !reopen.IsCompleted && oldState.Released == 0,
                "A blocked Flush must not execute a second owner or release its pin.");
            output.Resume.TrySetResult();
            var current = await reopen.WaitAsync(TimeSpan.FromSeconds(5));
            RequireWire(await close && current.Generation == 2 && ReferenceEquals(oldState, source._streams[0]),
                "The actual writer must execute Close/update/Open in order and reuse the original state.");
            await RejectLifecycleAsync(source.EnqueueAsync(old, Packet(99)).AsTask());
            await source.EnqueueAsync(current, Packet(20)); await ReadData(20);
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await ReturnWireCredit();
            var snapshot = await source.OnWriterAsync(() => (oldState.Credit, oldState.Outstanding, source._releases, oldState.Released));
            RequireWire(snapshot == (bytes, 0L, 2L, 1), "Each generation must independently settle DATA and writer ownership.");
            RequireWire(await source.CloseStreamAsync(current).WaitAsync(TimeSpan.FromSeconds(5)), "Second lifecycle must close.");
            var retired = await source.OnWriterAsync(() => source._retiredStreams.Count == 1 && source._identities.Count == 0);
            RequireWire(retired, "Actual writer lifecycle must finish with no retained identity.");
        }
        finally
        {
            output.Resume.TrySetResult(); await session.DisposeAsync();
            try { await source.Completion; } catch (Exception) { }
        }
    }
}
#endif
