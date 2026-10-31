using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class Protocol3RequestIdExhaustionTests
{
    private static readonly FieldInfo Counter = typeof(PendingRequestTable).GetField(
        "_nextId", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public void LastPositiveIdPermanentlySealsEvenIfTheCounterChanges()
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(8, owner);
        Counter.SetValue(table, long.MaxValue - 1);
        Ensure(table.AllocateRequestId() == long.MaxValue);
        Ensure(table.RequestIdsExhausted && table.HighestAllocatedRequestId == long.MaxValue && owner.Seals == 1);
        for (var attempt = 0; attempt < 8; attempt++)
            ExpectUnavailable(() => table.AllocateRequestId());
        // Test-only corruption confirms that the permanent seal, not a wrapped counter, wins.
        Counter.SetValue(table, 0L);
        ExpectUnavailable(() => table.AllocateRequestId());
        Ensure(table.HighestAllocatedRequestId == long.MaxValue && owner.Seals == 1);
    }

    [Test]
    public void ConcurrentBoundaryAllocatorsNeverIssueZeroNegativeOrDuplicateIds()
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(8, owner);
        Counter.SetValue(table, long.MaxValue - 32);
        var issued = new ConcurrentBag<long>();
        var refused = 0;
        Parallel.For(0, 256, _ =>
        {
            try
            {
                var id = table.AllocateRequestId();
                Ensure(id > 0 && id <= table.HighestAllocatedRequestId);
                issued.Add(id);
            }
            catch (SharpLinkException failure) when (failure.Code == SharpLinkErrorCode.Unavailable)
            {
                Interlocked.Increment(ref refused);
            }
        });
        Ensure(issued.Count == 32 && refused == 224 && owner.Seals == 1);
        Ensure(new HashSet<long>(issued).Count == issued.Count);
        Ensure(table.HighestAllocatedRequestId == long.MaxValue);
    }

    [Test]
    public async Task ExhaustionWakesCapacityWaitersWithoutTerminatingAnExistingCall()
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = table.Rent<int>(out var id);
        var waiting = new Task<PendingRequestLease<int>>[8];
        for (var index = 0; index < waiting.Length; index++)
            waiting[index] = table.RentAsync<int>(true, default, CancellationToken.None).AsTask();
        Counter.SetValue(table, long.MaxValue - 1);
        Ensure(table.AllocateRequestId() == long.MaxValue);
        foreach (var waiter in waiting)
        {
            try { _ = await waiter; throw new InvalidOperationException("Expected exhausted waiter."); }
            catch (SharpLinkException failure) when (failure.Code == SharpLinkErrorCode.Unavailable) { }
        }
        Ensure(table.Count == 1 && table.ActiveCount == 1 && owner.Completions == 0);
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 42);
        var payload = new ReadOnlySequence<byte>(bytes);
        Ensure(table.Dispatch(id, ref payload));
        Ensure(await pending.AsValueTask() == 42);
        Ensure(table.ActiveCount == 0 && owner.Completions == 1);
    }

    [Test]
    public void ExistingServerAndDuplexStreamEntriesSurviveTheSeal()
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(8, owner);
        var dispatcher = new Dispatcher();
        var server = table.RegisterStream(PendingCallKind.ServerStreaming, dispatcher, default, default);
        var duplex = table.RegisterStream(PendingCallKind.DuplexStreaming, dispatcher, default, default);
        Counter.SetValue(table, long.MaxValue - 1);
        _ = table.AllocateRequestId();
        Ensure(table.Count == 2 && owner.Completions == 0);
        ExpectUnavailable(() => table.RegisterStream(PendingCallKind.ServerStreaming, dispatcher, default, default));
        Ensure(table.TryComplete(server, PendingCallCompletionReason.RemoteStreamComplete));
        Ensure(table.TryComplete(duplex, PendingCallCompletionReason.RemoteStreamComplete));
        Ensure(table.Count == 0 && owner.Completions == 2);
    }

    private static void ExpectUnavailable(Action action)
    {
        try { action(); }
        catch (SharpLinkException failure) when (failure.Code == SharpLinkErrorCode.Unavailable) { return; }
        throw new InvalidOperationException("Expected permanent request ID exhaustion.");
    }

    private static void Ensure(bool value)
    {
        if (!value) throw new InvalidOperationException("Protocol3 request ID guard assertion failed.");
    }

    private sealed class Owner : IPendingCallOwner
    {
        internal int Seals;
        internal int Completions;
        public void OnRequestIdsExhausted() => Interlocked.Increment(ref Seals);
        public void OnPendingCallRegistered() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion) => Interlocked.Increment(ref Completions);
        public void OnProducerCancellationCallbackFailed(Exception exception) => throw exception;
    }

    private sealed class Dispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
    }
}
