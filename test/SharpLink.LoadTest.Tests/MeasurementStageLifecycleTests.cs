using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class MeasurementStageLifecycleTests
{
    [Test]
    public async Task MeasurementShouldStartOnlyAfterEveryWorkerIsReady()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 3);
        var first = lifecycle.ReadyAndWaitForStartAsync(0);
        var second = lifecycle.ReadyAndWaitForStartAsync(1);

        Ensure(!lifecycle.AllWorkersReady.IsCompleted,
            "the ready barrier must remain closed while any worker is missing");
        Ensure(!first.IsCompleted && !second.IsCompleted,
            "ready workers wait outside measurement at the synchronized start gate");
        var earlyStartFailure = CaptureFailure(() => lifecycle.StartMeasurement());
        Ensure(earlyStartFailure is InvalidOperationException &&
               earlyStartFailure.Message.Contains("every worker is ready", StringComparison.Ordinal),
            "timer start before the final ready signal is rejected");

        var third = lifecycle.ReadyAndWaitForStartAsync(2);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!first.IsCompleted && !second.IsCompleted && !third.IsCompleted,
            "all-ready notification alone does not release workers");

        var startedTimestamp = lifecycle.StartMeasurement();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(startedTimestamp > 0, "measurement start returns its explicit timestamp");
        Ensure(lifecycle.CanStartOperation, "workers can start operations only after timer publication");
    }

    [Test]
    public async Task MeasurementStopShouldPreventStartingAnotherOperation()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 1);
        var worker = lifecycle.ReadyAndWaitForStartAsync(0);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(lifecycle.CanStartOperation, "measurement initially accepts operations");

        var stoppedTimestamp = lifecycle.StopStartingNewOperations();

        Ensure(stoppedTimestamp > 0, "measurement stop returns its explicit timestamp");
        Ensure(!lifecycle.CanStartOperation,
            "the stop boundary is visible before a worker can begin its next loop iteration");
        var repeatedStopFailure = CaptureFailure(() => lifecycle.StopStartingNewOperations());
        Ensure(repeatedStopFailure is InvalidOperationException,
            "a repeated stop cannot create a second measurement boundary");
    }

    [Test]
    public async Task DrainShouldWaitForAndObserveInflightCompletion()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 1);
        var workerStarted = lifecycle.ReadyAndWaitForStartAsync(0);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await workerStarted.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StopStartingNewOperations();
        var releaseInflightOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new SharpLink.LoadTestBase.StageLatencyRecorder(
            workerCount: 1,
            maximumTotalSamples: 1,
            stopwatchFrequency: 1_000_000);
        var inflightCompletion = Task.Run(async () =>
        {
            await releaseInflightOperation.Task;
            recorder.GetWorker(0).RecordTicks(0, 42);
        });

        var drain = lifecycle.WaitForDrainAsync(
            inflightCompletion,
            TimeSpan.FromSeconds(2));
        Ensure(!drain.IsCompleted,
            "drain remains pending while an operation started before the stop boundary is in flight");

        releaseInflightOperation.SetResult();
        var drainDurationSeconds = await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(drainDurationSeconds >= 0,
            "the in-flight completion is observed and produces a separately reported drain duration");
        var statistics = recorder.Complete();
        Ensure(statistics.Count == 1 && statistics.P99Us == 42,
            "latency from the pre-deadline in-flight operation remains in the final formal sample set");
    }

    [Test]
    public async Task DrainTimeoutShouldMarkTheRunAsFailed()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 1);
        var workerStarted = lifecycle.ReadyAndWaitForStartAsync(0);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await workerStarted.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StopStartingNewOperations();
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var failure = await CaptureFailureAsync(lifecycle.WaitForDrainAsync(
            neverCompletes.Task,
            TimeSpan.FromMilliseconds(50)));

        Ensure(failure is TimeoutException,
            "a bounded drain timeout fails the run instead of ignoring an in-flight operation");
    }

    [Test]
    public async Task CompletedMeasurementShouldRejectRestart()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 1);
        var worker = lifecycle.ReadyAndWaitForStartAsync(0);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StopStartingNewOperations();

        var failure = CaptureFailure(() => lifecycle.StartMeasurement());

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("already started", StringComparison.Ordinal),
            "one lifecycle cannot publish a second measurement window after entering drain");
        Ensure(!lifecycle.CanStartOperation,
            "failed restart leaves new-operation admission closed");
    }

    [Test]
    public async Task DrainShouldRejectBeforeMeasurementStops()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 1);
        var worker = lifecycle.ReadyAndWaitForStartAsync(0);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await CaptureFailureAsync(lifecycle.WaitForDrainAsync(
            Task.CompletedTask,
            TimeSpan.FromSeconds(1)));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("before measurement has stopped", StringComparison.Ordinal),
            "drain cannot overlap the interval that still admits new operations");
        Ensure(lifecycle.CanStartOperation,
            "rejected early drain does not silently close or alter the measurement window");
        lifecycle.StopStartingNewOperations();
    }

    [Test]
    public async Task ReadyBarrierShouldRejectDuplicateLogicalWorkerSignals()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 2);
        _ = lifecycle.ReadyAndWaitForStartAsync(0);

        var failure = await CaptureFailureAsync(lifecycle.ReadyAndWaitForStartAsync(0));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("more than once", StringComparison.Ordinal),
            "one worker cannot satisfy another worker's ready slot");
        Ensure(!lifecycle.AllWorkersReady.IsCompleted,
            "duplicate readiness leaves the barrier waiting for the missing logical worker");
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

    private static async Task<Exception> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
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
