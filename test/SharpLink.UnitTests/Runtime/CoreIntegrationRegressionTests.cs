using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public sealed class CoreIntegrationRegressionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalAbortLateUpdateMustNotRefundAnotherStream(bool resolved)
    {
        var flow = new StreamFlowController(4, 4, 1024);
        await flow.AcquireSendCreditAsync(1, 0, 4, CancellationToken.None);
        var aborted = new SharpLinkException(SharpLinkErrorCode.Cancelled, "local abort");
        flow.AbortSendStreams(1, aborted);
        var second = resolved
            ? await flow.AcquireSendCreditLeaseAsync(2, 0, 4, CancellationToken.None)
            : default;
        if (!resolved)
            await flow.AcquireSendCreditAsync(2, 0, 4, CancellationToken.None);

        flow.ApplyWindowUpdate(1, 0, 4);
        Require(flow.SendConnectionCredit == 0,
            "A's already-refunded abort must not credit bytes still owned by B.");
        if (resolved) flow.ReturnUnsentCredit(in second, 4);
        else flow.ReturnUnsentCredit(2, 0, 4);
        Require(flow.SendConnectionCredit == 4, "B's first unsent return must succeed exactly once.");
        flow.Complete(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test cleanup"));
    }

    [Test]
    public async Task AbortLateUpdateMustNotAdmitAWaitingThirdStream()
    {
        var flow = new StreamFlowController(4, 4, 1024);
        await flow.AcquireSendCreditAsync(1, 0, 4, CancellationToken.None);
        flow.AbortSendStreams(1, new SharpLinkException(SharpLinkErrorCode.Cancelled, "abort"));
        await flow.AcquireSendCreditAsync(2, 0, 4, CancellationToken.None);
        var waiting = flow.AcquireSendCreditAsync(3, 0, 4, CancellationToken.None);
        Require(!waiting.IsCompleted, "C initially waits for B's connection debt.");
        flow.ApplyWindowUpdate(1, 0, int.MaxValue);
        Require(flow.SendConnectionCredit == 0 && !waiting.IsCompleted,
            "Excess late credit after abort cannot admit C.");
        flow.ApplyWindowUpdate(2, 0, 4);
        await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Require(flow.SendConnectionCredit == 0, "C now owns B's real refund.");
        flow.Complete(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test cleanup"));
    }

    [Test]
    public async Task FailedCompletionWithDebtMustStillAcceptItsFinalUpdate()
    {
        var flow = new StreamFlowController(4, 4, 1024, 1);
        await flow.AcquireSendCreditAsync(1, 0, 4, CancellationToken.None);
        flow.CompleteSendStream(1, 0, new SharpLinkException(SharpLinkErrorCode.Internal, "failed"));
        Require(flow.RetainedSendStreamCount == 1, "Failed completion retains outstanding debt.");
        flow.ApplyWindowUpdate(1, 0, 4);
        Require(flow.SendConnectionCredit == 4 && flow.RetainedSendStreamCount == 0,
            "Completion is not local abort: its real refund releases the tombstone.");
    }

    [Test]
    public async Task ActiveWirePermissionCompatibilityMustNotBeChanged()
    {
        var flow = new StreamFlowController(16, 32, 1024);
        await flow.AcquireSendCreditAsync(1, 0, 16, CancellationToken.None);
        await flow.AcquireSendCreditAsync(2, 0, 16, CancellationToken.None);
        flow.ApplyWindowUpdate(1, 0, 16);
        flow.ApplyWindowUpdate(1, 0, 16);
        Require(flow.SendConnectionCredit == 32,
            "Keep the documented active-stream independent permission clamps; do not apply a global coupled-policy rewrite.");
        flow.Complete(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test cleanup"));
    }

    [Test]
    public void FusedRetirementMustCloseBeforeReturningAndKeepHeldData()
    {
        var probe = new EntryProbe();
        Require(probe.Acquire(), "Acquire old DATA before retirement.");
        Require(probe.ClaimAndClose(), "First retirement has one winner.");
        Require(!probe.Acquire() && probe.State.HasActiveDispatches && !probe.State.IsDetached,
            "Retirement excludes new DATA without releasing existing ownership.");
        Require(!probe.ClaimAndClose(), "Duplicate retirement cannot win.");
        probe.Detach();
        Require(probe.Dispatcher.Drained == 0, "Held DATA still pins dispatcher.");
        probe.Release();
        Require(probe.Dispatcher.Drained == 1 && !probe.State.HasActiveDispatches,
            "Exactly one final drain after release.");
    }

    [Test]
    public void FusedRetirementMustHaveExactlyOneConcurrentWinner()
    {
        for (var round = 0; round < 128; round++)
        {
            var probe = new EntryProbe();
            Require(probe.Acquire(), "Pin DATA through competing claims.");
            var winners = 0;
            Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
            {
                if (probe.ClaimAndClose()) Interlocked.Increment(ref winners);
                Require(!probe.Acquire(), "Every returned claim observes a closed route.");
            });
            Require(winners == 1 && probe.State.HasActiveDispatches, "One retirement, unchanged held count.");
            probe.Detach();
            probe.Release();
            Require(probe.Dispatcher.Drained == 1, "Race must not duplicate pool return.");
        }
    }

    [Test]
    public void ExistingStandaloneClaimMustRemainIndependentForItsCallers()
    {
        var probe = new EntryProbe();
        Require(probe.ClaimOnly() && probe.Acquire(), "Standalone claim has no new Close semantics.");
        Require(!probe.ClaimAndClose() && !probe.Acquire(), "Fusion closes even after an earlier claim, without a second winner.");
        probe.Detach();
        probe.Release();
        Require(probe.Dispatcher.Drained == 1, "One final drain.");
    }

    [Test]
    public void FusedRetirementMustPreserveCleanupPinAndColdFlags()
    {
        var probe = new EntryProbe();
        Require(probe.Acquire() && probe.PublishTerminal(), "Initialize DATA and terminal bit.");
        probe.PeerTerminal();
        Require(probe.ClaimAndClose(), "Close and claim.");
        var cleanup = 0;
        probe.WhenDrained(() =>
        {
            cleanup++;
            Require(probe.State.HasActiveDispatches, "Cleanup pin remains live.");
            Require(!probe.PublishTerminal() && !probe.ClaimAndClose(), "Cold claims remain unique.");
            probe.Detach();
            Require(probe.Dispatcher.Drained == 0, "Detach waits for cleanup pin.");
        });
        probe.Release();
        Require(cleanup == 1 && probe.Dispatcher.Drained == 1 && !probe.State.HasActiveDispatches,
            "Cleanup and pool return each occur exactly once.");
    }

    private sealed class EntryProbe
    {
        internal readonly CaptureDispatcher Dispatcher = new();
        internal readonly IStreamDispatchState State;
        internal readonly Func<bool> Acquire, ClaimAndClose, ClaimOnly, PublishTerminal;
        internal readonly Action Release, Detach, PeerTerminal;
        internal readonly Action<Action> WhenDrained;
        internal EntryProbe()
        {
            var type = typeof(StreamManager).GetNestedType("DispatcherEntry", BindingFlags.NonPublic)!;
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(IStreamDispatcher), typeof(StreamFlowController.ResolvedReceiveCreditLease)], null)!;
            State = (IStreamDispatchState)constructor.Invoke([Dispatcher, default(StreamFlowController.ResolvedReceiveCreditLease)]);
            T Bind<T>(string method) where T : Delegate => type.GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<T>(State);
            Acquire = Bind<Func<bool>>("TryAcquire");
            ClaimAndClose = Bind<Func<bool>>("TryClaimRetirementAndClose");
            ClaimOnly = Bind<Func<bool>>("TryClaimRetirement");
            PublishTerminal = Bind<Func<bool>>("TryPublishReceiveTerminal");
            Release = Bind<Action>("Release");
            Detach = Bind<Action>("Detach");
            PeerTerminal = Bind<Action>("MarkPeerTerminalReceived");
            WhenDrained = Bind<Action<Action>>("RunWhenDispatchesDrained");
        }
    }
    private sealed class CaptureDispatcher : IStreamDispatcher, IStreamDispatchLease
    {
        internal int Drained;
        public void BindDispatchState(IStreamDispatchState state) { }
        public void OnDispatchesDrained() => Interlocked.Increment(ref Drained);
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => ValueTask.CompletedTask;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
