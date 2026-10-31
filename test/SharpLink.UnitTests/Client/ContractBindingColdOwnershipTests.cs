using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Client;

// Tests the shipping cold-state owners directly. These are not transport or physical-tail proofs.
public sealed class ContractBindingColdOwnershipTests
{
    [Test]
    public void AggregateOperationBudgetIsBoundedAndRecoverable()
    {
        var budget = new ClientBindingBudget();
        for (var index = 0; index < ClientBindingBudget.MaximumOperations; index++)
            Ensure(budget.TryReserveOperation(), "every available operation must reserve");
        Ensure(!budget.TryReserveOperation(), "the aggregate limit must reject without changing ownership");
        Ensure(budget.Operations == ClientBindingBudget.MaximumOperations, "failed reservation cannot consume quota");
        budget.ReleaseOperation();
        Ensure(budget.TryReserveOperation(), "one returned owner makes exactly one capacity available");
        for (var index = 0; index < ClientBindingBudget.MaximumOperations; index++) budget.ReleaseOperation();
        Ensure(budget.Operations == 0, "all owners released");
    }

    [Test]
    public void TrackingBudgetRejectsInvalidAndOverLimitAmountsWithoutReservation()
    {
        var budget = new ClientBindingBudget();
        Ensure(!budget.TryReserveTrackingSlots(0) && !budget.TryReserveTrackingSlots(-1) &&
            !budget.TryReserveTrackingSlots(ClientBindingBudget.MaximumTrackingSlots + 1), "invalid tracking requests");
        Ensure(budget.TrackingSlots == 0, "invalid tracking requests cannot reserve");
        Ensure(budget.TryReserveTrackingSlots(ClientBindingBudget.MaximumTrackingSlots), "exact tracking ceiling");
        Ensure(!budget.TryReserveTrackingSlots(1), "aggregate tracking ceiling across Sessions");
        budget.ReleaseTrackingSlots(ClientBindingBudget.MaximumTrackingSlots);
        Ensure(budget.TrackingSlots == 0, "tracking released after Session close");
    }

    [Test]
    public void OperationTerminalCleanupReleasesAggregateQuotaOnlyOnce()
    {
        var budget = new ClientBindingBudget();
        Ensure(budget.TryReserveOperation(), "reserve owner");
        var operation = new ClientBindingSession.Operation(1, default, 0, budget);
        operation.ReleaseBudget();
        operation.ReleaseBudget(); // duplicate terminal notification owns the same idempotent release
        Ensure(budget.Operations == 0, "logical result/Session cleanup cannot double release aggregate ownership");
    }

    [Test]
    public void NewerValidatedResultMakesOlderResultIneligibleEvenWithoutInvalidation()
    {
        var publication = new ClientBindingPublication(2);
        const ulong older = (2UL << 16) | 1;
        const ulong newer = (3UL << 16) | 1;
        Ensure(publication.ObserveValidatedSuccess(newer), "new incarnation is eligible");
        var commits = 0;
        Ensure(publication.TryCommit(newer, () => commits++), "new incarnation publishes");
        Ensure(!publication.ObserveValidatedSuccess(older), "dropped Invalidate cannot allow older result");
        Ensure(!publication.TryCommit(older, () => commits++), "older route must not publish");
        Ensure(publication.IsEligible(newer) && commits == 1, "new route is not damaged by delayed older result");
    }

    [Test]
    public void InvalidationBetweenEarlyResultAndFinalCommitPreventsPublication()
    {
        var publication = new ClientBindingPublication(1);
        const ulong token = 1UL << 16;
        Ensure(publication.ObserveValidatedSuccess(token), "correlated early result observed");
        publication.Invalidate(token);
        var called = false;
        Ensure(!publication.TryCommit(token, () => called = true) && !called,
            "final publication rechecks the same floor after enqueue confirmation");
    }

    [Test]
    public void ClosePermanentlyRejectsCapturedTokensAndDetachesTracking()
    {
        var publication = new ClientBindingPublication(1);
        const ulong token = 1UL << 16;
        Ensure(publication.ObserveValidatedSuccess(token), "valid initial token");
        publication.Close();
        Ensure(!publication.IsEligible(token) && !publication.ObserveValidatedSuccess(token), "terminal cannot resurrect");
        Ensure(!publication.TryCommit(token, () => throw new InvalidOperationException("terminal callback")), "terminal no commit");
        publication.Invalidate(token); // valid late terminal control is harmless, not new tracking
        publication.Close();
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
