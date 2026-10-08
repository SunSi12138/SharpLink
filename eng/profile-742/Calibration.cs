using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpLink.Profiling742;

if (args.Length is not (1 or 2)) throw new ArgumentException("Calibration REPORT_JSON [START_GATE_FILE]");
if (args.Length == 2)
{
    Console.WriteLine("CALIBRATION_READY " + Environment.ProcessId);
    while (!File.Exists(args[1])) Thread.Sleep(10);
}
Calibration.BusyBeforeWindow(1200);
Calibration.AllocateBeforeWindow();
using var process = Process.GetCurrentProcess();
process.Refresh();
var cpuBefore = process.TotalProcessorTime;
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var before = Stopwatch.GetTimestamp();
ProfileWindow742.Log.WindowBegin("calibration");
Calibration.BusyInsideWindow(2200);
Calibration.AllocateInsideWindow();
Calibration.ContendedInsideMonitor();
Calibration.ContendedInsideSystemLock();
ProfileWindow742.Log.WindowEnd("calibration", 3, 3);
var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
var elapsed = Stopwatch.GetElapsedTime(before).TotalSeconds;
process.Refresh();
var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
Calibration.BusyAfterWindow(1200);
Calibration.AllocateAfterWindow();
File.WriteAllText(args[0], JsonSerializer.Serialize(new {
    transport = "synthetic", allocatedBytes,
    workload = "calibration", operations = 3, items = 3, commit = "calibration",
    validationFailures = 0, failure = 0, cancelled = 0, operationsStarted = 3,
    processCpuMs = cpu, profileWindowSeconds = elapsed,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    dynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
    caveat = "Synthetic attribution and interval calibration, not SharpLink performance evidence."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("CALIBRATION_COMPLETE");

internal static class Calibration
{
    private static long s_sink;
    private static object? s_allocationSink;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AllocateBeforeWindow()
    { for (int i = 0; i < 8192; i++) Volatile.Write(ref s_allocationSink, new BeforeAllocationProbe()); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AllocateInsideWindow()
    { for (int i = 0; i < 8192; i++) Volatile.Write(ref s_allocationSink, new InsideAllocationProbe()); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AllocateAfterWindow()
    { for (int i = 0; i < 8192; i++) Volatile.Write(ref s_allocationSink, new AfterAllocationProbe()); }
    private static readonly object MonitorGate = new();
    private static readonly System.Threading.Lock SystemLockGate = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BusyBeforeWindow(int ms)
    {
        long x = 11; var until = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until) for (int i = 0; i < 20000; i++) x = unchecked((x * 1664525) + 1013904223);
        Volatile.Write(ref s_sink, x);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BusyInsideWindow(int ms)
    {
        long x = 13; var until = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until) for (int i = 0; i < 20000; i++) x = unchecked((x * 1103515245) + 12345);
        Volatile.Write(ref s_sink, x);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BusyAfterWindow(int ms)
    {
        long x = 17; var until = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until) for (int i = 0; i < 20000; i++) x = unchecked((x * 214013) + 2531011);
        Volatile.Write(ref s_sink, x);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ContendedInsideMonitor()
    {
        using var locked = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (MonitorGate) { locked.Set(); Thread.Sleep(400); } });
        holder.Start(); locked.Wait();
        lock (MonitorGate) Volatile.Write(ref s_sink, 19);
        holder.Join();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ContendedInsideSystemLock()
    {
        using var locked = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (SystemLockGate) { locked.Set(); Thread.Sleep(400); } });
        holder.Start(); locked.Wait();
        lock (SystemLockGate) Volatile.Write(ref s_sink, 23);
        holder.Join();
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 8192)]
internal sealed class BeforeAllocationProbe { }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 8192)]
internal sealed class InsideAllocationProbe { }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 8192)]
internal sealed class AfterAllocationProbe { }
