using System.Collections.Generic;

namespace SharpLink.UnitTests.Builder;

public sealed class SynchronousBuildTransactionTests
{
    [Test]
    public void RollbackShouldUseReferenceIdentityAndReverseFrameworkCleanupOnly()
    {
        var events = new List<string>();
        var first = new TrackingResource("first", events);
        var second = new TrackingResource("second", events);
        var callerOwned = new TrackingResource("caller", events);
        var transaction = new SynchronousBuildTransaction();

        transaction.Own(
            first,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("first"));
        transaction.Own(
            second,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("second"));
        transaction.Own(
            callerOwned,
            cleanup: null,
            metadata: SynchronousBuildResourceMetadata.CallerOwned("caller"));

        transaction.Rollback();

        EnsureSequence(events, "disposed:second", "disposed:first");
        Ensure(first.DisposeCount == 1 && second.DisposeCount == 1,
            "distinct resources with equal values must each be released once by reference identity");
        Ensure(callerOwned.DisposeCount == 0, "caller-owned resources must never be disposed by the transaction");
    }

    [Test]
    public void DuplicateOwnAndOwnRangeShouldFailImmediatelyWithoutDoubleCleanup()
    {
        var events = new List<string>();
        var first = new TrackingResource("first", events);
        var second = new TrackingResource("second", events);
        var transaction = new SynchronousBuildTransaction();

        transaction.Own(
            first,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("first"));
        var duplicate = Capture(() => transaction.Own(
            first,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("duplicate first")));
        var rangeDuplicate = Capture(() => transaction.OwnRange(
            [second, first],
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("range")));

        Ensure(duplicate is InvalidOperationException, "duplicate Own must be rejected immediately");
        Ensure(rangeDuplicate is InvalidOperationException, "OwnRange must validate each item by reference identity");

        transaction.Rollback();

        EnsureSequence(events, "disposed:second", "disposed:first");
        Ensure(first.DisposeCount == 1 && second.DisposeCount == 1,
            "a duplicate registration must not result in a duplicate cleanup");
    }

    [Test]
    public void RollbackShouldPreservePrimaryAndAppendCleanupFailuresInReverseOrder()
    {
        var events = new List<string>();
        var first = new TrackingResource("first", events, "first cleanup failed");
        var second = new TrackingResource("second", events, "second cleanup failed");
        var transaction = new SynchronousBuildTransaction();
        transaction.Own(
            first,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("first"));
        transaction.Own(
            second,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("second"));
        var primary = Capture(ThrowPrimary);

        var failure = Capture(() => transaction.Rollback(primary));

        if (failure is not AggregateException aggregate)
            throw new Exception("cleanup failures must aggregate with the primary failure");
        Ensure(aggregate.InnerExceptions.Count == 3, "aggregate must contain primary and both cleanup failures");
        Ensure(ReferenceEquals(aggregate.InnerExceptions[0], primary), "primary failure must remain first and unchanged");
        Ensure(aggregate.InnerExceptions[1].Message.Contains("second cleanup failed", StringComparison.Ordinal) &&
               aggregate.InnerExceptions[2].Message.Contains("first cleanup failed", StringComparison.Ordinal),
            "cleanup failures must follow actual reverse rollback order");
        EnsureSequence(events, "disposed:second", "disposed:first");
    }

    [Test]
    public void RollbackWithoutCleanupFailuresShouldRethrowTheOriginalFailureInstance()
    {
        var transaction = new SynchronousBuildTransaction();
        var primary = Capture(ThrowPrimary);

        var failure = Capture(() => transaction.Rollback(primary));

        Ensure(ReferenceEquals(failure, primary), "ExceptionDispatchInfo must preserve the original primary exception");
        Ensure(failure.StackTrace?.Contains(nameof(ThrowPrimary), StringComparison.Ordinal) == true,
            "primary failure stack must not be reset during rollback");
    }

    [Test]
    public void OwnershipMetadataShouldRejectInconsistentCleanupDeclarations()
    {
        var events = new List<string>();
        var frameworkOwned = new TrackingResource("framework", events);
        var callerOwned = new TrackingResource("caller", events);
        var transaction = new SynchronousBuildTransaction();

        var missingFrameworkCleanup = Capture(() => transaction.Own(
            frameworkOwned,
            cleanup: null,
            metadata: SynchronousBuildResourceMetadata.FrameworkOwned("framework")));
        var callerCleanup = Capture(() => transaction.Own(
            callerOwned,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.CallerOwned("caller")));

        Ensure(missingFrameworkCleanup is ArgumentException,
            "framework-owned resources must declare a cleanup action");
        Ensure(callerCleanup is ArgumentException,
            "caller-owned resources must reject a cleanup action");
        transaction.Rollback();
        Ensure(events.Count == 0, "invalid ownership declarations must not register cleanup");
    }

