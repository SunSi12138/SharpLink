using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;

// Marker transport compatibility only: no product reference, workload, TCP or timings for promotion.
const string source = "3d76a3a8b55a37e24c62a073fe588cff3d3867be";
if (args.Length != 3 || Environment.Version != new Version(10, 0, 14) ||
    Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") != source) return 64;
var output = Path.GetFullPath(args[0]);
var ready = Path.GetFullPath(args[1]);
var go = Path.GetFullPath(args[2]);
var pending = ready + ".pending";
string[] paths = [output, ready, go, pending];
if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length ||
    paths.Any(path => File.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path)))) return 64;
var pid = Environment.ProcessId;
using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    JsonSerializer.Serialize(file, new { schema = 1, mode = "trace", pid, sourceRevision = source });
File.Move(pending, ready, overwrite: false);
var handshakeDeadline = Stopwatch.GetTimestamp() + 60L * Stopwatch.Frequency;
while (!File.Exists(go))
{
    if (Stopwatch.GetTimestamp() >= handshakeDeadline) return 65;
    Thread.Sleep(10);
}
if (File.ReadAllText(go) != $"GO trace {pid}\n") return 65;
string[] payloadNames = ["sourceRevision", "variant", "mode", "itemCount", "workSeconds", "window", "timestamp"];
using var listener = new MarkerListener();
var phase = DiagnosticPhase.Log;
var enabled = phase.IsEnabled(EventLevel.Informational, DiagnosticPhase.Keywords.Work);
var constructionExceptionNull = phase.ConstructionException is null;
var start = Stopwatch.GetTimestamp();
phase.WorkStart(source, "trace", "synthetic-marker", 100, 1, "marker-smoke", start);
// A fixed one-second marker window, not a CPU workload or a throughput experiment.
var deadline = start + Stopwatch.Frequency;
while (Stopwatch.GetTimestamp() < deadline) Thread.Sleep(1);
var stop = Stopwatch.GetTimestamp();
phase.WorkStop(source, "trace", "synthetic-marker", 100, 1, "marker-smoke", stop);
var events = listener.Snapshot();
var passed = constructionExceptionNull && phase.ConstructionException is null && enabled &&
    stop >= deadline && events.Length == 2;
for (var index = 0; index < events.Length; index++)
{
    var e = events[index];
    var expectedTick = index == 0 ? start : stop;
    passed &= e.Id == index + 1 && e.Name == (index == 0 ? "WorkStart" : "WorkStop") &&
        e.Names.SequenceEqual(payloadNames) && e.Values.Length == 7 &&
        e.Values[0] is string revision && revision == source &&
        e.Values[1] is string variant && variant == "trace" &&
        e.Values[2] is string mode && mode == "synthetic-marker" &&
        e.Values[3] is int items && items == 100 &&
        e.Values[4] is int seconds && seconds == 1 &&
        e.Values[5] is string window && window == "marker-smoke" &&
        e.Values[6] is long tick && tick == expectedTick;
}
var result = new
{
    schemaVersion = 1, purpose = "MarkerCompatibilityOnly", listenerPassed = passed,
    runtimeVersion = Environment.Version.ToString(), pid = Environment.ProcessId,
    sourceRevision = source, variant = "trace", mode = "synthetic-marker", itemCount = 100,
    workSeconds = 1, window = "marker-smoke", startTick = start, stopTick = stop,
    frequency = Stopwatch.Frequency, eventCount = events.Length, constructionExceptionNull,
    enabled, eventIds = events.Select(e => e.Id).ToArray(),
    eventNames = events.Select(e => e.Name).ToArray(), payloadNames
};
using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    JsonSerializer.Serialize(file, result);
Console.WriteLine(passed ? "MARKER_LISTENER_PASS" : "MARKER_LISTENER_FAIL");
return passed ? 0 : 2;

internal sealed record MarkerEvent(int Id, string? Name, string[] Names, object?[] Values);
internal sealed class MarkerListener : EventListener
{
    private readonly List<MarkerEvent> events = [];
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "SharpLink-DiagnosticPhase")
            EnableEvents(source, EventLevel.Informational, DiagnosticPhase.Keywords.Work);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventSource.Name != "SharpLink-DiagnosticPhase") return;
        lock (events)
            events.Add(new(e.EventId, e.EventName, e.PayloadNames?.ToArray() ?? [], e.Payload?.ToArray() ?? []));
    }
    internal MarkerEvent[] Snapshot() { lock (events) return events.ToArray(); }
}
