using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

const string Provider = "SharpLink-Allocation-Calibration";
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (args is ["calibrate"])
{
    Calibration.AllocateBefore();
    CalibrationEvents.Log.Begin();
    Calibration.AllocateInside();
    CalibrationEvents.Log.End();
    Calibration.AllocateAfter();
    return;
}
if (args.Length != 4 || args[0] != "parse")
    throw new ArgumentException("Use calibrate, or parse TRACE GATE_JSON_OR_calibration OUTPUT_JSON.");
var output = Path.GetFullPath(args[3]);
if (File.Exists(output) || File.Exists(Path.ChangeExtension(output, ".etlx")))
    throw new IOException("Refusing to overwrite trace evidence.");
var calibration = args[2] == "calibration";
int rawLoss;
var rawMarkers = new List<(int Id, double Time)>();
using (var source = new EventPipeEventSource(args[1]))
{
    source.Dynamic.All += data =>
    {
        if (data.ProviderName == Provider)
            rawMarkers.Add(((int)data.ID, data.TimeStampRelativeMSec));
    };
    source.Process();
    rawLoss = source.EventsLost;
}
var conversionProblems = new List<string>();
var etlx = Path.ChangeExtension(output, ".etlx");
using (var log = new StreamWriter(output + ".conversion.log"))
{
    TraceLog.CreateFromEventPipeDataFile(args[1], etlx, new TraceLogOptions
    {
        ContinueOnError = false,
        KeepAllEvents = true,
        LocalSymbolsOnly = true,
        ConversionLog = log,
        OnLostEvents = (truncated, lost, total) =>
        {
            if (truncated || lost != 0)
                conversionProblems.Add($"truncated={truncated}, lost={lost}, total={total}");
        }
    });
}
using var trace = new TraceLog(etlx);
if (rawLoss != 0 || trace.EventsLost != 0 || conversionProblems.Count != 0)
    throw new InvalidDataException("Trace loss or truncation invalidates allocation attribution.");
var allocations = new List<Allocation>();
var markers = new List<(int Id, double Time)>();
using (var source = trace.Events.GetSource())
{
    source.Dynamic.All += data =>
    {
        if (data.ProviderName == Provider)
            markers.Add(((int)data.ID, data.TimeStampRelativeMSec));
    };
    source.Clr.GCAllocationTick += data =>
    {
        var frames = new List<string>();
        for (var stack = data.CallStack(); stack is not null; stack = stack.Caller)
            frames.Add(stack.CodeAddress.FullMethodName ?? "unresolved");
        allocations.Add(new Allocation(data.ProcessID, data.TimeStamp.ToUniversalTime(),
            data.TimeStampRelativeMSec, data.TypeName, data.ObjectSize, data.AllocationAmount64,
            frames.ToArray()));
    };
    source.Process();
}
if (allocations.Count == 0 || allocations.Select(row => row.ProcessId).Distinct().Count() != 1)
    throw new InvalidDataException("Expected allocation events from exactly one traced process.");
