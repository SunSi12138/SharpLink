using System.Diagnostics.Tracing;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("sample JSON output path");
const int operations = 100000;
Markers.Log.Start("warmup");
Markers.Log.Stop("warmup", 0, 1);
for (int i = 0; i < 10000; i++) GC.KeepAlive(new byte[1000]);
Markers.Log.Start("synthetic-smoke");
long begin = GC.GetTotalAllocatedBytes(true);
for (int i = 0; i < operations; i++) GC.KeepAlive(new byte[1000]);
long bytes = GC.GetTotalAllocatedBytes(true) - begin;
Markers.Log.Stop("synthetic-smoke", bytes, operations);
File.WriteAllText(args[0], JsonSerializer.Serialize(new { schemaVersion = 1, sample = "synthetic-smoke", bytes, operations, runtime = Environment.Version.ToString(), sourceSha = "synthetic-fixture-not-a-benchmark", processId = Environment.ProcessId }));
Thread.Sleep(1000);

[EventSource(Name = "SharpLink-Issue739")]
sealed class Markers : EventSource
{
    public static readonly Markers Log = new();
    [Event(1)] public void Start(string sample) => WriteEvent(1, sample);
    [Event(2)] public void Stop(string sample, long bytes, long operations) => WriteEvent(2, sample, bytes, operations);
}
