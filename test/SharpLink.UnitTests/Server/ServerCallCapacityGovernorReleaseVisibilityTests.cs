using System.Diagnostics.CodeAnalysis;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerCallCapacityGovernorReleaseVisibilityTests
{
    [Test]
    public async Task ConcurrentDisposeMustNotReturnBeforeBackingCapacityIsReleased()
    {
        using var releaseClaimed = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        using var secondObservedReleasing = new ManualResetEventSlim();

        var hooks = new ServerCallCapacityGovernorTestHooks
        {
            ReservationReleaseClaimed = () =>
            {
                releaseClaimed.Set();
                if (!allowRelease.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out waiting to release reservation capacity.");
            },
            DisposeObservedReleasing = () => secondObservedReleasing.Set(),
        };
        var governor = new ServerCallCapacityGovernor(1, hooks);
        Ensure(governor.TryReserve(out var reservation), "reservation must acquire capacity");
        var alias = reservation;

        var firstDispose = Task.Factory.StartNew(
            reservation.Dispose,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task? secondDispose = null;
        try
        {
            Ensure(
                releaseClaimed.Wait(TimeSpan.FromSeconds(10)),
                "first disposer must claim release before the aggregate counter is changed");

            var whileReleasing = governor.CaptureSnapshot();
            await Assert.That(whileReleasing.ReservedCalls).IsEqualTo(1);
            await Assert.That(whileReleasing.ActiveCalls).IsEqualTo(0);
            await Assert.That(governor.TryReserve(out _)).IsFalse();

            var secondReturned = 0;
            secondDispose = Task.Factory.StartNew(
                () =>
                {
                    alias.Dispose();
                    Volatile.Write(ref secondReturned, 1);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Ensure(
                secondObservedReleasing.Wait(TimeSpan.FromSeconds(10)),
                "second disposer must observe the in-progress Releasing state");
            await Assert.That(Volatile.Read(ref secondReturned)).IsEqualTo(0);
            await Assert.That(secondDispose.IsCompleted).IsFalse();
            await Assert.That(governor.CaptureSnapshot().OccupiedCalls).IsEqualTo(1);
        }
        finally
        {
            allowRelease.Set();
        }

        await firstDispose.WaitAsync(TimeSpan.FromSeconds(10));
        if (secondDispose is not null)
            await secondDispose.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(governor.CaptureSnapshot().OccupiedCalls).IsEqualTo(0);
        Ensure(governor.TryReserve(out var replacement), "capacity must be reusable after release completes");
        replacement.Dispose();
        governor.AssertInvariant();
    }

    private static void Ensure([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
