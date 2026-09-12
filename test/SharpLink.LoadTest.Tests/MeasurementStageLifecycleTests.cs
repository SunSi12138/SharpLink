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
    public async Task OperationAdmissionShouldBeAtomicWithTheStopBoundary()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 1);
        var worker = lifecycle.ReadyAndWaitForStartAsync(0);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));

        using var operationFactoryEntered = new ManualResetEventSlim();
        using var releaseOperationFactory = new ManualResetEventSlim();
        var admission = Task.Run(() =>
        {
            var admitted = lifecycle.TryBeginOperationStart(0, out var admission);
            using (admission)
            {
                operationFactoryEntered.Set();
                if (!releaseOperationFactory.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("The test did not release the operation start.");
            }
            return admitted;
        });
        Ensure(operationFactoryEntered.Wait(TimeSpan.FromSeconds(2)),
            "the admitted operation factory starts while holding the lifecycle boundary");

        var stopAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = Task.Run(() =>
        {
            stopAttempted.SetResult();
            return lifecycle.StopStartingNewOperations();
        });
        await stopAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        Ensure(!stop.IsCompleted,
            "stop cannot publish its timestamp while an admitted operation is being invoked");

        releaseOperationFactory.Set();
        var admittedOperation = await admission.WaitAsync(TimeSpan.FromSeconds(2));
        var stoppedTimestamp = await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admittedOperation && stoppedTimestamp > 0,
            "the already-admitted invocation completes before the stop boundary is published");

        var invokedAfterStop = false;
        var admittedAfterStop = lifecycle.TryBeginOperationStart(0, out var rejectedAdmission);
        if (admittedAfterStop)
        {
            using (rejectedAdmission)
                invokedAfterStop = true;
        }
        Ensure(!admittedAfterStop && !invokedAfterStop,
            "no operation factory can run after the stop boundary");
    }

    [Test]
    public async Task IndependentWorkerAdmissionSlotsShouldNotSerializeOperationFactories()
    {
        var lifecycle = new MeasurementStageLifecycle(workerCount: 2);
        var firstWorker = lifecycle.ReadyAndWaitForStartAsync(0);
        var secondWorker = lifecycle.ReadyAndWaitForStartAsync(1);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await Task.WhenAll(firstWorker, secondWorker).WaitAsync(TimeSpan.FromSeconds(2));

        using var firstFactoryEntered = new ManualResetEventSlim();
        using var releaseFirstFactory = new ManualResetEventSlim();
        var firstAdmission = Task.Run(() =>
        {
            var admitted = lifecycle.TryBeginOperationStart(0, out var admission);
            using (admission)
            {
                firstFactoryEntered.Set();
                if (!releaseFirstFactory.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("The test did not release the first operation start.");
            }
            return admitted;
        });
        Ensure(firstFactoryEntered.Wait(TimeSpan.FromSeconds(2)),
            "the first worker is inside its operation factory");

        var secondFactoryInvoked = false;
        var secondAdmission = lifecycle.TryBeginOperationStart(1, out var secondAdmissionScope);
        if (secondAdmission)
        {
            using (secondAdmissionScope)
                secondFactoryInvoked = true;
        }
        Ensure(secondAdmission && secondFactoryInvoked,
            "a different worker can initiate its RPC without waiting for the first worker");

        releaseFirstFactory.Set();
        Ensure(await firstAdmission.WaitAsync(TimeSpan.FromSeconds(2)),
            "the first worker remains admitted after its factory is released");
        lifecycle.StopStartingNewOperations();
    }

    [Test]
    public async Task OperationAdmissionSlotsShouldBePaddedAndAllocationFree()
    {
        Ensure(MeasurementStageLifecycle.OperationAdmissionSlotStrideBytes == 128,
            "worker-owned admission flags are separated by two conventional cache lines");

        var lifecycle = new MeasurementStageLifecycle(workerCount: 1);
        var worker = lifecycle.ReadyAndWaitForStartAsync(0);
        await lifecycle.AllWorkersReady.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycle.StartMeasurement();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));

        for (var index = 0; index < 100; index++)
        {
            Ensure(lifecycle.TryBeginOperationStart(0, out var warmupAdmission),
                "warmup admission remains open");
            warmupAdmission.Dispose();
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            if (!lifecycle.TryBeginOperationStart(0, out var admission))
                throw new Exception("Measurement unexpectedly stopped during allocation validation.");
            admission.Dispose();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Ensure(allocated == 0,
            $"operation admission must not allocate in the measurement hot path; allocated={allocated}");
        lifecycle.StopStartingNewOperations();
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
