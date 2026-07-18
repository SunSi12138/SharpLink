using System.Diagnostics.Metrics;
using System.Threading;

namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkTelemetryTests
{
    [Test]
    public void AbandonedAndLateResponseMetricsShouldExposeStableTags()
    {
        const string side = "telemetry-unit-test";
        var abandoned = 0L;
        var lateDropped = 0L;
        string? terminationReason = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name is
                "sharplink.calls.abandoned" or "sharplink.responses.late_dropped")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var matchingSide = false;
            string? measuredReason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && Equals(tag.Value, side))
                    matchingSide = true;
                else if (tag.Key == "rpc.sharplink.termination_reason")
                    measuredReason = tag.Value as string;
            }
            if (!matchingSide)
                return;
            if (instrument.Name == "sharplink.calls.abandoned")
            {
                Interlocked.Add(ref abandoned, measurement);
                terminationReason = measuredReason;
            }
            else if (instrument.Name == "sharplink.responses.late_dropped")
                Interlocked.Add(ref lateDropped, measurement);
        });
        listener.Start();

        SharpLinkTelemetry.RecordAbandonedCall(side, "deadline_exceeded");
        SharpLinkTelemetry.RecordLateResponseDropped(side);

        Ensure(Volatile.Read(ref abandoned) == 1, "abandoned measurement");
        Ensure(Volatile.Read(ref lateDropped) == 1, "late response measurement");
        Ensure(terminationReason == "deadline_exceeded", "termination reason tag");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
