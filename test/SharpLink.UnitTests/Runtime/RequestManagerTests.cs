using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class RequestManagerTests
{
    private const int RingBufferSize = 65536;

    [Test]
    public void ConstructorShouldRequirePowerOfTwoCapacity()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new RequestManager(0));
        AssertThrows<ArgumentException>(() => _ = new RequestManager(3));
    }

    [Test]
    public async Task OccupiedWrappedSlotShouldReturnResourceExhaustedWithoutPoisoningOtherSlots()
    {
        var manager = new RequestManager(4);
        var longRequest = manager.Rent<int>(out var longRequestId);

        for (var index = 0; index < 3; index++)
        {
            var shortRequest = manager.Rent<int>(out var shortRequestId);
            var payload = ReadOnlySequence<byte>.Empty;
            Ensure(manager.Dispatch(shortRequestId, ref payload), "short request dispatch");
            _ = await shortRequest.AsValueTask();
        }

        ExpectResourceExhausted(() => manager.Rent<int>(out _));

        var healthyRequest = manager.Rent<int>(out var healthyRequestId);
        var emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(healthyRequestId, ref emptyPayload), "free slot after collision should remain usable");
        _ = await healthyRequest.AsValueTask();

        emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(longRequestId, ref emptyPayload), "long request should remain registered");
        _ = await longRequest.AsValueTask();
    }

    [Test]
    public async Task DefaultCapacityShouldRejectRequest65537AsResourceExhausted()
    {
        var manager = new RequestManager();
        var operations = new RpcRequestOperation<int>[RingBufferSize];
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
        var manager = new RequestManager();

        var op1 = manager.Rent<int>(out var requestId1);
        var emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(requestId1, ref emptyPayload), "request1 should dispatch");
        _ = await op1.AsValueTask();

        SetNextId(manager, requestId1 + RingBufferSize - 1);
        var op2 = manager.Rent<int>(out var requestId2);
        Ensure(requestId2 - requestId1 == RingBufferSize, "request ids should reuse same slot");

        emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(!manager.Dispatch(requestId1, ref emptyPayload), "stale response should be rejected");

        emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(requestId2, ref emptyPayload), "current pending should still dispatch");
        _ = await op2.AsValueTask();
    }

    [Test]
    public async Task DispatchErrorShouldNotDropCurrentPendingWhenStaleErrorArrives()
    {
        var manager = new RequestManager();

        var op1 = manager.Rent<int>(out var requestId1);
        var emptyPayload = ReadOnlySequence<byte>.Empty;
        Ensure(manager.Dispatch(requestId1, ref emptyPayload), "request1 should dispatch");
        _ = await op1.AsValueTask();

        SetNextId(manager, requestId1 + RingBufferSize - 1);
        var op2 = manager.Rent<int>(out var requestId2);

        Ensure(!manager.DispatchError(requestId1, new InvalidOperationException("stale")), "stale error should be rejected");
        Ensure(manager.DispatchError(requestId2, new ApplicationException("boom")), "current error should dispatch");
        await EnsureThrows<ApplicationException>(op2.AsValueTask(), "boom");
    }

    [Test]
    public async Task FailAllPendingRequestsShouldFailEveryPendingOperation()
    {
        var manager = new RequestManager();
        var op1 = manager.Rent<int>(out _);
        var op2 = manager.Rent<int>(out _);
        var ex = new IOException("disconnected");

        manager.FailAllPendingRequests(ex);

        await EnsureThrows<IOException>(op1.AsValueTask(), "disconnected");
        await EnsureThrows<IOException>(op2.AsValueTask(), "disconnected");
    }

    private static void SetNextId(RequestManager manager, long nextId)
    {
        var field = typeof(RequestManager).GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
            throw new Exception("cannot find _nextId field");

        field.SetValue(manager, nextId);
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
