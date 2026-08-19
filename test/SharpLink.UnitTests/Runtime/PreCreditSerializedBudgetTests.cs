using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class PreCreditSerializedBudgetTests
{
    [Test]
    public async Task WaitersShouldRemainFifoWhenASmallerFollowerWouldFit()
    {
        var budget = new PreCreditSerializedBudget(10);
        await budget.AcquireAsync(6, CancellationToken.None);

        var first = budget.AcquireAsync(6, CancellationToken.None).AsTask();
        var second = budget.AcquireAsync(4, CancellationToken.None).AsTask();
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
    }

    [Test]
    public async Task CancellingHeadWaiterShouldAdmitNextFittingWaiter()
    {
        var budget = new PreCreditSerializedBudget(10);
        await budget.AcquireAsync(6, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var cancelledHead = budget.AcquireAsync(6, cancellation.Token).AsTask();
        var follower = budget.AcquireAsync(4, CancellationToken.None).AsTask();
        Ensure(!cancelledHead.IsCompleted && !follower.IsCompleted,
            "the smaller follower should initially remain behind the FIFO head");

        cancellation.Cancel();
        await ExpectCancellation(cancelledHead);
        await follower.WaitAsync(TimeSpan.FromSeconds(2));

        budget.Release(4);
        budget.Release(6);
        Ensure(budget.ReservedBytes == 0, "cancellation must not leak a pre-credit reservation");
    }

    [Test]
    public async Task OversizedReservationShouldBorrowOnlyWhenSoleOwner()
    {
        var budget = new PreCreditSerializedBudget(8);
        await budget.AcquireAsync(8, CancellationToken.None);
        budget.ResizeReservation(8, 32);
        Ensure(budget.ReservedBytes == 32, "one legal oversized item should be allowed to own the budget");

        var follower = budget.AcquireAsync(1, CancellationToken.None).AsTask();
        Ensure(!follower.IsCompleted, "a second reservation must wait behind an oversized owner");

        budget.Release(32);
        await follower.WaitAsync(TimeSpan.FromSeconds(2));
        budget.Release(1);
        Ensure(budget.ReservedBytes == 0, "oversized ownership should release exactly once");
    }

    [Test]
    public async Task CompletionShouldRejectQueuedWaitersWithoutStealingOwnedBytes()
    {
        var budget = new PreCreditSerializedBudget(8);
        await budget.AcquireAsync(8, CancellationToken.None);
        var waiter = budget.AcquireAsync(1, CancellationToken.None).AsTask();
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "terminal");

        budget.Complete(terminal);
        await ExpectSameException(waiter, terminal);
        Ensure(budget.ReservedBytes == 8,
            "terminal completion must leave already-owned bytes for their normal finally-path release");

        budget.Release(8);
        Ensure(budget.ReservedBytes == 0, "owned bytes should still be releasable after completion");
        await ExpectSameException(budget.AcquireAsync(1, CancellationToken.None).AsTask(), terminal);
    }

    [Test]
    public async Task RepeatedFastPathReservationsShouldReturnAccountingToZero()
    {
        var budget = new PreCreditSerializedBudget(1024);
        for (var index = 0; index < 100_000; index++)
        {
            await budget.AcquireAsync(1, CancellationToken.None);
            budget.Release(1);
        }
        Ensure(budget.ReservedBytes == 0, "100k acquire/release churn must leave no byte accounting behind");
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
