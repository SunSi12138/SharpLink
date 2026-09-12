using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        var report = CreateReport(options, "gate");
        try
        {
            EnsureReleaseBuild();
            var budgets = LoadBudgets(options.BudgetPath);
            ValidateBudgetDocument(budgets);
            if (budgets.RuntimeMajor != Environment.Version.Major)
            {
                throw new InvalidOperationException(
                    $"Budgets target .NET {budgets.RuntimeMajor}, active runtime is {Environment.Version}; explicit rebaseline required.");
            }

            var selected = ResolveCases(options.Filter);
            foreach (var definition in selected)
            {
                if (!budgets.Cases.TryGetValue(definition.Name, out var budget))
                    throw new InvalidDataException($"Missing budget for '{definition.Name}'.");
                ValidateBudget(definition.Name, budget);
            }

            var rpcCases = selected.Where(static item => item.Kind != CaseKind.SendPumpIdleWake).ToArray();
            if (rpcCases.Length > 0)
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

            report.Passed = report.Cases.Count > 0 && report.Cases.All(static item => item.Passed);
            if (!report.Passed)
                report.Errors.Add("One or more allocation cases failed their budget or stability check.");
        }
        catch (Exception exception)
        {
            report.Passed = false;
            report.Errors.Add(exception.ToString());
        }
        finally
        {
            CompleteAndWrite(report, options.OutputPath);
        }

        if (!report.Passed)
            Environment.ExitCode = 1;
    }

    internal static void RunSelfTests(string[] args)
    {
        var options = GateOptions.Parse(args);
        var report = CreateReport(options, "self-test");
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
            CompleteAndWrite(report, options.OutputPath);
        }

        if (!report.Passed)
            Environment.ExitCode = 1;
    }

    private static AllocationGateReport CreateReport(GateOptions options, string mode) => new()
    {
        SchemaVersion = SchemaVersion,
        Mode = mode,
        RuntimeVersion = Environment.Version.ToString(),
        RuntimeMajor = Environment.Version.Major,
        Configuration = IsReleaseBuild ? "Release" : "NonRelease",
        BudgetPath = options.BudgetPath,
        Filter = options.Filter,
        InjectedBytesPerOperation = options.InjectedBytesPerOperation,
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    private static async Task<AllocationCaseReport> RunRpcCaseAsync(
        CaseDefinition definition,
        AllocationBudget budget,
        BenchmarkEnvironment environment,
        int injectedBytesPerOperation)
    {
        Func<ValueTask> operation;
        if (definition.Kind == CaseKind.Add)
        {
            operation = () => AddOnceAsync(environment.Rpc);
        }
        else
        {
            var nextPublished = environment.LocalService.PublishedCount;
            operation = async () =>
            {
                var target = Interlocked.Increment(ref nextPublished);
                await environment.Rpc.PublishEventAsync(7, 11, "allocation-gate").ConfigureAwait(false);
                WaitUntilPublished(environment.LocalService, target);
            };
        }

        return await RunMeasuredCaseAsync(
            definition, budget, operation, injectedBytesPerOperation).ConfigureAwait(false);
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
                    throw new InvalidOperationException($"Send pump left {queued} bytes queued.");
            }

            return await RunMeasuredCaseAsync(
                definition, budget, Operation, injectedBytesPerOperation).ConfigureAwait(false);
        }
        finally
        {
            await benchmark.Cleanup().ConfigureAwait(false);
        }
    }

    private static async ValueTask AddOnceAsync(IBenchmarkRpc rpc)
    {
        if (await rpc.AddAsync(20, 22).ConfigureAwait(false) != 42)
            throw new InvalidOperationException("Tiny unary allocation fixture returned the wrong value.");
    }

    private static void WaitUntilPublished(BenchmarkRpcService service, long target)
    {
        var deadline = Stopwatch.GetTimestamp() + 5L * Stopwatch.Frequency;
        var spin = new SpinWait();
        while (service.PublishedCount < target)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException($"OneWay fixture did not publish operation {target} within five seconds.");
            spin.SpinOnce();
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

        var samples = new List<AllocationSampleReport>(budget.Samples);
        for (var index = 0; index < budget.Samples; index++)
        {
            ForceGc();
            samples.Add(await MeasureAsync(
                index,
                definition.OperationsPerSample,
                definition.Concurrency,
                operation,
                injectedBytesPerOperation).ConfigureAwait(false));
        }

        var ordered = samples.Select(static item => item.BytesPerOperation).OrderBy(static value => value).ToArray();
        var median = Median(ordered);
        var min = ordered[0];
        var max = ordered[^1];
        var spread = max - min;
        var enough = ordered.Length >= 5;
        var within = median <= budget.MaxBytesPerOperation;
        var stable = spread <= budget.MaxSpreadBytesPerOperation;
        var passed = enough && within && stable;

        return new AllocationCaseReport
        {
            Name = definition.Name,
            Concurrency = definition.Concurrency,
            WarmupOperations = definition.WarmupOperations,
            OperationsPerSample = definition.OperationsPerSample,
            SampleCount = ordered.Length,
            MaxBytesPerOperation = budget.MaxBytesPerOperation,
            MaxSpreadBytesPerOperation = budget.MaxSpreadBytesPerOperation,
            MedianBytesPerOperation = median,
            MinBytesPerOperation = min,
            MaxBytesPerOperationObserved = max,
            SpreadBytesPerOperation = spread,
            Passed = passed,
            Failure = passed ? null : BuildFailure(enough, within, stable, median, spread, budget),
            Samples = samples
        };
    }

    private static async Task<AllocationSampleReport> MeasureAsync(
        int sampleIndex,
        int operations,
        int concurrency,
        Func<ValueTask> operation,
        int injectedBytesPerOperation)
    {
        var counter = new CompletionCounter();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = CreateWorkers(
            operations, concurrency, start.Task, operation, injectedBytesPerOperation, counter);
        var completion = Task.WhenAll(workers);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        start.TrySetResult();
        await completion.ConfigureAwait(false);
        var after = GC.GetTotalAllocatedBytes(precise: true);
        var completed = Volatile.Read(ref counter.Completed);
        if (completed != operations)
        {
            throw new InvalidOperationException(
                $"Sample completed {completed}/{operations} operations; refusing a false denominator.");
        }

        var allocated = Math.Max(0, after - before);
        return new AllocationSampleReport
        {
            Index = sampleIndex,
            CompletedOperations = completed,
            AllocatedBytes = allocated,
            BytesPerOperation = ComputeBytesPerOperation(allocated, completed, operations)
        };
    }

    private static Task ExecuteWorkersAsync(
        int operations,
        int concurrency,
        Func<ValueTask> operation,
        int injectedBytesPerOperation)
    {
        var counter = new CompletionCounter();
        return Task.WhenAll(CreateWorkers(
            operations, concurrency, Task.CompletedTask, operation, injectedBytesPerOperation, counter));
    }

    private static Task[] CreateWorkers(
        int operations,
        int concurrency,
        Task start,
        Func<ValueTask> operation,
        int injectedBytesPerOperation,
        CompletionCounter counter)
    {
        if (operations <= 0 || concurrency <= 0 || concurrency > operations)
            throw new ArgumentOutOfRangeException(nameof(operations));

        var workers = new Task[concurrency];
        var baseCount = operations / concurrency;
        var remainder = operations % concurrency;
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = RunWorkerAsync(
                start,
                baseCount + (index < remainder ? 1 : 0),
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
            throw new InvalidOperationException("Only fully completed samples may be normalized.");
        return allocatedBytes / (double)completed;
    }

    private static string BuildFailure(
        bool enough,
        bool within,
        bool stable,
        double median,
        double spread,
        AllocationBudget budget)
    {
        var reasons = new List<string>();
        if (!enough)
            reasons.Add("fewer than five samples");
        if (!within)
            reasons.Add($"median {median:F3} B/op exceeds {budget.MaxBytesPerOperation:F3} B/op");
        if (!stable)
            reasons.Add($"spread {spread:F3} B/op exceeds {budget.MaxSpreadBytesPerOperation:F3} B/op");
        return string.Join("; ", reasons);
    }

    private static void ForceGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    private static double Median(IReadOnlyList<double> ordered)
    {
        if (ordered.Count == 0)
            throw new InvalidOperationException("No allocation samples were produced.");
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static AllocationBudgetDocument LoadBudgets(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Allocation budget file was not found.", path);
        try
        {
            return JsonSerializer.Deserialize<AllocationBudgetDocument>(File.ReadAllText(path), JsonOptions)
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
            throw new InvalidDataException($"Unsupported budget schema {document.SchemaVersion}.");
        if (document.RuntimeMajor <= 0 || document.Cases.Count == 0)
            throw new InvalidDataException("Budget document must define runtimeMajor and at least one case.");
        foreach (var pair in document.Cases)
            ValidateBudget(pair.Key, pair.Value);
    }

    private static void ValidateBudget(string name, AllocationBudget budget)
    {
        if (budget.Samples < 5)
            throw new InvalidDataException($"'{name}' must use at least five samples.");
        if (budget.MaxBytesPerOperation < 0 || budget.MaxSpreadBytesPerOperation < 0)
            throw new InvalidDataException($"'{name}' contains a negative budget.");
    }

    private static IReadOnlyList<CaseDefinition> ResolveCases(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return Cases;
        var requested = filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0)
            throw new ArgumentException("Allocation filter is empty.", nameof(filter));
        var selected = Cases.Where(item => requested.Contains(item.Name, StringComparer.Ordinal)).ToArray();
        var missing = requested.Where(name => selected.All(item => item.Name != name)).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0 || missing.Length > 0)
            throw new InvalidOperationException($"Unknown allocation case(s): {string.Join(", ", missing)}.");
        return selected;
    }

    private static void RunPolicySelfTests()
    {
        if (ComputeBytesPerOperation(0, 100, 100) != 0)
            throw new InvalidOperationException("Zero-allocation self-test failed.");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 2_048; index++)
        {
            var payload = new byte[128];
            GC.KeepAlive(payload);
        }
        if (GC.GetAllocatedBytesForCurrentThread() - before < 128L * 2_048)
            throw new InvalidOperationException("new byte[128] self-test was not observed.");

        ExpectFailure(() => ComputeBytesPerOperation(1_000, 99, 100), "false denominator");
        ExpectFailure(() => ResolveCases("does-not-exist"), "unknown filter");
        ExpectFailure(() => JsonSerializer.Deserialize<AllocationBudgetDocument>("{broken", JsonOptions), "malformed budget");
        ExpectFailure(
            () => LoadBudgets(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")),
            "missing budget");
        ExpectFailure(
            () => ValidateBudget("insufficient", new AllocationBudget
            {
                Samples = 4,
                MaxBytesPerOperation = 100,
                MaxSpreadBytesPerOperation = 10
            }),
            "insufficient samples");

        AssertSynthetic(new[] { 95d, 96d, 97d, 98d, 99d }, 100, 10, expected: true);
        AssertSynthetic(new[] { 100d, 101d, 102d, 103d, 104d }, 100, 10, expected: false);
        AssertSynthetic(new[] { 90d, 91d, 92d, 93d, 110d }, 100, 10, expected: false);

        var serialized = JsonSerializer.Serialize(new AllocationGateReport { Passed = true }, JsonOptions);
        if (!serialized.Contains("\"passed\": true", StringComparison.Ordinal))
            throw new InvalidOperationException("JSON serialization self-test failed.");
    }

    private static void AssertSynthetic(
        IReadOnlyList<double> samples,
        double maxBytes,
        double maxSpread,
        bool expected)
    {
        var ordered = samples.OrderBy(static value => value).ToArray();
        var actual = ordered.Length >= 5 &&
                     Median(ordered) <= maxBytes &&
                     ordered[^1] - ordered[0] <= maxSpread;
        if (actual != expected)
            throw new InvalidOperationException("Budget pass/fail self-test failed.");
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
        throw new InvalidOperationException($"Self-test '{name}' did not fail closed.");
    }

    private static void CompleteAndWrite(AllocationGateReport report, string outputPath)
    {
        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions));
        foreach (var item in report.Cases)
        {
            Console.WriteLine(
                $"[AllocationGate] {item.Name}: median={item.MedianBytesPerOperation:F3} B/op " +
                $"range={item.MinBytesPerOperation:F3}..{item.MaxBytesPerOperationObserved:F3} " +
                $"spread={item.SpreadBytesPerOperation:F3} => {(item.Passed ? "PASS" : "FAIL")}");
        }
        foreach (var error in report.Errors)
            Console.Error.WriteLine($"[AllocationGate] {error}");
        Console.WriteLine($"[AllocationGate] report={outputPath} result={(report.Passed ? "PASS" : "FAIL")}");
    }

    private static void EnsureReleaseBuild()
    {
        if (!IsReleaseBuild)
            throw new InvalidOperationException("Allocation gate must run from a Release build.");
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

    private enum CaseKind { Add, OneWay, SendPumpIdleWake }

    private sealed record CaseDefinition(
        string Name,
        int Concurrency,
        int WarmupOperations,
        int OperationsPerSample,
        CaseKind Kind);

    private sealed class CompletionCounter { internal int Completed; }

    private sealed class GateOptions
    {
        internal string BudgetPath { get; private init; } = "eng/perf/allocation-budgets.json";
        internal string OutputPath { get; private init; } = "artifacts/perf/allocation-gate.json";
        internal string? Filter { get; private init; }
        internal int InjectedBytesPerOperation { get; private init; }

        internal static GateOptions Parse(string[] args)
        {
            var budget = "eng/perf/allocation-budgets.json";
            var output = "artifacts/perf/allocation-gate.json";
            string? filter = null;
            var injected = 0;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--budgets":
                        budget = Value(args, ref index, "--budgets");
                        break;
                    case "--output":
                        output = Value(args, ref index, "--output");
                        break;
                    case "--filter":
                        filter = Value(args, ref index, "--filter");
                        break;
                    case "--inject-bytes-per-operation":
                        var text = Value(args, ref index, "--inject-bytes-per-operation");
                        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out injected) || injected < 0)
                            throw new ArgumentException("Injected bytes must be a non-negative integer.");
                        break;
                    default:
                        throw new ArgumentException($"Unknown allocation gate argument '{args[index]}'.");
                }
            }
            return new GateOptions
            {
                BudgetPath = budget,
                OutputPath = output,
                Filter = filter,
                InjectedBytesPerOperation = injected
            };
        }

        private static string Value(string[] args, ref int index, string option)
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
        public string Mode { get; set; } = string.Empty;
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
