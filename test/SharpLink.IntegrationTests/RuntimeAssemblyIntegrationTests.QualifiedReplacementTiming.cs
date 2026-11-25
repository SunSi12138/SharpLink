using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static async Task MeasureReplacementAsync(
        ReplacementTimingOptions options, string transport,
        Func<int, CancellationToken, ValueTask<int>> unary, IShutdownCleanupProbe sibling,
        ReplacementGeneration current, Func<ReplacementGeneration> load, Action<ReplacementGeneration> release,
        Func<Assembly, Assembly, CancellationToken, Task> replace, CancellationToken ct)
    {
        const int warmupSeconds = 11;
        const int measureSeconds = 21;
        const int minimumCompletedPerService = 100_000;
        const int sampleCapacity = 2_097_152;

        // Warm both peers and the actual update path at the same cadence as the observed phase.
        await RunPhaseAsync(warmupSeconds, 0, null, null, null);
        var callsA = new ReplacementCall[sampleCapacity];
        var callsB = new ReplacementCall[sampleCapacity];
        Array.Fill(callsA, new ReplacementCall(-1, -1));
        Array.Fill(callsB, new ReplacementCall(-1, -1));
        var events = new List<ReplacementObservation>();
        var sample = await RunPhaseAsync(measureSeconds, minimumCompletedPerService, callsA, callsB, events);

        RequireNoReplacementTracing();
        // Serialize only after both workers have physically exited. The exact start/end ticks make
        // whole-trial and update-overlap B populations independently reconstructible.
        var directory = Path.GetDirectoryName(options.Output)!;
        var name = Path.GetFileNameWithoutExtension(options.Output);
        WriteCalls(Path.Combine(directory, name + ".a.bin.gz"), callsA, sample.CompletedA);
        WriteCalls(Path.Combine(directory, name + ".b.bin.gz"), callsB, sample.CompletedB);
        using var output = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(output, new
        {
            Workload = "SharpLink.IntegrationTests.QualifiedReplacement",
            SchemaVersion = 2,
            options.SourceCommit,
            RpcTracingEnabled = ReplacementTracingEnabled(),
            Transport = transport,
            options.Cadence,
            options.Interceptor,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            GcLatencyMode = GCSettings.LatencyMode.ToString(),
            StopwatchFrequency = Stopwatch.Frequency,
            WarmupSeconds = warmupSeconds,
            MeasureSeconds = measureSeconds,
            sample.ElapsedTicks,
            sample.CompletedA,
            sample.CompletedB,
            sample.CpuTimeTicks,
            sample.ManagedAllocatedBytes,
            sample.Gen0,
            Replacements = events
        }, cancellationToken: ct);
        Console.WriteLine($"REPLACE_TIMING_COMPLETE transport={transport} cadence={options.Cadence} a={sample.CompletedA} b={sample.CompletedB} updates={events.Count}");

        async Task<ReplacementSample> RunPhaseAsync(int seconds, int minimum, ReplacementCall[]? observationsA,
            ReplacementCall[]? observationsB, List<ReplacementObservation>? updates)
        {
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var process = Process.GetCurrentProcess();
            var token = guard.Token;
            var start = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var counts = new int[2];
            var workers = new[] { Task.Run(() => RunWorkerAsync(0)), Task.Run(() => RunWorkerAsync(1)) };
            var cpu = process.TotalProcessorTime.Ticks;
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var gen0 = GC.CollectionCount(0);
            var origin = Stopwatch.GetTimestamp();
            start.SetResult(origin);
            await Task.WhenAll(workers);
            var elapsed = Stopwatch.GetTimestamp() - origin;
            process.Refresh();
            return new(elapsed, counts[0], counts[1], process.TotalProcessorTime.Ticks - cpu,
                GC.GetTotalAllocatedBytes(precise: true) - allocated, GC.CollectionCount(0) - gen0);

            async Task RunWorkerAsync(int service)
            {
                var began = await start.Task;
                var deadline = began + seconds * Stopwatch.Frequency;
                var observations = service == 0 ? observationsA : observationsB;
                try
                {
                    var completed = 0;
                    while (Stopwatch.GetTimestamp() < deadline ||
                           Volatile.Read(ref counts[0]) < minimum || Volatile.Read(ref counts[1]) < minimum)
                    {
                        token.ThrowIfCancellationRequested();
                        if (observations is not null && completed == observations.Length)
                            throw new InvalidOperationException("Replacement trial exceeded its frozen raw sample capacity.");
                        ReplacementGeneration? retired = null;
                        long updateStart = 0, loadComplete = 0, publicComplete = 0;
                        if (service == 0 && completed > 0 && options.Cadence > 0 && completed % options.Cadence == 0)
                        {
                            updateStart = Stopwatch.GetTimestamp() - began;
                            retired = current;
                            var next = load();
                            loadComplete = Stopwatch.GetTimestamp() - began;
                            await replace(retired.Assembly, next.Assembly, token);
                            publicComplete = Stopwatch.GetTimestamp() - began;
                            current = next;
                        }

                        var input = completed & 1023;
                        var beginning = Stopwatch.GetTimestamp() - began;
                        var actual = service == 0 ? await unary(input, token) : await sibling.TouchAsync(input, token);
                        var ending = Stopwatch.GetTimestamp() - began;
                        Ensure(actual == input + (service == 0 ? 1 : 100), "Replacement workload returned the wrong business value.");
                        if (retired is not null)
                        {
                            Ensure(GenerationCounter(current.ServiceType, "Created") == 1 &&
                                   GenerationCounter(retired.ServiceType, "Disposed") == 1,
                                "First post-update A must reach the new generation and the old generation must be disposed.");
                            release(retired);
                            updates?.Add(new(completed, updateStart, loadComplete, publicComplete, beginning, ending));
                        }

                        if (observations is not null)
                            observations[completed] = new(beginning, ending);
                        Volatile.Write(ref counts[service], ++completed);
                    }
                }
                catch
                {
                    guard.Cancel();
                    throw;
                }
            }
        }
    }

    private static void WriteCalls(string path, ReplacementCall[] samples, int count)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new GZipStream(file, CompressionLevel.Fastest);
        using var buffer = new BufferedStream(archive, 65_536);
        using var binary = new BinaryWriter(buffer);
        for (var i = 0; i < count; i++)
        {
            binary.Write(samples[i].StartedTicks);
            binary.Write(samples[i].CompletedTicks);
        }
    }

    private readonly record struct ReplacementCall(long StartedTicks, long CompletedTicks);
    private sealed record ReplacementObservation(int CompletedBefore, long UpdateStartedTicks, long LoadCompleteTicks,
        long PublicCompleteTicks, long FirstNewCallStartedTicks, long FirstNewCallCompleteTicks);
    private sealed record ReplacementSample(long ElapsedTicks, int CompletedA, int CompletedB, long CpuTimeTicks,
        long ManagedAllocatedBytes, int Gen0);

    private static bool ReplacementTracingEnabled() =>
        Activity.Current is not null || SharpLinkTelemetry.ClientActivitySource.HasListeners() ||
        SharpLinkTelemetry.ServerActivitySource.HasListeners();

    private static void RequireNoReplacementTracing() =>
        Ensure(!ReplacementTracingEnabled(), "The trial must not inherit TUnit or other RPC activity tracing.");

    private static string ReadReplacementInterceptor() =>
        Environment.GetEnvironmentVariable("SHARPLINK_999_REPLACE_INTERCEPTOR") switch
        {
            null or "none" => "none",
            "forward" => "forward",
            _ => throw new InvalidOperationException("Unknown replacement interceptor configuration.")
        };

    private sealed record ReplacementTimingOptions(string Output, string SourceCommit, int Cadence, string Interceptor)
    {
        internal static ReplacementTimingOptions? FromEnvironment(string interceptor)
        {
            var output = Environment.GetEnvironmentVariable("SHARPLINK_999_REPLACE_REPORT");
            var source = Environment.GetEnvironmentVariable("SHARPLINK_999_REPLACE_SOURCE");
            var rate = Environment.GetEnvironmentVariable("SHARPLINK_999_REPLACE_CADENCE");
            if (output is null && source is null && rate is null)
                return null;

            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
                throw new InvalidOperationException("The paired replacement timing mode is reserved for Linux GitHub Actions.");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathFullyQualified(output) ||
                !output.EndsWith(".json", StringComparison.Ordinal) || source is not { Length: 40 } ||
                !source.All(Uri.IsHexDigit) || rate is not ("0" or "100" or "10000") ||
                (interceptor == "forward" && rate != "0"))
                throw new InvalidOperationException("The qualified replacement workload requires a frozen output, commit and cadence.");
            return new(output, source, int.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), interceptor);
        }
    }
}
