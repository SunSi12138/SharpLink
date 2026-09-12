using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class LatencyRecorderTests
{
    private const long TestFrequency = 1_000_000;

    [Test]
    public void RawRecorderShouldReportExplicitEmptyStatistics()
    {
        var recorder = new StageLatencyRecorder(2, 4, TestFrequency);

        var statistics = recorder.Complete();

        Ensure(statistics == LatencyStatistics.Empty, "zero samples use the explicit empty statistics value");
        Ensure(statistics.Count == 0, "empty merged sample count");
        Ensure(statistics.MinUs == 0 && statistics.MaxUs == 0 && statistics.AverageUs == 0,
            "empty aggregate statistics contract");
        Ensure(statistics.P50Us == 0 && statistics.P95Us == 0 &&
               statistics.P99Us == 0 && statistics.P999Us == 0,
            "empty percentile contract");
    }

    [Test]
    public void RawRecorderShouldPreserveSingleTickSample()
    {
        var recorder = new StageLatencyRecorder(1, 1, TestFrequency);
        recorder.GetWorker(0).RecordTicks(0, 123);

        var statistics = recorder.Complete();

        Ensure(statistics.Count == 1, "single raw sample count");
        Ensure(statistics.MinUs == 123 && statistics.MaxUs == 123 && statistics.AverageUs == 123,
            "single raw sample aggregate statistics");
        Ensure(statistics.P50Us == 123 && statistics.P95Us == 123 &&
               statistics.P99Us == 123 && statistics.P999Us == 123,
            "every nearest-rank percentile selects the single raw sample");
    }

    [Test]
    public void RawRecorderShouldMergeAllWorkersExactly()
    {
        var recorder = new StageLatencyRecorder(3, 7, TestFrequency);
        Record(recorder.GetWorker(0), 0, 70, 10, 40);
        Record(recorder.GetWorker(1), 1, 60, 20);
        Record(recorder.GetWorker(2), 2, 50, 30);

        var statistics = recorder.Complete();

        Ensure(recorder.WorkerCount == 3, "worker count remains explicit");
        Ensure(recorder.MaximumTotalSamples == 7, "total bound remains explicit");
        Ensure(statistics.Count == 7, "merge includes every worker's complete recorded prefix");
        Ensure(statistics.MinUs == 10 && statistics.MaxUs == 70,
            "merge retains global min and max rather than one worker's extrema");
        Ensure(statistics.AverageUs == 40, "merge average uses all seven exact samples");
    }

    [Test]
    public void WorkerRecorderShouldAcceptExactlyItsCapacity()
    {
        var recorder = new StageLatencyRecorder(2, 5, TestFrequency);
        var first = recorder.GetWorker(0);
        var second = recorder.GetWorker(1);

        Record(first, 0, 1, 2, 3);
        Record(second, 1, 4, 5);

        Ensure(first.Capacity == 3 && first.Count == 3,
            "remainder capacity is deterministically assigned and fully usable");
        Ensure(second.Capacity == 2 && second.Count == 2,
            "base per-worker capacity is fully usable");
        Ensure(recorder.Complete().Count == recorder.MaximumTotalSamples,
            "the exact total boundary merges successfully");
    }

    [Test]
    public void WorkerRecorderShouldFailTheNextSampleWithoutDroppingExistingSamples()
    {
        var recorder = new StageLatencyRecorder(1, 2, TestFrequency);
        var worker = recorder.GetWorker(0);
        Record(worker, 0, 11, 22);

        var failure = CaptureFailure(() => worker.RecordTicks(0, 33));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("run is invalid", StringComparison.Ordinal),
            "sample N+1 explicitly invalidates the run");
        Ensure(worker.Count == 2, "overflow does not increment or silently replace the bounded prefix");
        var statistics = recorder.Complete();
        Ensure(statistics.Count == 2 && statistics.MinUs == 11 && statistics.MaxUs == 22,
            "the accepted prefix remains exact after overflow rejection");
    }

    [Test]
    public void RawRecorderShouldUseNearestRankForSortedSamples()
    {
        var recorder = new StageLatencyRecorder(1, 1_000, TestFrequency);
        var worker = recorder.GetWorker(0);
        for (var sample = 1_000; sample >= 1; sample--)
            worker.RecordTicks(0, sample);

        var statistics = recorder.Complete();

        Ensure(statistics.P50Us == NearestRank(1_000, 50), "raw nearest-rank P50");
        Ensure(statistics.P95Us == NearestRank(1_000, 95), "raw nearest-rank P95");
        Ensure(statistics.P99Us == NearestRank(1_000, 99), "raw nearest-rank P99");
        Ensure(statistics.P999Us == NearestRank(1_000, 99.9), "raw nearest-rank P99.9");
    }

    [Test]
    public void RawRecorderShouldCalculateMinMaxAndAverageFromRealSamples()
    {
        var recorder = new StageLatencyRecorder(2, 4, TestFrequency);
        Record(recorder.GetWorker(0), 0, 1, 2);
        Record(recorder.GetWorker(1), 1, 8, 10);

        var statistics = recorder.Complete();

        Ensure(statistics.MinUs == 1, "exact raw minimum");
        Ensure(statistics.MaxUs == 10, "exact raw maximum");
        Ensure(Math.Abs(statistics.AverageUs - 5.25) < 1e-12, "raw average floating-point tolerance");
    }

    [Test]
    public void RawRecorderShouldConvertTicksUsingConfiguredFrequency()
    {
        var recorder = new StageLatencyRecorder(1, 1, stopwatchFrequency: 10_000_000);

        Ensure(recorder.StopwatchFrequency == 10_000_000, "configured frequency is retained for evidence");
        Ensure(Math.Abs(recorder.TicksToMicroseconds(10) - 1d) < 1e-12,
            "ten ticks at 10MHz convert to one microsecond");
    }

    [Test]
    public void WorkerRecorderShouldRejectRecordingFromTheWrongLogicalWorker()
    {
        var recorder = new StageLatencyRecorder(2, 2, TestFrequency);
        var workerZero = recorder.GetWorker(0);

        var failure = CaptureFailure(() => workerZero.RecordTicks(1, 10));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("logical worker 1", StringComparison.Ordinal),
            "a deterministic logical-owner mismatch fails immediately");
        Ensure(workerZero.Count == 0 && recorder.Complete().Count == 0,
            "wrong-worker rejection cannot mutate the owned buffer");
    }

    [Test]
    public void ValidationDualShouldRejectARareTailThatLegacyHistogramClamps()
    {
        const int sampleCount = 1_000;
        var exact = new StageLatencyRecorder(1, sampleCount, TestFrequency);
        var legacy = new LatencyHistogram();
        var worker = exact.GetWorker(0);
        for (var sample = 0; sample < sampleCount - 2; sample++)
        {
            worker.RecordTicks(0, 10);
            legacy.Record(10);
        }
        for (var sample = 0; sample < 2; sample++)
        {
            worker.RecordTicks(0, 3_000_000);
            legacy.Record(3_000_000);
        }

        var failure = CaptureFailure(() =>
            LatencyRecorderValidation.ValidateAgainstLegacy(exact.Complete(), legacy));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("P99.9 mismatch", StringComparison.Ordinal),
            "a rare tail selected by P99.9 above the legacy bucket range must invalidate dual validation");
    }

    [Test]
    public void StageRecorderShouldRejectInvalidConstructionBounds()
    {
        var noWorkerFailure = CaptureFailure(() =>
            new StageLatencyRecorder(0, 1, TestFrequency));
        Ensure(noWorkerFailure is ArgumentOutOfRangeException { ParamName: "workerCount" },
            "a stage must have at least one logical worker");

        var zeroCapacityWorkerFailure = CaptureFailure(() =>
            new StageLatencyRecorder(2, 1, TestFrequency));
        Ensure(zeroCapacityWorkerFailure is ArgumentOutOfRangeException
        {
            ParamName: "maximumTotalSamples"
        },
            "the hard bound must provide every worker at least one slot");

        var frequencyFailure = CaptureFailure(() =>
            new StageLatencyRecorder(1, 1, stopwatchFrequency: -1));
        Ensure(frequencyFailure is ArgumentOutOfRangeException,
            "a non-positive conversion frequency cannot produce formal statistics");
    }

    [Test]
    public void RawRecorderShouldRejectNegativeTicksWithoutMutation()
    {
        var recorder = new StageLatencyRecorder(1, 1, TestFrequency);
        var worker = recorder.GetWorker(0);

        var recordFailure = CaptureFailure(() => worker.RecordTicks(0, -1));
        Ensure(recordFailure is ArgumentOutOfRangeException { ParamName: "elapsedTicks" },
            "negative elapsed ticks invalidate a sample before recording");
        Ensure(worker.Count == 0 && recorder.Complete().Count == 0,
            "a rejected negative duration cannot contaminate formal statistics");

        var conversionFailure = CaptureFailure(() => recorder.TicksToMicroseconds(-1));
        Ensure(conversionFailure is ArgumentOutOfRangeException { ParamName: "ticks" },
            "negative ticks are also rejected at the deterministic conversion boundary");
    }

    private static long NearestRank(int count, double percentile)
        => decimal.ToInt64(decimal.Ceiling(count * ((decimal)percentile / 100m)));

    private static void Record(
        WorkerLatencyRecorder recorder,
        int logicalWorkerIndex,
        params long[] samples)
    {
        foreach (var sample in samples)
            recorder.RecordTicks(logicalWorkerIndex, sample);
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
