using System.Reflection;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public partial class PendingRequestTableTests
{

    [Test]
    public async Task CapacityDeadlineShouldReadTheExplicitTimeProvider()
    {
        var utcNow = new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var timeProvider = new TrackingTimeProvider(utcNow);
        using var manager = CreateTable(1, timeProvider: timeProvider);
        var occupied = manager.Rent<int>(out _);
        var deadline = RpcDeadline.FromTimestamp(timeProvider.GetTimestamp() - 1);
        var timestampReadsBeforeWait = timeProvider.TimestampReadCount;

        var failure = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            deadline,
            CancellationToken.None).AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the injected monotonic source must make an already-expired capacity deadline fail immediately");
        Ensure(timeProvider.TimestampReadCount > timestampReadsBeforeWait,
            "the capacity wait must read the injected monotonic time source");
        Ensure(timeProvider.UtcReadCount == 0,
            "capacity deadline arbitration must not consult wall-clock UTC time");
        manager.FailAllPendingRequests(new IOException("test cleanup"));
        await EnsureThrows<IOException>(occupied.AsValueTask(), "test cleanup");
    }


    [Test]
    public async Task FakeTimeCapacityWaitShouldExpireAtItsMonotonicBoundaryWithoutLeakingAWaiter()
    {
        var timeProvider = new ManualTimeProvider();
        using var manager = CreateTable(1, timeProvider: timeProvider);
        var occupied = manager.Rent<int>(out _);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(2), timeProvider);
        var waiting = manager.RentAsync<int>(
            waitForSlot: true,
            deadline,
            CancellationToken.None).AsTask();

        timeProvider.Advance(TimeSpan.FromSeconds(2).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!waiting.IsCompleted,
            "capacity wait must remain pending one provider tick before its deadline");

        timeProvider.Advance(TimeSpan.FromTicks(1));
        var failure = await CaptureExceptionAsync(waiting);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "capacity wait must fail with DeadlineExceeded at the exact monotonic boundary");
        Ensure(manager.Count == 1,
            "a timed-out waiter must not occupy or release the existing pending slot");

        manager.FailAllPendingRequests(new IOException("fake-time cleanup"));
        await EnsureThrows<IOException>(occupied.AsValueTask(), "fake-time cleanup");
        Ensure(manager.Count == 0, "capacity timeout cleanup must leave zero pending calls");
    }


    [Test]
    public async Task FakeTimeDeadlineSchedulerShouldExpireEqualDeadlinesTogetherAndLaterDeadlineInOrder()
    {
        var timeProvider = new ManualTimeProvider();
        using var manager = CreateTable(8, timeProvider: timeProvider);
        var firstDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var laterDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(2), timeProvider);
        var first = manager.Rent(
            new Int32Codec(), PendingCallKind.Unary, firstDeadline,
            CancellationToken.None, out _).AsValueTask().AsTask();
        var tied = manager.Rent(
            new Int32Codec(), PendingCallKind.Unary, firstDeadline,
            CancellationToken.None, out _).AsValueTask().AsTask();
        var later = manager.Rent(
            new Int32Codec(), PendingCallKind.Unary, laterDeadline,
            CancellationToken.None, out _).AsValueTask().AsTask();

        timeProvider.Advance(TimeSpan.FromSeconds(1).Subtract(TimeSpan.FromTicks(1)));
        Ensure(!first.IsCompleted && !tied.IsCompleted && !later.IsCompleted,
            "no pending call may expire before the earliest monotonic timestamp");

        timeProvider.Advance(TimeSpan.FromTicks(1));
        Ensure(await CaptureExceptionAsync(first) is SharpLinkException
        { Code: SharpLinkErrorCode.DeadlineExceeded },
            "first equal deadline result");
        Ensure(await CaptureExceptionAsync(tied) is SharpLinkException
        { Code: SharpLinkErrorCode.DeadlineExceeded },
            "second equal deadline result");
        Ensure(!later.IsCompleted && manager.Count == 1,
            "later deadline must remain registered after equal earlier deadlines expire");

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Ensure(await CaptureExceptionAsync(later) is SharpLinkException
        { Code: SharpLinkErrorCode.DeadlineExceeded },
            "later deadline result");
        Ensure(manager.Count == 0,
            "ordered fake-time deadline scans must release every pending slot");
    }


    [Test]
    public async Task ResponseAtExpiredTimestampShouldLoseBeforeDeadlineTimerCallbackRuns()
    {
        var timeProvider = new ManualTimeProvider();
        using var manager = CreateTable(8, timeProvider: timeProvider);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var operation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadline,
            CancellationToken.None,
            out var requestId).AsValueTask().AsTask();

        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));
        var payload = SInt32Payload;
        Ensure(manager.Dispatch(requestId, ref payload),
            "matching response should claim the pending slot");

        var failure = await CaptureExceptionAsync(operation);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "response processing must consult the monotonic boundary even before the timer callback runs");
        Ensure(manager.Count == 0,
            "deadline-gated response must release the pending slot exactly once");
        timeProvider.Advance(TimeSpan.Zero);
    }



    [Test]
    public async Task StreamDataAfterExpiredTimestampShouldBeRejectedBeforeDeadlineTimerCallbackRuns()
    {
        var timeProvider = new ManualTimeProvider();
        using var manager = CreateTable(8, timeProvider: timeProvider);
        var requestId = manager.RegisterStream(
            PendingCallKind.ServerStreaming,
            new NoopStreamDispatcher(),
            RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider),
            CancellationToken.None);

        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

        Ensure(!manager.TryAcceptStreamData(requestId),
            "StreamData arriving at/after the monotonic boundary must not reach the dispatcher");
        Ensure(manager.Count == 0,
            "the stream-data deadline gate must atomically retire the pending stream");
        timeProvider.Advance(TimeSpan.Zero);
        await Task.CompletedTask;
    }


    [Test]
    public async Task FakeTimeCancellationAndDisposeShouldRemoveCallsAndTheOwnedTimerExactlyOnce()
    {
        var timeProvider = new ManualTimeProvider();
        var manager = CreateTable(2, timeProvider: timeProvider);
        using var cancellation = new CancellationTokenSource();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), timeProvider);
        var canceled = manager.Rent(
            new Int32Codec(), PendingCallKind.Unary, deadline,
            cancellation.Token, out _).AsValueTask().AsTask();
        var disposed = manager.Rent(
            new Int32Codec(), PendingCallKind.Unary, deadline,
            CancellationToken.None, out _).AsValueTask().AsTask();

        Ensure(timeProvider.ActiveTimerCount == 1,
            "one pending table must own exactly one provider timer");
        cancellation.Cancel();
        Ensure(await CaptureExceptionAsync(canceled) is OperationCanceledException,
            "caller cancellation must win before the fake deadline");

        manager.Dispose();
        Ensure(await CaptureExceptionAsync(disposed) is SharpLinkException
        { Code: SharpLinkErrorCode.ConnectionClosed },
            "table disposal must complete the remaining call as ConnectionClosed");
        Ensure(manager.Count == 0 && timeProvider.ActiveTimerCount == 0,
            "dispose must drain calls and dispose its single owned timer");

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        Ensure(manager.Count == 0 && timeProvider.ActiveTimerCount == 0,
            "advancing fake time after dispose must not resurrect timer work");
    }


    [Test]
    public async Task TerminalRaceShouldNotifyItsOwnerExactlyOnce()
    {
        var owner = new TrackingPendingCallOwner();
        using var manager = CreateTable(1, owner);
        var operation = manager.Rent<int>(out var requestId);
        var responsePayload = SInt32Payload;
        var winners = await Task.WhenAll(
            Task.Run(() => manager.Dispatch(requestId, ref responsePayload)),
            Task.Run(() => manager.TryComplete(requestId, PendingCallCompletionReason.UserCancellation)),
            Task.Run(() => manager.TryComplete(requestId, PendingCallCompletionReason.DeadlineExceeded)),
            Task.Run(() => manager.TryComplete(requestId, PendingCallCompletionReason.ConnectionClosed)));

        Ensure(winners.Count(static winner => winner) == 1, "one terminal path must win");
        Ensure(owner.RegisteredCount == 1, "the owner must observe one registration");
        Ensure(owner.CompletedCount == 1, "the owner must observe one terminal callback");
        Ensure(owner.ActiveCount == 0 && owner.MinimumActiveCount >= 0,
            "the owner count must balance without underflow");
        _ = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
    }
}
