using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class StaticBindingEvidence
{
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 7) throw new ArgumentException("transport shape concurrency pool warmup operations output.json");
        var transport = args[0];
        var shape = args[1];
        var concurrency = int.Parse(args[2], CultureInfo.InvariantCulture);
        var pool = int.Parse(args[3], CultureInfo.InvariantCulture);
        var warmup = int.Parse(args[4], CultureInfo.InvariantCulture);
        var operations = int.Parse(args[5], CultureInfo.InvariantCulture);
        Require(shape is "unary" or "oneway", "Unsupported shape");
        Require(concurrency is >= 1 and <= 64 && pool is >= 1 and <= 4, "Invalid concurrency/pool");
        Require(warmup >= 1 && operations is >= 1000 and <= 1000000, "Invalid operation counts");
        await using var fixture = await StaticBindingFixture.CreateAsync(transport, pool);
        _ = await MeasureAsync(fixture, shape, concurrency, warmup);
        var measured = await MeasureAsync(fixture, shape, concurrency, operations);
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
        var diagnostics = fixture.Client.StaticBindingDiagnostics<IBenchmarkRpc>();
        Require(diagnostics.Routes == pool && diagnostics.Binds == pool && diagnostics.Pending == 0,
            "Steady state must not rebind or retain pending Bind operations");
        const string dialect = "static-binding";
#else
        var diagnostics = (Routes: pool, Binds: 0L, Pending: 0);
        const string dialect = "original-dev";
#endif
        var report = new
        {
            Schema = 1, Passed = true, Dialect = dialect, Transport = transport, Shape = shape,
            Concurrency = concurrency, Pool = pool, Warmup = warmup, Operations = operations,
            Runtime = Environment.Version.ToString(), ProcessorCount = Environment.ProcessorCount,
            StopwatchFrequency = Stopwatch.Frequency, RoutePrefixBytes = 16,
            fixture.BindMilliseconds, fixture.FirstCallMicroseconds,
            BoundRoutes = diagnostics.Routes, BindCount = diagnostics.Binds, PendingBinds = diagnostics.Pending,
            Measurement = measured
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[6]))!);
        await File.WriteAllTextAsync(args[6], JsonSerializer.Serialize(report));
        Console.WriteLine($"{dialect} {transport} {shape} c{concurrency}/p{pool}: " +
            $"{measured.ReceiverOperationsPerSecond:F0} completed ops/s, sender {measured.SenderOperationsPerSecond:F0}");
    }

    private static async Task<Measurement> MeasureAsync(StaticBindingFixture fixture,
        string shape, int concurrency, int operations)
    {
        fixture.Receiver.Reset(operations);
        var senderLatencies = new long[operations];
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, concurrency).Select(WorkerAsync).ToArray();
        using var process = Process.GetCurrentProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        gate.SetResult();
        await Task.WhenAll(tasks).WaitAsync(timeout.Token);
        var senderEnd = Stopwatch.GetTimestamp();
        if (shape == "oneway")
        {
            while (fixture.Receiver.Completed != operations || fixture.Receiver.LastCompletion == 0)
                await Task.Delay(1, timeout.Token);
            Require(fixture.Receiver.Seen.All(static seen => seen == 1), "Missing receiver operations");
        }
        var receiverEnd = shape == "oneway" ? fixture.Receiver.LastCompletion : senderEnd;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var cpu = process.TotalProcessorTime - cpuBefore;
        var receiverLatencies = shape == "oneway" ? fixture.Receiver.Latencies : senderLatencies;
        var senderSeconds = (senderEnd - started) / (double)Stopwatch.Frequency;
        var receiverSeconds = (receiverEnd - started) / (double)Stopwatch.Frequency;
        return new Measurement(operations, operations,
            operations / senderSeconds, operations / receiverSeconds,
            senderSeconds, receiverSeconds, Math.Max(0, receiverSeconds - senderSeconds),
            cpu.TotalMilliseconds, allocated / (double)operations,
            Quantiles(senderLatencies), Quantiles(receiverLatencies), senderLatencies, receiverLatencies);

        async Task WorkerAsync(int worker)
        {
            await gate.Task;
            for (var index = worker; index < operations; index += concurrency)
            {
                var sent = Stopwatch.GetTimestamp();
                if (shape == "oneway")
                    await fixture.Rpc.PublishEventAsync(index, sent, "binding");
                else
                    Require(await fixture.Rpc.AddAsync(index, 7) == index + 7, "Unary response mismatch");
                senderLatencies[index] = Stopwatch.GetTimestamp() - sent;
            }
        }
    }

    private static double[] Quantiles(long[] samples)
    {
        var sorted = samples.Order().ToArray();
        return new[] { 0.50, 0.99, 0.999 }.Select(q =>
            sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)] *
            1_000_000d / Stopwatch.Frequency).ToArray();
    }

    private sealed record Measurement(int SenderCompleted, int ReceiverCompleted,
        double SenderOperationsPerSecond, double ReceiverOperationsPerSecond,
        double SenderSeconds, double ReceiverSeconds, double DrainSeconds,
        double CpuMilliseconds, double ProcessBytesPerCompletedOperation,
        double[] SenderP50P99P999Microseconds, double[] ReceiverP50P99P999Microseconds,
        long[] SenderLatencyTicks, long[] ReceiverLatencyTicks);
}
