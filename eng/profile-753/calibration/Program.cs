using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpLink.Profiling753;

if (args.Length != 1) throw new ArgumentException("Calibration REPORT_JSON");
ProfileWindow753.Initialize();
Calibration.BusyBeforeWindow(1000);
using var process = Process.GetCurrentProcess();
process.Refresh();
ProfileWindow753.Begin("calibration");
var cpuBefore = process.TotalProcessorTime;
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var started = Stopwatch.GetTimestamp();
Calibration.BusyInsideWindow(1800);
Calibration.ContendedInsideMonitor();
Calibration.ContendedInsideSystemLock();
var stopped = Stopwatch.GetTimestamp();
ProfileWindow753.Close("calibration", started, stopped);
var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
process.Refresh();
var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
ProfileWindow753.End("calibration", 3);
Calibration.BusyAfterWindow(1000);
var elapsed = Stopwatch.GetElapsedTime(started, stopped).TotalSeconds;
using var marker = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SHARPLINK_PROFILE_METADATA")!));
var m = marker.RootElement;
if (m.GetProperty("runId").GetString() != ProfileWindow753.RunId ||
    m.GetProperty("processId").GetInt32() != Environment.ProcessId ||
    m.GetProperty("runtimeVersion").GetString() != Environment.Version.ToString() ||
    m.GetProperty("startedTicks").GetInt64() != started || m.GetProperty("stoppedTicks").GetInt64() != stopped ||
    m.GetProperty("operations").GetInt64() != 3)
    throw new InvalidOperationException("Calibration sidecar identity mismatch.");
using var output = new FileStream(args[0], FileMode.CreateNew, FileAccess.Write);
JsonSerializer.Serialize(output, new {
    workload = "calibration", transport = "synthetic", runId = ProfileWindow753.RunId,
    processId = Environment.ProcessId, runtimeVersion = Environment.Version.ToString(),
    stopwatchFrequency = Stopwatch.Frequency, commit = "calibration",
    beginTicks = m.GetProperty("beginTicks").GetInt64(), startedTicks = started, stoppedTicks = stopped,
    endTicks = m.GetProperty("endTicks").GetInt64(),
    operations = 3, items = 3, operationsStarted = 3, failure = 0, cancelled = 0, validationFailures = 0,
    processCpuMs = cpu, allocatedBytes = allocated,
    profileWindowSeconds = elapsed, measurementSeconds = elapsed, drainSeconds = 0d
}, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine("CALIBRATION_COMPLETE");

internal static class Calibration
{
    private static long s_sink;
    private static readonly object MonitorGate = new();
    private static readonly System.Threading.Lock SystemLockGate = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BusyBeforeWindow(int ms)
    {
        long x = 11; var until = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until) for (int i = 0; i < 20000; i++) x = unchecked(x * 1664525 + 1013904223);
        Volatile.Write(ref s_sink, x);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BusyInsideWindow(int ms)
    {
        long x = 13; var until = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until) for (int i = 0; i < 20000; i++) x = unchecked(x * 1103515245 + 12345);
        Volatile.Write(ref s_sink, x);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BusyAfterWindow(int ms)
    {
        long x = 17; var until = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until) for (int i = 0; i < 20000; i++) x = unchecked(x * 214013 + 2531011);
        Volatile.Write(ref s_sink, x);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ContendedInsideMonitor()
    {
        using var ready = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (MonitorGate) { ready.Set(); Thread.Sleep(400); } });
        holder.Start(); ready.Wait();
        lock (MonitorGate) Volatile.Write(ref s_sink, 19);
        holder.Join();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ContendedInsideSystemLock()
    {
        using var ready = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (SystemLockGate) { ready.Set(); Thread.Sleep(400); } });
        holder.Start(); ready.Wait();
        lock (SystemLockGate) Volatile.Write(ref s_sink, 23);
        holder.Join();
    }
}
