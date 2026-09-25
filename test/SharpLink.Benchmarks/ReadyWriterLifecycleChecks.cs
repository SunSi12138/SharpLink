#if SHARPLINK_READY_WRITER_EXPERIMENT
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunLifecycleChecksAsync()
    {
        string[] scenarios = ["empty", "queued", "late-credit", "writer-pin", "held-capacity", "producer-close",
            "ordered-inbox", "foreign", "old-notification", "old-completion", "generation-limit", "terminal", "ready-order", "same-key-wire", "reuse100k"];
        var failures = new List<Exception>();
        foreach (var scenario in scenarios)
        {
            try
            {
                await CheckLifecycleAsync(scenario);
                Console.WriteLine($"PASS lifecycle/{scenario}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL lifecycle/{scenario}: {error}");
                failures.Add(error);
            }
        }
        if (failures.Count != 0) throw new AggregateException("B3 stream lifecycle checks failed.", failures);
        return scenarios.Length;
    }

    private static async Task RejectLifecycleAsync(Task operation)
    {
        try { await operation; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected stale/retained lifecycle rejection.");
    }

    private static async Task CheckLifecycleAsync(string scenario)
    {
        await using var fixture = await LifecycleFixture.CreateAsync(scenario is "foreign" or "ready-order" ? 2 : 1);
        var owner = fixture.Owner;
        var old = new StreamHandle(owner, 0, 1);
        var slot = owner._streams[0];
        var originalObject = slot;
        if (scenario == "terminal")
        {
            var pendingClose = owner.CloseStreamAsync(old);
            var pendingOpen = owner.OpenStreamAsync(2, 1);
            var error = new InvalidOperationException("controlled terminal before inbox execution");
            owner.Stopped(error);
            await RejectLifecycleAsync(pendingClose);
            await RejectLifecycleAsync(pendingOpen);
            RequireWire(owner._retiredLifetimes == 0, "Terminal must reject pending operations without executing them.");
            return;
        }
        if (scenario == "foreign")
        {
            await using var foreign = await LifecycleFixture.CreateAsync(1);
            await RejectLifecycleAsync(owner.EnqueueAsync(new StreamHandle(foreign.Owner, 0, 1), fixture.Packet(1, 1)).AsTask());
            var badClose = owner.CloseStreamAsync(new StreamHandle(foreign.Owner, 0, 1)); owner.DrainNotifications();
            await RejectLifecycleAsync(badClose);
            RequireWire(slot.ProducerBusy == 0 && slot.Frames.Count == 0 && !slot.Closed && owner._creditDebits == 0,
                "Foreign handle must not acquire a producer or touch current credit.");
            return;
        }
        if (scenario == "ready-order")
        {
            await fixture.CloseAsync(old);
            var reused = await fixture.OpenAsync(9, 7);
            await fixture.EnqueueAsync(new StreamHandle(owner, 1, 1), 2, 1);
            await fixture.EnqueueAsync(reused, 9, 7);
            RequireWire(owner.TryTake(out var first) && first.Slot == 1, "Reused slot must not jump an older ready stream.");
            fixture.Release(first);
            RequireWire(owner.TryTake(out var second) && second.Slot == 0, "Reused stream must eventually be selected.");
            fixture.Release(second);
            return;
        }
        if (scenario is "queued" or "held-capacity" or "producer-close")
        {
            await fixture.EnqueueAsync(old, 1, 1); // one-slot ring, with a pending notification
            ValueTask held = default;
            Task? producer = null;
            if (scenario == "held-capacity") held = slot.WaitForSpace(CancellationToken.None);
            if (scenario == "producer-close") producer = owner.EnqueueAsync(old, fixture.Packet(1, 1)).AsTask();
            await fixture.CloseAsync(old);
            RequireWire(slot.Frames.Count == 0 && slot.QueuedBytes == 0 && owner._discarded == 1 && owner._creditDebits == 0,
                "Close must discard unadmitted DATA without inventing credit or releases.");
            if (scenario == "held-capacity")
            {
                RequireWire(!slot.Retired && held.IsCompleted, "A signaled but held capacity result must pin the slot.");
                var denied = owner.OpenStreamAsync(2, 1); owner.DrainNotifications(); await RejectLifecycleAsync(denied);
                try { held.GetAwaiter().GetResult(); throw new Exception("Closed capacity result unexpectedly succeeded."); }
                catch (InvalidOperationException) { }
                owner.DrainNotifications();
            }
            if (producer is not null) { await RejectLifecycleAsync(producer); owner.DrainNotifications(); }
            RequireWire(slot.Retired && owner._retiredStreams.Count == 1, "Only consumed results and finished producers allow reuse.");
        }
        else if (scenario is "late-credit" or "writer-pin" or "ordered-inbox")
        {
            var frame = await fixture.TakeAsync(old, 1, 1);
            if (scenario != "writer-pin") fixture.Release(frame);
            if (scenario == "writer-pin") await fixture.UpdateAsync(1, 1, 16);
            if (scenario == "ordered-inbox")
            {
                var close = owner.CloseStreamAsync(old);
                await owner.EnqueueUpdateAsync(1, 1, 16);
                var open = owner.OpenStreamAsync(1, 2);
                owner.DrainNotifications();
                RequireWire(await close && (await open).Generation == 2, "Close/update/open must execute in one FIFO inbox.");
                RequireWire(owner._identities.ContainsKey(new StreamIdentity(1, 2)), "Composite stream ID must survive reuse.");
                return;
            }
            await fixture.CloseAsync(old);
            RequireWire(!slot.Retired && owner._identities.Count == 1, "A tombstone/pin must retain the identity and bounded capacity.");
            var denied = owner.OpenStreamAsync(1, 1); owner.DrainNotifications(); await RejectLifecycleAsync(denied);
            denied = owner.OpenStreamAsync(2, 1); owner.DrainNotifications(); await RejectLifecycleAsync(denied);
            if (scenario == "writer-pin")
            {
                RequireWire(slot.Outstanding == 0 && slot.Taken == 1 && slot.Released == 0,
                    "Early peer credit cannot release writer-owned bytes.");
                fixture.Release(frame);
            }
            else await fixture.UpdateAsync(1, 1, 16);
            RequireWire(slot.Retired, "Late credit and final writer release must independently permit retirement.");
        }
        else await fixture.CloseAsync(old);

        if (scenario == "generation-limit")
        {
            slot.Generation = long.MaxValue;
            var overflow = owner.OpenStreamAsync(2, 1); owner.DrainNotifications();
            try { await overflow; throw new Exception("Generation wrapped."); } catch (OverflowException) { }
            RequireWire(slot.Retired && owner._retiredStreams.Count == 1 && owner._identities.Count == 0,
                "Overflow must be rejected before popping or mutating the pool.");
            return;
        }
        var current = await fixture.OpenAsync(1, 7);
        RequireWire(current.Slot == old.Slot && current.Generation == 2 && ReferenceEquals(originalObject, owner._streams[0]),
            "The test must reuse the actual same stream object, not allocate a substitute state.");
        await RejectLifecycleAsync(owner.EnqueueAsync(old, fixture.Packet(1, 1)).AsTask());
        await RejectLifecycleAsync(owner.EnqueueAsync(0, fixture.Packet(1, 1)).AsTask());
        var staleClose = owner.CloseStreamAsync(old); owner.DrainNotifications();
        RequireWire(!await staleClose && !slot.Closed && slot.ProducerBusy == 0 && slot.Frames.Count == 0,
            "Old handles/legacy slot calls must not act on the next lifecycle.");
        var beforeCredit = owner._connectionCredit;
        await fixture.UpdateAsync(1, 1, 16); // retired old composite key
        await fixture.UpdateAsync(1, 7, 16); // new key, but no first admission
        RequireWire(owner._connectionCredit == beforeCredit && owner._returned <= owner._creditDebits * 16,
            "Retired and pre-admission updates cannot create permission or settle DATA.");
        if (scenario == "old-notification")
        {
            // Defensive check: a delayed old epoch message must not schedule the new ring.
            RequireWire(owner._notifications.Writer.TryWrite(new ReadyNotification(slot, 1)), "Old notification fixture overflowed.");
            Interlocked.Increment(ref owner._notificationsPending);
            owner.DrainNotifications();
            RequireWire(slot.Node.List is null && owner._staleEvents == 1, "An old notification scheduled a new generation.");
        }
        if (scenario == "old-completion")
        {
            var first = await fixture.TakeAsync(current, 1, 7); fixture.Release(first);
            await fixture.UpdateAsync(1, 7, 16);
            await fixture.CloseAsync(current);
            current = await fixture.OpenAsync(1, 7);
            var second = await fixture.TakeAsync(current, 1, 7);
            var outstanding = slot.Outstanding; var releases = owner._releases;
            // Do not double-return a buffer: only replay the retired immutable callback.
            first.Completion.Complete(null);
            originalObject.Complete(null); // the embedded generation-one target
            RequireWire(slot.Outstanding == outstanding && owner._releases == releases && slot.Released == 0,
                "Old callback completed a new writer frame.");
            fixture.Release(second);
            await fixture.UpdateAsync(1, 7, 16);
        }
        if (scenario == "same-key-wire")
        {
            // Generation is an internal handle property, not a field in WindowUpdate.
            // Identical key-only bits after legitimate reuse resolve the CURRENT life.
            var frame = await fixture.TakeAsync(current, 1, 7); fixture.Release(frame);
            await fixture.UpdateAsync(1, 7, 16); await fixture.CloseAsync(current);
            current = await fixture.OpenAsync(1, 7);
            frame = await fixture.TakeAsync(current, 1, 7); fixture.Release(frame);
            await fixture.UpdateAsync(1, 7, 16);
            RequireWire(slot.Outstanding == 0 && slot.Credit == 16,
                "Wire messages cannot be relabeled as generation-discriminating handles.");
        }
        if (scenario == "reuse100k")
        {
            for (var i = 0; i < 100000; i++)
            {
                var frame = await fixture.TakeAsync(current, 1, 7);
                fixture.Release(frame);
                await fixture.UpdateAsync(1, 7, 16);
                await fixture.CloseAsync(current);
                var previous = current;
                current = await fixture.OpenAsync(1, 7);
                if (i % 1000 == 0)
                    await RejectLifecycleAsync(owner.EnqueueAsync(previous, fixture.Packet(1, 7)).AsTask());
            }
            RequireWire(current.Generation == 100002 && owner._retiredLifetimes == 100001 &&
                ReferenceEquals(originalObject, owner._streams[0]) && slot.Credit == 16 && owner._connectionCredit == 16 &&
                owner._identities.Count == 1 && owner._ready.Count == 0,
                "100000 actual pooled lifetimes must preserve permission and reject old handles.");
        }
    }

    private sealed class LifecycleFixture : IAsyncDisposable
    {
        internal readonly ReadyWriterCoordinator Owner;
        private readonly SharpLinkRuntimeContext _context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        private readonly RpcSession _session;
        private readonly ITransportConnection _peer;
        private readonly CancellationTokenSource _cancel = new();
        private LifecycleFixture(ITransportConnection transport, ITransportConnection peer, int streams)
        {
            _peer = peer;
            _session = new RpcSession(transport, new RpcSessionCreationOptions(RpcSessionRole.Client, _context));
            Owner = new ReadyWriterCoordinator(_session, _context, _cancel, false, streams, int.MaxValue, 16, 16, streams * 16, 1, dynamicLifetimes: true);
        }
        internal static async Task<LifecycleFixture> CreateAsync(int streams)
        {
            var pair = await PhaseBTransportPair.CreateAsync("pipe");
            return new LifecycleFixture(pair.Client, pair.Server, streams);
        }
        internal IRpcByteBufferWriter Packet(long requestId, ushort streamId)
        {
            var packet = _context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)requestId))
            {
                var span = packet.GetSpan(18)[..18]; span.Clear(); BinaryPrimitives.WriteUInt16LittleEndian(span, streamId); packet.Advance(18);
            }
            return packet;
        }
        internal ValueTask EnqueueAsync(StreamHandle handle, long requestId, ushort streamId) => Owner.EnqueueAsync(handle, Packet(requestId, streamId));
        internal async Task<ReadyStreamFrame> TakeAsync(StreamHandle handle, long requestId, ushort streamId)
        {
            await EnqueueAsync(handle, requestId, streamId);
            RequireWire(Owner.TryTake(out var frame), "Expected ready DATA for a live generation.");
            return frame;
        }
        internal void Release(ReadyStreamFrame frame) { _context.Buffers.Return(frame.Packet); frame.Completion.Complete(null); }
        internal async Task CloseAsync(StreamHandle handle)
        { var pending = Owner.CloseStreamAsync(handle); Owner.DrainNotifications(); RequireWire(await pending, "Expected initial close."); }
        internal async Task<StreamHandle> OpenAsync(long requestId, ushort streamId)
        { var pending = Owner.OpenStreamAsync(requestId, streamId); Owner.DrainNotifications(); return await pending; }
        internal async Task UpdateAsync(long requestId, ushort streamId, int bytes)
        { await Owner.EnqueueUpdateAsync(requestId, streamId, bytes); Owner.DrainNotifications(); }
        public async ValueTask DisposeAsync()
        {
            Owner.Stopped(new OperationCanceledException("lifecycle fixture cleanup"));
            try { await Owner.Completion; } catch (Exception) { }
            await _session.DisposeAsync(); await _peer.DisposeAsync(); _cancel.Dispose(); _context.Dispose();
        }
    }
}
#endif