    [Test]
    public void TerminalOperationsShouldHaveExplicitStateRules()
    {
        var committed = new TrackingResource("committed", []);
        var committedTransaction = new SynchronousBuildTransaction();
        committedTransaction.Own(
            committed,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("committed"));
        committedTransaction.Transfer();
        committedTransaction.Dispose();

        Ensure(committed.DisposeCount == 0, "Dispose after Commit/Transfer must not clean transferred resources");
        Ensure(Capture(committedTransaction.Commit) is InvalidOperationException,
            "a second Commit must not be silently accepted");
        Ensure(Capture(committedTransaction.Rollback) is InvalidOperationException,
            "Rollback after Commit must not be silently accepted");
        Ensure(Capture(() => committedTransaction.Own(
            new TrackingResource("late", []),
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("late"))) is InvalidOperationException,
            "Own after Commit must be rejected");

        var rolledBackTransaction = new SynchronousBuildTransaction();
        rolledBackTransaction.Rollback();
        rolledBackTransaction.Dispose();
        Ensure(Capture(rolledBackTransaction.Rollback) is InvalidOperationException,
            "a second explicit Rollback must not be silently accepted");
        Ensure(Capture(rolledBackTransaction.Commit) is InvalidOperationException,
            "Commit after Rollback must not be silently accepted");
    }

    [Test]
    public void ReentrantCleanupShouldBeRejectedAndMustNotBlockEarlierCleanup()
    {
        var events = new List<string>();
        var first = new TrackingResource("first", events);
        var second = new TrackingResource("second", events);
        var transaction = new SynchronousBuildTransaction();
        transaction.Own(
            first,
            static resource => resource.Cleanup(),
            SynchronousBuildResourceMetadata.FrameworkOwned("first"));
        transaction.Own(
            second,
            _ =>
            {
                second.Cleanup();
                transaction.Own(
                    new TrackingResource("reentrant", events),
                    static resource => resource.Cleanup(),
                    SynchronousBuildResourceMetadata.FrameworkOwned("reentrant"));
            },
            SynchronousBuildResourceMetadata.FrameworkOwned("second"));
        var primary = Capture(ThrowPrimary);

        var exception = Capture(() => transaction.Rollback(primary));
        if (exception is not AggregateException failure)
            throw new Exception("reentrant cleanup rejection must aggregate with the primary failure");

        Ensure(failure.InnerExceptions.Count == 2, "reentrant cleanup rejection must aggregate with the primary failure");
        Ensure(ReferenceEquals(failure.InnerExceptions[0], primary), "primary failure remains first after reentrant cleanup");
        Ensure(failure.InnerExceptions[1] is InvalidOperationException,
            "cleanup-time registration must be explicitly rejected");
        EnsureSequence(events, "disposed:second", "disposed:first");
        Ensure(first.DisposeCount == 1 && second.DisposeCount == 1,
            "a reentrant cleanup failure must not block earlier registered cleanup");
    }

    private static void ThrowPrimary() => throw new InvalidOperationException("primary build failure");

    private static Exception Capture(Action action)
    {
        try
        {
            action();
            throw new Exception("expected failure");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void EnsureSequence(IReadOnlyList<string> actual, params string[] expected)
    {
        Ensure(actual.Count == expected.Length,
            $"cleanup count must be {expected.Length}, but was {actual.Count}");
        for (var index = 0; index < expected.Length; index++)
        {
            Ensure(StringComparer.Ordinal.Equals(actual[index], expected[index]),
                $"cleanup index {index} must be '{expected[index]}', but was '{actual[index]}'");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TrackingResource(
        string name,
        List<string> events,
        string? cleanupFailure = null)
    {
        internal int DisposeCount { get; private set; }

        internal void Cleanup()
        {
            DisposeCount++;
            events.Add($"disposed:{name}");
            if (cleanupFailure is not null)
                throw new InvalidOperationException(cleanupFailure);
        }

        public override bool Equals(object? obj) => obj is TrackingResource;

        public override int GetHashCode() => 0;
    }
}
