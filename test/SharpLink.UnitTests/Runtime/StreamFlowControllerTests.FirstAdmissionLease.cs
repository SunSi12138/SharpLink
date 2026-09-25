using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowControllerTests
{
    [Test]
    public async Task FirstSendAdmissionShouldKeepOriginalGenerationAfterDelayedContinuation()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "first-send-admission-generation",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 1,
            connectionReceiveWindowBytes: 1);
        var controller = GetPrivateField<RpcSessionProtocolState>(session, "_protocolState")
            .FlowController!;

        await controller.AcquireSendCreditAsync(801, 1, 1, CancellationToken.None);
        var pending = session.AcquireStreamSendCreditAsync(default, 802, 1, 1, CancellationToken.None);
        Ensure(!pending.IsCompleted, "first admission should wait for connection credit");
        var original = controller.ResolveSendCreditLease(802, 1);
        Ensure(original.IsResolved, "the blocked first admission should own a lifecycle");

        StreamFlowController.ResolvedSendCreditLease replacement;
        // Grant and retire the original admission while its continuation cannot resolve a
        // key under this gate. Reuse both the key and pooled object before releasing it.
        // No sleeps, retries or thread-pool configuration are needed for this interleaving.
        var gate = GetPrivateField<Lock>(controller, "_gate");
        lock (gate)
        {
            controller.ApplyWindowUpdate(801, 1, 1);
            controller.ApplyWindowUpdate(802, 1, 1);
            controller.CompleteSendStream(802, 1);
            Ensure(controller.TryAcquireSendCredit(802, 1, 1), "replacement should acquire credit");
            replacement = controller.ResolveSendCreditLease(802, 1);
            Ensure(ReferenceEquals(original.State, replacement.State),
                "the test must exercise reuse of the original pooled state");
            Ensure(original.Generation != replacement.Generation, "reuse must advance the generation");
        }

        var admitted = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(admitted.Generation == original.Generation,
            "first admission must return the generation that reserved credit, not a later key resolution");
        controller.ReturnUnsentCredit(in admitted, 1);
        Ensure(controller.SendConnectionCredit == 0,
            "late return from the original admission must not refund replacement credit");
        controller.ReturnUnsentCredit(in replacement, 1);
        Ensure(controller.SendConnectionCredit == 1, "replacement should return only its own debit");
    }

    [Test]
    public async Task FirstSynchronousAdmissionShouldCaptureDebitedState()
    {
        var controller = new StreamFlowController(1, 1, 1024, maxConcurrentStreams: 1);
        Ensure(controller.TryAcquireSendCreditLease(811, 1, 1, out var original),
            "first synchronous admission should reserve credit and resolve a lease");
        controller.ReturnUnsentCredit(in original, 1);
        controller.CompleteSendStream(811, 1);
        Ensure(controller.TryAcquireSendCreditLease(811, 1, 1, out var replacement),
            "the same key should start a new lifecycle");
        Ensure(ReferenceEquals(original.State, replacement.State), "the pooled object must be reused");
        Ensure(original.Generation != replacement.Generation, "the admission lease must be immutable");
        controller.ReturnUnsentCredit(in original, 1);
        Ensure(controller.SendConnectionCredit == 0, "an old first-use handle must not refund new credit");
        controller.ReturnUnsentCredit(in replacement, 1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FailedFirstProbeShouldNotPublishOrCaptureAnUnadmittedState()
    {
        var controller = new StreamFlowController(1, 1, 1024, maxConcurrentStreams: 2);
        var holder = await controller.AcquireSendCreditLeaseAsync(812, 1, 1, CancellationToken.None);
        Ensure(!controller.TryAcquireSendCreditLease(813, 1, 1, out var unresolved),
            "the first probe should fail while connection credit is exhausted");
        Ensure(!unresolved.IsResolved, "an unadmitted probe must leave its lease unresolved");
        Ensure(!controller.ResolveSendCreditLease(813, 1).IsResolved,
            "the rejected probe must not consume stream-state capacity");
        controller.ReturnUnsentCredit(in holder, 1);
        Ensure(controller.TryAcquireSendCreditLease(813, 1, 1, out var admitted) && admitted.IsResolved,
            "the probe should resolve only when credit can be reserved");
    }

    [Test]
    public async Task BlockedProbeShouldRetainAnExistingGeneration()
    {
        var controller = new StreamFlowController(1, 1, 1024);
        var first = await controller.AcquireSendCreditLeaseAsync(814, 1, 1, CancellationToken.None);
        Ensure(!controller.TryAcquireSendCreditLease(814, 1, 1, out var blocked),
            "the existing lifecycle has no remaining stream credit");
        Ensure(blocked.IsResolved && blocked.Generation == first.Generation,
            "a blocked probe on an existing state must preserve its generation for ordered admission");
        using var cancellation = new CancellationTokenSource();
        var pending = controller.AcquireSendCreditAsync(in blocked, 1, cancellation.Token);
        cancellation.Cancel();
        await ExpectCancellation(pending);
        controller.ReturnUnsentCredit(in first, 1);
    }

    [Test]
    public async Task FirstAdmissionShouldPreserveFifoAndCancellation()
    {
        var controller = new StreamFlowController(1, 1, 1024);
        var holder = await controller.AcquireSendCreditLeaseAsync(815, 1, 1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = controller.AcquireSendCreditLeaseAsync(816, 1, 1, cancellation.Token);
        var second = controller.AcquireSendCreditLeaseAsync(817, 1, 1, CancellationToken.None);
        var third = controller.AcquireSendCreditLeaseAsync(818, 1, 1, CancellationToken.None);
        cancellation.Cancel();
        await ExpectCancellation(new ValueTask(cancelled.AsTask()));
        controller.ReturnUnsentCredit(in holder, 1);
        var secondLease = await second.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(!third.IsCompleted, "a later first-admission waiter must not bypass exhausted credit");
        controller.ReturnUnsentCredit(in secondLease, 1);
        var thirdLease = await third.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        controller.ReturnUnsentCredit(in thirdLease, 1);
        Ensure(controller.SendConnectionCredit == 1, "all admitted debits must be accounted for once");
    }

    [Test]
    public async Task FirstAdmissionShouldNotLetBlockedStreamHeadStallEligibleStream()
    {
        var controller = new StreamFlowController(1, 2, 1024);
        var holder = await controller.AcquireSendCreditLeaseAsync(819, 1, 1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var head = controller.AcquireSendCreditLeaseAsync(819, 1, 1, cancellation.Token);
        var eligible = controller.AcquireSendCreditLeaseAsync(820, 1, 1, CancellationToken.None);
        Ensure(eligible.IsCompletedSuccessfully, "stream-credit-blocked head must preserve existing fairness");
        var eligibleLease = await eligible;
        cancellation.Cancel();
        await ExpectCancellation(new ValueTask(head.AsTask()));
        controller.ReturnUnsentCredit(in holder, 1);
        controller.ReturnUnsentCredit(in eligibleLease, 1);
    }

    [Test]
    public async Task FirstAdmissionWaitingForTombstoneCapacityShouldCaptureCreatedState()
    {
        var controller = new StreamFlowController(1, 1, 1024, maxConcurrentStreams: 1);
        var original = await controller.AcquireSendCreditLeaseAsync(821, 1, 1, CancellationToken.None);
        controller.CompleteSendStream(821, 1);
        var pending = controller.AcquireSendCreditLeaseAsync(822, 1, 1, CancellationToken.None);
        Ensure(!pending.IsCompleted, "first admission should wait for the completed tombstone's slot");
        Ensure(!controller.ResolveSendCreditLease(822, 1).IsResolved, "no state may exist before admission");
        controller.ApplyWindowUpdate(821, 1, 1);
        var admitted = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var current = controller.ResolveSendCreditLease(822, 1);
        Ensure(admitted.IsResolved && ReferenceEquals(admitted.State, current.State) &&
            admitted.Generation == current.Generation, "the created state must be bound at waiter admission");
        controller.ReturnUnsentCredit(in original, 1);
        Ensure(controller.SendConnectionCredit == 0, "released tombstone handle must not refund the new stream");
        controller.ReturnUnsentCredit(in admitted, 1);
    }

    [Test]
    public async Task FirstAdmissionShouldPreserveOversizedBorrowAndRepay()
    {
        var controller = new StreamFlowController(2, 4, 16);
        var borrowed = await controller.AcquireSendCreditLeaseAsync(823, 1, 6, CancellationToken.None);
        Ensure(controller.SendConnectionCredit == -2, "the oversized item should borrow the same connection credit");
        Ensure(!controller.TryAcquireSendCreditLease(823, 1, 1, out var blocked),
            "the borrowed stream cannot acquire more credit before repayment");
        Ensure(blocked.Generation == borrowed.Generation, "the borrow must not replace the stream lifecycle");
        controller.ReturnUnsentCredit(in borrowed, 6);
        Ensure(controller.SendConnectionCredit == 4, "oversized first admission must repay exactly");
    }

    [Test]
    public async Task TerminalShouldRejectFirstAdmissionWaiterWithOriginalException()
    {
        var controller = new StreamFlowController(1, 1, 1024);
        _ = await controller.AcquireSendCreditLeaseAsync(824, 1, 1, CancellationToken.None);
        var pending = controller.AcquireSendCreditLeaseAsync(825, 1, 1, CancellationToken.None);
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test terminal");
        controller.Complete(terminal);
        await ExpectSameException(new ValueTask(pending.AsTask()), terminal);
    }
}
