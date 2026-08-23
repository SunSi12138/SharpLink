using System.Reflection;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableTests
{
    private const int TableCapacity = 65536;
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);
    private static readonly ReadOnlySequence<byte> SInt32Payload = new(new byte[sizeof(int)]);

    [Test]
    public void ConstructorShouldRequirePowerOfTwoCapacity()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => _ = CreateTable(0));
        AssertThrows<ArgumentException>(() => _ = CreateTable(3));
    }

    [Test]
    public void ConstructorShouldRejectEveryMissingRuntimeDependency()
    {
        AssertThrows<ArgumentNullException>(() => _ = new PendingRequestTable(
            8,
            null!,
            PendingRequestTableTestFixture.Owner,
            TimeProvider.System));
        AssertThrows<ArgumentNullException>(() => _ = new PendingRequestTable(
            8,
            PendingRequestTableTestFixture.Codecs,
            null!,
            TimeProvider.System));
        AssertThrows<ArgumentNullException>(() => _ = new PendingRequestTable(
            8,
            PendingRequestTableTestFixture.Codecs,
            PendingRequestTableTestFixture.Owner,
            null!));
    }

    [Test]
    public void DisposeShouldNotDisposeCallerOwnedDependencies()
    {
        var codecs = new TrackingCodecProvider();
        var owner = new TrackingPendingCallOwner();
        var timeProvider = new TrackingTimeProvider();
        var manager = new PendingRequestTable(8, codecs, owner, timeProvider);

        manager.Dispose();
        manager.Dispose();

        Ensure(codecs.DisposeCount == 0, "the table must not dispose its caller-owned codec provider");
        Ensure(owner.DisposeCount == 0, "the table must not dispose its caller-owned pending owner");
        Ensure(timeProvider.DisposeCount == 0, "the table must not dispose its caller-owned time provider");
    }

    [Test]
    public async Task CapacityDeadlineShouldReadTheExplicitTimeProvider()
    {
        var utcNow = new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var timeProvider = new TrackingTimeProvider(utcNow);
        using var manager = CreateTable(1, timeProvider: timeProvider);
        var occupied = manager.Rent<int>(out _);
        var deadline = RpcDeadline.FromTimestamp(timeProvider.GetTimestamp() - 1);

        var failure = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            deadline,
            CancellationToken.None).AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the injected UTC source must make an already-expired capacity deadline fail immediately");
        Ensure(timeProvider.UtcReadCount == 1, "the capacity wait must read the injected time source once");
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

    [Test]
    public async Task PayloadBearingResponseShouldNotTreatMissingPayloadAsDefaultValue()
    {
        using var manager = CreateTable(8);
        var operation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadline: default,
            CancellationToken.None,
            out var requestId);
        var payload = ReadOnlySequence<byte>.Empty;

        Ensure(manager.Dispatch(requestId, ref payload), "missing response payload should reach its pending call");
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            $"missing Int32 response payload must be DataLoss, not {failure?.GetType().Name ?? "a default value"}");

        var payloadlessOperation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadline: default,
            CancellationToken.None,
            out requestId,
            hasResponsePayload: false);
        payload = new ReadOnlySequence<byte>(new byte[] { 1 });
        Ensure(manager.Dispatch(requestId, ref payload), "unexpected response payload should reach its pending call");
        failure = await CaptureExceptionAsync(payloadlessOperation.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "payload-less acknowledgements must reject unexpected response bytes");
    }

    [Test]
    public async Task RequiredScalarResponseMustRejectDecodedNull()
    {
        using var manager = CreateTable(8);
        var operation = manager.Rent(
            new NullStringCodec(),
            PendingCallKind.Unary,
            deadline: default,
            CancellationToken.None,
            out var requestId);
        var payload = new ReadOnlySequence<byte>(new byte[] { 1 });

        Ensure(manager.Dispatch(requestId, ref payload), "null response should reach its pending call");
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "a required scalar response decoded as null must be DataLoss");

        var nullableOperation = manager.Rent(
            new NullStringCodec(),
            PendingCallKind.Unary,
            deadline: default,
            CancellationToken.None,
            out requestId,
            responseNullable: true);
        payload = new ReadOnlySequence<byte>(new byte[] { 1 });
        Ensure(manager.Dispatch(requestId, ref payload), "nullable response dispatch");
        Ensure(await nullableOperation.AsValueTask() is null,
            "an explicitly nullable scalar response must preserve null");
    }

    [Test]
    public async Task OccupiedWrappedSlotShouldAdvanceIdToAnotherFreeSlot()
    {
        var manager = CreateTable(4);
        var longRequest = manager.Rent<int>(out var longRequestId);

        for (var index = 0; index < 3; index++)
        {
            var shortRequest = manager.Rent<int>(out var shortRequestId);
            var shortPayload = SInt32Payload;
            Ensure(manager.Dispatch(shortRequestId, ref shortPayload), "short request dispatch");
            _ = await shortRequest.AsValueTask();
        }

        var fourthRequest = manager.Rent<int>(out var fourthRequestId);
        Ensure((fourthRequestId & 3) != (longRequestId & 3), "collision should advance request ID");
        Ensure(manager.Count == 2, "collision probing should use another free slot");
        var payload = SInt32Payload;
        Ensure(manager.Dispatch(fourthRequestId, ref payload), "probed slot should dispatch");
        _ = await fourthRequest.AsValueTask();

        var healthyRequest = manager.Rent<int>(out var healthyRequestId);
        payload = SInt32Payload;
        Ensure(manager.Dispatch(healthyRequestId, ref payload), "free slot after collision should remain usable");
        _ = await healthyRequest.AsValueTask();

        payload = SInt32Payload;
        Ensure(manager.Dispatch(longRequestId, ref payload), "long request should remain registered");
        _ = await longRequest.AsValueTask();
    }

    [Test]
    public async Task DefaultCapacityShouldRejectRequest65537AsResourceExhausted()
    {
        var manager = CreateTable();
        var operations = new RpcRequestOperation<int>[TableCapacity];
        for (var index = 0; index < operations.Length; index++)
            operations[index] = manager.Rent<int>(out _);

        ExpectResourceExhausted(() => manager.Rent<int>(out _));

        var failure = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test cleanup");
        manager.FailAllPendingRequests(failure);
        foreach (var operation in operations)
            await EnsureThrows<SharpLinkException>(operation.AsValueTask(), "test cleanup");
    }

    [Test]
    public async Task DispatchShouldNotDropCurrentPendingWhenStaleResponseArrives()
    {
        var manager = CreateTable();

        var op1 = manager.Rent<int>(out var requestId1);
        var payload = SInt32Payload;
        Ensure(manager.Dispatch(requestId1, ref payload), "request1 should dispatch");
        _ = await op1.AsValueTask();

        SetNextId(manager, requestId1 + TableCapacity - 1);
        var op2 = manager.Rent<int>(out var requestId2);
        Ensure(requestId2 - requestId1 == TableCapacity, "request ids should reuse same slot");

        payload = SInt32Payload;
        Ensure(!manager.Dispatch(requestId1, ref payload), "stale response should be rejected");

        payload = SInt32Payload;
        Ensure(manager.Dispatch(requestId2, ref payload), "current pending should still dispatch");
        _ = await op2.AsValueTask();
    }

    [Test]
    public async Task DispatchErrorShouldNotDropCurrentPendingWhenStaleErrorArrives()
    {
        var manager = CreateTable();

        var op1 = manager.Rent<int>(out var requestId1);
        var payload = SInt32Payload;
        Ensure(manager.Dispatch(requestId1, ref payload), "request1 should dispatch");
        _ = await op1.AsValueTask();

        SetNextId(manager, requestId1 + TableCapacity - 1);
        var op2 = manager.Rent<int>(out var requestId2);

        Ensure(!manager.DispatchError(requestId1, new InvalidOperationException("stale")), "stale error should be rejected");
        Ensure(manager.DispatchError(requestId2, new ApplicationException("boom")), "current error should dispatch");
        await EnsureThrows<ApplicationException>(op2.AsValueTask(), "boom");
    }

    [Test]
    public async Task RequestIdWrapShouldSkipZeroAndKeepFullIdentity()
    {
        var manager = CreateTable(4);
        SetNextId(manager, long.MaxValue - 1);

        var beforeWrap = manager.Rent<int>(out var beforeWrapId);
        var afterWrap = manager.Rent<int>(out var afterWrapId);
        Ensure(beforeWrapId == long.MaxValue, "last positive request ID");
        Ensure(afterWrapId == long.MinValue, "request ID should preserve all 64 bits across wrap");
        Ensure(afterWrapId != 0, "request ID zero is reserved");

        var payload = SInt32Payload;
        Ensure(manager.Dispatch(afterWrapId, ref payload), "wrapped request should dispatch by full ID");
        _ = await afterWrap.AsValueTask();
        payload = SInt32Payload;
        Ensure(manager.Dispatch(beforeWrapId, ref payload), "pre-wrap request should remain independent");
        _ = await beforeWrap.AsValueTask();
    }

    [Test]
    public async Task FailAllPendingRequestsShouldFailEveryPendingOperation()
    {
        var manager = CreateTable();
        var op1 = manager.Rent<int>(out _);
        var op2 = manager.Rent<int>(out _);
        var ex = new IOException("disconnected");

        manager.FailAllPendingRequests(ex);

        await EnsureThrows<IOException>(op1.AsValueTask(), "disconnected");
        await EnsureThrows<IOException>(op2.AsValueTask(), "disconnected");
    }

    [Test]
    public async Task FullTableWaitShouldResumeWhenAnySlotCompletes()
    {
        var manager = CreateTable(2);
        var first = manager.Rent<int>(out var firstId);
        var second = manager.Rent<int>(out _);

        var waiting = manager.RentAsync<int>(
            waitForSlot: true,
            RpcDeadline.Create(TimeSpan.FromSeconds(5), TimeProvider.System),
            System.Threading.CancellationToken.None).AsTask();
        Ensure(!waiting.IsCompleted, "full table waiter should suspend");

        var payload = SInt32Payload;
        Ensure(manager.Dispatch(firstId, ref payload), "first request should dispatch");
        _ = await first.AsValueTask();

        var lease = await waiting;
        Ensure(manager.Count == 2, "released capacity should be handed to waiter");
        payload = SInt32Payload;
        Ensure(manager.Dispatch(lease.Id, ref payload), "waited request should dispatch");
        _ = await lease.Operation.AsValueTask();
        manager.FailAllPendingRequests(new IOException("cleanup"));
        await EnsureThrows<IOException>(second.AsValueTask(), "cleanup");
    }

    [Test]
    public async Task FullTableWaitShouldHonorDeadlineAndCancellation()
    {
        var manager = CreateTable(1);
        var operation = manager.Rent<int>(out _);

        var timeout = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            RpcDeadline.Create(TimeSpan.FromMilliseconds(20), TimeProvider.System),
            System.Threading.CancellationToken.None).AsTask());
        Ensure(timeout is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "deadline error");

        using var cancellation = new System.Threading.CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            deadline: default,
            cancellation.Token).AsTask());
        Ensure(canceled is OperationCanceledException, "cancellation error");

        manager.FailAllPendingRequests(new IOException("cleanup"));
        await EnsureThrows<IOException>(operation.AsValueTask(), "cleanup");
    }

    [Test]
    public async Task FullTableFarFutureDeadlineShouldRemainCancellable()
    {
        using var manager = CreateTable(1);
        var occupied = manager.Rent<int>(out _);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var failure = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            RpcDeadline.FromTimestamp(long.MaxValue),
            cancellation.Token).AsTask());

        Ensure(failure is OperationCanceledException,
            $"far-future slot wait should remain cancellable, not fail as {failure?.GetType().Name}");
        manager.FailAllPendingRequests(new IOException("cleanup"));
        await EnsureThrows<IOException>(occupied.AsValueTask(), "cleanup");
    }

    [Test]
    public async Task CompletionRaceShouldHaveExactlyOneWinnerAndReleaseOneSlot()
    {
        var manager = CreateTable(1);
        var operation = manager.Rent<int>(out var requestId);
        var payload1 = SInt32Payload;

        var response = Task.Run(() => manager.Dispatch(requestId, ref payload1));
        var cancel = Task.Run(() => manager.DispatchError(requestId, new OperationCanceledException()));
        var results = await Task.WhenAll(response, cancel);

        var winnerCount = 0;
        for (var index = 0; index < results.Length; index++)
            if (results[index])
                winnerCount++;
        Ensure(winnerCount == 1, "one completion winner");
        Ensure(manager.Count == 0, "slot released exactly once");
        try
        {
            _ = await operation.AsValueTask();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Test]
    public async Task StreamingResponseObservationShouldPrecedeTerminalCompletion()
    {
        using var manager = CreateTable(8);
        using var observer = new BlockingStreamingCompletionObserver();
        var requestId = manager.RegisterStream(
            PendingCallKind.ServerStreaming,
            new NoopStreamDispatcher(),
            deadline: default,
            CancellationToken.None,
            observer);

        var response = LongRunningTestWorker.Run(() =>
        {
            var payload = ReadOnlySequence<byte>.Empty;
            return manager.Dispatch(requestId, ref payload);
        });
        Task<bool>? terminal = null;
        try
        {
            await observer.ResponseObservationEntered.WaitAsync(TimeSpan.FromSeconds(2));

            terminal = LongRunningTestWorker.Run(() => manager.TryComplete(
                requestId,
                PendingCallCompletionReason.ConnectionClosed,
                new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test disconnect")));
            await Task.Delay(50);
            Ensure(!observer.TerminalCompletionObserved.IsCompleted,
                "terminal completion must wait until the matched streaming response is observed");

            observer.ReleaseResponseObservation();
            Ensure(await response, "streaming response acknowledgement");
            Ensure(await terminal, "terminal completion");
            await observer.TerminalCompletionObserved.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(observer.TerminalSawResponseObservation,
                "terminal observer must see the prior response acknowledgement");
        }
        finally
        {
            observer.ReleaseResponseObservation();
            await LongRunningTestWorker.JoinAsync(response, RaceCoordinationTimeout);
            if (terminal is not null)
                await LongRunningTestWorker.JoinAsync(terminal, RaceCoordinationTimeout);
        }
    }

    [Test]
    public async Task ThrowingProducerCancellationCallbackShouldNotStrandCompletion()
    {
        var owner = new RecordingPendingCallOwner();
        using var manager = CreateTable(8, owner: owner);
        var lease = manager.RegisterOneWayClientStream(
            deadline: default,
            CancellationToken.None);
        using var callback = manager.GetProducerCancellationToken(lease.Id).Register(
            static () => throw new InvalidOperationException("producer cancellation callback failed"));
        var terminal = new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "producer connection closed");

        Exception? completionFailure = null;
        var completed = false;
        try
        {
            completed = manager.TryComplete(
                lease.Id,
                PendingCallCompletionReason.ConnectionClosed,
                terminal);
        }
        catch (Exception exception)
        {
            completionFailure = exception;
        }

        await Assert.That(completionFailure).IsNull();
        await Assert.That(completed).IsTrue();
        await Assert.That(manager.Count).IsEqualTo(0);
        await Assert.That(owner.ProducerCancellationFailure).IsTypeOf<AggregateException>();
        var observed = await CaptureExceptionAsync(lease.Operation.AsValueTask().AsTask());
        await Assert.That(observed).IsSameReferenceAs(terminal);
    }

    [Test]
    public async Task DisposedTableShouldRejectEveryStreamRegistration()
    {
        var manager = CreateTable(8);
        manager.Dispose();

        long registeredStreamId = 0;
        var streamFailure = CaptureException(() =>
        {
            registeredStreamId = manager.RegisterStream(
                PendingCallKind.ServerStreaming,
                new NoopStreamDispatcher(),
                deadline: default,
                CancellationToken.None);
        });
        PendingRequestLease<RpcEmptyRequest> registeredOneWay = default;
        var oneWayFailure = CaptureException(() =>
        {
            registeredOneWay = manager.RegisterOneWayClientStream(
                deadline: default,
                CancellationToken.None);
        });

        if (registeredStreamId != 0)
            manager.TryComplete(registeredStreamId, PendingCallCompletionReason.ConnectionClosed);
        if (registeredOneWay.Id != 0)
        {
            manager.TryComplete(registeredOneWay.Id, PendingCallCompletionReason.ConnectionClosed);
            _ = await CaptureExceptionAsync(registeredOneWay.Operation.AsValueTask().AsTask());
        }

        await Assert.That(streamFailure).IsTypeOf<ObjectDisposedException>();
        await Assert.That(oneWayFailure).IsTypeOf<ObjectDisposedException>();
        await Assert.That(manager.Count).IsEqualTo(0);

        for (var iteration = 0; iteration < 512; iteration++)
        {
            var racingTable = CreateTable(1);
            using var start = new ManualResetEventSlim();
            RpcRequestOperation<int>? operation = null;
            var rent = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    operation = racingTable.Rent<int>(out _);
                }
                catch (ObjectDisposedException)
                {
                }
            });
            var dispose = Task.Run(() =>
            {
                start.Wait();
                racingTable.Dispose();
            });
            start.Set();
            await Task.WhenAll(rent, dispose);

            Ensure(racingTable.Count == 0,
                $"Dispose/Rent race stranded a pending slot at iteration {iteration}");
            if (operation is not null)
            {
                var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                    "a call registered during disposal must observe connection closure");
            }
        }
    }

    [Test]
    public async Task ConnectionClosedCompletionWithoutAnExplicitExceptionShouldKeepItsWireCode()
    {
        using var manager = CreateTable(1);
        var operation = manager.Rent<int>(out var requestId);

        Ensure(manager.TryComplete(requestId, PendingCallCompletionReason.ConnectionClosed),
            "the pending call should accept connection closure");

        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "implicit connection closure must not be rewritten as Internal");
    }

    [Test]
    public async Task MonotonicDeadlineScanShouldCompleteWithoutCompletionPathRemoval()
    {
        using var manager = CreateTable(8);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
        var operation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            RpcDeadline.FromTimestamp(deadline),
            CancellationToken.None,
            out _);

        var exception = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "monotonic deadline should produce DeadlineExceeded");
        Ensure(manager.Count == 0, "deadline scan should release the slot");
    }

    [Test]
    public async Task LongMonotonicDeadlineShouldNotExceedTheNativeTimerRange()
    {
        using var manager = CreateTable(8);
        var deadline = Stopwatch.GetTimestamp() +
            (long)(TimeSpan.FromDays(60).TotalSeconds * Stopwatch.Frequency);
        RpcRequestOperation<int>? operation = null;
        Exception? registrationFailure = null;
        long requestId = 0;
        try
        {
            operation = manager.Rent(
                new Int32Codec(),
                PendingCallKind.Unary,
                RpcDeadline.FromTimestamp(deadline),
                CancellationToken.None,
                out requestId);
        }
        catch (Exception exception)
        {
            registrationFailure = exception;
        }

        await Assert.That(registrationFailure).IsNull();
        await Assert.That(operation).IsNotNull();
        await Assert.That(manager.TryComplete(
            requestId,
            PendingCallCompletionReason.ConnectionClosed,
            new IOException("test cleanup"))).IsTrue();
        await EnsureThrows<IOException>(operation!.AsValueTask(), "test cleanup");
    }

    [Test]
    public async Task CancellationResponseDeadlineRaceShouldHaveOneWinnerAndNotCorruptPool()
    {
        using var manager = CreateTable(8);
        for (var iteration = 0; iteration < 100_000; iteration++)
        {
            var operation = manager.Rent(
                new Int32Codec(),
                PendingCallKind.Unary,
                deadline: default,
                CancellationToken.None,
                out var requestId);
            var payload = SInt32Payload;
            var winners = new bool[3];
            Parallel.Invoke(
                () => winners[0] = manager.Dispatch(requestId, ref payload),
                () => winners[1] = manager.TryComplete(
                    requestId,
                    PendingCallCompletionReason.UserCancellation),
                () => winners[2] = manager.TryComplete(
                    requestId,
                    PendingCallCompletionReason.DeadlineExceeded));

            var winnerCount = (winners[0] ? 1 : 0) + (winners[1] ? 1 : 0) + (winners[2] ? 1 : 0);
            Ensure(winnerCount == 1,
                "response, cancel, and deadline must have exactly one terminal winner");
            var exception = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
            Ensure(exception is null or OperationCanceledException or
                    SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the operation must expose the winning completion reason");
        }

        Ensure(manager.Count == 0, "all racing calls should release their slots");
    }

    [Test]
    public async Task CancellationShouldNotCompleteOwnerBeforeRegistrationIsPublished()
    {
        using var owner = new BlockingPendingCallOwner();
        using var manager = CreateTable(8, owner: owner);
        using var cancellation = new CancellationTokenSource();
        var rentTask = LongRunningTestWorker.Run(() => manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadline: default,
            cancellation.Token,
            out _));
        Task cancelTask = Task.CompletedTask;
        try
        {
            Ensure(owner.RegistrationEntered.Wait(RaceCoordinationTimeout),
                "registration callback should reach the deterministic race gate");
            cancelTask = LongRunningTestWorker.Run(cancellation.Cancel);
            await Task.Delay(20);
            owner.AllowRegistration.Set();

            var operation = await rentTask;
            await cancelTask;
            var exception = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
            Ensure(exception is OperationCanceledException, "the racing call should still observe cancellation");
            Ensure(owner.MinimumActiveCount >= 0, "completion must not precede owner registration");
            Ensure(owner.ActiveCount == 0, "registration and completion must balance exactly once");
        }
        finally
        {
            owner.AllowRegistration.Set();
            await LongRunningTestWorker.JoinAsync(rentTask, RaceCoordinationTimeout);
            await LongRunningTestWorker.JoinAsync(cancelTask, RaceCoordinationTimeout);
        }
    }

    private static PendingRequestTable CreateTable(
        int capacity = TableCapacity,
        IPendingCallOwner? owner = null,
        IRpcCodecProvider? codecs = null,
        TimeProvider? timeProvider = null)
        => PendingRequestTableTestFixture.Create(capacity, owner, codecs, timeProvider);

    private static void SetNextId(PendingRequestTable manager, long nextId)
    {
        var field = typeof(PendingRequestTable).GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
            throw new Exception("cannot find _nextId field");

        field.SetValue(manager, nextId);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task EnsureThrows<TException>(ValueTask<int> task, string message)
        where TException : Exception
    {
        try
        {
            _ = await task;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException ex)
        {
            Ensure(ex.Message.Contains(message, StringComparison.Ordinal), "exception message");
        }
    }

    private static void ExpectResourceExhausted(Action action)
    {
        try
        {
            action();
            throw new Exception("expected ResourceExhausted");
        }
        catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
        {
        }
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BlockingPendingCallOwner : IPendingCallOwner, IDisposable
    {
        private int _activeCount;
        private int _minimumActiveCount;

        internal ManualResetEventSlim RegistrationEntered { get; } = new(initialState: false);
        internal ManualResetEventSlim AllowRegistration { get; } = new(initialState: false);
        internal int ActiveCount => Volatile.Read(ref _activeCount);
        internal int MinimumActiveCount => Volatile.Read(ref _minimumActiveCount);

        public void OnPendingCallRegistered()
        {
            RegistrationEntered.Set();
            AllowRegistration.Wait(RaceCoordinationTimeout);
            Interlocked.Increment(ref _activeCount);
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            var active = Interlocked.Decrement(ref _activeCount);
            var minimum = Volatile.Read(ref _minimumActiveCount);
            while (active < minimum)
            {
                var observed = Interlocked.CompareExchange(ref _minimumActiveCount, active, minimum);
                if (observed == minimum)
                    break;
                minimum = observed;
            }
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }

        public void Dispose()
        {
            AllowRegistration.Set();
            RegistrationEntered.Dispose();
            AllowRegistration.Dispose();
        }
    }

    private sealed class RecordingPendingCallOwner : IPendingCallOwner
    {
        internal Exception? ProducerCancellationFailure { get; private set; }

        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
            => ProducerCancellationFailure = exception;
    }

    private sealed class TrackingCodecProvider : IRpcCodecProvider, IDisposable
    {
        internal int DisposeCount { get; private set; }

        public IRpcCodec<T> GetCodec<T>() => PendingRequestTableTestFixture.Codecs.GetCodec<T>();

        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingPendingCallOwner : IPendingCallOwner, IDisposable
    {
        private int _activeCount;
        private int _minimumActiveCount;
        private int _registeredCount;
        private int _completedCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);
        internal int MinimumActiveCount => Volatile.Read(ref _minimumActiveCount);
        internal int RegisteredCount => Volatile.Read(ref _registeredCount);
        internal int CompletedCount => Volatile.Read(ref _completedCount);
        internal int DisposeCount { get; private set; }

        public void OnPendingCallRegistered()
        {
            Interlocked.Increment(ref _registeredCount);
            Interlocked.Increment(ref _activeCount);
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            Interlocked.Increment(ref _completedCount);
            var active = Interlocked.Decrement(ref _activeCount);
            while (true)
            {
                var minimum = Volatile.Read(ref _minimumActiveCount);
                if (active >= minimum ||
                    Interlocked.CompareExchange(ref _minimumActiveCount, active, minimum) == minimum)
                {
                    break;
                }
            }
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingTimeProvider : TimeProvider, IDisposable
    {
        private readonly DateTimeOffset? _utcNow;

        internal TrackingTimeProvider(DateTimeOffset? utcNow = null)
        {
            _utcNow = utcNow;
        }

        internal int DisposeCount { get; private set; }
        internal int UtcReadCount { get; private set; }

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override DateTimeOffset GetUtcNow()
        {
            UtcReadCount++;
            return _utcNow ?? TimeProvider.System.GetUtcNow();
        }

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public void Dispose() => DisposeCount++;
    }

    private sealed class NoopStreamDispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;

        public void Complete(bool isError, string? errorMessage)
        {
        }

        public void Complete(Exception? exception)
        {
        }
    }

    private sealed class NullStringCodec : IRpcCodec<string>
    {
        public void Serialize(in string value, IBufferWriter<byte> buffer)
            => throw new NotSupportedException();

        public string Deserialize(in ReadOnlySequence<byte> buffer) => null!;
    }

    private sealed class BlockingStreamingCompletionObserver : IPendingCallCompletionObserver, IDisposable
    {
        private readonly TaskCompletionSource _responseObservationEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _responseObservationRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminalCompletionObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _responseObserved;
        private int _terminalSawResponse;

        public Task ResponseObservationEntered => _responseObservationEntered.Task;
        public Task TerminalCompletionObserved => _terminalCompletionObserved.Task;
        public bool TerminalSawResponseObservation => Volatile.Read(ref _terminalSawResponse) != 0;

        public void OnResponseObserved()
        {
            Volatile.Write(ref _responseObserved, 1);
            _responseObservationEntered.TrySetResult();
            _responseObservationRelease.Task.GetAwaiter().GetResult();
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            Volatile.Write(ref _terminalSawResponse, Volatile.Read(ref _responseObserved));
            _terminalCompletionObserved.TrySetResult();
        }

        public void ReleaseResponseObservation() => _responseObservationRelease.TrySetResult();

        public void Dispose() => _responseObservationRelease.TrySetResult();
    }
}
