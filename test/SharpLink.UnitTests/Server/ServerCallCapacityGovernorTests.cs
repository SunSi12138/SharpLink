using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerCallCapacityGovernorTests
{
    [Test]
    public async Task ReservationConsumesCapacityBeforeActivation()
    {
        var governor = new ServerCallCapacityGovernor(1);

        Ensure(governor.TryReserve(out var reservation), "first reservation must acquire capacity");
        try
        {
            var reserved = governor.CaptureSnapshot();
            await Assert.That(reserved.ReservedCalls).IsEqualTo(1);
            await Assert.That(reserved.ActiveCalls).IsEqualTo(0);
            await Assert.That(reserved.OccupiedCalls).IsEqualTo(1);
            await Assert.That(governor.TryReserve(out _)).IsFalse();

            reservation.Activate();

            var active = governor.CaptureSnapshot();
            await Assert.That(active.ReservedCalls).IsEqualTo(0);
            await Assert.That(active.ActiveCalls).IsEqualTo(1);
            await Assert.That(active.OccupiedCalls).IsEqualTo(1);
            governor.AssertInvariant();
        }
        finally
        {
            reservation.Dispose();
        }

        await Assert.That(governor.CaptureSnapshot().OccupiedCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ReservedAndActiveCallsShareTheSameCapacityBoundary()
    {
        var governor = new ServerCallCapacityGovernor(2);
        Ensure(governor.TryReserve(out var active), "first reservation must acquire capacity");
        Ensure(governor.TryReserve(out var reserved), "second reservation must acquire capacity");
        try
        {
            active.Activate();

            var snapshot = governor.CaptureSnapshot();
            await Assert.That(snapshot.ReservedCalls).IsEqualTo(1);
            await Assert.That(snapshot.ActiveCalls).IsEqualTo(1);
            await Assert.That(snapshot.OccupiedCalls).IsEqualTo(2);
            await Assert.That(governor.TryReserve(out _)).IsFalse();
            governor.AssertInvariant();
        }
        finally
        {
            reserved.Dispose();
            active.Dispose();
        }
    }

    [Test]
    public async Task DisposingUnactivatedReservationReturnsCapacity()
    {
        var governor = new ServerCallCapacityGovernor(1);
        Ensure(governor.TryReserve(out var reservation), "reservation must acquire capacity");

        reservation.Dispose();

        var released = governor.CaptureSnapshot();
        await Assert.That(released.ReservedCalls).IsEqualTo(0);
        await Assert.That(released.ActiveCalls).IsEqualTo(0);
        Ensure(governor.TryReserve(out var replacement), "released capacity must be reusable");
        replacement.Dispose();
    }

    [Test]
    public async Task DisposeIsExactlyOnceForOneReservationOwner()
    {
        var governor = new ServerCallCapacityGovernor(1);
        Ensure(governor.TryReserve(out var reservation), "reservation must acquire capacity");
        reservation.Activate();

        reservation.Dispose();
        reservation.Dispose();

        var snapshot = governor.CaptureSnapshot();
        await Assert.That(snapshot.ReservedCalls).IsEqualTo(0);
        await Assert.That(snapshot.ActiveCalls).IsEqualTo(0);
        governor.AssertInvariant();
    }

    [Test]
    public async Task ActivationDoesNotPermitAnAdditionalCall()
    {
        var governor = new ServerCallCapacityGovernor(1);
        Ensure(governor.TryReserve(out var reservation), "reservation must acquire capacity");
        try
        {
            reservation.Activate();
            await Assert.That(governor.TryReserve(out _)).IsFalse();
        }
        finally
        {
            reservation.Dispose();
        }
    }

    [Test]
    public async Task ConcurrentReservationChurnPreservesCapacityInvariant()
    {
        const int capacity = 16;
        const int iterations = 100_000;
        var governor = new ServerCallCapacityGovernor(capacity);
        var invariantFailures = 0;

        Parallel.For(0, iterations, index =>
        {
            if (!governor.TryReserve(out var reservation))
                return;

            try
            {
                if ((index & 1) == 0)
                    reservation.Activate();

                var snapshot = governor.CaptureSnapshot();
                if (snapshot.ReservedCalls < 0 ||
                    snapshot.ActiveCalls < 0 ||
                    snapshot.OccupiedCalls > capacity)
                {
                    Interlocked.Increment(ref invariantFailures);
                }
            }
            finally
            {
                reservation.Dispose();
            }
        });

        await Assert.That(invariantFailures).IsEqualTo(0);
        await Assert.That(governor.CaptureSnapshot().OccupiedCalls).IsEqualTo(0);
        governor.AssertInvariant();
    }

    [Test]
    public async Task InvalidCapacityIsRejected()
    {
        await Assert.That(() => new ServerCallCapacityGovernor(0))
            .Throws<ArgumentOutOfRangeException>();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
