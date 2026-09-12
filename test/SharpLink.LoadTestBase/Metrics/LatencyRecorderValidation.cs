using System;

namespace SharpLink.LoadTestBase;

public static class LatencyRecorderValidation
{
    public static void ValidateAgainstLegacy(
        in LatencyStatistics exact,
        LatencyHistogram legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        if (exact.Count != legacy.Count)
        {
            throw new InvalidOperationException(
                $"Validation-dual count mismatch: exact={exact.Count}, legacy={legacy.Count}.");
        }

        Validate("min", exact.MinUs, legacy.Min);
        Validate("max", exact.MaxUs, legacy.Max);
        Validate("P50", exact.P50Us, legacy.Percentile(50));
        Validate("P95", exact.P95Us, legacy.Percentile(95));
        Validate("P99", exact.P99Us, legacy.Percentile(99));
        Validate("P99.9", exact.P999Us, legacy.Percentile(99.9));
    }

    private static void Validate(string name, double exact, double approximate)
    {
        var tolerance = Math.Max(1d, Math.Abs(exact) * 0.005d);
        if (Math.Abs(exact - approximate) > tolerance)
        {
            throw new InvalidOperationException(
                $"Validation-dual {name} mismatch: exact={exact:F3}us, " +
                $"legacy={approximate:F3}us, tolerance={tolerance:F3}us.");
        }
    }
}
