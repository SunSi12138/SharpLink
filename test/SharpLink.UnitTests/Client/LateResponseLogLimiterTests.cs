using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class LateResponseLogLimiterTests
{
    [Test]
    public void LimiterShouldLogOncePerConnectionWindowAndReportSuppressedCount()
    {
        var firstConnection = new LateResponseLogLimiter();
        var secondConnection = new LateResponseLogLimiter();
        const long started = 1;

        Ensure(firstConnection.ShouldLog(started, out var firstSuppressed),
            "first response should log immediately");
        Ensure(firstSuppressed == 0, "first warning suppressed count");
        Ensure(!firstConnection.ShouldLog(started + 1, out _), "second response should be suppressed");
        Ensure(!firstConnection.ShouldLog(started + 2, out _), "third response should be suppressed");

        Ensure(secondConnection.ShouldLog(started + 2, out var secondSuppressed),
            "a different connection must have an independent window");
        Ensure(secondSuppressed == 0, "second connection suppressed count");

        Ensure(firstConnection.ShouldLog(
                started + LateResponseLogLimiter.IntervalTimestampTicks,
                out var suppressed),
            "response at the next window should log");
        Ensure(suppressed == 2, "warning should report responses suppressed in the prior window");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
