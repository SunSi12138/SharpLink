namespace SharpLink.LoadTest.Tests;

public class LatencyHistogramTests
{
    [Test]
    public void EmptyHistogramShouldUseExplicitZeroStatisticsContract()
    {
        var histogram = new SharpLink.LoadTestBase.LatencyHistogram(bucketCount: 16);

        Ensure(histogram.Count == 0, "an empty histogram must report zero samples");
        Ensure(histogram.Min == 0, "empty minimum contract");
        Ensure(histogram.Max == 0, "empty maximum contract");
        Ensure(histogram.Average == 0, "empty average contract");
        Ensure(histogram.Percentile(50) == 0, "empty P50 contract");
        Ensure(histogram.Percentile(99) == 0, "empty P99 contract");
        Ensure(histogram.Percentile(99.9) == 0, "empty P99.9 contract");
    }

    [Test]
    public void SingleSampleShouldRoundOnceAndPopulateEveryStatistic()
    {
        var histogram = new SharpLink.LoadTestBase.LatencyHistogram(bucketCount: 1_000);

        histogram.Record(123.4);

        Ensure(histogram.Count == 1, "single sample count");
        Ensure(histogram.Min == 123, "single sample minimum uses the legacy rounding rule");
        Ensure(histogram.Max == 123, "single sample maximum uses the legacy rounding rule");
        Ensure(histogram.Average == 123, "single sample average uses the legacy rounding rule");
        Ensure(histogram.Percentile(50) == 123, "single sample P50");
        Ensure(histogram.Percentile(99) == 123, "single sample P99");
        Ensure(histogram.Percentile(99.9) == 123, "single sample P99.9");
    }

    [Test]
    public void KnownDistributionShouldUseNearestRankForEveryPercentile()
    {
        var histogram = new SharpLink.LoadTestBase.LatencyHistogram(bucketCount: 1_001);
        for (var microseconds = 1; microseconds <= 1_000; microseconds++)
            histogram.Record(microseconds);

        Ensure(histogram.Count == 1_000, "known distribution count");
        Ensure(histogram.Percentile(50) == NearestRank(1_000, 50), "nearest-rank P50");
        Ensure(histogram.Percentile(95) == NearestRank(1_000, 95), "nearest-rank P95");
        Ensure(histogram.Percentile(99) == NearestRank(1_000, 99), "nearest-rank P99");
        Ensure(histogram.Percentile(99.9) == NearestRank(1_000, 99.9), "nearest-rank P99.9");
    }

    [Test]
    public void ExtremeTailDistributionShouldPreserveTailQuantilesAndMaximum()
    {
        var histogram = new SharpLink.LoadTestBase.LatencyHistogram(bucketCount: 100_001);
        RecordRepeated(histogram, 10, 99_900);
        RecordRepeated(histogram, 100, 90);
        RecordRepeated(histogram, 1_000, 9);
        RecordRepeated(histogram, 100_000, 1);

        Ensure(histogram.Count == 100_000, "extreme distribution count");
        Ensure(histogram.Percentile(50) == 10, "extreme distribution P50");
        Ensure(histogram.Percentile(99) == 10, "extreme distribution P99");
        Ensure(histogram.Percentile(99.9) == 10,
            "nearest-rank P99.9 lands on the final 10us sample");
        Ensure(histogram.Max == 100_000, "the isolated 100ms tail remains visible as maximum");
    }

    [Test]
    public async Task ConcurrentRecordingShouldPreserveEveryLegacySample()
    {
        const int workerCount = 8;
        const int samplesPerWorker = 2_000;
        var histogram = new SharpLink.LoadTestBase.LatencyHistogram(bucketCount: 256);
        var workers = new Task[workerCount];

        for (var worker = 0; worker < workers.Length; worker++)
        {
            var sample = 100 + worker;
            workers[worker] = Task.Run(() => RecordRepeated(histogram, sample, samplesPerWorker));
        }

        await Task.WhenAll(workers);

        Ensure(histogram.Count == workerCount * samplesPerWorker,
            "legacy atomic recorder must not lose concurrent samples");
        Ensure(histogram.Min == 100, "concurrent minimum");
        Ensure(histogram.Max == 107, "concurrent maximum");
        Ensure(histogram.Average == 103.5, "concurrent average");
        Ensure(histogram.Percentile(50) == 103, "concurrent nearest-rank P50");
        Ensure(histogram.Percentile(99) == 107, "concurrent nearest-rank P99");
    }

    [Test]
    public void OutOfRangeSampleShouldExposeLegacyPercentileClamp()
    {
        var histogram = new SharpLink.LoadTestBase.LatencyHistogram(bucketCount: 10);

        histogram.Record(30);

        Ensure(histogram.Count == 1, "clamped sample is still counted");
        Ensure(histogram.Min == 30, "legacy minimum retains the unclamped rounded value");
        Ensure(histogram.Max == 30, "legacy maximum retains the unclamped rounded value");
        Ensure(histogram.Average == 30, "legacy average retains the unclamped rounded value");
        Ensure(histogram.Percentile(50) == 9, "legacy percentile silently clamps to the final bucket");
        Ensure(histogram.Percentile(99.9) == 9, "all legacy percentiles expose the same clamp");
    }

    private static long NearestRank(int count, double percentile)
        => decimal.ToInt64(decimal.Ceiling(count * ((decimal)percentile / 100m)));

    private static void RecordRepeated(
        SharpLink.LoadTestBase.LatencyHistogram histogram,
        double microseconds,
        int count)
    {
        for (var index = 0; index < count; index++)
            histogram.Record(microseconds);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
