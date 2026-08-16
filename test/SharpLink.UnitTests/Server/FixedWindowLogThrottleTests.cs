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
