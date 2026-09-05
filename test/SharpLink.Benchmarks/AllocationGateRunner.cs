using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class AllocationGateRunner
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly CaseDefinition[] Cases =
    [
        new("rpc-add-sharedmemory-c1", 1, 512, 4_000, CaseKind.Add),
        new("rpc-add-sharedmemory-c8", 8, 512, 4_096, CaseKind.Add),
        new("rpc-oneway-sharedmemory-c1", 1, 512, 4_000, CaseKind.OneWay),
        new("send-pump-idle-wake-balanced", 1, 256, 2_000, CaseKind.SendPumpIdleWake)
    ];

    internal static async Task RunAsync(string[] args)
    {
        var options = GateOptions.Parse(args);
        var report = new AllocationGateReport
        {
            SchemaVersion = SchemaVersion,
            RuntimeVersion = Environment.Version.ToString(),
            RuntimeMajor = Environment.Version.Major,
            Configuration = IsReleaseBuild ? "Release" : "NonRelease",
            BudgetPath = options.BudgetPath,
            Filter = options.Filter,
            InjectedBytesPerOperation = options.InjectedBytesPerOperation,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            EnsureReleaseBuild();
            var budgets = LoadBudgets(options.BudgetPath);
            ValidateBudgetDocument(budgets);
            if (budgets.RuntimeMajor != Environment.Version.Major)
            {
                throw new InvalidOperationException(
                    $"Allocation budgets target .NET major {budgets.RuntimeMajor}, but the active runtime is {Environment.Version}. " +
                    "Rebaseline explicitly when the runtime major changes.");
            }

            var selected = ResolveCases(options.Filter);
            foreach (var definition in selected)
            {
                if (!budgets.Cases.TryGetValue(definition.Name, out var budget))
                    throw new InvalidDataException($"Missing allocation budget for case '{definition.Name}'.");
                ValidateBudget(definition.Name, budget);
            }

            var rpcCases = selected.Where(static item => item.Kind is CaseKind.Add or CaseKind.OneWay).ToArray();
            if (rpcCases.Length != 0)
            {
                await using var environment = await BenchmarkEnvironment.CreateSharedMemoryAsync().ConfigureAwait(false);
                foreach (var definition in rpcCases)
                {
                    report.Cases.Add(await RunRpcCaseAsync(
                        definition,
                        budgets.Cases[definition.Name],
                        environment,
                        options.InjectedBytesPerOperation).ConfigureAwait(false));
                }
            }

            foreach (var definition in selected.Where(static item => item.Kind == CaseKind.SendPumpIdleWake))
            {
                report.Cases.Add(await RunSendPumpCaseAsync(
                    definition,
                    budgets.Cases[definition.Name],
                    options.InjectedBytesPerOperation).ConfigureAwait(false));
            }

            report.Passed = report.Cases.Count != 0 && report.Cases.All(static item => item.Passed);
            if (!report.Passed)
                report.Errors.Add("One or more allocation cases exceeded their absolute or stability budget.");
        }
        catch (Exception exception)
        {
            report.Passed = false;
            report.Errors.Add(exception.ToString());
        }
        finally
        {
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteReport(options.OutputPath, report);
            PrintReport(report, options.OutputPath);
        }

        if (!report.Passed)
            Environment.ExitCode = 1;
    }

    internal static void RunSelfTests(string[] args)
    {
        var options = GateOptions.Parse(args, allowBudgetless: true);
        var report = new AllocationGateReport
        {
            SchemaVersion = SchemaVersion,
            RuntimeVersion = Environment.Version.ToString(),
            RuntimeMajor = Environment.Version.Major,
            Configuration = IsReleaseBuild ? "Release" : "NonRelease",
            BudgetPath = options.BudgetPath,
            Filter = "self-test",
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            EnsureReleaseBuild();
            RunPolicySelfTests();
            report.Passed = true;
        }
        catch (Exception exception)
        {
            report.Passed = false;
            report.Errors.Add(exception.ToString());
        }
        finally
        {
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteReport(options.OutputPath, report);
            PrintReport(report, options.OutputPath);
        }

        if (!report.Passed)
            Environment.ExitCode = 1;
    }

    private static async Task<AllocationCaseReport> RunRpcCaseAsync(
        CaseDefinition definition,
        AllocationBudget budget,
        BenchmarkEnvironment environment,
        int injectedBytesPerOperation)
    {
        Func<ValueTask> operation = definition.Kind switch
        {
            CaseKind.Add => () => AddOnceAsync(environment.Rpc),
            CaseKind.OneWay => () => environment.Rpc.PublishEventAsync(7, 11, "allocation-gate"),
            _ => throw new InvalidOperationException($"Unsupported RPC case kind {definition.Kind}.")
        };

        return await RunMeasuredCaseAsync(
            definition,
            budget,
            operation,
            injectedBytesPerOperation).ConfigureAwait(false);
    }

    private static async Task<AllocationCaseReport> RunSendPumpCaseAsync(
        CaseDefinition definition,
        AllocationBudget budget,
        int injectedBytesPerOperation)
    {
        var benchmark = new SendPumpIdleWakeBenchmarks
        {
            Scenario = SendPumpIdleWakeBenchmarks.IdleWakeScenario.Balanced
        };
        benchmark.Setup();
        try
        {
            async ValueTask Operation()
            {
                var queued = await benchmark.IdleWakeForceFlushCycle().ConfigureAwait(false);
                if (queued != 0)
                    throw new InvalidOperationException($"Send pump did not drain: {queued} bytes remain queued.");
            }

            return await RunMeasuredCaseAsync(
                definition,
                budget,
                Operation,
                injectedBytesPerOperation).ConfigureAwait(false);
        }
        finally
        {
            await benchmark.Cleanup().ConfigureAwait(false);
        }
    }

    private static async Task<AllocationCaseReport> RunMeasuredCaseAsync(
        CaseDefinition definition,
        AllocationBudget budget,
        Func<ValueTask> operation,
        int injectedBytesPerOperation)
    {
        await ExecuteWorkersAsync(
            definition.WarmupOperations,
            definition.Concurrency,
            operation,
            injectedBytesPerOperation).ConfigureAwait(false);

        var samples = new List<double>(budget.Samples);
        var rawSamples = new List<AllocationSampleReport>(budget.Samples);
        for (var sampleIndex = 0; sampleIndex < budget.Samples; sampleIndex++)
        {
            ForceGc();
            var sample = await MeasureAsync(
                definition.OperationsPerSample,
                definition.Concurrency,
                operation,
                injectedBytesPerOperation).ConfigureAwait(false);
            samples.Add(sample.BytesPerOperation);
            rawSamples.Add(new AllocationSampleReport
            {
                Index = sampleIndex,
                CompletedOperations = sample.CompletedOperations,
                AllocatedBytes = sample.AllocatedBytes,
                BytesPerOperation = sample.BytesPerOperation
            });
        }

        samples.Sort();
        var median = Median(samples);
        var min = samples[0];
        var max = samples[^1];
        var spread = max - min;
        var enoughSamples = samples.Count >= 5;
        var withinAllocationBudget = median <= budget.MaxBytesPerOperation;
        var stable = spread <= budget.MaxSpreadBytesPerOperation;
        var passed = enoughSamples && withinAllocationBudget && stable;

        return new AllocationCaseReport
        {
            Name = definition.Name,
            Concurrency = definition.Concurrency,
            WarmupOperations = definition.WarmupOperations,
            OperationsPerSample = definition.OperationsPerSample,
            SampleCount = samples.Count,
            MaxBytesPerOperation = budget.MaxBytesPerOperation,
            MaxSpreadBytesPerOperation = budget.MaxSpreadBytesPerOperation,
            MedianBytesPerOperation = median,
            MinBytesPerOperation = min,
            MaxBytesPerOperationObserved = max,
            SpreadBytesPerOperation = spread,
            Passed = passed,
            Failure = passed
                ? null
                : BuildFailure(enoughSamples, withinAllocationBudget, stable, median, spread, budget),
            Samples = rawSamples
        };
    }

    private static async ValueTask AddOnceAsync(IBenchmarkRpc rpc)
    {
        var value = await rpc.AddAsync(20, 22).ConfigureAwait(false);
        if (value != 42)
            throw new InvalidOperationException($"Allocation gate RPC returned {value} instead of 42.");
    }

    private static async Task<Measurement> MeasureAsync(
        int operations,
        int concurrency,
        Func<ValueTask> operation,
        int injectedBytesPerOperation)
    {
        var counter = new CompletionCounter();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = CreateWorkers(
            operations,
            concurrency,
            start.Task,
            operation,
            injectedBytesPerOperation,
            counter);
        var completion = Task.WhenAll(workers);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        start.TrySetResult();
        await completion.ConfigureAwait(false);
        var after = GC.GetTotalAllocatedBytes(precise: true);

        var completed = Volatile.Read(ref counter.Completed);
        if (completed != operations)
        {
            throw new InvalidOperationException(
                $"Allocation sample completed {completed} successful operations out of {operations}; refusing a false denominator.");
        }

        var allocated = Math.Max(0, after - before);
        return new Measurement(completed, allocated, ComputeBytesPerOperation(allocated, completed, operations));
    }

    private static Task ExecuteWorkersAsync(
        int operations,
        int concurrency,
        Func<ValueTask> operation,
        int injectedBytesPerOperation)
    {
        var counter = new CompletionCounter();
        return Task.WhenAll(CreateWorkers(
            operations,
            concurrency,
            Task.CompletedTask,
            operation,
            injectedBytesPerOperation,
            counter));
    }

    private static Task[] CreateWorkers(
        int operations,
        int concurrency,
        Task start,
        Func<ValueTask> operation,
        int injectedBytesPerOperation,
        CompletionCounter counter)
    {
        if (operations <= 0)
            throw new ArgumentOutOfRangeException(nameof(operations));
        if (concurrency <= 0 || concurrency > operations)
            throw new ArgumentOutOfRangeException(nameof(concurrency));

        var workers = new Task[concurrency];
        var baseCount = operations / concurrency;
        var remainder = operations % concurrency;
        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            var count = baseCount + (workerIndex < remainder ? 1 : 0);
            workers[workerIndex] = RunWorkerAsync(
                start,
                count,
                operation,
                injectedBytesPerOperation,
                counter);
        }
        return workers;
    }

    private static async Task RunWorkerAsync(
        Task start,
        int operations,
        Func<ValueTask> operation,
        int injectedBytesPerOperation,
        CompletionCounter counter)
    {
        await start.ConfigureAwait(false);
        for (var index = 0; index < operations; index++)
        {
            await operation().ConfigureAwait(false);
            if (injectedBytesPerOperation > 0)
            {
                var injected = new byte[injectedBytesPerOperation];
                GC.KeepAlive(injected);
            }
            Interlocked.Increment(ref counter.Completed);
        }
    }

    private static double ComputeBytesPerOperation(long allocatedBytes, int completed, int requested)
    {
        if (requested <= 0 || completed != requested)
            throw new InvalidOperationException("Only fully completed samples may be normalized per operation.");
        return allocatedBytes / (double)completed;
    }

    private static string BuildFailure(
        bool enoughSamples,
        bool withinAllocationBudget,
        bool stable,
        double median,
        double spread,
        AllocationBudget budget)
    {
        var reasons = new List<string>(3);
        if (!enoughSamples)
            reasons.Add("fewer than five samples");
        if (!withinAllocationBudget)
        {
            reasons.Add(
                $"median {median:F3} B/op exceeds budget {budget.MaxBytesPerOperation:F3} B/op");
        }
        if (!stable)
        {
            reasons.Add(
                $"sample spread {spread:F3} B/op exceeds stability budget {budget.MaxSpreadBytesPerOperation:F3} B/op");
        }
        return string.Join("; ", reasons);
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
            throw new InvalidOperationException("Cannot compute the median of an empty sample set.");
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2d
            : sorted[middle];
    }

    private static void ForceGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    private static AllocationBudgetDocument LoadBudgets(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Allocation budget file was not found.", path);
        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<AllocationBudgetDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("Allocation budget document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Allocation budget document is malformed.", exception);
        }
    }

    private static void ValidateBudgetDocument(AllocationBudgetDocument document)
    {
        if (document.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Unsupported allocation budget schema {document.SchemaVersion}.");
        if (document.RuntimeMajor <= 0)
            throw new InvalidDataException("runtimeMajor must be a positive integer.");
        if (document.Cases.Count == 0)
            throw new InvalidDataException("Allocation budget document contains no cases.");
        foreach (var pair in document.Cases)
            ValidateBudget(pair.Key, pair.Value);
    }

    private static void ValidateBudget(string name, AllocationBudget budget)
    {
        if (budget.Samples < 5)
            throw new InvalidDataException($"Allocation case '{name}' must use at least five samples.");
        if (budget.MaxBytesPerOperation < 0 || budget.MaxSpreadBytesPerOperation < 0)
            throw new InvalidDataException($"Allocation case '{name}' contains a negative budget.");
    }

    private static IReadOnlyList<CaseDefinition> ResolveCases(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return Cases;

        var requested = filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0)
            throw new ArgumentException("Allocation gate filter is empty.", nameof(filter));
        var selected = Cases.Where(item => requested.Contains(item.Name, StringComparer.Ordinal)).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException($"Allocation gate filter '{filter}' matched no cases.");
        if (selected.Length != requested.Distinct(StringComparer.Ordinal).Count())
        {
            var missing = requested.Where(name => selected.All(item => item.Name != name)).Distinct(StringComparer.Ordinal);
            throw new InvalidOperationException($"Unknown allocation gate case(s): {string.Join(", ", missing)}.");
        }
        return selected;
    }

    private static void RunPolicySelfTests()
    {
        if (ComputeBytesPerOperation(0, 100, 100) != 0)
            throw new InvalidOperationException("Zero-allocation normalization self-test failed.");

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int allocationIterations = 2_048;
        for (var index = 0; index < allocationIterations; index++)
        {
            var payload = new byte[128];
            GC.KeepAlive(payload);
        }
        var deliberate = GC.GetAllocatedBytesForCurrentThread() - before;
        if (deliberate < 128L * allocationIterations)
            throw new InvalidOperationException("Deliberate new byte[128] allocation self-test was not observed.");

        ExpectFailure(() => ComputeBytesPerOperation(1_000, 99, 100), "false denominator");
        ExpectFailure(() => ResolveCases("does-not-exist"), "empty case filter");
        ExpectFailure(
            () => JsonSerializer.Deserialize<AllocationBudgetDocument>("{ broken", JsonOptions),
            "malformed JSON");
        ExpectFailure(
            () => LoadBudgets(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")),
            "missing budget file");

        var passBudget = new AllocationBudget { Samples = 5, MaxBytesPerOperation = 100, MaxSpreadBytesPerOperation = 10 };
        var passSamples = new[] { 95d, 96d, 97d, 98d, 99d };
        AssertSyntheticEvaluation(passSamples, passBudget, expected: true);
        AssertSyntheticEvaluation(new[] { 100d, 101d, 102d, 103d, 104d }, passBudget, expected: false);
        AssertSyntheticEvaluation(new[] { 90d, 91d, 92d, 93d, 110d }, passBudget, expected: false);
        ExpectFailure(() => ValidateBudget("insufficient", new AllocationBudget
        {
            Samples = 4,
            MaxBytesPerOperation = 100,
            MaxSpreadBytesPerOperation = 10
        }), "insufficient samples");

        var json = JsonSerializer.Serialize(new AllocationGateReport
        {
            SchemaVersion = SchemaVersion,
            Passed = true,
            RuntimeVersion = Environment.Version.ToString()
        }, JsonOptions);
        if (!json.Contains("\"passed\": true", StringComparison.Ordinal))
            throw new InvalidOperationException("JSON report serialization self-test failed.");
    }

    private static void AssertSyntheticEvaluation(
        IReadOnlyList<double> samples,
        AllocationBudget budget,
        bool expected)
    {
        var ordered = samples.Order().ToArray();
        var median = Median(ordered);
        var spread = ordered[^1] - ordered[0];
        var actual = ordered.Length >= 5 &&
                     median <= budget.MaxBytesPerOperation &&
                     spread <= budget.MaxSpreadBytesPerOperation;
        if (actual != expected)
            throw new InvalidOperationException("Allocation budget pass/fail self-test failed.");
    }

    private static void ExpectFailure(Action action, string name)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException($"Allocation gate self-test '{name}' did not fail closed.");
    }

    private static void WriteReport(string path, AllocationGateReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    private static void PrintReport(AllocationGateReport report, string outputPath)
    {
        foreach (var item in report.Cases)
        {
            Console.WriteLine(
                $"[AllocationGate] {item.Name}: median={item.MedianBytesPerOperation:F3} B/op " +
                $"range={item.MinBytesPerOperation:F3}..{item.MaxBytesPerOperationObserved:F3} " +
                $"spread={item.SpreadBytesPerOperation:F3} budget={item.MaxBytesPerOperation:F3} " +
                $"stability={item.MaxSpreadBytesPerOperation:F3} => {(item.Passed ? "PASS" : "FAIL")}");
        }
        foreach (var error in report.Errors)
            Console.Error.WriteLine($"[AllocationGate] {error}");
        Console.WriteLine($"[AllocationGate] report={outputPath} result={(report.Passed ? "PASS" : "FAIL")}");
    }

    private static void EnsureReleaseBuild()
    {
        if (!IsReleaseBuild)
            throw new InvalidOperationException("Allocation regression gates must run from a Release build.");
    }

    private static bool IsReleaseBuild
    {
        get
        {
#if SHARPLINK_RELEASE_BUILD
            return true;
#else
            return false;
#endif
        }
    }

    private enum CaseKind
    {
        Add,
        OneWay,
        SendPumpIdleWake
    }

    private sealed record CaseDefinition(
        string Name,
        int Concurrency,
        int WarmupOperations,
        int OperationsPerSample,
        CaseKind Kind);

    private sealed class CompletionCounter
    {
        internal int Completed;
    }

    private readonly record struct Measurement(
        int CompletedOperations,
        long AllocatedBytes,
        double BytesPerOperation);

    private sealed class GateOptions
    {
        internal string BudgetPath { get; private init; } = "eng/perf/allocation-budgets.json";
        internal string OutputPath { get; private init; } = "artifacts/perf/allocation-gate.json";
        internal string? Filter { get; private init; }
        internal int InjectedBytesPerOperation { get; private init; }

        internal static GateOptions Parse(string[] args, bool allowBudgetless = false)
        {
            var budgetPath = "eng/perf/allocation-budgets.json";
            var outputPath = "artifacts/perf/allocation-gate.json";
            string? filter = null;
            var injected = 0;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--budgets":
                        budgetPath = RequireValue(args, ref index, "--budgets");
                        break;
                    case "--output":
                        outputPath = RequireValue(args, ref index, "--output");
                        break;
                    case "--filter":
                        filter = RequireValue(args, ref index, "--filter");
                        break;
                    case "--inject-bytes-per-operation":
                        var value = RequireValue(args, ref index, "--inject-bytes-per-operation");
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out injected) || injected < 0)
                            throw new ArgumentException("Injected bytes per operation must be a non-negative integer.");
                        break;
                    default:
                        throw new ArgumentException($"Unknown allocation gate argument '{args[index]}'.");
                }
            }

            if (!allowBudgetless && string.IsNullOrWhiteSpace(budgetPath))
                throw new ArgumentException("Allocation budget path is required.");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Allocation gate output path is required.");
            return new GateOptions
            {
                BudgetPath = budgetPath,
                OutputPath = outputPath,
                Filter = filter,
                InjectedBytesPerOperation = injected
            };
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException($"{option} requires a value.");
            return args[index];
        }
    }

    private sealed class AllocationBudgetDocument
    {
        public int SchemaVersion { get; set; }
        public int RuntimeMajor { get; set; }
        public Dictionary<string, AllocationBudget> Cases { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class AllocationBudget
    {
        public int Samples { get; set; }
        public double MaxBytesPerOperation { get; set; }
        public double MaxSpreadBytesPerOperation { get; set; }
    }

    private sealed class AllocationGateReport
    {
        public int SchemaVersion { get; set; }
        public bool Passed { get; set; }
        public string RuntimeVersion { get; set; } = string.Empty;
        public int RuntimeMajor { get; set; }
        public string Configuration { get; set; } = string.Empty;
        public string BudgetPath { get; set; } = string.Empty;
        public string? Filter { get; set; }
        public int InjectedBytesPerOperation { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public List<AllocationCaseReport> Cases { get; set; } = [];
        public List<string> Errors { get; set; } = [];
    }

    private sealed class AllocationCaseReport
    {
        public string Name { get; set; } = string.Empty;
        public int Concurrency { get; set; }
        public int WarmupOperations { get; set; }
        public int OperationsPerSample { get; set; }
        public int SampleCount { get; set; }
        public double MaxBytesPerOperation { get; set; }
        public double MaxSpreadBytesPerOperation { get; set; }
        public double MedianBytesPerOperation { get; set; }
        public double MinBytesPerOperation { get; set; }
        public double MaxBytesPerOperationObserved { get; set; }
        public double SpreadBytesPerOperation { get; set; }
        public bool Passed { get; set; }
        public string? Failure { get; set; }
        public List<AllocationSampleReport> Samples { get; set; } = [];
    }

    private sealed class AllocationSampleReport
    {
        public int Index { get; set; }
        public int CompletedOperations { get; set; }
        public long AllocatedBytes { get; set; }
        public double BytesPerOperation { get; set; }
    }
}
