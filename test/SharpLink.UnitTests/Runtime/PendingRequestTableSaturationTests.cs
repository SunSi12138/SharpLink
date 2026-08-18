using System.Collections.Generic;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableSaturationTests
{
    [Test]
    [Arguments(1)]
    [Arguments(8)]
    [Arguments(64)]
    [Arguments(1024)]
    [Arguments(65_536)]
    public async Task FullUnaryTableShouldFailWithoutProbingRequestIds(int capacity)
    {
        using var manager = PendingRequestTableTestFixture.Create(capacity);
        var operations = new RpcRequestOperation<int>[capacity];
        long lastRequestId = 0;
        for (var index = 0; index < operations.Length; index++)
            operations[index] = manager.Rent<int>(out lastRequestId);

        var failure = CaptureException(() => manager.Rent<int>(out _));

        await Assert.That(failure).IsTypeOf<SharpLinkException>();
        await Assert.That(((SharpLinkException)failure!).Code).IsEqualTo(SharpLinkErrorCode.ResourceExhausted);
        await Assert.That(manager.AllocateRequestId()).IsEqualTo(lastRequestId + 1);

        manager.FailAllPendingRequests(new IOException("saturation cleanup"));
        await ConsumeFailuresAsync(operations);
    }

    [Test]
    public async Task FullWaiterShouldNotProbeBeforeObservingCancellation()
    {
        using var manager = PendingRequestTableTestFixture.Create(64);
        var operations = new RpcRequestOperation<int>[64];
        long lastRequestId = 0;
        for (var index = 0; index < operations.Length; index++)
            operations[index] = manager.Rent<int>(out lastRequestId);

        var waiting = manager.RentAsync<int>(
            waitForSlot: true,
            deadline: default,
            new CancellationToken(canceled: true)).AsTask();
        var failure = await CaptureExceptionAsync(waiting);

        await Assert.That(failure).IsTypeOf<OperationCanceledException>();
        await Assert.That(manager.AllocateRequestId()).IsEqualTo(lastRequestId + 1);

        manager.FailAllPendingRequests(new IOException("waiter saturation cleanup"));
        await ConsumeFailuresAsync(operations);
    }

    [Test]
    public async Task FullTableShouldRejectStreamWithoutProbingRequestIds()
    {
        using var manager = PendingRequestTableTestFixture.Create(8);
        var operations = new RpcRequestOperation<int>[8];
        long lastRequestId = 0;
        for (var index = 0; index < operations.Length; index++)
            operations[index] = manager.Rent<int>(out lastRequestId);

        var failure = CaptureException(() => manager.RegisterStream(
            PendingCallKind.ServerStreaming,
            NoopStreamDispatcher.Instance,
            deadline: default,
            CancellationToken.None));

        await Assert.That(failure).IsTypeOf<SharpLinkException>();
        await Assert.That(((SharpLinkException)failure!).Code).IsEqualTo(SharpLinkErrorCode.ResourceExhausted);
        await Assert.That(manager.AllocateRequestId()).IsEqualTo(lastRequestId + 1);

        manager.FailAllPendingRequests(new IOException("stream saturation cleanup"));
        await ConsumeFailuresAsync(operations);
    }

    [Test]
    public async Task TerminalRemovalShouldReturnCapacityPermit()
    {
        using var manager = PendingRequestTableTestFixture.Create(1);
        var first = manager.Rent<int>(out var firstId);
        var fullFailure = CaptureException(() => manager.Rent<int>(out _));
        await Assert.That(fullFailure).IsTypeOf<SharpLinkException>();
        await Assert.That(((SharpLinkException)fullFailure!).Code).IsEqualTo(SharpLinkErrorCode.ResourceExhausted);

        var payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        await Assert.That(manager.Dispatch(firstId, ref payload)).IsTrue();
        _ = await first.AsValueTask();

        var second = manager.Rent<int>(out var secondId);
        payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        await Assert.That(manager.Dispatch(secondId, ref payload)).IsTrue();
        _ = await second.AsValueTask();
        await Assert.That(manager.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentRegistrationsShouldGrantTheLastPermitExactlyOnce()
    {
        using var manager = PendingRequestTableTestFixture.Create(1);
        using var start = new ManualResetEventSlim(initialState: false);
        var attempts = new Task<RegistrationAttempt>[32];
        for (var index = 0; index < attempts.Length; index++)
        {
            attempts[index] = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    var operation = manager.Rent<int>(out var requestId);
                    return new RegistrationAttempt(operation, requestId, null);
                }
                catch (Exception exception)
                {
                    return new RegistrationAttempt(null, 0, exception);
                }
            });
        }

        start.Set();
        var results = await Task.WhenAll(attempts);
        RegistrationAttempt? winner = null;
        var successCount = 0;
        foreach (var result in results)
        {
            if (result.Operation is not null)
            {
                winner = result;
                successCount++;
                continue;
            }

            await Assert.That(result.Exception).IsTypeOf<SharpLinkException>();
            await Assert.That(((SharpLinkException)result.Exception!).Code)
                .IsEqualTo(SharpLinkErrorCode.ResourceExhausted);
        }

        await Assert.That(successCount).IsEqualTo(1);
        await Assert.That(winner).IsNotNull();
        var payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        await Assert.That(manager.Dispatch(winner!.RequestId, ref payload)).IsTrue();
        _ = await winner.Operation!.AsValueTask();

        var reused = manager.Rent<int>(out var reusedId);
        payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        await Assert.That(manager.Dispatch(reusedId, ref payload)).IsTrue();
        _ = await reused.AsValueTask();
        await Assert.That(manager.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentRegistrationsWithinAvailableCapacityShouldAllPublish()
    {
        using var manager = PendingRequestTableTestFixture.Create(8);
        var occupied = new RpcRequestOperation<int>[4];
        for (var index = 0; index < occupied.Length; index++)
            occupied[index] = manager.Rent<int>(out _);

        using var start = new ManualResetEventSlim(initialState: false);
        var attempts = new Task<RegistrationAttempt>[4];
        for (var index = 0; index < attempts.Length; index++)
        {
            attempts[index] = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    var operation = manager.Rent<int>(out var requestId);
                    return new RegistrationAttempt(operation, requestId, null);
                }
                catch (Exception exception)
                {
                    return new RegistrationAttempt(null, 0, exception);
                }
            });
        }

        start.Set();
        var results = await Task.WhenAll(attempts);
        var concurrentOperations = new List<RpcRequestOperation<int>>(results.Length);
        foreach (var result in results)
        {
            await Assert.That(result.Exception).IsNull();
            await Assert.That(result.Operation).IsNotNull();
            concurrentOperations.Add(result.Operation!);
        }

        await Assert.That(manager.Count).IsEqualTo(8);
        manager.FailAllPendingRequests(new IOException("concurrent permit cleanup"));
        await ConsumeFailuresAsync(occupied);
        await ConsumeFailuresAsync(concurrentOperations);
        await Assert.That(manager.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeShouldWakeAllWaitersBeforeDisposingSlotSignal()
    {
        const int waiterCount = 32;
        var manager = PendingRequestTableTestFixture.Create(1);
        var occupied = manager.Rent<int>(out _);
        var waiters = new Task<PendingRequestLease<int>>[waiterCount];
        for (var index = 0; index < waiters.Length; index++)
        {
            waiters[index] = manager.RentAsync<int>(
                waitForSlot: true,
                deadline: default,
                CancellationToken.None).AsTask();
            await Assert.That(waiters[index].IsCompleted).IsFalse();
        }

        manager.Dispose();

        var failures = new Task<Exception?>[waiters.Length];
        for (var index = 0; index < failures.Length; index++)
            failures[index] = CaptureExceptionAsync(waiters[index]);

        var allFailures = Task.WhenAll(failures);
        var completed = await Task.WhenAny(allFailures, Task.Delay(TimeSpan.FromSeconds(10)));
        if (!ReferenceEquals(completed, allFailures))
            throw new Exception("disposing a full table left one or more pending waiters blocked");

        foreach (var failure in await allFailures)
            await Assert.That(failure).IsTypeOf<ObjectDisposedException>();

        var occupiedFailure = await CaptureExceptionAsync(occupied.AsValueTask().AsTask());
        await Assert.That(occupiedFailure).IsTypeOf<SharpLinkException>();
        await Assert.That(((SharpLinkException)occupiedFailure!).Code)
            .IsEqualTo(SharpLinkErrorCode.ConnectionClosed);
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

    private static async Task ConsumeFailuresAsync(IEnumerable<RpcRequestOperation<int>> operations)
    {
        foreach (var operation in operations)
        {
            try
            {
                _ = await operation.AsValueTask();
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record RegistrationAttempt(
        RpcRequestOperation<int>? Operation,
        long RequestId,
        Exception? Exception);

    private sealed class NoopStreamDispatcher : IStreamDispatcher
    {
        internal static NoopStreamDispatcher Instance { get; } = new();

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;

        public void Complete(bool isError, string? errorMessage)
        {
        }

        public void Complete(Exception? exception)
        {
        }
    }
}
