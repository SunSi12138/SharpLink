using System.Reflection;
using System.Diagnostics;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableTests
{
    private const int TableCapacity = 65536;

    [Test]
    public void ConstructorShouldRequirePowerOfTwoCapacity()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new PendingRequestTable(0));
        AssertThrows<ArgumentException>(() => _ = new PendingRequestTable(3));
    }

    [Test]
    public async Task OccupiedWrappedSlotShouldAdvanceIdToAnotherFreeSlot()
    {
        var manager = new PendingRequestTable(4);
        var longRequest = manager.Rent<int>(out var longRequestId);

        for (var index = 0; index < 3; index++)
        {
            var shortRequest = manager.Rent<int>(out var shortRequestId);
            var payload = ReadOnlySequence<byte>.Empty;
            Ensure(manager.Dispatch(shortRequestId, ref payload), "short request dispatch");
            _ = await shortRequest.AsValueTask();
        }

        var fourthRequest = manager.Rent<int>(out var fourthRequestId);
        Ensure((fourthRequestId & 3) != (longRequestId & 3), "collision should advance request ID");
        Ensure(manager.Count == 2, "collision probing should use another free slot");
        var emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(fourthRequestId, ref emptyPayload), "probed slot should dispatch");
        _ = await fourthRequest.AsValueTask();

        var healthyRequest = manager.Rent<int>(out var healthyRequestId);
        emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(healthyRequestId, ref emptyPayload), "free slot after collision should remain usable");
        _ = await healthyRequest.AsValueTask();

        emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(longRequestId, ref emptyPayload), "long request should remain registered");
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
        var emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(requestId1, ref emptyPayload), "request1 should dispatch");
        _ = await op1.AsValueTask();

        SetNextId(manager, requestId1 + TableCapacity - 1);
        var op2 = manager.Rent<int>(out var requestId2);
        Ensure(requestId2 - requestId1 == TableCapacity, "request ids should reuse same slot");

        emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(!manager.Dispatch(requestId1, ref emptyPayload), "stale response should be rejected");

        emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(requestId2, ref emptyPayload), "current pending should still dispatch");
        _ = await op2.AsValueTask();
    }

    [Test]
    public async Task DispatchErrorShouldNotDropCurrentPendingWhenStaleErrorArrives()
    {
        var manager = new PendingRequestTable();

        var op1 = manager.Rent<int>(out var requestId1);
        var emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(requestId1, ref emptyPayload), "request1 should dispatch");
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

        var payload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(afterWrapId, ref payload), "wrapped request should dispatch by full ID");
        _ = await afterWrap.AsValueTask();
        payload = ReadOnlySequence<byte>.Empty;
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

        var payload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(firstId, ref payload), "first request should dispatch");
        _ = await first.AsValueTask();

        var lease = await waiting;
        Ensure(manager.Count == 2, "released capacity should be handed to waiter");
        payload = ReadOnlySequence<byte>.Empty;
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
    public async Task CompletionRaceShouldHaveExactlyOneWinnerAndReleaseOneSlot()
    {
        var manager = new PendingRequestTable(1);
        var operation = manager.Rent<int>(out var requestId);
        var payload1 = ReadOnlySequence<byte>.Empty;
        var payload2 = ReadOnlySequence<byte>.Empty;

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
    public async Task CancellationResponseRaceShouldNotLeaveTombstonesOrCorruptPool()
    {
        using var manager = new PendingRequestTable(8);
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            var operation = manager.Rent(
                new Int32Codec(),
                PendingCallKind.Unary,
                deadlineTimestamp: 0,
                cancellation.Token,
                out var requestId);
            var payload = ReadOnlySequence<byte>.Empty;
            await Task.WhenAll(
                Task.Run(cancellation.Cancel),
                Task.Run(() => manager.Dispatch(requestId, ref payload)));
            try
            {
                _ = await operation.AsValueTask();
            }
            catch (OperationCanceledException)
            {
            }
        }

        Ensure(manager.Count == 0, "all racing calls should release their slots");
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
}
