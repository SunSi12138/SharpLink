using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class LatencyRecorderCapacitySkewTests
{
    private const long TestFrequency = 1_000_000;
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(5);

    [Test]
    public void SingleHotWorkerShouldUseTheStageBudgetWhile511WorkersRemainIdle()
    {
        const int capacity = 1_024;
        var recorder = new StageLatencyRecorder(512, capacity, TestFrequency);
        var hotWorker = recorder.GetWorker(15);
        var expected = new List<long>(capacity);
        for (var sample = 1; sample <= capacity; sample++)
        {
            hotWorker.RecordTicks(15, sample);
            expected.Add(sample);
        }

        Ensure(hotWorker.Count == capacity,
            "one hot worker must retain the entire stage budget, not only its initial two slots");
        for (var worker = 0; worker < recorder.WorkerCount; worker++)
        {
            if (worker != 15)
                Ensure(recorder.GetWorker(worker).Count == 0,
                    "idle logical workers cannot acquire fabricated samples during borrowing");
        }
        AssertExactStatistics(recorder, expected);
    }

    [Test]
    public void SkewedWorkersShouldPreserveEarlierPrefixesAndExactNearestRankWhenDonorsResume()
    {
        var recorder = new StageLatencyRecorder(6, 64, TestFrequency);
        var expected = new List<long>(64);
        Record(recorder, expected, 0, 5, 60);
        Record(recorder, expected, 1, 1, 90);
        Record(recorder, expected, 2, 1_000_000);
        Record(recorder, expected, 4, 44);
        Record(recorder, expected, 5, 2, 55);

        for (var sample = 0; sample < 41; sample++)
            Record(recorder, expected, 3, 10 + sample * 37 % 83);
        // A worker with an existing prefix writes again after the hot worker has
        // consumed spare stage capacity. Neither earlier prefix may be replaced.
        for (var sample = 0; sample < 15; sample++)
            Record(recorder, expected, 1, 500 - sample * 11);

        Ensure(recorder.GetWorker(0).Count == 2 && recorder.GetWorker(1).Count == 17 &&
               recorder.GetWorker(2).Count == 1 && recorder.GetWorker(3).Count == 41 &&
               recorder.GetWorker(4).Count == 1 && recorder.GetWorker(5).Count == 2,
            "skew and resumed writers retain every real logical worker's count");
        AssertExactStatistics(recorder, expected);
    }

    [Test]
    public void FullStageShouldRejectEveryWorkersNextSampleWithoutReplacingAnyAcceptedPrefix()
    {
        var recorder = new StageLatencyRecorder(4, 16, TestFrequency);
        var expected = new List<long>(16);
        for (var worker = 0; worker < 4; worker++)
            Record(recorder, expected, worker, 10 + worker, 20 + worker);
        for (var sample = 0; sample < 8; sample++)
            Record(recorder, expected, 3, 100 + sample);
        AssertExactStatistics(recorder, expected);
        var beforeOverflow = recorder.Complete();
        var counts = new int[recorder.WorkerCount];
        for (var worker = 0; worker < counts.Length; worker++)
            counts[worker] = recorder.GetWorker(worker).Count;

        for (var worker = 0; worker < recorder.WorkerCount; worker++)
        {
            var logicalWorker = worker;
            var failure = CaptureFailure(() => recorder.GetWorker(logicalWorker)
                .RecordTicks(logicalWorker, 9_999_999));
            Ensure(failure is LatencySampleCapacityExceededException &&
                   failure.Message.Contains("run is invalid", StringComparison.Ordinal),
                "only real total exhaustion rejects N+1 and explicitly invalidates the run");
            for (var owner = 0; owner < counts.Length; owner++)
                Ensure(recorder.GetWorker(owner).Count == counts[owner],
                    "an overflow attempt cannot consume another worker's accepted prefix");
            Ensure(recorder.Complete() == beforeOverflow,
                "all M accepted samples retain their exact count, extrema, mean and percentiles after rejection");
        }
        AssertExactStatistics(recorder, expected);
    }

    [Test]
    public void WrongOwnerAndNegativeTicksShouldNotConsumeCapacityBeforeOrAfterBorrowing()
    {
        var recorder = new StageLatencyRecorder(4, 32, TestFrequency);
        var hotWorker = recorder.GetWorker(2);
        var expected = new List<long>(32);
        for (var sample = 1; sample <= 7; sample++)
            Record(recorder, expected, 2, sample);
        RejectInvalidInputs(hotWorker, expectedCount: 7);

        for (var sample = 8; sample <= 20; sample++)
            Record(recorder, expected, 2, sample);
        RejectInvalidInputs(hotWorker, expectedCount: 20);
        for (var sample = 21; sample <= 32; sample++)
            Record(recorder, expected, 0, sample);

        Ensure(hotWorker.Count == 20 && recorder.GetWorker(0).Count == 12 &&
               recorder.GetWorker(1).Count == 0 && recorder.GetWorker(3).Count == 0,
            "rejected inputs do not spend any stage budget or alter another logical worker");
        AssertExactStatistics(recorder, expected);
        Ensure(CaptureFailure(() => hotWorker.RecordTicks(2, 33)) is LatencySampleCapacityExceededException,
            "all 32 real samples, rather than rejected inputs, consume the strict stage budget");
    }

    [Test]
    public async Task ConcurrentSkewedLogicalWritersShouldKeepAllSamplesAtTheExactStageLimit()
    {
        int[] sampleCounts = [3_000, 300, 200, 190, 150, 100, 80, 76];
        const int capacity = 4_096;
        var recorder = new StageLatencyRecorder(sampleCounts.Length, capacity, TestFrequency);
        var expected = new List<long>(capacity);
        var workers = new Task[sampleCounts.Length];
        using var stop = new CancellationTokenSource();
        using var start = new ManualResetEventSlim();
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var offset = 0;
        for (var worker = 0; worker < workers.Length; worker++)
        {
            var logicalWorker = worker;
            var firstSample = offset + 1;
            var count = sampleCounts[worker];
            offset += count;
            for (var sample = 0; sample < count; sample++)
                expected.Add(firstSample + sample);
            // One dedicated writer owns each logical recorder. The start gate
            // creates actual concurrent writes without sharing a worker buffer.
            workers[worker] = Task.Factory.StartNew(() =>
            {
                if (Interlocked.Increment(ref readyCount) == sampleCounts.Length)
                    allReady.TrySetResult();
                start.Wait(stop.Token);
                var ownedRecorder = recorder.GetWorker(logicalWorker);
                for (var sample = count - 1; sample >= 0; sample--)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    ownedRecorder.RecordTicks(logicalWorker, firstSample + sample);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        var allWorkers = Task.WhenAll(workers);
        try
        {
            await allReady.Task.WaitAsync(TestGuard);
            start.Set();
            await allWorkers.WaitAsync(TestGuard);
            for (var worker = 0; worker < workers.Length; worker++)
                Ensure(recorder.GetWorker(worker).Count == sampleCounts[worker],
                    "concurrent region borrowing preserves each logical writer's entire sample prefix");
            AssertExactStatistics(recorder, expected);
            Ensure(CaptureFailure(() => recorder.GetWorker(7).RecordTicks(7, 4_097))
                    is LatencySampleCapacityExceededException,
                "concurrent completion does not permit a sample beyond the stage limit");
            AssertExactStatistics(recorder, expected);
        }
        finally
        {
            stop.Cancel();
            start.Set();
            try
            {
                await allWorkers.WaitAsync(TestGuard);
            }
            catch (Exception) when (allWorkers.IsCompleted)
            {
                // Preserve the original assertion/worker failure while observing
                // all writer exceptions and joining every released start waiter.
                _ = allWorkers.Exception;
            }
        }
    }

    private static void RejectInvalidInputs(WorkerLatencyRecorder worker, int expectedCount)
    {
        var wrongOwner = CaptureFailure(() => worker.RecordTicks(3, 10));
        Ensure(wrongOwner is InvalidOperationException &&
               wrongOwner is not LatencySampleCapacityExceededException &&
               wrongOwner.Message.Contains("logical worker 3", StringComparison.Ordinal),
            "wrong logical owner is rejected before sample admission");
        Ensure(CaptureFailure(() => worker.RecordTicks(2, -1)) is ArgumentOutOfRangeException
        { ParamName: "elapsedTicks" },
            "negative elapsed ticks are rejected before sample admission");
        Ensure(worker.Count == expectedCount, "rejected inputs cannot increment the owned sample count");
    }

    private static void Record(StageLatencyRecorder recorder, List<long> expected,
        int logicalWorker, params long[] samples)
    {
        var ownedRecorder = recorder.GetWorker(logicalWorker);
        foreach (var sample in samples)
        {
            ownedRecorder.RecordTicks(logicalWorker, sample);
            expected.Add(sample);
        }
    }

    private static void AssertExactStatistics(StageLatencyRecorder recorder, List<long> samples)
    {
        var expected = samples.ToArray();
        Array.Sort(expected);
        var sum = 0d;
        foreach (var sample in expected)
            sum += sample;
        var statistics = recorder.Complete();
        Ensure(statistics.Count == expected.Length && expected.Length == recorder.MaximumTotalSamples,
            "the exact M real samples survive with no truncation, replacement or manufactured observations");
        Ensure(statistics.MinUs == expected[0] && statistics.MaxUs == expected[^1] &&
               statistics.AverageUs == sum / expected.Length,
            "borrowing preserves both early prefixes and all later samples in aggregate statistics");
        Ensure(statistics.P50Us == RankValue(expected, 50) &&
               statistics.P95Us == RankValue(expected, 95) &&
               statistics.P99Us == RankValue(expected, 99) &&
               statistics.P999Us == RankValue(expected, 99.9),
            "nearest-rank values come from the whole exact sample set, including the rare tail");
    }

    private static long RankValue(long[] sortedSamples, double percentile)
    {
        var rank = decimal.ToInt32(decimal.Ceiling(sortedSamples.Length * ((decimal)percentile / 100m)));
        return sortedSamples[rank - 1];
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
        throw new Exception("Expected recording to fail.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
