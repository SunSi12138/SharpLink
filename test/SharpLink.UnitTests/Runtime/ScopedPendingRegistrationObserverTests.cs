using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class ScopedPendingRegistrationObserverTests
{
    [Test]
    public async Task ConcurrentRegistrationWitnessesMustIgnoreOtherScopesAndUnmarkedCalls()
    {
        var firstCount = 0;
        var secondCount = 0;
        using var first = new ScopedPendingRegistrationObserver(() => Interlocked.Increment(ref firstCount));
        using var second = new ScopedPendingRegistrationObserver(() => Interlocked.Increment(ref secondCount));
        using var start = new Barrier(2);
        using var unmarked = PendingRequestTableTestFixture.Create(1);
        Complete(unmarked, unmarked.Rent<int>(out var before), before);
        var tasks = new[] { first, second }.Select(observer => LongRunningTestWorker.Run(() =>
        {
            using var table = PendingRequestTableTestFixture.Create(1);
            Ensure(start.SignalAndWait(TimeSpan.FromSeconds(10)), "both dedicated registration workers must start");
            for (var index = 0; index < 128; index++)
            {
                long id = 0;
                var operation = observer.Observe(() => table.Rent<int>(out id));
                Complete(table, operation, id);
            }
        })).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            await Task.WhenAll(tasks.Select(task =>
                LongRunningTestWorker.JoinAsync(task, TimeSpan.FromSeconds(10))));
        }
        Complete(unmarked, unmarked.Rent<int>(out var after), after);
        Ensure(firstCount == 128 && secondCount == 128,
            "each live listener must see only its explicitly scoped registrations, not global traffic");
    }

    [Test]
    public void NestedRegistrationWitnessMustRestoreItsParentScope()
    {
        var firstCount = 0;
        var secondCount = 0;
        using var first = new ScopedPendingRegistrationObserver(() => firstCount++);
        using var second = new ScopedPendingRegistrationObserver(() => secondCount++);
        using var firstTable = PendingRequestTableTestFixture.Create(1);
        using var secondTable = PendingRequestTableTestFixture.Create(1);
        long firstId = 0;
        var firstOperation = first.Observe(() =>
        {
            long secondId = 0;
            var secondOperation = second.Observe(() => secondTable.Rent<int>(out secondId));
            Complete(secondTable, secondOperation, secondId);
            return firstTable.Rent<int>(out firstId);
        });
        Complete(firstTable, firstOperation, firstId);
        Ensure(firstCount == 1 && secondCount == 1, "nested registration contexts must not replace one another");
    }

    [Test]
    public void RegistrationWitnessFailureMustNotBeHiddenByTelemetryIsolation()
    {
        var expected = new InvalidOperationException("test observation failed");
        using var observer = new ScopedPendingRegistrationObserver(() => throw expected);
        using var table = PendingRequestTableTestFixture.Create(1);
        RpcRequestOperation<int>? operation = null;
        long id = 0;
        Exception? observed = null;
        try { observer.Observe(() => operation = table.Rent<int>(out id)); }
        catch (Exception exception) { observed = exception; }
        try
        {
            Ensure(ReferenceEquals(observed, expected), "an observer failure must fail the test with its exact exception");
            Ensure(operation is not null && table.Contains(id), "real registration must remain observable despite observer failure");
        }
        finally
        {
            if (operation is not null) Complete(table, operation, id);
        }
    }

    private static void Complete(PendingRequestTable table, RpcRequestOperation<int> operation, long id)
    {
        var payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        Ensure(table.Dispatch(id, ref payload), "registration must retain its real pending identity");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
