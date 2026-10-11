using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamManagerTests
{
    [Test]
    public async Task TerminalDiscardShouldRefundBufferedAndNewBytesWithoutInvokingChildOrDecoder()
    {
        var accepted = 0;
        var consumed = 0;
        var released = 0;
        var decoded = 0;
        var completed = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes, (_, _, bytes) => consumed += bytes, (_, _) => completed++);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        manager.ReservePreAdmissionStreams(901, 1, buffers, _ => true, bytes => released += bytes,
            () => throw new Exception("unexpected capacity failure"),
            _ => { decoded++; throw new Exception("terminal frames must not decode"); },
            retainUntilLocalCompletion: true);
        await manager.DispatchChunkAsync(901, 1, new ReadOnlySequence<byte>(new byte[4]));
        Ensure(manager.TryDispatchPreAdmissionCompressed(901, 1,
            new ReadOnlySequence<byte>(new byte[2]), 11, out var buffered), "buffered compressed route");
        await buffered;
        Ensure(accepted == 15 && consumed == 0, "initial receive bytes stay buffered");
        manager.AbandonExistingRequestStreams(901, 1);
        Ensure(manager.TryDiscardTerminalStreamData(901, 1,
            new ReadOnlySequence<byte>(new byte[3]), 17, out var rejected), "existing stable terminal route");
        await rejected;
        Ensure(accepted == 32 && consumed == 32 && released == 6,
            "old retained bytes and newly accepted original bytes must refund exactly once");
        Ensure(decoded == 0, "discard must not invoke a compressed provider");
        Ensure(manager.TryDiscardTerminalStreamData(901, 1,
            new ReadOnlySequence<byte>(new byte[4]), 4, out var late), "late existing drain route");
        await late;
        Ensure(accepted == 36 && consumed == 36,
            "repeated terminal data must refund once per frame");
        Ensure(manager.TryCompleteTerminalStream(901, 1, null), "peer completion reaches existing drain route");
        Ensure(manager.ActiveStreamCount == 0 && completed == 1, "peer completion retires the route once");
        Ensure(!manager.TryCompleteTerminalStream(901, 1, null) && completed == 1,
            "duplicate completion must not publish twice");
    }

    [Test]
    public async Task TerminalDiscardShouldCloseAttachedStableChildWithoutDeliveringData()
    {
        var accepted = 0;
        var consumed = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes, (_, _, bytes) => consumed += bytes, null);
        manager.ReservePreAdmissionStreams(906, 1,
            new SharpLinkBufferWriterPool(new BufferWriterPoolOptions()), _ => true, _ => { },
            () => throw new Exception("unexpected capacity failure"), retainUntilLocalCompletion: true);
        var child = new RecordingDispatcher();
        manager.Register(906, 1, child);
        manager.AbandonExistingRequestStreams(906, 1);
        Ensure(manager.TryDiscardTerminalStreamData(906, 1,
            new ReadOnlySequence<byte>(new byte[4]), 4, out var dispatch), "stable attached route");
        await dispatch;
        Ensure(child.DispatchCount == 0 && accepted == 4 && consumed == 4,
            "terminal data must bypass the typed child and refund once");
        Ensure(manager.TryCompleteTerminalStream(906, 1, null) && manager.ActiveStreamCount == 0,
            "terminal completion must retire attached stable route");
    }

    [Test]
    public async Task TerminalDiscardShouldNotAbandonALiveStableRoute()
    {
        var manager = new StreamManager();
        manager.ReservePreAdmissionStreams(907, 1,
            new SharpLinkBufferWriterPool(new BufferWriterPoolOptions()), _ => true, _ => { },
            () => throw new Exception("unexpected stream capacity"), retainUntilLocalCompletion: true);
        var child = new RecordingDispatcher();
        manager.Register(907, 1, child);
        Ensure(!manager.TryDiscardTerminalStreamData(907, 1,
            new ReadOnlySequence<byte>(new byte[4]), 4, out _),
            "a stale terminal decision must not gain authority over a live stable route");
        Ensure(!manager.TryCompleteTerminalStream(907, 1, null),
            "terminal cleanup must not complete a newer live route");
        await manager.DispatchChunkAsync(907, 1, new ReadOnlySequence<byte>(new byte[4]));
        Ensure(child.DispatchCount == 1 && child.CompleteCount == 0,
            "a live route must remain available to its current owner");
        manager.CompleteRequestStreams(907, null);
    }

    [Test]
    public async Task TerminalDiscardShouldNotCreateUnknownRoutesOrInvokeDirectConsumers()
    {
        var accepted = 0;
        var consumed = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(),
            (_, _, bytes) => accepted += bytes, (_, _, bytes) => consumed += bytes, null);
        Ensure(!manager.TryDiscardTerminalStreamData(902, 1, ReadOnlySequence<byte>.Empty, 4, out _),
            "unknown route must stay absent");
        Ensure(!manager.TryCompleteTerminalStream(902, 1, null) && manager.ActiveStreamCount == 0,
            "unknown completion must not create routing state");
        var child = new RecordingDispatcher();
        manager.Register(902, 1, child);
        Ensure(!manager.TryDiscardTerminalStreamData(902, 1, ReadOnlySequence<byte>.Empty, 4, out _),
            "direct typed consumer is not a stable drain route");
        Ensure(!manager.TryCompleteTerminalStream(902, 1, null), "terminal-only helper must not complete direct consumer");
        Ensure(accepted == 0 && consumed == 0 && child.DispatchCount == 0 && child.CompleteCount == 0,
            "unknown/direct routes must receive no credit or callbacks");
        manager.CompleteRequestStreams(902, null);
        await Task.CompletedTask;
    }

    [Test]
    public async Task TerminalDiscardShouldPreserveFlowLimitsAndFlushFinalCredit()
    {
        var controller = new StreamFlowController(64, 64, 128);
        var updates = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), controller.AcceptReceived,
            (id, stream, bytes) => updates += controller.RecordConsumed(id, stream, bytes),
            (id, stream) => updates += controller.FlushConsumed(id, stream));
        manager.DrainRejectedRequestStreams(903, 1);
        Ensure(manager.TryDiscardTerminalStreamData(903, 1, ReadOnlySequence<byte>.Empty, 4, out var small),
            "existing discarding route accepts validated data");
        await small;
        Ensure(updates == 0, "small refund remains below update threshold");
        Ensure(manager.TryCompleteTerminalStream(903, 1, null) && updates == 4,
            "terminal completion flushes remaining credit exactly once");
        manager.DrainRejectedRequestStreams(904, 1);
        try
        {
            manager.TryDiscardTerminalStreamData(904, 1, ReadOnlySequence<byte>.Empty, 129, out _);
            throw new Exception("oversized terminal data must be rejected");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
        }
        Ensure(updates == 4, "invalid frame must not receive credit");
        manager.CompleteRequestStreams(904, null);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedTerminalCompletionShouldNotCompleteAReplacementRoute(bool keepRequestContainer)
    {
        const long requestId = 909;
        var manager = new StreamManager();
        manager.DrainRejectedRequestStreams(requestId, keepRequestContainer ? 2 : 1);
        var map = typeof(StreamManager).GetField("_dispatchersByRequestId",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        object?[] lookup = [requestId, null];
        Ensure((bool)map.GetType().GetMethod("TryGetValue")!.Invoke(map, lookup)!, "original request routes");
        var requestRoutes = lookup[1]!;
        object?[] acquire = [(ushort)1, null];
        Ensure((bool)requestRoutes.GetType().GetMethod("TryAcquire")!.Invoke(requestRoutes, acquire)!,
            "capture original discard entry lease");
        var oldEntry = acquire[1]!;
        try
        {
            manager.Unregister(requestId, 1);
            var replacement = new RecordingDispatcher();
            manager.Register(requestId, 1, replacement);
            var complete = typeof(StreamManager).GetMethod("CompleteCapturedTerminalStream",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Ensure(!(bool)complete.Invoke(manager, [requestId, (ushort)1, requestRoutes, oldEntry, null])!,
                "old terminal completion must refuse a different entry or request container");
            await manager.DispatchChunkAsync(requestId, 1, new ReadOnlySequence<byte>(new byte[4]));
            Ensure(replacement.DispatchCount == 1 && replacement.CompleteCount == 0,
                "replacement route must remain live and uncompleted");
        }
        finally
        {
            oldEntry.GetType().GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(oldEntry, null);
            manager.CompleteRequestStreams(requestId, null);
        }
    }

    [Test]
    public async Task TerminalDiscardShouldRejectDataAfterPeerTerminal()
    {
        var manager = new StreamManager();
        manager.ReservePreAdmissionStreams(905, 1,
            new SharpLinkBufferWriterPool(new BufferWriterPoolOptions()), _ => true, _ => { },
            () => throw new Exception("unexpected capacity failure"), retainUntilLocalCompletion: true);
        manager.CompletePeerStream(905, 1, null);
        await Assert.That(() => manager.TryDiscardTerminalStreamData(905, 1,
            ReadOnlySequence<byte>.Empty, 4, out _)).Throws<SharpLinkProtocolViolationException>();
        manager.AbandonExistingRequestStreams(905, 1);
        Ensure(manager.ActiveStreamCount == 0, "late-data rejection must preserve terminal route cleanup");
    }
}
