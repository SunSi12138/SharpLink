using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class PreCreditSerializedBudgetTests
{
    [Test]
    public async Task WaitersShouldRemainFifoWhenASmallerFollowerWouldFit()
    {
        var budget = new PreCreditSerializedBudget(10, maxWaiters: 8);
        await budget.AcquireAsync(1, 1, 6, CancellationToken.None);

        var first = budget.AcquireAsync(2, 1, 6, CancellationToken.None).AsTask();
        var second = budget.AcquireAsync(3, 1, 4, CancellationToken.None).AsTask();
        Ensure(!first.IsCompleted && !second.IsCompleted, "both waiters should queue behind the initial owner");

        budget.Release(2);
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!second.IsCompleted,
            "the smaller follower must not bypass the FIFO head after capacity becomes available");

        budget.Release(6);
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        budget.Release(4);
        budget.Release(4);
        Ensure(budget.ReservedBytes == 0, "all FIFO reservations should return to zero");
        Ensure(budget.WaiterCount == 0, "all FIFO waiters should leave the queue");
    }

    [Test]
    public async Task CancellingHeadWaiterShouldAdmitNextFittingWaiter()
    {
        var budget = new PreCreditSerializedBudget(10, maxWaiters: 8);
        await budget.AcquireAsync(1, 1, 6, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var cancelledHead = budget.AcquireAsync(2, 1, 6, cancellation.Token).AsTask();
        var follower = budget.AcquireAsync(3, 1, 4, CancellationToken.None).AsTask();
        Ensure(!cancelledHead.IsCompleted && !follower.IsCompleted,
            "the smaller follower should initially remain behind the FIFO head");

        cancellation.Cancel();
        await ExpectCancellation(cancelledHead);
        await follower.WaitAsync(TimeSpan.FromSeconds(2));

        budget.Release(4);
        budget.Release(6);
        Ensure(budget.ReservedBytes == 0, "cancellation must not leak a pre-credit reservation");
        Ensure(budget.WaiterCount == 0, "cancellation must remove the queued waiter exactly once");
    }

    [Test]
    public async Task StreamCompletionShouldRejectOnlyMatchingQueuedWaiters()
    {
        var budget = new PreCreditSerializedBudget(8, maxWaiters: 8);
        await budget.AcquireAsync(1, 1, 8, CancellationToken.None);
        var matching = budget.AcquireAsync(2, 7, 1, CancellationToken.None).AsTask();
        var other = budget.AcquireAsync(3, 7, 1, CancellationToken.None).AsTask();
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "stream closed");

        budget.CompleteStream(2, 7, terminal);
        await ExpectSameException(matching, terminal);
        Ensure(!other.IsCompleted, "a different stream waiter must remain queued");
        Ensure(budget.WaiterCount == 1, "only the matching stream waiter should be removed");

        budget.Release(8);
        await other.WaitAsync(TimeSpan.FromSeconds(2));
        budget.Release(1);
        Ensure(budget.ReservedBytes == 0 && budget.WaiterCount == 0,
            "stream completion must preserve unrelated waiter/accounting state");
    }

    [Test]
    public async Task WaiterCountShouldFailBoundedlyInsteadOfGrowingWithoutLimit()
    {
        var budget = new PreCreditSerializedBudget(1, maxWaiters: 2);
        await budget.AcquireAsync(1, 1, 1, CancellationToken.None);
        var first = budget.AcquireAsync(2, 1, 1, CancellationToken.None).AsTask();
        var second = budget.AcquireAsync(3, 1, 1, CancellationToken.None).AsTask();

        Exception? failure = null;
        try
        {
            await budget.AcquireAsync(4, 1, 1, CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
            "the bounded waiter queue should reject excess producers with ResourceExhausted");
        Ensure(budget.WaiterCount == 2, "the rejected producer must not enter the waiter queue");

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "cleanup");
        budget.Complete(terminal);
        await ExpectSameException(first, terminal);
        await ExpectSameException(second, terminal);
        budget.Release(1);
        Ensure(budget.ReservedBytes == 0 && budget.WaiterCount == 0,
            "bounded waiter cleanup must return all accounting to zero");
    }

    [Test]
    public async Task OversizedReservationShouldBorrowOnlyWhenSoleOwner()
    {
        var budget = new PreCreditSerializedBudget(8, maxWaiters: 8);
        await budget.AcquireAsync(1, 1, 32, CancellationToken.None);
        Ensure(budget.ReservedBytes == 32, "one legal oversized item should be allowed to own the budget");

        var follower = budget.AcquireAsync(2, 1, 1, CancellationToken.None).AsTask();
        Ensure(!follower.IsCompleted, "a second reservation must wait behind an oversized owner");

        budget.Release(32);
        await follower.WaitAsync(TimeSpan.FromSeconds(2));
        budget.Release(1);
        Ensure(budget.ReservedBytes == 0, "oversized ownership should release exactly once");
    }

    [Test]
    public async Task CompletionShouldRejectQueuedWaitersWithoutStealingOwnedBytes()
    {
        var budget = new PreCreditSerializedBudget(8, maxWaiters: 8);
        await budget.AcquireAsync(1, 1, 8, CancellationToken.None);
        var waiter = budget.AcquireAsync(2, 1, 1, CancellationToken.None).AsTask();
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "terminal");

        budget.Complete(terminal);
        await ExpectSameException(waiter, terminal);
        Ensure(budget.ReservedBytes == 8,
            "terminal completion must leave already-owned bytes for their normal finally-path release");
        Ensure(budget.WaiterCount == 0, "terminal completion must empty the bounded waiter queue");

        budget.Release(8);
        Ensure(budget.ReservedBytes == 0, "owned bytes should still be releasable after completion");
        await ExpectSameException(
            budget.AcquireAsync(3, 1, 1, CancellationToken.None).AsTask(),
            terminal);
    }

    [Test]
    public async Task RepeatedFastPathReservationsShouldReturnAccountingToZero()
    {
        var budget = new PreCreditSerializedBudget(1024, maxWaiters: 8);
        for (var index = 0; index < 100_000; index++)
        {
            await budget.AcquireAsync(index + 1, 1, 1, CancellationToken.None);
            budget.Release(1);
        }
        Ensure(budget.ReservedBytes == 0, "100k acquire/release churn must leave no byte accounting behind");
        Ensure(budget.WaiterCount == 0, "100k fast-path churn must leave no waiters behind");
    }

    private static async Task ExpectCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("The pre-credit waiter did not observe cancellation.");
    }

    private static async Task ExpectSameException(Task task, Exception expected)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (ReferenceEquals(exception, expected))
        {
            return;
        }
        throw new InvalidOperationException("The pre-credit waiter did not observe the expected terminal exception.");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Pre-credit budget assertion failed: {scenario}.");
    }
}