var windows = new List<object>();
if (calibration)
{
    if (markers.Count != 2 || rawMarkers.Count != 2 ||
        markers[0].Id != 1 || markers[1].Id != 2 ||
        rawMarkers[0].Id != 1 || rawMarkers[1].Id != 2 ||
        Math.Abs((markers[1].Time - markers[0].Time) - (rawMarkers[1].Time - rawMarkers[0].Time)) > 1)
        throw new InvalidDataException("Calibration markers are missing, unordered or changed during conversion.");
    var inside = allocations.Where(row => row.TimeMs > markers[0].Time && row.TimeMs < markers[1].Time).ToArray();
    static bool HasFrame(Allocation row, string method) => row.Frames.Any(frame => frame.Contains(method, StringComparison.Ordinal));
    if (inside.Count(row => HasFrame(row, "AllocateInside")) < 10 ||
        inside.Any(row => HasFrame(row, "AllocateBefore") || HasFrame(row, "AllocateAfter")) ||
        !allocations.Any(row => row.TimeMs < markers[0].Time && HasFrame(row, "AllocateBefore")) ||
        !allocations.Any(row => row.TimeMs > markers[1].Time && HasFrame(row, "AllocateAfter")))
        throw new InvalidDataException("Known allocation callsites did not obey the calibrated trace window.");
    windows.Add(Summarize("calibration", inside));
}
else
{
    using var gate = JsonDocument.Parse(File.ReadAllText(args[2]));
    foreach (var item in gate.RootElement.GetProperty("cases").EnumerateArray())
    {
        foreach (var sample in item.GetProperty("samples").EnumerateArray())
        {
            var before = sample.GetProperty("diagnosticsBefore").GetProperty("timestampUtc").GetDateTimeOffset();
            var after = sample.GetProperty("diagnosticsAfter").GetProperty("timestampUtc").GetDateTimeOffset();
            var elapsed = sample.GetProperty("elapsedMilliseconds").GetDouble();
            if (after <= before || Math.Abs((after - before).TotalMilliseconds - elapsed) > 10)
                throw new InvalidDataException("Sample wall clock and monotonic diagnostic interval disagree.");
            var inside = allocations.Where(row => row.Utc >= before.UtcDateTime && row.Utc <= after.UtcDateTime).ToArray();
            if (inside.Length == 0 || !inside.Any(row => row.Frames.Any(frame => frame.Contains("SharpLink", StringComparison.Ordinal))))
                throw new InvalidDataException("A measured sample has no resolved SharpLink allocation stack; attribution is inconclusive.");
            windows.Add(Summarize($"{item.GetProperty("name").GetString()}/{sample.GetProperty("index").GetInt32()}", inside));
        }
    }
    if (windows.Count != 10)
        throw new InvalidDataException("Expected all five samples for both unary cases.");
}
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    diagnosticOnly = true,
    calibration,
    rawEventsLost = rawLoss,
    convertedEventsLost = trace.EventsLost,
    processId = allocations[0].ProcessId,
    totalAllocationEvents = allocations.Count,
    sampleBoundaryMethod = calibration ? "EventSource markers" : "approximate UTC diagnostic snapshots outside allocation counters",
    limitations = "Allocation ticks sample intervals. Interval bytes are not attributed to the triggering object's type or stack. Object-size totals describe sampled objects only. Trace perturbs scheduling and allocation. No budget certification or exact causal attribution.",
    windows
}, json));

static object Summarize(string name, Allocation[] rows) => new
{
    name,
    allocationEvents = rows.Length,
    sampledAllocationIntervalBytes = rows.Sum(row => (double)row.IntervalBytes),
    sampledObjectBytes = rows.Sum(row => (double)row.ObjectBytes),
    sampledObjects = rows.GroupBy(row => new { row.Type, Frames = string.Join("\n", row.Frames) })
        .Select(group => new
        {
            type = group.Key.Type,
            frames = group.First().Frames,
            count = group.Count(),
            sampledObjectBytes = group.Sum(row => (double)row.ObjectBytes)
        }).OrderByDescending(row => row.count).ToArray()
};

internal sealed record Allocation(int ProcessId, DateTime Utc, double TimeMs, string Type,
    long ObjectBytes, long IntervalBytes, string[] Frames);

[EventSource(Name = "SharpLink-Allocation-Calibration")]
internal sealed class CalibrationEvents : EventSource
{
    internal static readonly CalibrationEvents Log = new();
    [Event(1)] public void Begin() => WriteEvent(1);
    [Event(2)] public void End() => WriteEvent(2);
}

internal static class Calibration
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AllocateBefore()
    {
        for (var i = 0; i < 4_096; i++) GC.KeepAlive(new byte[4_096]);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AllocateInside()
    {
        for (var i = 0; i < 16_384; i++) GC.KeepAlive(new byte[4_096]);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AllocateAfter()
    {
        for (var i = 0; i < 4_096; i++) GC.KeepAlive(new byte[4_096]);
    }
}
