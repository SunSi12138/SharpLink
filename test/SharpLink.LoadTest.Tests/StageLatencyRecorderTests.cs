using System.Diagnostics;
using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class StageLatencyRecorderTests
{
    [Test]
    public void EmptyRecorderHasExplicitZeroContract()
    {
        var statistics = new StageLatencyRecorder(2, 4).Complete();
        Check(statistics == LatencyStatistics.Empty, "empty statistics must be explicit zeros");
    }

    [Test]
    public void RecorderMergesWorkersAndUsesNearestRank()
    {
        var recorder = new StageLatencyRecorder(4, 1_000);
        for (var value = 1; value <= 1_000; value++)
            recorder.GetWorker((value - 1) % 4).RecordTicks(ToTicks(value));

        var statistics = recorder.Complete();
        Check(statistics.Count == 1_000, "merge count");
        CheckNear(statistics.MinUs, 1, 1);
        CheckNear(statistics.MaxUs, 1_000, 1);
        CheckNear(statistics.P50Us, 500, 1);
        CheckNear(statistics.P99Us, 990, 1);
        CheckNear(statistics.P999Us, 999, 1);
    }

    [Test]
    public void CapacityBoundaryFailsInsteadOfDroppingASample()
    {
        var worker = new WorkerLatencyRecorder(1);
        worker.RecordTicks(1);
        Check(worker.Count == 1, "exact capacity is accepted");
        try
        {
            worker.RecordTicks(2);
            throw new InvalidOperationException("capacity overflow was silently accepted");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("run is invalid", StringComparison.Ordinal))
        {
        }
        Check(worker.Count == 1, "overflow does not mutate count");
    }

    [Test]
    public void NegativeElapsedTicksInvalidateTheRun()
    {
        var worker = new WorkerLatencyRecorder(1);
        try
        {
            worker.RecordTicks(-1);
            throw new InvalidOperationException("negative ticks were accepted");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static long ToTicks(double microseconds)
        => (long)Math.Round(microseconds * Stopwatch.Frequency / 1_000_000d);

    private static void CheckNear(double actual, double expected, double tolerance)
        => Check(Math.Abs(actual - expected) <= tolerance, $"expected {expected} +/- {tolerance}, actual {actual}");

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
