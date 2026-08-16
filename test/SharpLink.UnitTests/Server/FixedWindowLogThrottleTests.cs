using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class FixedWindowLogThrottleTests
{
    private const long Frequency = TimeSpan.TicksPerSecond;

    [Test]
    public async Task FirstEventIsAdmittedAndRemainingWindowIsSuppressed()
    {
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), Frequency);
        var start = 100 * Frequency;

        await Assert.That(throttle.ShouldLog(start, out var firstSuppressed)).IsTrue();
        await Assert.That(firstSuppressed).IsEqualTo(0);

        await Assert.That(throttle.ShouldLog(start + 1, out _)).IsFalse();
        await Assert.That(throttle.ShouldLog(start + 2 * Frequency, out _)).IsFalse();
        // The event exactly one tick before the window boundary is still suppressed.
        await Assert.That(throttle.ShouldLog(start + 5 * Frequency - 1, out _)).IsFalse();
    }

    [Test]
    public async Task NextWindowIsAdmittedAndReportsTheSuppressedCount()
    {
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), Frequency);
        var start = 100 * Frequency;

        Ensure(throttle.ShouldLog(start, out _), "first event must be admitted");
        Ensure(!throttle.ShouldLog(start, out _), "same-timestamp duplicate must be suppressed");
        Ensure(!throttle.ShouldLog(start + Frequency, out _), "in-window event must be suppressed");
        Ensure(!throttle.ShouldLog(start + 4 * Frequency, out _), "in-window event must be suppressed");

        // The boundary event opens the next window and reports the three suppressed events.
        await Assert.That(throttle.ShouldLog(start + 5 * Frequency, out var suppressed)).IsTrue();
        await Assert.That(suppressed).IsEqualTo(3);

        await Assert.That(throttle.ShouldLog(start + 5 * Frequency + 1, out _)).IsFalse();
        await Assert.That(throttle.ShouldLog(start + 10 * Frequency, out var next)).IsTrue();
        await Assert.That(next).IsEqualTo(1);
    }

    [Test]
    public async Task SuppressedEventsNeverProduceCountsForUnrelatedWindows()
    {
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), Frequency);
        var start = 100 * Frequency;

        Ensure(throttle.ShouldLog(start, out _), "window one opens");
        Ensure(!throttle.ShouldLog(start + Frequency, out _), "window one suppressed");
        Ensure(throttle.ShouldLog(start + 5 * Frequency, out var windowTwo), "window two opens");
        await Assert.That(windowTwo).IsEqualTo(1);
        Ensure(!throttle.ShouldLog(start + 9 * Frequency, out _), "window two remains open");
        Ensure(throttle.ShouldLog(start + 10 * Frequency, out var windowThree), "window three opens");
        await Assert.That(windowThree).IsEqualTo(1);
    }

    [Test]
    public async Task ZeroIntervalAdmitsEveryEventWithoutAccumulatingSuppression()
    {
        var throttle = new FixedWindowLogThrottle(TimeSpan.Zero, Frequency);
        for (var index = 0; index < 3; index++)
        {
            await Assert.That(throttle.ShouldLog(index, out var suppressed)).IsTrue();
            await Assert.That(suppressed).IsEqualTo(0);
        }
    }

    [Test]
    public async Task HighFrequencyIntervalsScaleExactlyWithoutClosingTheGate()
    {
        // At 1e12 Hz the five-second window is exactly 5e12 timestamp ticks, even though
        // the intermediate frequency*ticks product overflows Int64. The gate must stay
        // open and re-admit at the exact boundary instead of closing permanently.
        const long frequency = 1_000_000_000_000;
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), frequency);

        await Assert.That(throttle.ShouldLog(0, out _)).IsTrue();
        await Assert.That(throttle.ShouldLog(5_000_000_000_000 - 1, out _)).IsFalse();
        await Assert.That(throttle.ShouldLog(5_000_000_000_000, out var suppressed)).IsTrue();
        await Assert.That(suppressed).IsEqualTo(1);
    }

    [Test]
    public async Task TrueOverflowSaturatesTheFinalIntervalAndStaysClosed()
    {
        // long.MaxValue ticks per second puts the five-second window beyond Int64 range.
        // Only the final result saturates: the first event is admitted once, then the
        // gate stays closed for every later timestamp.
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), long.MaxValue);

        await Assert.That(throttle.ShouldLog(0, out _)).IsTrue();
        await Assert.That(throttle.ShouldLog(1_000_000_000_000, out _)).IsFalse();
        await Assert.That(throttle.ShouldLog(long.MaxValue - 1, out _)).IsFalse();
    }

    [Test]
    public async Task WindowSpanningRolloverPreservesTheRemainingInterval()
    {
        // A window opened one second before the counter wraps must stay closed for the
        // remaining four seconds after the wrap instead of reopening immediately.
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), Frequency);

        await Assert.That(throttle.ShouldLog(long.MaxValue - Frequency, out _)).IsTrue();
        // Just wrapped: only ~1s has elapsed of the five-second window.
        await Assert.That(throttle.ShouldLog(long.MinValue + 1, out _)).IsFalse();
        // Exactly the remaining four seconds later the boundary is reached.
        await Assert.That(throttle.ShouldLog(long.MinValue + 1 + 4 * Frequency, out var suppressed)).IsTrue();
        await Assert.That(suppressed).IsEqualTo(1);
    }

    [Test]
    public async Task GateReopensExactlyAtTheRolloverShiftedBoundary()
    {
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), Frequency);

        await Assert.That(throttle.ShouldLog(long.MaxValue - 1, out _)).IsTrue();
        await Assert.That(throttle.ShouldLog(long.MinValue + 1, out _)).IsFalse();
        await Assert.That(throttle.ShouldLog(long.MinValue + 1 + 5 * Frequency, out var suppressed)).IsTrue();
        await Assert.That(suppressed).IsEqualTo(1);
    }

    [Test]
    public async Task MaximumTimestampStaysClosedAfterTheFirstAdmission()
    {
        // When the provider reports long.MaxValue, the boundary saturates to the same
        // value. The gate must admit the first event once and then stay closed instead
        // of re-admitting every call at the terminal timestamp.
        var throttle = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), Frequency);

        await Assert.That(throttle.ShouldLog(long.MaxValue, out _)).IsTrue();
        await Assert.That(throttle.ShouldLog(long.MaxValue, out _)).IsFalse();
        await Assert.That(throttle.ShouldLog(long.MaxValue - 1, out _)).IsFalse();
    }

    [Test]
    public async Task InvalidIntervalsAreRejected()
    {
        await Assert.ThrowsAsync(() =>
        {
            _ = new FixedWindowLogThrottle(TimeSpan.FromSeconds(-1), Frequency);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync(() =>
        {
            _ = new FixedWindowLogThrottle(TimeSpan.FromSeconds(5), 0);
            return Task.CompletedTask;
        });
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
