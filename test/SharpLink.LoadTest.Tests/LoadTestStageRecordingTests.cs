using SharpLink.LoadTestBase;
using WorkerFailureRecorder = SharpLink.LoadTest.FailureRecorder;

namespace SharpLink.LoadTest.Tests;

public class LoadTestStageRecordingTests
{
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments("formal")]
    [Arguments("validation-dual")]
    public async Task SuccessfulWarmupShouldNotContaminatePreparedMeasurementOrObserver(string mode)
    {
        var options = Options(mode, tailObserver: true);
        var measurement = new LoadTestStageRecording(options, concurrency: 1);
        var warmup = new LoadTestStageRecording(options, concurrency: 1, isWarmup: true);
        var formal = measurement.FormalRecorder ?? throw new Exception("The measurement recorder is missing.");
        var observerRecorder = measurement.TailObserverRecorder ?? throw new Exception("The observer recorder is missing.");

        await RunOneOperationAsync(warmup);
        Ensure(warmup.Mode == LatencyRecordingMode.Off && warmup.FormalRecorder is null &&
               warmup.DiagnosticHistogram is null && warmup.Realtime is null && warmup.TailObserverRecorder is null,
            "a successful real warmup worker has no dummy measurement or observer buffer");
        Ensure(formal.GetWorker(0).Count == 0 && observerRecorder.GetWorker(0).Count == 0,
            "the already prepared formal and observer buffers exclude warmup success");
        if (measurement.DiagnosticHistogram is { } emptyLegacy)
        {
            Ensure(emptyLegacy.Count == 0, "validation's legacy recorder also excludes warmup");
            LatencyRecorderValidation.ValidateAgainstLegacy(formal.Complete(), emptyLegacy);
        }

        await RunOneOperationAsync(measurement);
        Ensure(formal.Complete().Count == 1 && observerRecorder.GetWorker(0).Count == 0,
            "the same prepared state records only its one completed measurement RPC");
        if (measurement.DiagnosticHistogram is { } legacy)
            Ensure(legacy.Count == 1, "validation's real worker records the same one measurement sample");

        await RunOneOperationAsync(measurement, observer: true);
        Ensure(observerRecorder.Complete().Count == 1 && formal.Complete().Count == 1,
            "an independently completed observer RPC never enters the workload sample buffer");
        if (measurement.DiagnosticHistogram is { } workloadLegacy)
            Ensure(workloadLegacy.Count == 1, "observer success also stays outside workload validation samples");
    }

    [Test]
    public async Task PreparedStagesShouldKeepSuccessfulWorkerSamplesIndependent()
    {
        var options = Options("formal");
        var first = new LoadTestStageRecording(options, concurrency: 1);
        var next = new LoadTestStageRecording(options, concurrency: 1);
        var firstRecorder = first.FormalRecorder ?? throw new Exception("The first stage recorder is missing.");
        var nextRecorder = next.FormalRecorder ?? throw new Exception("The next stage recorder is missing.");

        await RunOneOperationAsync(first);
        Ensure(firstRecorder.Complete().Count == 1 && nextRecorder.GetWorker(0).Count == 0,
            "a prepared later stage cannot inherit the earlier stage's successful RPC");
        await RunOneOperationAsync(next);
        Ensure(firstRecorder.Complete().Count == 1 && nextRecorder.Complete().Count == 1,
            "both stages retain exactly their own measurement sample");
    }

    [Test]
    public async Task RecordingOffShouldCompleteARealWorkerWithoutAnySampleBuffer()
    {
        var recording = new LoadTestStageRecording(Options("off"), concurrency: 1);

        await RunOneOperationAsync(recording);
        Ensure(recording.FormalRecorder is null && recording.DiagnosticHistogram is null &&
               recording.Realtime is null && recording.TailObserverRecorder is null,
            "recording-off success does not create a dummy recorder or latency sample");
    }

    [Test]
    public async Task ValidationDualShouldRejectLegacySamplesFromAnotherPreparedStage()
    {
        var options = Options("validation-dual");
        var measured = new LoadTestStageRecording(options, concurrency: 1);
        var untouched = new LoadTestStageRecording(options, concurrency: 1);
        var formal = measured.FormalRecorder ?? throw new Exception("The measurement recorder is missing.");
        var wrongLegacy = untouched.DiagnosticHistogram ?? throw new Exception("The validation recorder is missing.");

        await RunOneOperationAsync(measured);
        Ensure(formal.Complete().Count == 1 && measured.DiagnosticHistogram!.Count == 1 && wrongLegacy.Count == 0,
            "a real completed measurement and an untouched prepared stage have different sample counts");
        Exception? failure = null;
        try
        {
            LatencyRecorderValidation.ValidateAgainstLegacy(formal.Complete(), wrongLegacy);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }
        Ensure(failure is InvalidOperationException && failure.Message.Contains("count mismatch", StringComparison.Ordinal),
            "validation must reject a different stage's legacy samples rather than accept the wrong buffer");
    }

    private static LoadTestOptions Options(string mode, bool tailObserver = false)
        => LoadTestOptions.Parse(tailObserver
            ? ["--operation", "add", "--recording", mode, "--concurrency", "1",
                "--maximum-recorded-operations", "4", "--tail-observer"]
            : ["--operation", "add", "--recording", mode, "--concurrency", "1",
                "--maximum-recorded-operations", "4"]);

    private static async Task RunOneOperationAsync(LoadTestStageRecording recording, bool observer = false)
    {
        await using var fixture = new WorkerFixture(recording, observer);
        await fixture.StartAndStopAsync();
        var drain = fixture.Lifecycle.WaitForDrainAsync(fixture.Worker, TestGuard);
        Ensure(!drain.IsCompleted, "drain observes the admitted RPC until its explicit completion");
        fixture.Service.Complete();
        await drain.WaitAsync(TestGuard);
        var outcome = await fixture.Worker;
        Ensure(outcome.OperationsStarted == 1 && outcome.Success == 1 && outcome.Failure == 0 &&
               outcome.SendQueueBackpressureRetries == 0 && fixture.Service.Calls == 1,
            "the real worker completes exactly one logical Add RPC after admission closes");
        Ensure(fixture.Failures.Top(3) == string.Empty, "the controlled success has no hidden worker failure");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private bool _started;
        private Task? _stopTask;

        internal MeasurementStageLifecycle Lifecycle { get; } = new(workerCount: 1);
        internal WorkerFailureRecorder Failures { get; } = new();
        internal ControlledService Service { get; } = new();
        internal Task<WorkerStageOutcome> Worker { get; }

        internal WorkerFixture(LoadTestStageRecording recording, bool observer)
        {
            var recorder = observer ? recording.TailObserverRecorder : recording.FormalRecorder;
            Worker = LoadTestWorker.RunAsync(
                Service, "add", string.Empty, 0, Lifecycle, recorder?.GetWorker(0), recorder,
                observer ? null : recording.DiagnosticHistogram, observer ? null : recording.Realtime,
                Failures, retryOneWaySendQueueBackpressure: false);
        }

        internal async Task StartAndStopAsync()
        {
            await Lifecycle.AllWorkersReady.WaitAsync(TestGuard);
            Lifecycle.StartMeasurement();
            _started = true;
            await Service.Entered.Task.WaitAsync(TestGuard);
            await BeginStopAsync().WaitAsync(TestGuard);
            Ensure(!Lifecycle.CanStartOperation && !Worker.IsCompleted,
                "closing measurement admission does not fabricate completion of the pending RPC");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_started)
            {
                await Lifecycle.AllWorkersReady.WaitAsync(TestGuard);
                Lifecycle.StartMeasurement();
                _started = true;
            }
            // Reuse Stop and release the RPC before joining either task, including
            // failure paths where a regressed worker holds admission across await.
            var stop = BeginStopAsync();
            Service.Complete();
            await Task.WhenAll(stop, Worker).WaitAsync(TestGuard);
        }

        private Task BeginStopAsync()
            => _stopTask ??= Task.Factory.StartNew(
                () => Lifecycle.StopStartingNewOperations(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private sealed class ControlledService : ILoadTestService
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls => Volatile.Read(ref _calls);

        public ValueTask<int> AddAsync(int left, int right)
        {
            Ensure(left == 7 && right == 9, "the real worker uses the original Add input");
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            return new ValueTask<int>(_completion.Task);
        }

        internal void Complete() => _completion.TrySetResult(16);

        public ValueTask PingAsync() => throw UnexpectedRpc();
        public ValueTask NotifyAsync(int left, int right) => throw UnexpectedRpc();
        public ValueTask<string> EchoAsync(string value) => throw UnexpectedRpc();
        public ValueTask<int> YieldAsync(int left, int right) => throw UnexpectedRpc();
        public ValueTask<int> DelayAsync(int left, int right) => throw UnexpectedRpc();
        public ValueTask<int> ResetHoldProbeAsync() => throw UnexpectedRpc();
        public ValueTask HoldAsync(int generation, int expectedAcceptedCalls, int holdDurationMilliseconds) => throw UnexpectedRpc();
        public ValueTask<int> GetHoldActiveCallsAsync() => throw UnexpectedRpc();
        public ValueTask<int> GetHoldPeakActiveCallsAsync() => throw UnexpectedRpc();
        public ValueTask<string> GetSessionIdAsync() => throw UnexpectedRpc();

        private static Exception UnexpectedRpc() => new InvalidOperationException("Unexpected RPC in the stage recording test.");
    }
}
