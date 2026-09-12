using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using SharpLink.LoadTestBase;

namespace SharpLink.Benchmarks;

internal static class LatencyRecorderEvidenceRunner
{
    private static readonly int[] SConcurrency = [1, 8, 32, 128, 512];

    public static void Run(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException(
                "Usage: --latency-recorder-evidence <target-records-per-scenario> <repetitions> <output-json>");
        }

        var targetRecordsPerScenario = int.Parse(args[0]);
        var repetitions = int.Parse(args[1]);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRecordsPerScenario);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repetitions);

        var measurements = new List<LatencyRecorderMeasurement>();
        foreach (var concurrency in SConcurrency)
        {
            var recordsPerWorker = checked(
                (targetRecordsPerScenario + concurrency - 1) / concurrency);
            foreach (var scenario in Enum.GetValues<LatencyRecorderScenario>())
            {
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    var measurement = Measure(scenario, concurrency, recordsPerWorker, repetition);
                    measurements.Add(measurement);
                    Console.WriteLine(
                        $"{scenario,-17} c={concurrency,3} r={repetition + 1} " +
                        $"{measurement.NanosecondsPerRecord,10:F2} ns/record " +
                        $"{measurement.RecordsPerSecond,14:F0} records/s " +
                        $"{measurement.AllocatedBytesPerRecord,8:F4} B/record");
                }
            }
        }

        var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var report = new LatencyRecorderEvidence(
            PerformanceReportCompatibility.CurrentSchemaVersion,
            ReadCommit(),
            DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            Stopwatch.Frequency,
            targetRecordsPerScenario,
            repetitions,
            measurements);
        File.WriteAllText(
            output,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Latency recorder evidence: {output}");
    }

    private static LatencyRecorderMeasurement Measure(
        LatencyRecorderScenario scenario,
        int concurrency,
        int recordsPerWorker,
        int repetition)
    {
        var totalRecords = checked((long)concurrency * recordsPerWorker);
        if (totalRecords > int.MaxValue && scenario == LatencyRecorderScenario.FormalWorkerLocal)
            throw new ArgumentOutOfRangeException(nameof(recordsPerWorker));

        var latencyInputs = Enumerable.Range(0, 1024)
            .Select(index => 10L + (index * 17L % 10_000L))
            .ToArray();
        var firstLegacy = scenario is LatencyRecorderScenario.LegacyOne or LatencyRecorderScenario.LegacyDouble
            ? new LatencyHistogram()
            : null;
        var secondLegacy = scenario == LatencyRecorderScenario.LegacyDouble
            ? new LatencyHistogram(200_000)
            : null;
        var formal = scenario == LatencyRecorderScenario.FormalWorkerLocal
            ? new StageLatencyRecorder(concurrency, checked((int)totalRecords), Stopwatch.Frequency)
            : null;
        var checksums = new long[concurrency];
        using var startGate = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(concurrency);
        using var completed = new CountdownEvent(concurrency);
        var threads = new Thread[concurrency];

        for (var workerIndex = 0; workerIndex < concurrency; workerIndex++)
        {
            var capturedWorker = workerIndex;
            threads[workerIndex] = new Thread(() =>
            {
                var checksum = 0L;
                var workerRecorder = formal?.GetWorker(capturedWorker);
                ready.Signal();
                startGate.Wait();
                for (var record = 0; record < recordsPerWorker; record++)
                {
                    var ticks = latencyInputs[(record + capturedWorker) & (latencyInputs.Length - 1)];
                    checksum += ticks;
                    firstLegacy?.Record(ticks);
                    secondLegacy?.Record(ticks);
                    workerRecorder?.RecordTicks(capturedWorker, ticks);
                }
                checksums[capturedWorker] = checksum;
                completed.Signal();
            })
            {
                IsBackground = true,
                Name = $"latency-evidence-{capturedWorker}"
            };
            threads[workerIndex].Start();
        }

        ready.Wait();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var contentionsBefore = Monitor.LockContentionCount;
        var started = Stopwatch.GetTimestamp();
        startGate.Set();
        completed.Wait();
        var stopped = Stopwatch.GetTimestamp();
        var contentions = Monitor.LockContentionCount - contentionsBefore;
        var allocated = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;

        foreach (var thread in threads)
            thread.Join();

        var elapsedSeconds = Stopwatch.GetElapsedTime(started, stopped).TotalSeconds;
        var checksumTotal = checksums.Sum();
        if (scenario == LatencyRecorderScenario.FormalWorkerLocal &&
            formal!.Complete().Count != totalRecords)
        {
            throw new InvalidOperationException("Formal recorder lost samples during evidence collection.");
        }

        return new LatencyRecorderMeasurement(
            scenario.ToString(),
            concurrency,
            repetition,
            recordsPerWorker,
            totalRecords,
            elapsedSeconds * 1_000_000_000d / totalRecords,
            totalRecords / elapsedSeconds,
            allocated / (double)totalRecords,
            cpu.TotalMilliseconds,
            contentions,
            checksumTotal);
    }

    private static string ReadCommit()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ??
                         Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        if (process is null)
            return "unknown";
        var output = process.StandardOutput.ReadToEnd();
        return process.WaitForExit(2_000) && process.ExitCode == 0 ? output.Trim() : "unknown";
    }
}

internal enum LatencyRecorderScenario
{
    Control,
    LegacyOne,
    LegacyDouble,
    FormalWorkerLocal
}

internal sealed record LatencyRecorderEvidence(
    int SchemaVersion,
    string SourceCommit,
    DateTimeOffset TimestampUtc,
    string OperatingSystem,
    string ProcessArchitecture,
    string Runtime,
    int ProcessorCount,
    bool ServerGc,
    long StopwatchFrequency,
    int TargetRecordsPerScenario,
    int Repetitions,
    IReadOnlyList<LatencyRecorderMeasurement> Measurements);

internal sealed record LatencyRecorderMeasurement(
    string Scenario,
    int Concurrency,
    int Repetition,
    int RecordsPerWorker,
    long TotalRecords,
    double NanosecondsPerRecord,
    double RecordsPerSecond,
    double AllocatedBytesPerRecord,
    double CpuMilliseconds,
    long LockContentions,
    long Checksum);
