using System;

namespace SharpLink.LoadTestBase;

public static class PerformanceReportCompatibility
{
    public const int CurrentSchemaVersion = 2;

    public static void EnsureComparable(
        int baselineSchemaVersion,
        string baselineRecorderVersion,
        int candidateSchemaVersion,
        string candidateRecorderVersion)
    {
        if (baselineSchemaVersion != candidateSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Performance report schema mismatch: baseline={baselineSchemaVersion}, candidate={candidateSchemaVersion}.");
        }

        if (!string.Equals(
                baselineRecorderVersion,
                candidateRecorderVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Performance recorder mismatch: baseline={baselineRecorderVersion}, candidate={candidateRecorderVersion}.");
        }
    }
}
