using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        EvidenceRun? run = null;
        try
        {
            run = new EvidenceRun(Options.Parse(args));
            await run.ExecuteAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            run?.WriteFailure(exception);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}

internal sealed record Options(
    string Transport, string Shape, int Items, int PayloadBytes, int Concurrency,
    int WarmupOpsPerWorker, int MeasuredOpsPerWorker, string BaselineSha,
    string RuntimeSourceTreeSha, string Mode, string Output)
{
    internal static Options Parse(string[] args)
    {
        if (args.Length != 11)
            throw new ArgumentException(
                "Usage: <tcp|sharedmemory> <Unary|Server|Client|Duplex> <items> <bytes> " +
                "<concurrency> <warmup-ops-per-worker> <measured-ops-per-worker> " +
                "<baseline-sha> <runtime-source-tree-sha> <mode> <output-file>");
        var result = new Options(
            args[0].ToLowerInvariant(), args[1],
            int.Parse(args[2], CultureInfo.InvariantCulture),
            int.Parse(args[3], CultureInfo.InvariantCulture),
            int.Parse(args[4], CultureInfo.InvariantCulture),
            int.Parse(args[5], CultureInfo.InvariantCulture),
            int.Parse(args[6], CultureInfo.InvariantCulture),
            args[7], args[8], args[9], Path.GetFullPath(args[10]));
        if (result.Transport is not ("tcp" or "sharedmemory"))
            throw new ArgumentException("Transport must be tcp or sharedmemory.");
        if (result.Shape is not ("Unary" or "Server" or "Client" or "Duplex"))
            throw new ArgumentException("Unknown RPC shape.");
        ArgumentOutOfRangeException.ThrowIfNegative(result.Items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(result.PayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(result.Concurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(result.WarmupOpsPerWorker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(result.MeasuredOpsPerWorker);
        if (result.Shape == "Unary" && result.Items != 1)
            throw new ArgumentException("Unary control requires items=1.");
        _ = checked(result.Concurrency * result.MeasuredOpsPerWorker);
        return result;
    }
}

internal sealed class EvidenceRun
{
    private const double MinimumWarmupSeconds = 5;
#if FLOW_EVIDENCE_NATIVE_AOT
    private const bool BuiltForNativeAot = true;
#else
    private const bool BuiltForNativeAot = false;
#endif
    private readonly Options _options;
    private WorkerState[] _states = [];
    private long _measurementStart;
    private long _measurementEnd;
    private string _identity = string.Empty;

    internal EvidenceRun(Options options) => _options = options;

    internal async Task ExecuteAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.Output)!);
        VerifyCompiledIdentity();
        _identity = BuildIdentity();
        File.WriteAllText(_options.Output + ".started.txt",
            _identity + "status=started\n", Encoding.UTF8);
        Console.Write(_identity);

        ThreadPool.GetMinThreads(out var originalWorkerMin, out var originalIoMin);
        var workerMin = Math.Max(32, Environment.ProcessorCount * 4);
        if (!ThreadPool.SetMinThreads(workerMin, originalIoMin))
            throw new InvalidOperationException("Unable to set the shared, fixed ThreadPool minimum.");
        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);

        var payload = CreatePayload(_options.PayloadBytes);
        var values = new byte[_options.Items][];
        Array.Fill(values, payload);
        var operation = new RpcOperation(_options, payload, values);
        _states = new WorkerState[_options.Concurrency];
        for (var worker = 0; worker < _states.Length; worker++)
            _states[worker] = new WorkerState(worker, _options.MeasuredOpsPerWorker);

        string report;
        var environment = await EvidenceEnvironment.CreateAsync(
            _options.Transport, _options.Items, payload).ConfigureAwait(false);
        try
        {
            operation.Rpc = environment.Rpc;
            var initialSession = await environment.ProbeSessionAsync().ConfigureAwait(false);
            await environment.WaitForIdleAsync().ConfigureAwait(false);

            using var stopWorkers = new CancellationTokenSource();
            var warmupStart = NewSignal();
            var measurementStart = NewSignal();
            var workersCreated = new CountedSignal(_options.Concurrency);
            var workersReadyForMeasurement = new CountedSignal(_options.Concurrency);
            var firstFailure = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new Task[_states.Length];

            // All tasks, closures and sample arrays exist before either measured boundary.
            // The same persistent workers execute warmup and measurement on one connection.
            for (var worker = 0; worker < tasks.Length; worker++)
            {
                var state = _states[worker];
                tasks[worker] = Task.Run(() => RunWorkerAsync(
                    state, operation, workersCreated, warmupStart.Task,
                    workersReadyForMeasurement, measurementStart.Task,
                    firstFailure, stopWorkers.Token));
            }
            var allWorkers = Task.WhenAll(tasks);
            // Prepare the one terminal race before measurement. A failed worker must
            // trigger teardown even if another RPC remains pending indefinitely.
            var completionOrFailure = Task.WhenAny(allWorkers, firstFailure.Task);
            try
            {
                await ReadyOrFailureAsync(workersCreated.Task, firstFailure.Task).ConfigureAwait(false);
                warmupStart.SetResult();
                await ReadyOrFailureAsync(
                    workersReadyForMeasurement.Task, firstFailure.Task).ConfigureAwait(false);
                await environment.WaitForIdleAsync().ConfigureAwait(false);
                environment.RequireOneConnection();

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                using var process = Process.GetCurrentProcess();
                process.Refresh();
                Console.WriteLine("phase=measurement_all_workers_ready");
                var threadsBefore = ThreadPool.ThreadCount;
                var threadPoolCompletedBefore = ThreadPool.CompletedWorkItemCount;
                var cpuBefore = process.TotalProcessorTime;
                var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
                var gen0Before = GC.CollectionCount(0);
                var gen1Before = GC.CollectionCount(1);
                var gen2Before = GC.CollectionCount(2);
                var pauseBefore = GC.GetTotalPauseDuration();
                _measurementStart = Stopwatch.GetTimestamp();
                measurementStart.SetResult();

                var completed = await completionOrFailure.ConfigureAwait(false);
                if (ReferenceEquals(completed, firstFailure.Task))
                    throw new InvalidOperationException("A persistent RPC worker failed during measurement.",
                        firstFailure.Task.GetAwaiter().GetResult());
                // WhenAll won, so this is already complete; observe any aggregate failure.
                allWorkers.GetAwaiter().GetResult();

                _measurementEnd = Stopwatch.GetTimestamp();
                var allocations = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
                var gen0 = GC.CollectionCount(0) - gen0Before;
                var gen1 = GC.CollectionCount(1) - gen1Before;
                var gen2 = GC.CollectionCount(2) - gen2Before;
                var pause = GC.GetTotalPauseDuration() - pauseBefore;
                var threadPoolCompleted = ThreadPool.CompletedWorkItemCount - threadPoolCompletedBefore;
                var threadsAfter = ThreadPool.ThreadCount;
                process.Refresh();
                var cpu = process.TotalProcessorTime - cpuBefore;

                var settleStarted = Stopwatch.GetTimestamp();
                await environment.WaitForIdleAsync().ConfigureAwait(false);
                var settledEnd = Stopwatch.GetTimestamp();
                var settledAllocations = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
                var settledGen0 = GC.CollectionCount(0) - gen0Before;
                var settledGen1 = GC.CollectionCount(1) - gen1Before;
                var settledGen2 = GC.CollectionCount(2) - gen2Before;
                var settledPause = GC.GetTotalPauseDuration() - pauseBefore;
                process.Refresh();
                var settledCpu = process.TotalProcessorTime - cpuBefore;
                var settleTicks = settledEnd - settleStarted;

                // All resource-cost observations precede raw-file I/O and the extra
                // session probe. Keep both RPC-completion and settled boundaries.
                WriteRawSamples();
                var probeStarted = Stopwatch.GetTimestamp();
                var finalSession = await environment.ProbeSessionAsync().ConfigureAwait(false);
                await environment.WaitForIdleAsync().ConfigureAwait(false);
                if (!string.Equals(initialSession, finalSession, StringComparison.Ordinal))
                    throw new InvalidOperationException("The physical RPC session changed during the run.");
                var probeTicks = Stopwatch.GetTimestamp() - probeStarted;

                report = BuildReport(environment, cpu, allocations, gen0, gen1, gen2,
                    pause, threadPoolCompleted, originalWorkerMin, originalIoMin,
                    workerMin, workerMax, ioMax, threadsBefore, threadsAfter, settleTicks,
                    initialSession, settledEnd, settledCpu, settledAllocations,
                    settledGen0, settledGen1, settledGen2, settledPause, probeTicks);
            }
            catch
            {
                stopWorkers.Cancel();
                // Observe failures from tasks still unwinding after transport disposal.
                _ = allWorkers.ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        catch (Exception exception)
        {
            // Keep the invocation/setup error first if transport cleanup also fails.
            await environment.DisposeAsync(exception).ConfigureAwait(false);
            throw;
        }
        await environment.DisposeAsync().ConfigureAwait(false);

        // Success is emitted only after the transport has actually torn down cleanly.
        File.WriteAllText(_options.Output, report, Encoding.UTF8);
        Console.Write(report);
    }

    private async Task RunWorkerAsync(
        WorkerState state, RpcOperation operation, CountedSignal workersCreated, Task warmupStart,
        CountedSignal readyForMeasurement, Task measurementStart,
        TaskCompletionSource<Exception> firstFailure, CancellationToken stop)
    {
        try
        {
            workersCreated.Signal();
            await warmupStart.WaitAsync(stop).ConfigureAwait(false);
            state.WarmupStart = Stopwatch.GetTimestamp();
            do
            {
                stop.ThrowIfCancellationRequested();
                await operation.InvokeAsync().ConfigureAwait(false);
                state.WarmupCompleted++;
                state.WarmupEnd = Stopwatch.GetTimestamp();
            }
            while (state.WarmupCompleted < _options.WarmupOpsPerWorker ||
                   (state.WarmupEnd - state.WarmupStart) / (double)Stopwatch.Frequency < MinimumWarmupSeconds);
            readyForMeasurement.Signal();
            await measurementStart.WaitAsync(stop).ConfigureAwait(false);
            for (var index = 0; index < _options.MeasuredOpsPerWorker; index++)
            {
                stop.ThrowIfCancellationRequested();
                state.Starts[index] = Stopwatch.GetTimestamp();
                await operation.InvokeAsync().ConfigureAwait(false);
                state.Ends[index] = Stopwatch.GetTimestamp();
                state.Completed = index + 1;
            }
        }
        catch (Exception exception)
        {
            state.Error = exception.ToString();
            try
            {
                // Preserve the original failure before waking teardown or waiting for
                // other workers. A later transport-disposal timeout cannot erase it.
                File.WriteAllText(_options.Output + ".worker-" + state.Id + ".failure.txt",
                    state.Error, Encoding.UTF8);
            }
            catch (Exception artifactException)
            {
                Console.Error.WriteLine("Failed to preserve worker " + state.Id +
                    " exception artifact: " + artifactException);
                Console.Error.WriteLine(state.Error);
            }
            firstFailure.TrySetResult(exception);
            throw;
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ReadyOrFailureAsync(Task ready, Task<Exception> failure)
    {
        if (ReferenceEquals(await Task.WhenAny(ready, failure).ConfigureAwait(false), failure))
            throw new InvalidOperationException("A persistent RPC worker failed before measurement.",
                await failure.ConfigureAwait(false));
        await ready.ConfigureAwait(false);
    }

    private void VerifyCompiledIdentity()
    {
        RequireSha(ModeIdentity.BaselineSha, "compiled baseline", allowSha256: false);
        RequireSha(ModeIdentity.CandidateSha, "compiled candidate", allowSha256: false);
        RequireSha(ModeIdentity.RuntimeSourceTreeSha, "compiled runtime source tree", allowSha256: true);
        RequireSha(ModeIdentity.MeasurementSourceSha, "compiled measurement source", allowSha256: true);
        RequireSha(ModeIdentity.ControllerSourceSha256, "compiled controller source", allowSha256: true);
        RequireSha(ModeIdentity.SendPumpSourceSha256, "compiled SendPump source", allowSha256: true);
        if (!string.Equals(_options.BaselineSha, ModeIdentity.BaselineSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_options.RuntimeSourceTreeSha, ModeIdentity.RuntimeSourceTreeSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_options.Mode, ModeIdentity.Mode, StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime arguments disagree with the compiled source identity.");
        var observedAot = !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled;
        if (BuiltForNativeAot != observedAot)
            throw new InvalidOperationException(
                "PublishAot build marker and executed RuntimeFeature flags disagree; refusing to relabel the binary.");
    }

    private static void RequireSha(string value, string name, bool allowSha256)
    {
        if (value.Length != 40 && !(allowSha256 && value.Length == 64))
            throw new InvalidOperationException(name + " was not compiled with a SHA identity.");
        foreach (var character in value)
            if (!Uri.IsHexDigit(character))
                throw new InvalidOperationException(name + " contains a non-hexadecimal character.");
    }

    private string BuildIdentity()
    {
        var builder = new StringBuilder();
        Add(builder, "schema", "sharplink-flow-architecture-evidence-v1");
        Add(builder, "baselineSha", ModeIdentity.BaselineSha);
        Add(builder, "candidateSha", ModeIdentity.CandidateSha);
        Add(builder, "runtimeSourceTreeSha", ModeIdentity.RuntimeSourceTreeSha);
        Add(builder, "measurementSourceSha", ModeIdentity.MeasurementSourceSha);
        Add(builder, "controllerSourceSha256", ModeIdentity.ControllerSourceSha256);
        Add(builder, "sendPumpSourceSha256", ModeIdentity.SendPumpSourceSha256);
        Add(builder, "compiledMode", ModeIdentity.Mode);
        Add(builder, "productionScheduler", "dev-SendPump");
        Add(builder, "b3ReadyWriterEnabled", false);
        Add(builder, "builtForNativeAot", BuiltForNativeAot);
        Add(builder, "dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
        Add(builder, "dynamicCodeCompiled", RuntimeFeature.IsDynamicCodeCompiled);
        Add(builder, "runtime", RuntimeInformation.FrameworkDescription);
        Add(builder, "runtimeVersion", Environment.Version.ToString());
        Add(builder, "os", RuntimeInformation.OSDescription);
        Add(builder, "architecture", RuntimeInformation.ProcessArchitecture.ToString());
        Add(builder, "processorCount", Environment.ProcessorCount);
        Add(builder, "machine", Environment.MachineName);
        Add(builder, "serverGc", GCSettings.IsServerGC);
        Add(builder, "gcLatencyMode", GCSettings.LatencyMode.ToString());
        Add(builder, "stopwatchFrequency", Stopwatch.Frequency);
        Add(builder, "utcStarted", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(builder, "transport", _options.Transport);
        Add(builder, "shape", _options.Shape);
        Add(builder, "itemsPerRpc", _options.Items);
        Add(builder, "payloadBytes", _options.PayloadBytes);
        Add(builder, "concurrency", _options.Concurrency);
        Add(builder, "warmupOpsPerWorker", _options.WarmupOpsPerWorker);
        Add(builder, "minimumWarmupSecondsPerWorker", MinimumWarmupSeconds);
        Add(builder, "warmupPolicy", "each-worker-runs-until-fixed-minimum-ops-and-minimum-duration-both-met");
        Add(builder, "tierStabilizationVerified", false);
        Add(builder, "measuredOpsPerWorker", _options.MeasuredOpsPerWorker);
        Add(builder, "generatedContract", "SharpLink.Benchmarks.IBenchmarkRpc");
        Add(builder, "validation", "full-payload-both-directions-and-exact-stream-item-count");
        Add(builder, "requestTimeout", "disabled-as-in-existing-BenchmarkEnvironment");
        Add(builder, "collectorTimeoutSeconds", "60-external-not-enforced-by-production-options");
        Add(builder, "heartbeat", _options.Transport == "sharedmemory"
            ? "5min-period-10min-timeout-as-in-existing-BenchmarkEnvironment" : "transport-default");
        Add(builder, "protocol", "v2-default-unmodified");
        Add(builder, "flushPolicy", "profile-default-no-override");
        Add(builder, "compression", "default-no-providers");
        Add(builder, "measurementScope", "same-process-generated-rpc-client-plus-server-plus-validation");
        Add(builder, "latencyDefinition", "individual-rpc-start-to-validated-completion-no-batch-latency");
        Add(builder, "workloadModel", "closed-loop-one-inflight-rpc-per-persistent-worker");
        Add(builder, "rawSamplesFile", _options.Output + ".samples.csv");
        Add(builder, "workersFile", _options.Output + ".workers.csv");
        Add(builder, "fairnessFile", _options.Output + ".fairness.csv");

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The executed binary path is unavailable.");
        Add(builder, "processExecutable", executable);
        Add(builder, "processExecutableSha256", FileHash(executable));
#if FLOW_EVIDENCE_NATIVE_AOT
        Add(builder, "measuredBinary", executable);
        Add(builder, "measuredBinarySha256", FileHash(executable));
#else
        var managedBinary = typeof(EvidenceRun).Assembly.Location;
        Add(builder, "measuredBinary", managedBinary);
        Add(builder, "measuredBinarySha256", FileHash(managedBinary));
        var runtimeBinary = typeof(StreamFlowController).Assembly.Location;
        Add(builder, "runtimeAssembly", runtimeBinary);
        Add(builder, "runtimeAssemblySha256", FileHash(runtimeBinary));
#endif
        foreach (var variable in new[]
        {
            "DOTNET_TieredPGO", "DOTNET_TieredCompilation", "DOTNET_TC_QuickJitForLoops",
            "DOTNET_ReadyToRun", "COMPlus_TieredPGO", "COMPlus_TieredCompilation",
            "COMPlus_ReadyToRun", "COMPlus_TC_QuickJitForLoops", "DOTNET_PROCESSOR_COUNT",
            "DOTNET_gcServer", "DOTNET_GCHeapCount", "DOTNET_GCConserveMemory",
            "DOTNET_ThreadPool_UnfairSemaphoreSpinLimit", "GITHUB_RUN_ID",
            "GITHUB_RUN_ATTEMPT", "GITHUB_SHA"
        })
            Add(builder, "env." + variable, Environment.GetEnvironmentVariable(variable) ?? "<unset>");
        return builder.ToString();
    }

    private string BuildReport(
        EvidenceEnvironment environment, TimeSpan cpu, long allocations, int gen0, int gen1,
        int gen2, TimeSpan pause, long threadPoolCompleted, int originalWorkerMin,
        int originalIoMin, int workerMin, int workerMax, int ioMax,
        int threadsBefore, int threadsAfter, long settleTicks, string sessionId,
        long settledEnd, TimeSpan settledCpu, long settledAllocations,
        int settledGen0, int settledGen1, int settledGen2, TimeSpan settledPause, long probeTicks)
    {
        var count = checked(_options.Concurrency * _options.MeasuredOpsPerWorker);
        var timings = new long[count];
        var offset = 0;
        foreach (var worker in _states)
        {
            if (worker.Completed != _options.MeasuredOpsPerWorker)
                throw new InvalidOperationException("A worker did not execute its fixed operation count.");
            for (var index = 0; index < worker.Completed; index++)
                timings[offset++] = worker.Ends[index] - worker.Starts[index];
        }
        Array.Sort(timings);
        var seconds = (_measurementEnd - _measurementStart) / (double)Stopwatch.Frequency;
        var settledSeconds = (settledEnd - _measurementStart) / (double)Stopwatch.Frequency;
        var logicalItems = (long)count * _options.Items;
        var streamDataItemsPerRpc = _options.Shape switch
        {
            "Unary" => 0L,
            "Duplex" => 2L * _options.Items,
            _ => _options.Items
        };
        var payloadTransitsPerRpc = _options.Shape == "Unary" ? 2L : streamDataItemsPerRpc;
        var builder = new StringBuilder(_identity);
        Add(builder, "status", "passed");
        long warmupRpcs = 0, firstWarmupStart = long.MaxValue, lastWarmupEnd = 0;
        long minWarmupRpcs = long.MaxValue, maxWarmupRpcs = 0;
        foreach (var worker in _states)
        {
            warmupRpcs += worker.WarmupCompleted;
            firstWarmupStart = Math.Min(firstWarmupStart, worker.WarmupStart);
            lastWarmupEnd = Math.Max(lastWarmupEnd, worker.WarmupEnd);
            minWarmupRpcs = Math.Min(minWarmupRpcs, worker.WarmupCompleted);
            maxWarmupRpcs = Math.Max(maxWarmupRpcs, worker.WarmupCompleted);
        }
        Add(builder, "actualWarmupRpcs", warmupRpcs);
        Add(builder, "actualWarmupMinRpcsPerWorker", minWarmupRpcs);
        Add(builder, "actualWarmupMaxRpcsPerWorker", maxWarmupRpcs);
        Add(builder, "actualWarmupWallSeconds", (lastWarmupEnd - firstWarmupStart) / (double)Stopwatch.Frequency);
        Add(builder, "successfulRpcs", count);
        Add(builder, "failedRpcs", 0);
        Add(builder, "elapsedSeconds", seconds);
        Add(builder, "throughputRpcPerSecond", count / seconds);
        Add(builder, "logicalItems", logicalItems);
        Add(builder, "throughputLogicalItemsPerSecond", logicalItems / seconds);
        Add(builder, "expectedStreamPayloadItemsPerRpc", streamDataItemsPerRpc);
        Add(builder, "throughputStreamPayloadItemsPerSecond", count * streamDataItemsPerRpc / seconds);
        Add(builder, "expectedApplicationPayloadBytesPerRpc",
            payloadTransitsPerRpc * _options.PayloadBytes);
        Add(builder, "wireDataFrameCount", "not-instrumented-not-inferred-from-logical-items");
        Add(builder, "wireBytes", "not-instrumented");
        Add(builder, "cpuTotalMicroseconds", cpu.TotalMicroseconds);
        Add(builder, "cpuMicrosecondsPerRpc", cpu.TotalMicroseconds / count);
        AddRatio(builder, "cpuMicrosecondsPerLogicalItem", cpu.TotalMicroseconds, logicalItems);
        Add(builder, "allocationBytesTotal", allocations);
        Add(builder, "allocationBytesPerRpc", (double)allocations / count);
        AddRatio(builder, "allocationBytesPerLogicalItem", allocations, logicalItems);
        Add(builder, "completedBoundaryDefinition", "all-measured-rpcs-validated-before-lifecycle-settlement");
        Add(builder, "settledElapsedSeconds", settledSeconds);
        Add(builder, "settledThroughputRpcPerSecond", count / settledSeconds);
        Add(builder, "settledThroughputLogicalItemsPerSecond", logicalItems / settledSeconds);
        Add(builder, "settledCpuTotalMicroseconds", settledCpu.TotalMicroseconds);
        Add(builder, "settledCpuMicrosecondsPerRpc", settledCpu.TotalMicroseconds / count);
        AddRatio(builder, "settledCpuMicrosecondsPerLogicalItem", settledCpu.TotalMicroseconds, logicalItems);
        Add(builder, "settledAllocationBytesTotal", settledAllocations);
        Add(builder, "settledAllocationBytesPerRpc", (double)settledAllocations / count);
        AddRatio(builder, "settledAllocationBytesPerLogicalItem", settledAllocations, logicalItems);
        Add(builder, "settledGen0Collections", settledGen0);
        Add(builder, "settledGen1Collections", settledGen1);
        Add(builder, "settledGen2Collections", settledGen2);
        Add(builder, "settledGcPauseTotalMicroseconds", settledPause.TotalMicroseconds);
        Add(builder, "settledBoundaryDefinition", "measured-rpcs-plus-observed-call-stream-settlement");
        Add(builder, "settledObserverCosts", "includes-WaitForIdle-polling-and-intermediate-completed-counter-observation");
        Add(builder, "settledExcludedWork", "raw-sample-file-IO-session-probe-report-formatting-and-transport-disposal");
        Add(builder, "p50Microseconds", QuantileUs(timings, 0.50));
        Add(builder, "p95Microseconds", QuantileUs(timings, 0.95));
        Add(builder, "p99Microseconds", QuantileUs(timings, 0.99));
        Add(builder, "p999Microseconds", QuantileUs(timings, 0.999));
        Add(builder, "maxMicroseconds", TicksToUs(timings[^1]));
        Add(builder, "quantileMethod", "nearest-rank");
        Add(builder, "p999SamplesInUpperTail", count / 1000.0);
        Add(builder, "gen0Collections", gen0);
        Add(builder, "gen1Collections", gen1);
        Add(builder, "gen2Collections", gen2);
        Add(builder, "gcPauseTotalMicroseconds", pause.TotalMicroseconds);
        Add(builder, "threadPoolCompletedWorkItems", threadPoolCompleted);
        Add(builder, "threadPoolCompletedWorkItemsPerRpc", (double)threadPoolCompleted / count);
        Add(builder, "threadPoolOriginalWorkerMinimum", originalWorkerMin);
        Add(builder, "threadPoolOriginalIoMinimum", originalIoMin);
        Add(builder, "threadPoolConfiguredWorkerMinimum", workerMin);
        Add(builder, "threadPoolWorkerMaximum", workerMax);
        Add(builder, "threadPoolIoMaximum", ioMax);
        Add(builder, "threadPoolThreadsBefore", threadsBefore);
        Add(builder, "threadPoolThreadsAfter", threadsAfter);
        Add(builder, "physicalReadyConnectionsAfter", environment.Client.ReadyConnectionCount);
        Add(builder, "samePhysicalSessionBeforeAndAfter", true);
        Add(builder, "physicalSessionId", sessionId);
        Add(builder, "pendingCallsAfterSettlement", environment.Client.PendingCallCount);
        Add(builder, "activeClientCallsAfterSettlement", environment.Client.ActiveClientCallCount);
        Add(builder, "activeClientStreamsAfterSettlement", environment.Client.ActiveClientStreamCount);
        Add(builder, "activeServerCallsAfterSettlement", environment.Server.ActiveCallCountForDiagnostics);
        Add(builder, "postCompletionSettlementMicroseconds", TicksToUs(settleTicks));
        Add(builder, "completedToSettledMicroseconds", TicksToUs(settledEnd - _measurementEnd));
        Add(builder, "postMeasurementProbeMicroseconds", TicksToUs(probeTicks));
        Add(builder, "actualClientStreamReceiveWindowBytes",
            environment.Runtime.FlowControl.StreamReceiveWindowBytes);
        Add(builder, "actualClientConnectionReceiveWindowBytes",
            environment.Runtime.FlowControl.ConnectionReceiveWindowBytes);
        Add(builder, "actualClientMaxSendQueueBytes", environment.Runtime.FlowControl.MaxSendQueueBytes);
        Add(builder, "actualClientMaxPreCreditSerializedBytes",
            environment.Runtime.FlowControl.MaxPreCreditSerializedBytes);
        Add(builder, "performanceProfile", environment.Runtime.PerformanceProfile.ToString());
        Add(builder, "schedulerCoordinationCounters", "not-instrumented");
        Add(builder, "queueDepthPeak", "not-instrumented");
        Add(builder, "preparedBudgetPeak", "not-instrumented");
        Add(builder, "fairnessDefinition", "per-worker-completions-at-common-wall-time-fractions");
        Add(builder, "fullAcceptanceGate", "false-stage1-throughput-evidence-only");
        return builder.ToString();
    }

    internal void WriteFailure(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.Output)!);
            WriteRawSamples();
            File.WriteAllText(_options.Output + ".failure.txt", exception.ToString(), Encoding.UTF8);
            File.WriteAllText(_options.Output,
                _identity + "status=failed\nexceptionFile=" + _options.Output + ".failure.txt\n",
                Encoding.UTF8);
        }
        catch (Exception artifactException)
        {
            Console.Error.WriteLine("Failed to preserve failure artifacts: " + artifactException);
        }
    }

    private void WriteRawSamples()
    {
        using var samples = new StreamWriter(_options.Output + ".samples.csv", false, Encoding.UTF8);
        using var workers = new StreamWriter(_options.Output + ".workers.csv", false, Encoding.UTF8);
        samples.WriteLine("worker,operation,start_timestamp,end_timestamp,latency_ticks,start_us_from_gate,end_us_from_gate,latency_us");
        workers.WriteLine("worker,completed,expected,first_start_us,last_end_us,active_span_us,mean_rpc_latency_us,max_rpc_latency_us,failed,warmup_completed,warmup_seconds");
        foreach (var state in _states)
        {
            long total = 0, max = 0;
            var completed = state.Completed;
            for (var index = 0; index < completed; index++)
            {
                var start = state.Starts[index];
                var end = state.Ends[index];
                var duration = end - start;
                total += duration;
                max = Math.Max(max, duration);
                samples.WriteLine(string.Join(",", state.Id, index, start, end, duration,
                    N(TicksToUs(start - _measurementStart)), N(TicksToUs(end - _measurementStart)),
                    N(TicksToUs(duration))));
            }
            var first = completed == 0 ? 0 : state.Starts[0];
            var last = completed == 0 ? 0 : state.Ends[completed - 1];
            workers.WriteLine(string.Join(",", state.Id, completed, _options.MeasuredOpsPerWorker,
                N(TicksToUs(first - _measurementStart)), N(TicksToUs(last - _measurementStart)),
                N(TicksToUs(last - first)),
                completed == 0 ? "N/A" : N(TicksToUs(total) / completed),
                N(TicksToUs(max)), state.Error is null ? "false" : "true",
                state.WarmupCompleted, N((state.WarmupEnd - state.WarmupStart) / (double)Stopwatch.Frequency)));
            if (state.Error is not null)
                File.WriteAllText(_options.Output + ".worker-" + state.Id + ".failure.txt",
                    state.Error, Encoding.UTF8);
        }

        using var fairness = new StreamWriter(_options.Output + ".fairness.csv", false, Encoding.UTF8);
        fairness.WriteLine("fraction,elapsed_us,min_completions,max_completions,total_completions,jain_index,per_worker_completions");
        if (_measurementStart == 0 || _measurementEnd <= _measurementStart || _states.Length == 0)
            return;
        foreach (var percentage in new[] { 10, 25, 50, 75, 90, 100 })
        {
            var elapsed = (_measurementEnd - _measurementStart) * percentage / 100;
            var threshold = _measurementStart + elapsed;
            var counts = new int[_states.Length];
            long sum = 0;
            double sumSquares = 0;
            var min = int.MaxValue;
            var max = 0;
            for (var worker = 0; worker < _states.Length; worker++)
            {
                var state = _states[worker];
                var completed = 0;
                while (completed < state.Completed && state.Ends[completed] <= threshold)
                    completed++;
                counts[worker] = completed;
                sum += completed;
                sumSquares += (double)completed * completed;
                min = Math.Min(min, completed);
                max = Math.Max(max, completed);
            }
            var jain = sumSquares == 0 ? "N/A" : N((double)sum * sum / (_states.Length * sumSquares));
            fairness.WriteLine(string.Join(",", N(percentage / 100.0), N(TicksToUs(elapsed)),
                min, max, sum, jain, string.Join(";", counts)));
        }
    }

    private static byte[] CreatePayload(int bytes)
    {
        if (bytes is 16 or 4096)
            return BenchmarkRpcService.GetPayload(bytes);
        var payload = new byte[bytes];
        // Match the existing zero-interior fixture style; validation checks all bytes.
        payload[0] = 17;
        payload[^1] = 31;
        return payload;
    }

    private static double QuantileUs(long[] sorted, double fraction)
        => TicksToUs(sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)]);
    private static double TicksToUs(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static void Add(StringBuilder builder, string key, string value)
        => builder.Append(key).Append('=').AppendLine(value.Replace('\r', ' ').Replace('\n', ' '));
    private static void Add(StringBuilder builder, string key, int value)
        => Add(builder, key, value.ToString(CultureInfo.InvariantCulture));
    private static void Add(StringBuilder builder, string key, long value)
        => Add(builder, key, value.ToString(CultureInfo.InvariantCulture));
    private static void Add(StringBuilder builder, string key, double value)
        => Add(builder, key, N(value));
    private static void Add(StringBuilder builder, string key, bool value)
        => Add(builder, key, value ? "true" : "false");
    private static void AddRatio(StringBuilder builder, string key, double numerator, long denominator)
        => Add(builder, key, denominator == 0 ? "N/A" : N(numerator / denominator));
}

internal sealed class WorkerState
{
    internal int Id { get; }
    internal long[] Starts { get; }
    internal long[] Ends { get; }
    internal int Completed;
    internal long WarmupStart;
    internal long WarmupEnd;
    internal long WarmupCompleted;
    internal string? Error;

    internal WorkerState(int id, int operations)
    {
        Id = id;
        Starts = new long[operations];
        Ends = new long[operations];
    }
}

internal sealed class CountedSignal
{
    private readonly int _target;
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;
    internal Task Task => _all.Task;
    internal CountedSignal(int target) => _target = target;
    internal void Signal()
    {
        if (Interlocked.Increment(ref _count) == _target)
            _all.TrySetResult();
    }
}

internal sealed class RpcOperation
{
    private readonly Options _options;
    private readonly byte[] _payload;
    private readonly byte[][] _values;
    private readonly long _expectedScore;
    internal IBenchmarkRpc Rpc { get; set; } = null!;

    internal RpcOperation(Options options, byte[] payload, byte[][] values)
    {
        _options = options;
        _payload = payload;
        _values = values;
        _expectedScore = options.Items * BenchmarkRpcService.GetPayloadScore(payload);
    }

    internal async ValueTask InvokeAsync()
    {
        if (_options.Shape == "Unary")
        {
            Verify(await Rpc.EchoBytesAsync(_payload).ConfigureAwait(false));
            return;
        }
        if (_options.Shape == "Client")
        {
            var score = await Rpc.UploadPayloadsAsync(
                BenchmarkEnvironment.ToStream(_values)).ConfigureAwait(false);
            if (score != _expectedScore)
                throw new InvalidDataException($"Client stream score mismatch: {score} != {_expectedScore}.");
            return;
        }

        var stream = _options.Shape == "Server"
            ? Rpc.DownloadPayloadsAsync(_options.Items, _options.PayloadBytes)
            : Rpc.DuplexPayloadsAsync(BenchmarkEnvironment.ToStream(_values));
        var count = 0;
        await foreach (var payload in stream.ConfigureAwait(false))
        {
            Verify(payload);
            if (++count > _options.Items)
                throw new InvalidDataException("Received too many stream items.");
        }
        if (count != _options.Items)
            throw new InvalidDataException($"Received {count} items; expected {_options.Items}.");
    }

    private void Verify(byte[] payload)
    {
        if (!payload.AsSpan().SequenceEqual(_payload))
            throw new InvalidDataException("Outgoing RPC payload failed complete byte comparison.");
    }
}

