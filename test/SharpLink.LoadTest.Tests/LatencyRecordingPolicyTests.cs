using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class LatencyRecordingPolicyTests
{
    [Test]
    public void FormalPolicyShouldCreateOnlyExactRecorderAndRemainComparable()
    {
        Ensure(LatencyRecordingPolicy.CreatesFormalRecorder(LatencyRecordingMode.Formal),
            "formal mode creates the worker-local exact recorder");
        Ensure(!LatencyRecordingPolicy.CreatesDiagnosticRecorder(LatencyRecordingMode.Formal),
            "formal mode does not double-write a diagnostic recorder");
        Ensure(!LatencyRecordingPolicy.StartsRealtimeReporter(LatencyRecordingMode.Formal),
            "formal mode never starts the realtime reporter");
        Ensure(LatencyRecordingPolicy.IsFormalComparable(LatencyRecordingMode.Formal),
            "formal mode alone produces decision-quality comparable evidence");
    }

    [Test]
    public void DiagnosticPolicyShouldStartRealtimeAndRemainNonComparable()
    {
        Ensure(!LatencyRecordingPolicy.CreatesFormalRecorder(LatencyRecordingMode.Diagnostic),
            "diagnostic mode does not allocate the formal raw sample buffer");
        Ensure(LatencyRecordingPolicy.CreatesDiagnosticRecorder(LatencyRecordingMode.Diagnostic),
            "diagnostic mode creates its approximate recorder");
        Ensure(LatencyRecordingPolicy.StartsRealtimeReporter(LatencyRecordingMode.Diagnostic),
            "diagnostic mode explicitly enables realtime output");
        Ensure(!LatencyRecordingPolicy.IsFormalComparable(LatencyRecordingMode.Diagnostic),
            "diagnostic evidence cannot enter formal base/head gates");
    }

    [Test]
    public void OffPolicyShouldCreateNoRecorderOrReporterAndRemainNonComparable()
    {
        Ensure(!LatencyRecordingPolicy.CreatesFormalRecorder(LatencyRecordingMode.Off),
            "off mode allocates no raw latency sample buffer");
        Ensure(!LatencyRecordingPolicy.CreatesDiagnosticRecorder(LatencyRecordingMode.Off),
            "off mode allocates no diagnostic histogram");
        Ensure(!LatencyRecordingPolicy.StartsRealtimeReporter(LatencyRecordingMode.Off),
            "off mode starts no latency reporter");
        Ensure(!LatencyRecordingPolicy.IsFormalComparable(LatencyRecordingMode.Off),
            "recording-off is an overhead control rather than formal latency evidence");
    }

    [Test]
    public void ValidationDualPolicyShouldCreateBothRecordersWithoutRealtimeOrComparability()
    {
        Ensure(LatencyRecordingPolicy.CreatesFormalRecorder(LatencyRecordingMode.ValidationDual),
            "validation run needs exact samples");
        Ensure(LatencyRecordingPolicy.CreatesDiagnosticRecorder(LatencyRecordingMode.ValidationDual),
            "validation run needs a legacy comparison path");
        Ensure(!LatencyRecordingPolicy.StartsRealtimeReporter(LatencyRecordingMode.ValidationDual),
            "dual-path validation must not add realtime reporter interference");
        Ensure(!LatencyRecordingPolicy.IsFormalComparable(LatencyRecordingMode.ValidationDual),
            "dual-write validation is never formal performance evidence");
    }

    [Test]
    public void ThroughputShouldUseOnlyMeasurementDuration()
    {
        const long operationsCompletedAfterDrain = 1_000;
        const double measurementDurationSeconds = 2;
        const double separatelyReportedDrainDurationSeconds = 8;

        var qps = LatencyRecordingPolicy.CalculateThroughput(
            operationsCompletedAfterDrain,
            measurementDurationSeconds);

        Ensure(qps == 500, "throughput denominator is the two-second measurement window");
        Ensure(qps != operationsCompletedAfterDrain /
               (measurementDurationSeconds + separatelyReportedDrainDurationSeconds),
            "the separately reported drain duration cannot dilute steady-state throughput");
    }

    [Test]
    public void ThroughputShouldRejectInvalidCountsAndMeasurementDuration()
    {
        var countFailure = CaptureFailure(() =>
            LatencyRecordingPolicy.CalculateThroughput(-1, 1));
        Ensure(countFailure is ArgumentOutOfRangeException { ParamName: "completedOperations" },
            "a negative completed-operation count cannot enter evidence");

        var durationFailure = CaptureFailure(() =>
            LatencyRecordingPolicy.CalculateThroughput(1, 0));
        Ensure(durationFailure is ArgumentOutOfRangeException { ParamName: "measurementDurationSeconds" },
            "zero measurement duration cannot be replaced by drain or an arbitrary denominator");
    }

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new Exception("Expected the operation to fail.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
