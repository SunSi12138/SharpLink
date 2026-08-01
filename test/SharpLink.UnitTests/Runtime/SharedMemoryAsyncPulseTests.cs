namespace SharpLink.UnitTests.Runtime;

public class SharedMemoryAsyncPulseTests
{
    [Test]
    public async Task LatchedPulseFastPathShouldRemainAllocationFree()
    {
        var pulse = new SharedMemoryAsyncPulse();
        // Keep tiered compilation and dynamic PGO outside the allocation window. A
        // short warmup can otherwise charge the runtime's one-time promotion work
        // to this thread when the wider test suite changes scheduling.
        for (var index = 0; index < 100_000; index++)
        {
            pulse.Pulse();
            Ensure(pulse.WaitAsync().Result, "shared-memory pulse warmup");
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            pulse.Pulse();
            var wait = pulse.WaitAsync();
            Ensure(wait.IsCompletedSuccessfully && wait.Result,
                "shared-memory pulse synchronous fast path");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task PendingPulseShouldBeReusableAndCompleteWithoutAStaleWake()
    {
        var pulse = new SharedMemoryAsyncPulse();
        var first = pulse.WaitAsync();
        Ensure(!first.IsCompleted, "shared-memory pulse pending wait");
        pulse.Pulse();
        Ensure(await first, "shared-memory pulse pending result");

        var second = pulse.WaitAsync();
        Ensure(!second.IsCompleted, "shared-memory pulse does not retain a stale wake");
        pulse.Complete();
        Ensure(!await second, "shared-memory pulse completion result");

        var completed = pulse.WaitAsync();
        Ensure(completed.IsCompletedSuccessfully && !completed.Result,
            "shared-memory pulse completed fast path");
    }

    [Test]
    public async Task CompletionShouldPreserveAPreviouslyLatchedPulse()
    {
        var pulse = new SharedMemoryAsyncPulse();
        pulse.Pulse();
        pulse.Complete();

        Ensure(await pulse.WaitAsync(), "shared-memory pulse drains the final latched wake");
        Ensure(!await pulse.WaitAsync(), "shared-memory pulse completes after the latched wake");
    }

    [Test]
    public async Task ConcurrentWaitersShouldBeRejectedWithoutBreakingTheRegisteredWaiter()
    {
        var pulse = new SharedMemoryAsyncPulse();
        var registered = pulse.WaitAsync();
        try
        {
            _ = pulse.WaitAsync();
            throw new Exception("expected concurrent shared-memory pulse waiter rejection");
        }
        catch (InvalidOperationException)
        {
        }

        pulse.Pulse();
        Ensure(await registered, "shared-memory registered waiter survived rejection");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
