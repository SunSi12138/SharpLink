using SharpLink.LoadTestBase;
using WorkerFailureRecorder = SharpLink.LoadTest.FailureRecorder;

namespace SharpLink.LoadTest.Tests;

public class OneWayRetryDrainTests
{
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(5);

    [Test]
    public async Task StartedOneWayShouldRetryQueuePressureAfterMeasurementStopsAndRecordOneSuccess()
        => await RetryStoppedOperationAsync(additionalFailures: 0);

    [Test]
    public async Task StoppedOneWayShouldRetainOneLogicalOperationAcrossSynchronousAndAwaitedRetries()
        => await RetryStoppedOperationAsync(additionalFailures: 2);

    [Test]
    public async Task SynchronousFirstQueuePressureShouldReleaseAdmissionBeforeAwaitingItsRetry()
    {
        await using var fixture = new WorkerFixture(retryEnabled: true, firstAttemptThrowsSynchronously: true);
        await fixture.StartFirstOperationAndStopAsync();
        Ensure(fixture.Service.Calls == 2 && !fixture.Worker.IsCompleted,
            "the synchronous first send failure reaches one pending retry before Stop");

        var drain = fixture.Lifecycle.WaitForDrainAsync(fixture.Worker, TestGuard);
        fixture.Service.CompleteFinalAttempt();
        await drain.WaitAsync(TestGuard);
        var outcome = await fixture.Worker;
        Ensure(outcome.OperationsStarted == 1 && outcome.Success == 1 && outcome.Failure == 0,
            "synchronous Start failure releases its admission and retains one logical operation");
        Ensure(outcome.SendQueueBackpressureRetries == 1 && fixture.Service.Calls == 2,
            "stopping admission while awaiting the retry does not admit another logical operation");
        Ensure(fixture.Recorder.Complete().Count == 1 && fixture.Failures.Top(3) == string.Empty,
            "the synchronous queue-pressure retry produces one real successful formal sample");
    }

    [Test]
    public async Task RawOneWayShouldKeepQueuePressureAsFailureWithoutRetry()
    {
        await using var fixture = new WorkerFixture(retryEnabled: false);
        await fixture.StartFirstOperationAndStopAsync();
        fixture.Service.FailFirst(QueuePressure());

        await fixture.Lifecycle.WaitForDrainAsync(fixture.Worker, TestGuard);
        var outcome = await fixture.Worker;
        Ensure(outcome.OperationsStarted == 1 && outcome.Success == 0 && outcome.Failure == 1,
            "raw saturation keeps one started/completed failed operation");
        Ensure(outcome.SendQueueBackpressureRetries == 0 && fixture.Service.Calls == 1,
            "raw mode must not enter a retry after measurement stops");
        Ensure(fixture.Recorder.Complete().Count == 0,
            "a raw failed send has no fabricated successful latency sample");
        Ensure(!string.IsNullOrEmpty(fixture.Failures.Top(3)),
            "raw failure remains visible in the real worker failure recorder");
    }

    [Test]
    public async Task OtherResourceExhaustionShouldRemainARealFailureAfterMeasurementStops()
    {
        await using var fixture = new WorkerFixture(retryEnabled: true);
        await fixture.StartFirstOperationAndStopAsync();
        fixture.Service.FailFirst(new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted, "RPC call capacity exhausted (active_call_capacity)."));

