using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SharpLink.LoadTestBase;

namespace SharpLink.Benchmarks;

internal static class LatencyRecorderBaselineAnalyzer
{
    private static readonly int[] SConcurrency = [128, 512];

    public static void Run(string[] args)
    {
        if (args.Length != 4)
        {
            throw new ArgumentException(
                "Usage: --analyze-latency-recorder-baseline " +
                "<micro-json> <macro-directory> <expected-runs> <output-json>");
        }

        var expectedRuns = int.Parse(args[2]);
        if (expectedRuns < 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRuns),
                expectedRuns,
                "A formal interference gate requires at least five alternating runs.");
        }
        var failures = new List<string>();
        var sourceCommit = ValidateMicro(args[0], expectedRuns, failures);

        var gates = new List<LatencyRecorderMacroGate>();
        foreach (var concurrency in SConcurrency)
        {
            var off = ReadRuns(
                args[1], concurrency, "off", "off", "off-v1", expectedRuns,
                false, failures, ref sourceCommit);
            var formal = ReadRuns(
                args[1], concurrency, "formal", "formal", StageLatencyRecorder.Version,
                expectedRuns, false, failures, ref sourceCommit);
            var tailOff = ReadRuns(
                args[1], concurrency, "tail-off", "off", "off-v1", expectedRuns,
                true, failures, ref sourceCommit);
            var tailFormal = ReadRuns(
                args[1], concurrency, "tail-formal", "formal", StageLatencyRecorder.Version,
                expectedRuns, true, failures, ref sourceCommit);
            ValidateDualRun(args[1], concurrency, failures, ref sourceCommit);

            var offQps = Median(off.Select(static run => run.Qps));
            var formalQps = Median(formal.Select(static run => run.Qps));
            var throughputDeltaPercent = (formalQps / offQps - 1d) * 100d;
            var throughputGatePassed = Math.Abs(throughputDeltaPercent) <= 3d;
            if (!throughputGatePassed)
            {
                failures.Add(
                    $"c{concurrency} formal/off throughput delta " +
                    $"{throughputDeltaPercent:F3}% exceeds the 3% gate.");
            }

            var offCpuPerOperation = Median(off.Select(static run => run.CpuMicrosecondsPerOperation));
            var formalCpuPerOperation = Median(formal.Select(static run => run.CpuMicrosecondsPerOperation));
            var offObserverP99 = Median(tailOff
                .Where(static run => run.ObserverP99Us.HasValue)
                .Select(static run => run.ObserverP99Us!.Value));
            var formalObserverP99 = Median(tailFormal
                .Where(static run => run.ObserverP99Us.HasValue)
                .Select(static run => run.ObserverP99Us!.Value));
            var observerP99DeltaPercent = (formalObserverP99 / offObserverP99 - 1d) * 100d;
            var observerP99GatePassed = Math.Abs(observerP99DeltaPercent) <= 3d;
            if (!observerP99GatePassed)
            {
                failures.Add(
                    $"c{concurrency} formal/off independent-observer P99 delta " +
                    $"{observerP99DeltaPercent:F3}% exceeds the 3% gate.");
            }

            var offObserverP999 = Median(tailOff
                .Where(static run => run.ObserverP999Us.HasValue)
                .Select(static run => run.ObserverP999Us!.Value));
            var formalObserverP999 = Median(tailFormal
                .Where(static run => run.ObserverP999Us.HasValue)
                .Select(static run => run.ObserverP999Us!.Value));
            var observerP999DeltaPercent = (formalObserverP999 / offObserverP999 - 1d) * 100d;
            var observerP999GatePassed = Math.Abs(observerP999DeltaPercent) <= 3d;
            if (!observerP999GatePassed)
            {
                failures.Add(
                    $"c{concurrency} formal/off independent-observer P99.9 delta " +
                    $"{observerP999DeltaPercent:F3}% exceeds the 3% gate.");
            }

            gates.Add(new LatencyRecorderMacroGate(
                concurrency,
                offQps,
                formalQps,
                throughputDeltaPercent,
                throughputGatePassed,
                offCpuPerOperation,
                formalCpuPerOperation,
                (formalCpuPerOperation / offCpuPerOperation - 1d) * 100d,
                Median(formal.Select(static run => run.P99Us!.Value)),
                Median(formal.Select(static run => run.P999Us!.Value)),
                offObserverP99,
                formalObserverP99,
                observerP99DeltaPercent,
                observerP99GatePassed,
                offObserverP999,
                formalObserverP999,
                observerP999DeltaPercent,
                observerP999GatePassed));
        }

        var report = new LatencyRecorderBaselineAnalysis(
            PerformanceReportCompatibility.CurrentSchemaVersion,
            sourceCommit ?? "unknown",
            failures.Count == 0,
            "Workload percentiles are unavailable in recording-off by contract. A dedicated " +
            "raw-sample Add probe runs identically beside off/formal workloads and gates its " +
            "P99/P99.9 median shift at 3%; validation-dual enforces percentile accuracy.",
            gates,
            failures);
        var output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(
            output,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static string? ValidateMicro(string path, int expectedRuns, List<string> failures)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var sourceCommit = root.GetProperty("SourceCommit").GetString();
        if (string.IsNullOrWhiteSpace(sourceCommit) || sourceCommit == "unknown")
            failures.Add("Micro evidence does not identify its source commit.");

        var measurements = root.GetProperty("Measurements").EnumerateArray().ToArray();
        foreach (var concurrency in new[] { 1, 8, 32, 128, 512 })
        {
            var formalAllocations = measurements
                .Where(item => item.GetProperty("Scenario").GetString() == "FormalWorkerLocal" &&
                               item.GetProperty("Concurrency").GetInt32() == concurrency)
                .Select(item => item.GetProperty("AllocatedBytesPerRecord").GetDouble())
                .ToArray();
            if (formalAllocations.Length != expectedRuns)
            {
                failures.Add(
                    $"Micro c{concurrency} expected {expectedRuns} formal runs, " +
                    $"found {formalAllocations.Length}.");
                continue;
            }

            var medianAllocation = Median(formalAllocations);
            if (medianAllocation > 0.001d)
            {
                failures.Add(
                    $"Micro c{concurrency} formal median allocation " +
                    $"{medianAllocation:F6} B/record is not steady-state zero.");
            }
        }

        return sourceCommit;
    }

    private static List<MacroRun> ReadRuns(
        string directory,
        int concurrency,
        string fileLabel,
        string mode,
        string recorderVersion,
        int expectedRuns,
        bool requireTailObserver,
        List<string> failures,
        ref string? sourceCommit)
    {
        var prefix = $"c{concurrency}-r";
        var suffix = $"-{fileLabel}.json";
        var files = Directory.GetFiles(directory, $"{prefix}*{suffix}")
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                var repetition = name[prefix.Length..^suffix.Length];
                return int.TryParse(repetition, out _);
            })
            .ToArray();
        if (files.Length != expectedRuns)
        {
            failures.Add(
                $"c{concurrency} {fileLabel} expected {expectedRuns} runs, found {files.Length}.");
        }

        var runs = new List<MacroRun>();
        foreach (var file in files.OrderBy(static path => path, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var schemaVersion = root.GetProperty("SchemaVersion").GetInt32();
            var commit = root.GetProperty("SourceCommit").GetString() ?? "unknown";
            sourceCommit ??= commit;
            if (!string.Equals(sourceCommit, commit, StringComparison.Ordinal))
                failures.Add($"Source commit mismatch in {Path.GetFileName(file)}.");

            var result = root.GetProperty("Results")[0];
            var actualMode = result.GetProperty("RecorderMode").GetString() ?? string.Empty;
            var actualVersion = result.GetProperty("RecorderVersion").GetString() ?? string.Empty;
            try
            {
                PerformanceReportCompatibility.EnsureComparable(
                    PerformanceReportCompatibility.CurrentSchemaVersion,
                    recorderVersion,
                    schemaVersion,
                    actualVersion);
            }
            catch (InvalidOperationException exception)
            {
                failures.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
            if (!string.Equals(actualMode, mode, StringComparison.Ordinal))
                failures.Add($"Recorder mode mismatch in {Path.GetFileName(file)}.");

            var failureCount = result.GetProperty("Failure").GetInt64();
            var started = result.GetProperty("OperationsStartedDuringMeasurement").GetInt64();
            var completed = result.GetProperty("OperationsCompleted").GetInt64();
            var success = result.GetProperty("Success").GetInt64();
            var sampleCount = result.GetProperty("SampleCount").GetInt64();
            var formalComparable = result.GetProperty("FormalComparable").GetBoolean();
            if (failureCount != 0 || started != completed)
                failures.Add($"Incomplete or failed workload in {Path.GetFileName(file)}.");
            if (mode == "formal" && (!formalComparable || sampleCount != success))
                failures.Add($"Invalid formal sample contract in {Path.GetFileName(file)}.");
            if (mode == "off" && (formalComparable || sampleCount != 0 || result.TryGetProperty("P99Us", out _)))
                failures.Add($"Invalid recording-off contract in {Path.GetFileName(file)}.");

            var observerSampleCount = result.GetProperty("TailObserverSampleCount").GetInt64();
            var observerFailure = result.GetProperty("TailObserverFailure").GetInt64();
            var observerP99 = result.TryGetProperty("TailObserverP99Us", out var observerP99Element)
                ? observerP99Element.GetDouble()
                : (double?)null;
            var observerP999 = result.TryGetProperty("TailObserverP999Us", out var observerP999Element)
                ? observerP999Element.GetDouble()
                : (double?)null;
            if (requireTailObserver &&
                (observerSampleCount == 0 || observerFailure != 0 ||
                 observerP99 is null || observerP999 is null))
            {
                failures.Add($"Invalid tail-observer contract in {Path.GetFileName(file)}.");
            }
            if (!requireTailObserver &&
                (observerSampleCount != 0 || observerFailure != 0 ||
                 observerP99 is not null || observerP999 is not null))
            {
                failures.Add($"Unexpected tail-observer data in {Path.GetFileName(file)}.");
            }

            var cpuMilliseconds = result.GetProperty("Evidence").GetProperty("CpuMilliseconds").GetDouble();
            runs.Add(new MacroRun(
                result.GetProperty("Qps").GetDouble(),
                cpuMilliseconds * 1_000d / completed,
                mode == "formal" ? result.GetProperty("P99Us").GetDouble() : null,
                mode == "formal" ? result.GetProperty("P999Us").GetDouble() : null,
                observerP99,
                observerP999));
        }
        return runs;
    }

    private static void ValidateDualRun(
        string directory,
        int concurrency,
        List<string> failures,
        ref string? sourceCommit)
    {
        var file = Path.Combine(directory, $"c{concurrency}-validation-dual.json");
        if (!File.Exists(file))
        {
            failures.Add($"Missing c{concurrency} validation-dual run.");
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var commit = root.GetProperty("SourceCommit").GetString() ?? "unknown";
        sourceCommit ??= commit;
        var result = root.GetProperty("Results")[0];
        if (root.GetProperty("SchemaVersion").GetInt32() !=
                PerformanceReportCompatibility.CurrentSchemaVersion ||
            !string.Equals(sourceCommit, commit, StringComparison.Ordinal) ||
            result.GetProperty("Failure").GetInt64() != 0 ||
            result.GetProperty("OperationsStartedDuringMeasurement").GetInt64() !=
                result.GetProperty("OperationsCompleted").GetInt64() ||
            result.GetProperty("RecorderMode").GetString() != "validationdual" ||
            result.GetProperty("FormalComparable").GetBoolean())
        {
            failures.Add($"Invalid c{concurrency} validation-dual report contract.");
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return double.NaN;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private sealed record MacroRun(
        double Qps,
        double CpuMicrosecondsPerOperation,
        double? P99Us,
        double? P999Us,
        double? ObserverP99Us,
        double? ObserverP999Us);
}

internal sealed record LatencyRecorderBaselineAnalysis(
    int SchemaVersion,
    string SourceCommit,
    bool GatePassed,
    string LatencyShiftContract,
    IReadOnlyList<LatencyRecorderMacroGate> MacroGates,
    IReadOnlyList<string> Failures);

internal sealed record LatencyRecorderMacroGate(
    int Concurrency,
    double OffMedianQps,
    double FormalMedianQps,
    double ThroughputDeltaPercent,
    bool ThroughputGatePassed,
    double OffMedianCpuMicrosecondsPerOperation,
    double FormalMedianCpuMicrosecondsPerOperation,
    double CpuDeltaPercent,
    double FormalMedianP99Us,
    double FormalMedianP999Us,
    double OffObserverMedianP99Us,
    double FormalObserverMedianP99Us,
    double ObserverP99DeltaPercent,
    bool ObserverP99GatePassed,
    double OffObserverMedianP999Us,
    double FormalObserverMedianP999Us,
    double ObserverP999DeltaPercent,
    bool ObserverP999GatePassed);
