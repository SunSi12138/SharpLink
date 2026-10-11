using System;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using TUnit.Core;

namespace SharpLink.UnitTests.Runtime;

// Diagnostic overlay tests. Every operation enters the actual public keyed API.
// No reflection, synthetic call to AcquireContendedSendCreditAsync, or altered
// pool is used. The identity getter only observes state while this worker pauses.
public sealed class StreamFlowControllerLeaseRaceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReusedObjectMustRejectOriginalAcquire(bool sameKey)
    {
        var controller = NewController();
        using var pause = new ProbePause();
        Task<Exception?>? pending = null;
        try
        {
            Reserve(controller, 1, 16);
            var original = controller.CaptureSendStateForLeaseRaceTests(1, 0);
            controller.AfterProbeBeforeContendedForTests = pause.Wait;
            pending = StartAcquire(controller, 1, CancellationToken.None);
            await pause.Entered.Task.WaitAsync(Bound);

            controller.ReturnUnsentCredit(1, 0, 16);
            controller.CompleteSendStream(1, 0);
            var replacementKey = sameKey ? 1L : 2L;
            Reserve(controller, replacementKey, 8);
            var replacement = controller.CaptureSendStateForLeaseRaceTests(replacementKey, 0);
            Ensure(ReferenceEquals(original.State, replacement.State), "Test did not actually reuse the same pooled object.");
            Ensure(replacement.Lease > original.Lease, "Reused object did not advance its lease.");
            Ensure(replacement.Credit == 8 && controller.SendConnectionCredit == 24,
                "Replacement must initially own exactly eight bytes.");

            pause.Release();
            var outcome = await pending.WaitAsync(Bound);
            var after = controller.CaptureSendStateForLeaseRaceTests(replacementKey, 0);
            Console.WriteLine($"case=reused sameKey={sameKey} sameObject={ReferenceEquals(original.State, replacement.State)} oldLease={original.Lease} newLease={replacement.Lease} originalAccepted={outcome is null} replacementCreditAfter={after.Credit} connectionCreditAfter={controller.SendConnectionCredit} outcome={outcome?.GetType().FullName ?? "accepted"}");
            Ensure(outcome is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "Stale original AcquireSendCreditAsync was accepted into a replacement generation.");
            Ensure(after.Lease == replacement.Lease && after.Credit == 8 && controller.SendConnectionCredit == 24,
                "Rejected stale acquire changed the replacement generation's credit.");
            Ensure(pause.Hits == 1, "The intended outside-gate pause was not visited exactly once.");
        }
        finally
        {
            pause.Release();
            controller.Complete(new OperationCanceledException("lease-race test cleanup"));
            await Observe(pending);
        }
    }

    [Test]
    public async Task CreditReturnWithoutRetirementMustStillAdmitOriginalAcquire()
    {
        var controller = NewController();
        using var pause = new ProbePause();
        Task<Exception?>? pending = null;
        try
        {
            Reserve(controller, 1, 16);
            var original = controller.CaptureSendStateForLeaseRaceTests(1, 0);
            controller.AfterProbeBeforeContendedForTests = pause.Wait;
            pending = StartAcquire(controller, 1, CancellationToken.None);
            await pause.Entered.Task.WaitAsync(Bound);
            controller.ReturnUnsentCredit(1, 0, 16);
            pause.Release();
            var outcome = await pending.WaitAsync(Bound);
            var current = controller.CaptureSendStateForLeaseRaceTests(1, 0);
            Ensure(outcome is null, "Same-generation credit return was incorrectly rejected.");
            Ensure(ReferenceEquals(original.State, current.State) && original.Lease == current.Lease,
                "The no-retirement control unexpectedly changed generation.");
            Ensure(current.Credit == 8 && controller.SendConnectionCredit == 24,
                "Same-generation admission must reserve exactly eight bytes.");
            Console.WriteLine($"case=no-retirement accepted=true lease={current.Lease} connectionCredit={controller.SendConnectionCredit}");
        }
        finally
        {
            pause.Release();
            controller.Complete(new OperationCanceledException("lease-race test cleanup"));
            await Observe(pending);
        }
    }

    [Test]
    public async Task CancellationAfterReuseMustNotReserveReplacementCredit()
    {
        var controller = NewController();
        using var pause = new ProbePause();
        using var cancellation = new CancellationTokenSource();
        Task<Exception?>? pending = null;
        try
        {
            Reserve(controller, 1, 16);
            var original = controller.CaptureSendStateForLeaseRaceTests(1, 0);
            controller.AfterProbeBeforeContendedForTests = pause.Wait;
            pending = StartAcquire(controller, 1, cancellation.Token);
            await pause.Entered.Task.WaitAsync(Bound);
            controller.ReturnUnsentCredit(1, 0, 16);
            controller.CompleteSendStream(1, 0);
            Reserve(controller, 1, 8);
            var replacement = controller.CaptureSendStateForLeaseRaceTests(1, 0);
            Ensure(ReferenceEquals(original.State, replacement.State) && original.Lease != replacement.Lease,
                "The cancellation control did not reuse the same object with a new lease.");
            cancellation.Cancel();
            pause.Release();
            var outcome = await pending.WaitAsync(Bound);
            Ensure(outcome is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token,
                "Cancellation must retain the original token at contended entry.");
            Ensure(controller.CaptureSendStateForLeaseRaceTests(1, 0).Credit == 8 && controller.SendConnectionCredit == 24,
                "Canceled old acquire consumed replacement credit.");
            Console.WriteLine("case=cancel-after-reuse canceled=true replacementCredit=8 connectionCredit=24");
        }
        finally
        {
            pause.Release();
            controller.Complete(new OperationCanceledException("lease-race test cleanup"));
            await Observe(pending);
        }
    }

    [Test]
    public async Task ConnectionCompletionDuringGapMustPreserveOriginalException()
    {
        var controller = NewController();
        using var pause = new ProbePause();
        Task<Exception?>? pending = null;
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "distinct lease-race connection terminal");
        try
        {
            Reserve(controller, 1, 16);
            controller.AfterProbeBeforeContendedForTests = pause.Wait;
            pending = StartAcquire(controller, 1, CancellationToken.None);
            await pause.Entered.Task.WaitAsync(Bound);
            controller.Complete(terminal);
            pause.Release();
            var outcome = await pending.WaitAsync(Bound);
            Ensure(ReferenceEquals(outcome, terminal), "Connection close lost its exact terminal exception instance.");
            Ensure(controller.ActiveSendStreamCount == 0 && controller.RetainedSendStreamCount == 0,
                "Connection completion retained send states.");
            Console.WriteLine("case=connection-complete sameException=true active=0 retained=0");
        }
        finally
        {
            pause.Release();
            controller.Complete(terminal);
            await Observe(pending);
        }
    }

    [Test]
    public async Task StreamCompletionWithoutReuseMustRejectOriginalAcquire()
    {
        var controller = NewController();
        using var pause = new ProbePause();
        Task<Exception?>? pending = null;
        try
        {
            Reserve(controller, 1, 16);
            controller.AfterProbeBeforeContendedForTests = pause.Wait;
            pending = StartAcquire(controller, 1, CancellationToken.None);
            await pause.Entered.Task.WaitAsync(Bound);
            controller.ReturnUnsentCredit(1, 0, 16);
            controller.CompleteSendStream(1, 0);
            pause.Release();
            var outcome = await pending.WaitAsync(Bound);
            Ensure(outcome is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "A retired stream without a replacement accepted new credit.");
            Ensure(controller.SendConnectionCredit == 32 && controller.RetainedSendStreamCount == 0,
                "Retired-stream rejection changed fully returned credit or recreated a state.");
            Console.WriteLine("case=complete-without-reuse rejected=true connectionCredit=32 retained=0");
        }
        finally
        {
            pause.Release();
            controller.Complete(new OperationCanceledException("lease-race test cleanup"));
            await Observe(pending);
        }
    }

    [Test]
    public async Task StreamAbortDuringGapMustPreserveOriginalException()
    {
        var controller = NewController();
        using var pause = new ProbePause();
        Task<Exception?>? pending = null;
        var terminal = new InvalidOperationException("distinct lease-race remote stream error");
        try
        {
            Reserve(controller, 1, 16);
            controller.AfterProbeBeforeContendedForTests = pause.Wait;
            pending = StartAcquire(controller, 1, CancellationToken.None);
            await pause.Entered.Task.WaitAsync(Bound);
            controller.AbortSendStreams(1, terminal);
            pause.Release();
            var outcome = await pending.WaitAsync(Bound);
            Ensure(ReferenceEquals(outcome, terminal), "Stream abort lost its exact original exception instance.");
            Ensure(controller.SendConnectionCredit == 32,
                "Aborted stream reserve changed already-refunded connection credit.");
            Console.WriteLine("case=stream-abort sameException=true connectionCredit=32");
        }
        finally
        {
            pause.Release();
            controller.Complete(new OperationCanceledException("lease-race test cleanup"));
            await Observe(pending);
        }
    }

    [Test]
    public async Task MissingStateMayStillAcquireAfterTombstoneCapacityClears()
    {
        var controller = NewController(maxConcurrentStreams: 1);
        using var pause = new ProbePause();
        Task<Exception?>? pending = null;
        try
        {
            Reserve(controller, 1, 16);
            var original = controller.CaptureSendStateForLeaseRaceTests(1, 0);
            controller.CompleteSendStream(1, 0); // Full dictionary, only a debt-bearing tombstone.
            controller.AfterProbeBeforeContendedForTests = pause.Wait;
            pending = StartAcquire(controller, 2, CancellationToken.None);
            await pause.Entered.Task.WaitAsync(Bound);
            controller.ApplyWindowUpdate(1, 0, 16);
            pause.Release();
            var outcome = await pending.WaitAsync(Bound);
            Ensure(outcome is null, "A previously unresolved caller could not create state after capacity returned.");
            var current = controller.CaptureSendStateForLeaseRaceTests(2, 0);
            Ensure(ReferenceEquals(original.State, current.State) && current.Lease > original.Lease,
                "The no-state control did not actually rent the retired object.");
            Ensure(current.Credit == 8 && controller.SendConnectionCredit == 24,
                "The no-state control reserved the wrong amount of credit.");
            Console.WriteLine($"case=missing-state-capacity accepted=true sameObject=true oldLease={original.Lease} newLease={current.Lease} connectionCredit=24");
        }
        finally
        {
            pause.Release();
            controller.Complete(new OperationCanceledException("lease-race test cleanup"));
            await Observe(pending);
        }
    }

    private static StreamFlowController NewController(int maxConcurrentStreams = 2)
        => new(streamWindow: 16, connectionWindow: 32, maxFramePayloadBytes: 128,
            maxConcurrentStreams: maxConcurrentStreams);

    private static void Reserve(StreamFlowController controller, long requestId, int bytes)
        => Ensure(controller.TryAcquireSendCredit(requestId, 0, bytes), "Setup reservation must succeed synchronously.");

    private static Task<Exception?> StartAcquire(
        StreamFlowController controller, long requestId, CancellationToken cancellation)
        => Task.Factory.StartNew(async () =>
        {
            try
            {
                await controller.AcquireSendCreditAsync(requestId, 0, 8, cancellation);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private static async Task Observe(Task<Exception?>? pending)
    {
        if (pending is not null)
            _ = await pending.WaitAsync(Bound);
    }

    private static void Ensure(bool okay, string message)
    {
        if (!okay)
            throw new InvalidOperationException(message);
    }

    private sealed class ProbePause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _hits;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Hits => Volatile.Read(ref _hits);

        internal void Wait()
        {
            if (Interlocked.Increment(ref _hits) != 1)
                throw new InvalidOperationException("Probe pause was invoked more than once.");
            Entered.TrySetResult();
            if (!_release.Wait(Bound))
                throw new TimeoutException("Diagnostic probe pause was not released within its fixed five-second bound.");
        }

        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
