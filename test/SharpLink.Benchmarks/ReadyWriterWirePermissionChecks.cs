#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunWirePermissionChecksAsync()
    {
        var passed = 0;
        var failures = new List<Exception>();
        foreach (var reference in new[] { true, false })
            foreach (var scenario in new[] { "duplicate", "excess", "unknown", "refund-conflict", "held-writer", "wire-duplicate" })
            {
                try
                {
                    await CheckWirePermissionAsync(reference, scenario);
                    Console.WriteLine($"PASS wire-permission/{reference}/{scenario}");
                    passed++;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"FAIL wire-permission/{reference}/{scenario}: {error}");
                    failures.Add(error);
                }
            }
        foreach (var seed in new[] { 17, 31, 97 })
        {
            try
            {
                await CheckWireDifferentialAsync(seed);
                Console.WriteLine($"PASS wire-permission/seed{seed}: 12000 fixed-lifecycle differential steps");
                passed++;
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Independent wire permission must not invent settled DATA.", failures);
        return passed;
    }

    private static void RequireWire(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task CheckWirePermissionAsync(bool reference, string scenario)
    {
        await using var fixture = await WirePermissionFixture.CreateAsync(reference, 2, 1, 16, 32);
        var owner = fixture.Owner;
        if (scenario == "unknown")
        {
            // No send admission exists yet, even though this fixture has two fixed slots.
            await fixture.UpdateAsync(1, 1, 16);
            RequireWire(owner._returned == 0 && !owner.Completion.IsCompleted, "Pre-admission credit must not settle unsent DATA.");
        }
        await fixture.TakeAsync(0);
        var held = await fixture.TakeAsync(1, release: scenario is not ("held-writer" or "refund-conflict"));
        try
        {
            await fixture.UpdateAsync(1, 1, 16, wire: scenario == "wire-duplicate");
            if (scenario == "unknown")
            {
                await fixture.UpdateAsync(999, 1, 16);
                await fixture.UpdateAsync(1, 2, 16);
                RequireWire(owner._returned == 16 && !owner.Completion.IsCompleted, "Unknown composite keys must not return another stream's credit.");
            }
            else
            {
                await fixture.UpdateAsync(1, 1, scenario == "excess" ? int.MaxValue : 16,
                    wire: scenario == "wire-duplicate");
                RequireWire(owner._returned == 16 && !owner.Completion.IsCompleted,
                    "Repeated permission must not count B's still-outstanding DATA as consumed.");
                if (!reference)
                    RequireWire(owner._connectionCredit == 32 && owner._streams[1].Credit == 0 &&
                        owner._streams[1].Outstanding == 16, "Connection permission clamps independently from B's debt and stream window.");
            }
            if (scenario == "refund-conflict")
            {
                // The frozen controller REJECTS a refund that would exceed either hard
                // window. Preserve that error, rather than quietly clamp a double return.
                fixture.ReturnBuffer(held);
                held = default;
                owner.Released(1, 16, admitted: false, error: null);
                Exception? failure = null;
                try { await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) { failure = error; }
                RequireWire(failure is InvalidOperationException && owner._streams[1].Released == 0 && owner._releases == 1,
                    "A conflicting refund must fail without publishing a release.");
                if (!reference)
                    RequireWire(owner._connectionCredit == 32 && owner._streams[1].Credit == 0 && owner._streams[1].Outstanding == 16,
                        "A rejected refund must not partially mutate either permission or debt.");
                return;
            }
            await fixture.UpdateAsync(2, 1, 16);
            if (scenario == "held-writer")
            {
                RequireWire(!owner.Completion.IsCompleted, "Early credit must not release the writer's retained frame.");
                fixture.Release(held);
                held = default;
            }
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            RequireWire(owner._returned == 32 && owner._releases == 2, "Both original DATA frames must settle exactly once.");
        }
        finally
        {
            if (held.Packet is not null && scenario is "held-writer" or "refund-conflict") fixture.Release(held);
        }
    }

    private static async Task CheckWireDifferentialAsync(int seed)
    {
        const int streams = 4, window = 64, connection = 128, bytes = 16;
        await using var fixture = await WirePermissionFixture.CreateAsync(false, streams, 100000, window, connection);
        var owner = fixture.Owner;
        var baseline = new StreamFlowController(window, connection, 1024, streams);
        var random = new Random(seed);
        // Establish the same four active identities. No retirement, waiter or pool
        // equivalence is inferred from these fixed-lifecycle traces.
        for (var i = 0; i < streams; i++)
        {
            RequireWire(baseline.TryAcquireSendCredit(i + 1, 1, bytes), "Initial reference admission failed.");
            await fixture.TakeAsync(i);
        }
        for (var step = 0; step < 12000; step++)
        {
            var index = random.Next(streams);
            if (random.Next(3) == 0)
            {
                var expected = baseline.TryAcquireSendCredit(index + 1, 1, bytes);
                var actual = Available(owner._streams[index].Credit, window, bytes) && Available(owner._connectionCredit, connection, bytes);
                RequireWire(actual == expected, $"Admission diverged at seed {seed}, step {step}.");
                if (expected) await fixture.TakeAsync(index);
            }
            else
            {
                var credit = random.Next(1, 129);
                baseline.ApplyWindowUpdate(index + 1, 1, credit);
                await fixture.UpdateAsync(index + 1, 1, credit);
            }
            for (var i = 0; i < streams; i++)
            {
                var expected = baseline.TryAcquireSendCredit(i + 1, 1, bytes);
                var actual = Available(owner._streams[i].Credit, window, bytes) && Available(owner._connectionCredit, connection, bytes);
                RequireWire(actual == expected, $"Post-update permission diverged at seed {seed}, step {step}, stream {i}.");
                if (expected) baseline.ReturnUnsentCredit(i + 1, 1, bytes);
                RequireWire(owner._streams[i].Credit + owner._streams[i].Outstanding == window &&
                    owner._streams[i].Outstanding >= 0, "Per-stream debt was lost or credited twice.");
            }
            RequireWire(owner._connectionCredit <= connection && owner._returned <= owner._creditDebits * bytes,
                "Permission exceeded the hard window or more DATA settled than was admitted.");
        }
    }

    // The writer side is driven synchronously by the test owner. The wire-duplicate
    // case additionally uses a real peer SendPump and frame/payload parser for updates.
    private sealed class WirePermissionFixture : IAsyncDisposable
    {
        internal readonly ReadyWriterCoordinator Owner;
        private readonly SharpLinkRuntimeContext _context;
        private readonly RpcSession _session, _peer;
        private readonly CancellationTokenSource _cancel = new();

        private WirePermissionFixture(bool reference, int streams, int items, int window, int connection,
            ITransportConnection transport, ITransportConnection peer)
        {
            _context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
            _session = new RpcSession(transport, new RpcSessionCreationOptions(RpcSessionRole.Client, _context));
            _peer = new RpcSession(peer, new RpcSessionCreationOptions(RpcSessionRole.Server, _context));
            foreach (var session in new[] { _session, _peer })
                RequireWire(session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                    ProtocolV2Capabilities.None, _context.Protocol.MaxFramePayloadBytes, window, connection, null)),
                    "Wire fixture handshake state was not installed.");
            Owner = new ReadyWriterCoordinator(_session, _context, _cancel, reference, streams, items, 16, window, connection, 4);
        }
        internal static async Task<WirePermissionFixture> CreateAsync(bool reference, int streams, int items, int window, int connection)
        {
            var pair = await PhaseBTransportPair.CreateAsync("pipe");
            return new WirePermissionFixture(reference, streams, items, window, connection, pair.Client, pair.Server);
        }
        internal async Task<ReadyStreamFrame> TakeAsync(int index, bool release = true)
        {
            var packet = _context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(index + 1)))
            {
                packet.GetSpan(18)[..18].Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(packet.GetSpan(18), 1);
                packet.Advance(18);
            }
            await Owner.EnqueueAsync(index, packet);
            RequireWire(Owner.TryTake(out var frame) && frame.Slot == index, "Expected exact ready frame admission.");
            if (release) Release(frame);
            return frame;
        }
        internal void ReturnBuffer(ReadyStreamFrame frame) => _context.Buffers.Return(frame.Packet);
        internal void Release(ReadyStreamFrame frame)
        { ReturnBuffer(frame); Owner.Released(frame.Slot, frame.CreditBytes, true, null); }
        internal async Task UpdateAsync(long requestId, ushort streamId, int credit, bool wire = false)
        {
            if (wire)
            {
                _peer.SendWindowUpdate(requestId, streamId, credit);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var read = await _session.Input.ReadAsync(timeout.Token);
                    var remaining = read.Buffer;
                    var parsed = false;
                    try
                    {
                        if (ProtocolV2FrameParser.TryReadFrame(ref remaining, _context.Protocol, out var header, out var payload))
                        {
                            parsed = true;
                            var update = ProtocolV2PayloadCodec.ReadWindowUpdate(payload);
                            RequireWire(header.Type == ProtocolV2FrameType.WindowUpdate && header.RequestId == (ulong)requestId &&
                                update.StreamId == streamId && update.Credit == (uint)credit, "The actual WindowUpdate bytes changed.");
                            break;
                        }
                        if (read.IsCompleted) throw new EndOfStreamException("Incomplete wire update.");
                    }
                    finally { _session.Input.AdvanceTo(remaining.Start, parsed ? remaining.Start : read.Buffer.End); }
                }
            }
            await Owner.EnqueueUpdateAsync(requestId, streamId, credit);
            Owner.DrainNotifications();
        }
        public async ValueTask DisposeAsync()
        {
            Owner.Stopped(new OperationCanceledException("wire permission fixture cleanup"));
            try { await Owner.Completion; } catch (Exception) { }
            await _session.DisposeAsync();
            await _peer.DisposeAsync();
            _cancel.Dispose();
            _context.Dispose();
        }
    }
}
#endif
