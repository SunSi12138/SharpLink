using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed class SmallStreamRouteManagerTests
{
    static void Check(bool ok, string msg) { if (!ok) throw new Exception(msg); }
    [Test]
    public async Task SparseOutOfOrderMixedRoutesStayDistinct()
    {
        var m = new StreamManager(); ushort[] ids = [65535, 2, 128, 0, 1, 4, 127, 7]; var ds = ids.ToDictionary(id => id, id => new Recorder()); foreach (var id in ids) m.Register(91, id, ds[id]);
        foreach (var id in ids) await m.DispatchChunkAsync(91, id, new ReadOnlySequence<byte>(new byte[1])); Check(ds.Values.All(d => d.Hits == 1), "distinct routing");
        m.CompleteStream(91, 2, null); var replacement = new Recorder(); m.Register(91, 2, replacement); await m.DispatchChunkAsync(91, 2, default); Check(ds[2].Hits == 1 && replacement.Hits == 1, "replacement identity");
        m.CompleteAll(null); Check(m.ActiveStreamCount == 0 && ds.Values.All(d => d.Completes == 1) && replacement.Completes == 1, "once-only cleanup");
    }
    [Test] public void UnknownSparseIdsDoNotAllocate() { var m = new StreamManager(); m.Register(1, 1, new Recorder()); for (int i = 0; i < 10000; i++) m.DispatchChunkAsync(1, 65535, default).GetAwaiter().GetResult(); long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 100000; i++) m.DispatchChunkAsync(1, 65535, default).GetAwaiter().GetResult(); Check(GC.GetAllocatedBytesForCurrentThread() == b, "unknown reads must allocate zero"); Check(m.ActiveStreamCount == 1, "miss accounting"); m.CompleteAll(null); }
    [Test]
    public async Task SlotAndFallbackDrainWaitForActiveLease()
    {
        foreach (ushort active in new ushort[] { 1, 2, 7, 127, 128, 65535 })
        {
            var done = new List<ushort>(); var m = new StreamManager(new RuntimeConcurrencyOptions(), null, null, (_, id) => done.Add(id)); var gate = new Gated(); m.Register(8, active, gate); ushort other = active == 1 ? (ushort)2 : (ushort)1; m.Register(8, other, new Recorder());
            var data = m.DispatchChunkAsync(8, active, default); await gate.Entered.Task; var drain = m.CompleteRequestStreamsAfterDispatchesAsync(8, null); Check(!drain.IsCompleted && m.ActiveStreamCount == 0 && done.Count == 0, "drain wait"); await m.DispatchChunkAsync(8, active, default); Check(m.DroppedStreamFrames == 1, "closed lookup"); gate.Release.SetResult(); await data; await drain; Check(done.Order().SequenceEqual(new[] { active, other }.Order()), "both final callbacks");
        }
    }
    [Test] public void DuplicateSlotsAndSparseRegistrationRollBackCount() { foreach (ushort id in new ushort[] { 0, 1, 2, 7, 65535 }) { var m = new StreamManager(); m.Register(42, id, new Recorder()); try { m.Register(42, id, new Recorder()); throw new Exception("duplicate accepted"); } catch (InvalidOperationException) { } Check(m.ActiveStreamCount == 1, "duplicate accounting"); m.CompleteAll(null); Check(m.ActiveStreamCount == 0, "cleanup accounting"); } }
    class Recorder : IStreamDispatcher { public int Hits, Completes; public ValueTask DispatchAsync(ReadOnlySequence<byte> p) { Hits++; return ValueTask.CompletedTask; } public void Complete(bool e, string? s) => Complete(null); public void Complete(Exception? e) => Completes++; }
    class Gated : IStreamDispatcher { public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously); public async ValueTask DispatchAsync(ReadOnlySequence<byte> p) { Entered.SetResult(); await Release.Task; } public void Complete(bool e, string? s) { } public void Complete(Exception? e) { } }
}
