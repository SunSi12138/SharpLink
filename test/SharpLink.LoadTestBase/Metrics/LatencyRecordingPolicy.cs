using System;

namespace SharpLink.LoadTestBase;

public static class LatencyRecordingPolicy
{
    public static bool CreatesFormalRecorder(LatencyRecordingMode mode)
        => mode is LatencyRecordingMode.Formal or LatencyRecordingMode.ValidationDual;

    public static bool CreatesDiagnosticRecorder(LatencyRecordingMode mode)
        => mode is LatencyRecordingMode.Diagnostic or LatencyRecordingMode.ValidationDual;

    public static bool StartsRealtimeReporter(LatencyRecordingMode mode)
        => mode == LatencyRecordingMode.Diagnostic;

    public static bool IsFormalComparable(LatencyRecordingMode mode)
        => mode == LatencyRecordingMode.Formal;

    public static double CalculateThroughput(long completedOperations, double measurementDurationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(measurementDurationSeconds);
        return completedOperations / measurementDurationSeconds;
    }
}
