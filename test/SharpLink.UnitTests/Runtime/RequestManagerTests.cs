using System.Reflection;
using System.Diagnostics;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableTests
{
    private const int TableCapacity = 65536;
    private static readonly ReadOnlySequence<byte> SInt32Payload = new(new byte[sizeof(int)]);

    [Test]
    public void ConstructorShouldRequirePowerOfTwoCapacity()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new PendingRequestTable(0));
        AssertThrows<ArgumentException>(() => _ = new PendingRequestTable(3));
    }

    [Test]
    public async Task PayloadBearingResponseShouldNotTreatMissingPayloadAsDefaultValue()
    {
        using var manager = new PendingRequestTable(8);
        var operation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadlineTimestamp: 0,
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
            deadlineTimestamp: 0,
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
        using var manager = new PendingRequestTable(8);
        var operation = manager.Rent(
            new NullStringCodec(),
            PendingCallKind.Unary,
            deadlineTimestamp: 0,
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
            deadlineTimestamp: 0,
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
        var manager = new PendingRequestTable(4);
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
        var manager = new PendingRequestTable();
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
        var manager = new PendingRequestTable();

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
        var manager = new PendingRequestTable();

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
        var manager = new PendingRequestTable(4);
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
        var manager = new PendingRequestTable();
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
        var manager = new PendingRequestTable(2);
        var first = manager.Rent<int>(out var firstId);
        var second = manager.Rent<int>(out _);

        var waiting = manager.RentAsync<int>(
            waitForSlot: true,
            DateTimeOffset.UtcNow.AddSeconds(5),
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
        var manager = new PendingRequestTable(1);
        var operation = manager.Rent<int>(out _);

        var timeout = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            DateTimeOffset.UtcNow.AddMilliseconds(20),
            System.Threading.CancellationToken.None).AsTask());
        Ensure(timeout is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "deadline error");

        using var cancellation = new System.Threading.CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            deadline: null,
            cancellation.Token).AsTask());
        Ensure(canceled is OperationCanceledException, "cancellation error");

        manager.FailAllPendingRequests(new IOException("cleanup"));
        await EnsureThrows<IOException>(operation.AsValueTask(), "cleanup");
    }

    [Test]
    public async Task FullTableFarFutureDeadlineShouldRemainCancellable()
    {
        using var manager = new PendingRequestTable(1);
        var occupied = manager.Rent<int>(out _);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var failure = await CaptureExceptionAsync(manager.RentAsync<int>(
            waitForSlot: true,
            DateTimeOffset.MaxValue,
            cancellation.Token).AsTask());

        Ensure(failure is OperationCanceledException,
            $"far-future slot wait should remain cancellable, not fail as {failure?.GetType().Name}");
        manager.FailAllPendingRequests(new IOException("cleanup"));
        await EnsureThrows<IOException>(occupied.AsValueTask(), "cleanup");
    }

    [Test]
    public async Task CompletionRaceShouldHaveExactlyOneWinnerAndReleaseOneSlot()
    {
        var manager = new PendingRequestTable(1);
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
        using var manager = new PendingRequestTable(8);
        using var observer = new BlockingStreamingCompletionObserver();
        var requestId = manager.RegisterStream(
            PendingCallKind.ServerStreaming,
            new NoopStreamDispatcher(),
            deadlineTimestamp: 0,
            CancellationToken.None,
            observer);

        var response = Task.Run(() =>
        {
            var payload = ReadOnlySequence<byte>.Empty;
            return manager.Dispatch(requestId, ref payload);
        });
        await observer.ResponseObservationEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var terminal = Task.Run(() => manager.TryComplete(
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

    [Test]
    public async Task ThrowingProducerCancellationCallbackShouldNotStrandCompletion()
    {
        var owner = new RecordingPendingCallOwner();
        using var manager = new PendingRequestTable(8, owner: owner);
        var lease = manager.RegisterOneWayClientStream(
            deadlineTimestamp: 0,
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
        var manager = new PendingRequestTable(8);
        manager.Dispose();

        long registeredStreamId = 0;
        var streamFailure = CaptureException(() =>
        {
            registeredStreamId = manager.RegisterStream(
                PendingCallKind.ServerStreaming,
                new NoopStreamDispatcher(),
                deadlineTimestamp: 0,
                CancellationToken.None);
        });
        PendingRequestLease<RpcEmptyRequest> registeredOneWay = default;
        var oneWayFailure = CaptureException(() =>
        {
            registeredOneWay = manager.RegisterOneWayClientStream(
                deadlineTimestamp: 0,
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
            var racingTable = new PendingRequestTable(1);
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
    public async Task MonotonicDeadlineScanShouldCompleteWithoutCompletionPathRemoval()
    {
        using var manager = new PendingRequestTable(8);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
        var operation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadline,
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
        using var manager = new PendingRequestTable(8);
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
                deadline,
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
        using var manager = new PendingRequestTable(8);
        for (var iteration = 0; iteration < 100_000; iteration++)
        {
            var operation = manager.Rent(
                new Int32Codec(),
                PendingCallKind.Unary,
                deadlineTimestamp: 0,
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
        using var manager = new PendingRequestTable(8, owner: owner);
        using var cancellation = new CancellationTokenSource();
        var rentTask = Task.Run(() => manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            deadlineTimestamp: 0,
            cancellation.Token,
            out _));

        Ensure(owner.RegistrationEntered.Wait(TimeSpan.FromSeconds(2)),
            "registration callback should reach the deterministic race gate");
        var cancelTask = Task.Run(cancellation.Cancel);
        await Task.Delay(20);
        owner.AllowRegistration.Set();

        var operation = await rentTask;
        await cancelTask;
        var exception = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(exception is OperationCanceledException, "the racing call should still observe cancellation");
        Ensure(owner.MinimumActiveCount >= 0, "completion must not precede owner registration");
        Ensure(owner.ActiveCount == 0, "registration and completion must balance exactly once");
    }

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
            AllowRegistration.Wait(TimeSpan.FromSeconds(2));
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