        await fixture.Lifecycle.WaitForDrainAsync(fixture.Worker, TestGuard);
        var outcome = await fixture.Worker;
        Ensure(outcome.OperationsStarted == 1 && outcome.Success == 0 && outcome.Failure == 1,
            "non-send-queue resource exhaustion is not reclassified as a successful send");
        Ensure(outcome.SendQueueBackpressureRetries == 0 && fixture.Service.Calls == 1,
            "retry is specific to send_queue_capacity rather than every ResourceExhausted code");
        Ensure(fixture.Recorder.Complete().Count == 0 && !string.IsNullOrEmpty(fixture.Failures.Top(3)),
            "the actual failure and absence of a successful sample are preserved");
    }

    [Test]
    public async Task PermanentQueuePressureShouldFailBoundedDrainAndStillAllowWorkerCleanup()
    {
        await using var fixture = new WorkerFixture(retryEnabled: true, permanentPressure: true);
        await fixture.StartFirstOperationAndStopAsync();
        fixture.Service.FailFirst(QueuePressure());
        await RequireRetryBeforeWorkerExitAsync(fixture.Service.RetryEntered.Task, fixture.Worker);

        var drain = fixture.Lifecycle.WaitForDrainAsync(
            fixture.Worker, TimeSpan.FromMilliseconds(50));
        Exception? observedDrainFailure = null;
        try
        {
            await drain.WaitAsync(TestGuard);
        }
        catch (TimeoutException exception)
        {
            observedDrainFailure = exception;
        }

        Ensure(drain.IsFaulted && observedDrainFailure is TimeoutException,
            "the real bounded drain must fail rather than report a valid completed measurement");
        Ensure(!fixture.Worker.IsCompleted,
            "the admitted logical send still retries until the test explicitly releases saturation");

        fixture.Service.ReleaseForCleanup();
        var cleanupOutcome = await fixture.Worker.WaitAsync(TestGuard);
        Ensure(cleanupOutcome.OperationsStarted == 1 && cleanupOutcome.Success == 1 && cleanupOutcome.Failure == 0,
            "cleanup observes the original logical operation instead of leaving a background worker");
        Ensure(cleanupOutcome.SendQueueBackpressureRetries > 0 && fixture.Recorder.Complete().Count == 1,
            "the eventually completed operation has one real sample, including its drain retries");
        Ensure(drain.IsFaulted && observedDrainFailure is TimeoutException,
            "later cleanup cannot turn the already timed-out run into valid evidence");
    }

    private static async Task RetryStoppedOperationAsync(int additionalFailures)
    {
        await using var fixture = new WorkerFixture(retryEnabled: true, additionalFailures: additionalFailures);
        await fixture.StartFirstOperationAndStopAsync();
        fixture.Service.FailFirst(QueuePressure());
        await RequireRetryBeforeWorkerExitAsync(fixture.Service.FinalAttemptEntered.Task, fixture.Worker);
        Ensure(!fixture.Lifecycle.CanStartOperation && !fixture.Service.RetriedWhileAdmissionOpen,
            "retries belong to the started operation even though new-operation admission is closed");

        var drain = fixture.Lifecycle.WaitForDrainAsync(fixture.Worker, TestGuard);
        Ensure(!drain.IsCompleted,
            "the real drain must observe the final pending retry rather than drop its completion");
        fixture.Service.CompleteFinalAttempt();
        await drain.WaitAsync(TestGuard);
        var outcome = await fixture.Worker;
        Ensure(outcome.OperationsStarted == 1 && outcome.Success + outcome.Failure == 1,
            "all send attempts remain one started/completed logical operation");
        Ensure(outcome.Success == 1 && outcome.Failure == 0,
            "measurement Stop is not a synthetic send failure");
        Ensure(outcome.SendQueueBackpressureRetries == 1 + additionalFailures,
            "every real queue-pressure retry is retained");
        Ensure(fixture.Service.Calls == 2 + additionalFailures,
            "the worker sends only the initial attempt and its retries, with no new logical operation");
        Ensure(fixture.Recorder.Complete().Count == 1 && fixture.Failures.Top(3) == string.Empty,
            "successful completion records exactly one formal sample without a false failure");
        Ensure(!fixture.Lifecycle.CanStartOperation,
            "completing a retry cannot reopen measurement admission");
    }

    private static async Task RequireRetryBeforeWorkerExitAsync(Task retryEntered, Task<WorkerStageOutcome> worker)
    {
        var first = await Task.WhenAny(retryEntered, worker).WaitAsync(TestGuard);
        Ensure(first == retryEntered,
            "the real worker returned before retrying its admitted operation after Stop");
        await retryEntered.WaitAsync(TestGuard);
    }

    private static SharpLinkException QueuePressure()
        => new(SharpLinkErrorCode.ResourceExhausted,
            "Session send queue exceeded its 67108864-byte limit (send_queue_capacity).");

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private bool _measurementStarted;
        private readonly bool _firstAttemptThrowsSynchronously;
        private Task? _stopTask;

        internal MeasurementStageLifecycle Lifecycle { get; } = new(workerCount: 1);
        internal StageLatencyRecorder Recorder { get; } = new(workerCount: 1, maximumTotalSamples: 4);
        internal WorkerFailureRecorder Failures { get; } = new();
        internal ControlledService Service { get; }
        internal Task<WorkerStageOutcome> Worker { get; }

        internal WorkerFixture(bool retryEnabled, int additionalFailures = 0, bool permanentPressure = false,
            bool firstAttemptThrowsSynchronously = false)
        {
            _firstAttemptThrowsSynchronously = firstAttemptThrowsSynchronously;
            Service = new ControlledService(Lifecycle, additionalFailures, permanentPressure,
                firstAttemptThrowsSynchronously);
            Worker = LoadTestWorker.RunAsync(
                Service, "oneway", string.Empty, 0, Lifecycle, Recorder.GetWorker(0), Recorder,
                diagnosticHistogram: null, realtime: null, failures: Failures,
                retryOneWaySendQueueBackpressure: retryEnabled);
        }

        internal async Task StartFirstOperationAndStopAsync()
        {
            await Lifecycle.AllWorkersReady.WaitAsync(TestGuard);
            Lifecycle.StartMeasurement();
            _measurementStarted = true;
            await Service.FirstAttemptEntered.Task.WaitAsync(TestGuard);
            if (_firstAttemptThrowsSynchronously)
                await RequireRetryBeforeWorkerExitAsync(Service.FinalAttemptEntered.Task, Worker);
            // Stop runs outside NotifyAsync: a synchronous throw must already have
            // unwound its admission before the pending retry can drain.
            await BeginStopAsync().WaitAsync(TestGuard);
            Ensure(!Lifecycle.CanStartOperation,
                "the real Stop closes new-operation admission before returning");
            Ensure(!Worker.IsCompleted,
                "the first operation remains awaited when measurement admission closes");
        }

        public async ValueTask DisposeAsync()
        {
            // Release and observe the same worker on every assertion/old-bug path.
            if (!_measurementStarted)
            {
                await Lifecycle.AllWorkersReady.WaitAsync(TestGuard);
                Lifecycle.StartMeasurement();
                _measurementStarted = true;
            }
            // Reuse the original Stop even when it timed out waiting for a leaked
            // admission. Release the RPC gates before joining either task so that
            // a scope held across await can unwind during failure cleanup.
            var stop = BeginStopAsync();
            Service.ReleaseForCleanup();
            await Task.WhenAll(stop, Worker).WaitAsync(TestGuard);
        }

        private Task BeginStopAsync()
            => _stopTask ??= Task.Factory.StartNew(
                () => Lifecycle.StopStartingNewOperations(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
    }

    private sealed class ControlledService(
        MeasurementStageLifecycle lifecycle, int additionalFailures, bool permanentPressure,
        bool firstAttemptThrowsSynchronously) : ILoadTestService
    {
        private readonly TaskCompletionSource _firstCompletion = NewGate();
        private readonly TaskCompletionSource _finalCompletion = NewGate();
        private readonly SharpLinkException _queuePressure = QueuePressure();
        private Task? _faultedRetry;
        private int _calls;
        private int _releasePressure;
        private int _retriedWhileAdmissionOpen;

        internal TaskCompletionSource FirstAttemptEntered { get; } = NewGate();
        internal TaskCompletionSource RetryEntered { get; } = NewGate();
        internal TaskCompletionSource FinalAttemptEntered { get; } = NewGate();
        internal int Calls => Volatile.Read(ref _calls);
        internal bool RetriedWhileAdmissionOpen => Volatile.Read(ref _retriedWhileAdmissionOpen) != 0;

        public ValueTask NotifyAsync(int left, int right)
        {
            Ensure(left == 7 && right == 9, "the real worker uses its original OneWay input");
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstAttemptEntered.TrySetResult();
                if (firstAttemptThrowsSynchronously)
                    throw _queuePressure;
                return new ValueTask(_firstCompletion.Task);
            }

            if (lifecycle.CanStartOperation)
                Interlocked.Exchange(ref _retriedWhileAdmissionOpen, 1);
            RetryEntered.TrySetResult();
            if (Volatile.Read(ref _releasePressure) != 0)
                return ValueTask.CompletedTask;
            if (permanentPressure)
                return FaultedRetry();
            if (call <= 1 + additionalFailures)
            {
                if ((call & 1) == 0)
                    throw _queuePressure;
                return FaultedRetry();
            }

            FinalAttemptEntered.TrySetResult();
            return new ValueTask(_finalCompletion.Task);
        }

        internal void FailFirst(Exception exception) => _firstCompletion.TrySetException(exception);
        internal void CompleteFinalAttempt() => _finalCompletion.TrySetResult();

        internal void ReleaseForCleanup()
        {
            Volatile.Write(ref _releasePressure, 1);
            _firstCompletion.TrySetResult();
            _finalCompletion.TrySetResult();
        }

        private static TaskCompletionSource NewGate()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ValueTask FaultedRetry()
            => new(_faultedRetry ??= Task.FromException(_queuePressure));

        // Any other RPC indicates that the production worker took the wrong path.
        public ValueTask PingAsync() => throw UnexpectedRpc();
        public ValueTask<int> AddAsync(int left, int right) => throw UnexpectedRpc();
        public ValueTask<string> EchoAsync(string value) => throw UnexpectedRpc();
        public ValueTask<int> YieldAsync(int left, int right) => throw UnexpectedRpc();
        public ValueTask<int> DelayAsync(int left, int right) => throw UnexpectedRpc();
        public ValueTask<int> ResetHoldProbeAsync() => throw UnexpectedRpc();
        public ValueTask HoldAsync(int generation, int expectedAcceptedCalls, int holdDurationMilliseconds) => throw UnexpectedRpc();
        public ValueTask<int> GetHoldActiveCallsAsync() => throw UnexpectedRpc();
        public ValueTask<int> GetHoldPeakActiveCallsAsync() => throw UnexpectedRpc();
        public ValueTask<string> GetSessionIdAsync() => throw UnexpectedRpc();

        private static Exception UnexpectedRpc() => new InvalidOperationException("Unexpected RPC in the OneWay worker test.");
    }
}
