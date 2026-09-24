#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class PhaseBTransportCase
{
    internal static async Task<int> RunCreditStalledProgressChecksAsync()
    {
        var passed = 0;
        foreach (var mode in new[] { "A-ready", "B3-ready" })
        {
            var pair = await PhaseBTransportPair.CreateAsync("pipe");
            await using var test = new PhaseBTransportCase(mode, "pipe", pair.Client, pair.Server,
                1, 3, 4096, 8192, 4, 16384, 16, 8192);
            var source = test._readyWriter!;
            source.Attach();
            // Two items use the entire connection/stream window. No peer credit is
            // returned until Ping is observed on the actual writer's byte stream.
            await source.EnqueueAsync(0, test.ProgressPacket(0));
            await source.EnqueueAsync(0, test.ProgressPacket(1));
            var third = source.EnqueueAsync(0, test.ProgressPacket(2)).AsTask();
            test._ownedWork = [third, source.Completion];
            await test.ReadProgressDataAsync(0);
            await test.ReadProgressDataAsync(1);
            if (mode == "B3-ready") await third.WaitAsync(TimeSpan.FromSeconds(5));

            test._sender.SendPingAsync();
            var ping = await ReadProgressFrameAsync(test._receiver);
            if (ping.Header.Type != ProtocolV2FrameType.Ping)
                throw new InvalidOperationException("A credit-stalled DATA frame bypassed its hard window or overtook Ping.");
            if (source.Metrics()["FramesReleased"] != 2)
                throw new InvalidOperationException("Reading Ping must not invent DATA credit or release a third DATA frame.");

            // Route an actual key-only WindowUpdate through the peer SendPump and
            // parser. In B3 the third frame is already queued, so a credit-only
            // wake must restart it without a new producer notification or timer.
            await test.ReturnProgressCreditAsync(4096);
            await third.WaitAsync(TimeSpan.FromSeconds(5));
            await test.ReadProgressDataAsync(2);
            await test.ReturnProgressCreditAsync(8192);
            await source.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var metrics = source.Metrics();
            if (metrics["FramesReleased"] != 3 || metrics["CreditBytesApplied"] != 12288 ||
                metrics["RemainingQueuedBytes"] != 0 || metrics["NormalQueueRejections"] != 0)
                throw new InvalidOperationException("Credit-only restart did not settle the original bytes and prepared queue.");
            Console.WriteLine($"PASS {mode} credit-stalled DATA preserves Ping and wire-credit-only restart");
            passed++;
        }
        return passed;
    }

    private IRpcByteBufferWriter ProgressPacket(int sequence)
    {
        var packet = _context.Buffers.Rent();
        try
        {
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, 1))
            {
                var payload = packet.GetSpan(4098)[..4098];
                payload.Fill(0xA5);
                BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
                BinaryPrimitives.WriteInt32LittleEndian(payload[2..], sequence);
                packet.Advance(4098);
            }
            return packet;
        }
        catch { _context.Buffers.Return(packet); throw; }
    }

    private async Task ReadProgressDataAsync(int sequence)
    {
        var data = await ReadProgressFrameAsync(_receiver);
        if (data.Header.Type != ProtocolV2FrameType.StreamData || data.Header.RequestId != 1 ||
            data.Payload.Length != 4098 || BinaryPrimitives.ReadUInt16LittleEndian(data.Payload) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(data.Payload.AsSpan(2)) != sequence)
            throw new InvalidOperationException("The ordered DATA frame was lost or changed across the progress boundary.");
    }

    private async Task ReturnProgressCreditAsync(int bytes)
    {
        _receiver.SendWindowUpdate(1, 1, bytes);
        var frame = await ReadProgressFrameAsync(_sender);
        if (frame.Header.Type != ProtocolV2FrameType.WindowUpdate || frame.Header.RequestId != 1)
            throw new InvalidOperationException("Expected the peer's actual WindowUpdate frame.");
        var update = ProtocolV2PayloadCodec.ReadWindowUpdate(new ReadOnlySequence<byte>(frame.Payload));
        if (update.StreamId != 1 || update.Credit != bytes)
            throw new InvalidOperationException("The key-only credit payload was changed.");
        await _readyWriter!.EnqueueUpdateAsync(1, update.StreamId, checked((int)update.Credit));
    }

    private static async Task<(ProtocolV2FrameHeader Header, byte[] Payload)> ReadProgressFrameAsync(RpcSession session)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var read = await session.Input.ReadAsync(deadline.Token);
            var remaining = read.Buffer;
            var parsed = false;
            try
            {
                if (ProtocolV2FrameParser.TryReadFrame(ref remaining, session.RuntimeContext.Protocol, out var header, out var payload))
                {
                    parsed = true;
                    return (header, payload.ToArray());
                }
                if (read.IsCompleted) throw new EndOfStreamException("The expected progress-test frame was incomplete.");
            }
            finally
            {
                // A following complete frame can already be buffered. Do not mark it
                // examined when returning just one frame from this deterministic peer.
                session.Input.AdvanceTo(remaining.Start, parsed ? remaining.Start : read.Buffer.End);
            }
        }
    }
}
#endif
