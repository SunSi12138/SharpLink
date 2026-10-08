using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

const string WindowProvider = "SharpLink-742-ProfileWindow";
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (args.Length == 1 && args[0] == "--self-test")
{
    RequireNoLoss(0, 0);
    ExpectFailure(() => RequireNoLoss(1, 0), "raw event-loss guard");
    ExpectFailure(() => RequireNoLoss(0, 1), "converted event-loss guard");
    if (InWindow(1, 1, 2) || !InWindow(1.5, 1, 2) || InWindow(2, 1, 2))
        throw new Exception("Open-interval boundary test failed.");
    if (ClippedNs(0, 2, 2000000, 1, 3) != 1000000 ||
        ClippedNs(0, 1, 1000000, 2, 3) != 0)
        throw new Exception("Contention boundary clipping failed.");
    Console.WriteLine("PASS loss guards (synthetic counts), exact sample boundaries, contention clipping. This is not a real dropped-event trace test.");
    return;
}
if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "--calibration"))
    throw new ArgumentException("ProfileParser TRACE RESULT_JSON OUTPUT_JSON [--calibration], or --self-test");
var calibration = args.Length == 4;
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var report = JsonDocument.Parse(File.ReadAllText(args[1]));
var normalized = NormalizeReport(report.RootElement, calibration);
var markers = new List<Marker>();
var markerErrors = new List<string>();
int rawLoss;
using (var source = new EventPipeEventSource(args[0]))
{
    source.Dynamic.All += data =>
    {
        if (data.ProviderName != WindowProvider) return;
        var id = (int)data.ID;
        if (id == 0) { markerErrors.Add(data.ToString()); return; }
        if (id is < 1 or > 3) { markerErrors.Add($"Unexpected marker id {id}"); return; }
        markers.Add(new(data.TimeStampRelativeMSec, data.ProcessID, id,
            Convert.ToString(data.PayloadByName("workload"), CultureInfo.InvariantCulture)!,
            id == 2 ? Convert.ToInt64(data.PayloadByName("operations"), CultureInfo.InvariantCulture) : 0,
            id == 2 ? Convert.ToInt64(data.PayloadByName("items"), CultureInfo.InvariantCulture) : 0));
    };
    source.Process();
    rawLoss = source.EventsLost;
}
RequireNoLoss(rawLoss, 0);
if (markerErrors.Count != 0) throw new InvalidOperationException(string.Join("; ", markerErrors));
var begin = markers.Single(x => x.Kind == 1);
var end = markers.Single(x => x.Kind == 2);
var admissions = markers.Where(x => x.Kind == 3).ToArray();
if (markers.Any(x => x.ProcessId != begin.ProcessId || x.Workload != normalized.Workload) ||
    begin.TimeMs >= end.TimeMs || end.Operations != normalized.Operations || end.Items != normalized.Items ||
    admissions.Length != (normalized.Kind == "c8" ? 1 : 0) ||
    admissions.Any(x => !InWindow(x.TimeMs, begin.TimeMs, end.TimeMs)))
    throw new InvalidOperationException("Measurement markers do not match report workload, PID, completed operations/items, or drain boundary.");

var etlx = Path.ChangeExtension(output, ".etlx");
if (File.Exists(etlx)) throw new IOException($"Refusing stale/overwritten converted trace: {etlx}");
var conversionWarnings = new List<string>();
using (var conversionLog = File.CreateText(output + ".conversion.log"))
{
    TraceLog.CreateFromEventPipeDataFile(args[0], etlx, new TraceLogOptions
    {
        ContinueOnError = false, KeepAllEvents = true, LocalSymbolsOnly = true,
        ConversionLog = conversionLog,
        OnLostEvents = (truncated, lost, total) => conversionWarnings.Add($"truncated={truncated} lost={lost} total={total}")
    });
}
using var trace = new TraceLog(etlx);
RequireNoLoss(rawLoss, trace.EventsLost);
if (conversionWarnings.Count != 0)
    throw new InvalidOperationException("Incomplete conversion: " + string.Join("; ", conversionWarnings));
var samples = new List<Sample>();
var allocations = new List<Allocation>();
var starts = new Dictionary<(int Pid, int Tid, int Clr), Stack<ContentionStart>>();
var contentions = new List<Contention>();
var unmatchedStops = new List<double>();
using (var source = trace.Events.GetSource())
{
    var sampleParser = new SampleProfilerTraceEventParser(source);
    sampleParser.ThreadSample += data =>
    {
        if (data.ProcessID != begin.ProcessId) return;
        samples.Add(new(data.TimeStampRelativeMSec, data.ThreadID, data.Type.ToString(), Frames(data)));
    };
    source.Clr.GCAllocationTick += data =>
    {
        if (data.ProcessID != begin.ProcessId) return;
        allocations.Add(new(data.TimeStampRelativeMSec, data.TypeName ?? "<unnamed>",
            data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount,
            data.ObjectSize, data.AllocationKind.ToString(), Frames(data)));
    };
    source.Clr.ContentionStart += data =>
    {
        if (data.ProcessID != begin.ProcessId) return;
        var key = (data.ProcessID, data.ThreadID, data.ClrInstanceID);
        if (!starts.TryGetValue(key, out var stack)) starts[key] = stack = new();
        stack.Push(new(data.TimeStampRelativeMSec, data.ThreadID, data.ContentionFlags.ToString(), Frames(data)));
    };
    source.Clr.ContentionStop += data =>
    {
        if (data.ProcessID != begin.ProcessId) return;
        var key = (data.ProcessID, data.ThreadID, data.ClrInstanceID);
        if (!starts.TryGetValue(key, out var stack) || stack.Count == 0)
        { unmatchedStops.Add(data.TimeStampRelativeMSec); return; }
        var start = stack.Pop();
        if (!double.IsFinite(data.DurationNs) || data.DurationNs < 0 || data.TimeStampRelativeMSec < start.TimeMs)
            throw new InvalidOperationException("Invalid contention event duration/order.");
        contentions.Add(new(start.TimeMs, data.TimeStampRelativeMSec, data.DurationNs,
            start.ThreadId, start.Flags, start.Frames));
    };
    source.Process();
}
var measured = samples.Where(x => InWindow(x.TimeMs, begin.TimeMs, end.TimeMs)).ToArray();
var managed = measured.Where(x => x.Type == "Managed").ToArray();
var uncoveredStops = unmatchedStops.Count(x => InWindow(x, begin.TimeMs, end.TimeMs));
var uncoveredStarts = starts.Values.SelectMany(x => x).Count(x => x.TimeMs < end.TimeMs);
if (uncoveredStops != 0 || uncoveredStarts != 0)
    throw new InvalidOperationException($"Unpaired contention intersecting measurement: starts={uncoveredStarts}, stops={uncoveredStops}.");
if (managed.Length < 100 || managed.Count(x => x.Frames.Any(Resolved)) < 100)
    throw new InvalidOperationException($"Insufficient attributed managed samples: total={managed.Length}, resolved={managed.Count(x => x.Frames.Any(Resolved))}; no attribution claim is allowed.");
var waited = contentions.Select(x => new { Entry = x,
    WaitNs = ClippedNs(x.StartMs, x.StopMs, x.DurationNs, begin.TimeMs, end.TimeMs) })
    .Where(x => x.WaitNs > 0).ToArray();
var measuredAllocations = allocations.Where(x => InWindow(x.TimeMs, begin.TimeMs, end.TimeMs)).ToArray();
if (measuredAllocations.Any(x => x.IntervalBytes <= 0) || (!calibration && measuredAllocations.Length < 100))
    throw new InvalidOperationException("Insufficient/invalid GC allocation ticks for type attribution.");
var allocationTypes = measuredAllocations.GroupBy(x => x.Type, StringComparer.Ordinal).Select(g => new {
    type = g.Key, samples = g.Count(), weightedBytes = g.Sum(x => x.IntervalBytes),
    estimatedBytesPerOperation = g.Sum(x => x.IntervalBytes) / (double)normalized.Operations,
    estimatedBytesPerItem = g.Sum(x => x.IntervalBytes) / (double)normalized.Items,
    representativeObjectSizes = g.Select(x => x.ObjectBytes).Distinct().Order().ToArray(),
    allocationKinds = g.Select(x => x.Kind).Distinct().Order().ToArray()
}).OrderByDescending(x => x.weightedBytes).ToArray();
var allocationStacks = measuredAllocations.GroupBy(x => (x.Type, StackText(x.Frames))).Select(g => new {
    type = g.Key.Type, stack = g.Key.Item2, samples = g.Count(), weightedBytes = g.Sum(x => x.IntervalBytes),
    estimatedBytesPerOperation = g.Sum(x => x.IntervalBytes) / (double)normalized.Operations,
    estimatedBytesPerItem = g.Sum(x => x.IntervalBytes) / (double)normalized.Items
}).OrderByDescending(x => x.weightedBytes).ToArray();
var methods = managed.SelectMany(s => s.Frames.Distinct(StringComparer.Ordinal))
    .GroupBy(x => x, StringComparer.Ordinal).Select(g => new {
        method = g.Key, inclusiveSamples = g.Count(),
        inclusiveSamplesPerOperation = g.Count() / (double)normalized.Operations,
        inclusiveSamplesPerItem = g.Count() / (double)normalized.Items,
        inclusiveFractionOfManagedSamples = g.Count() / (double)managed.Length
    }).OrderByDescending(x => x.inclusiveSamples).ThenBy(x => x.method).ToArray();
var stackRows = measured.GroupBy(x => (x.Type, StackText(x.Frames))).Select(g => new {
    sampleType = g.Key.Type, stack = g.Key.Item2, samples = g.Count(),
    samplesPerOperation = g.Count() / (double)normalized.Operations,
    samplesPerItem = g.Count() / (double)normalized.Items
}).OrderByDescending(x => x.samples).ThenBy(x => x.stack).ToArray();
var contentionRows = waited.GroupBy(x => (x.Entry.Flags, StackText(x.Entry.Frames))).Select(g => new {
    flags = g.Key.Flags, stack = g.Key.Item2, overlappingContentions = g.Count(),
    countPerOperation = g.Count() / (double)normalized.Operations,
    observedWaitNs = g.Sum(x => x.WaitNs),
    observedWaitNsPerOperation = g.Sum(x => x.WaitNs) / normalized.Operations,
    observedWaitNsPerItem = g.Sum(x => x.WaitNs) / normalized.Items,
    boundaryClippedEvents = g.Count(x => x.Entry.StartMs < begin.TimeMs || x.Entry.StopMs > end.TimeMs)
}).OrderByDescending(x => x.observedWaitNs).ToArray();
if (calibration)
{
    bool Has(IEnumerable<Sample> rows, string method) => rows.Any(x => x.Frames.Any(f => f.Contains(method, StringComparison.Ordinal)));
    if (!allocations.Any(x => x.TimeMs < begin.TimeMs && x.Type.Contains("BeforeAllocationProbe", StringComparison.Ordinal)) ||
        !allocations.Any(x => x.TimeMs > end.TimeMs && x.Type.Contains("AfterAllocationProbe", StringComparison.Ordinal)) ||
        !measuredAllocations.Any(x => x.Type.Contains("InsideAllocationProbe", StringComparison.Ordinal)) ||
        measuredAllocations.Any(x => x.Type.Contains("BeforeAllocationProbe", StringComparison.Ordinal) || x.Type.Contains("AfterAllocationProbe", StringComparison.Ordinal)))
        throw new InvalidOperationException("Allocation calibration failed: before/inside/after type boundaries.");
    if (!Has(samples.Where(x => x.TimeMs < begin.TimeMs), "BusyBeforeWindow") ||
        !Has(samples.Where(x => x.TimeMs > end.TimeMs), "BusyAfterWindow") ||
        !Has(managed, "BusyInsideWindow") || Has(measured, "BusyBeforeWindow") || Has(measured, "BusyAfterWindow") ||
        !waited.Any(x => x.Entry.Frames.Any(f => f.Contains("ContendedInsideMonitor", StringComparison.Ordinal))) ||
        !waited.Any(x => x.Entry.Frames.Any(f => f.Contains("ContendedInsideSystemLock", StringComparison.Ordinal))) ||
        waited.Sum(x => x.WaitNs) < 10_000_000)
        throw new InvalidOperationException("Calibration failed: attributed inside/outside busy methods or contended lock did not match expected window.");
}
var result = new {
    schemaVersion = 1, diagnosticOnly = true, calibration,
    trace = Path.GetFileName(args[0]), report = Path.GetFileName(args[1]),
    traceEventAssemblyVersion = typeof(TraceLog).Assembly.GetName().Version?.ToString(),
    traceEventInformationalVersion = typeof(TraceLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    normalized.Identity, normalized.Kind, normalized.Workload, normalized.Transport, normalized.Operations, normalized.Items,
    normalized.ProcessCpuUsPerOperation, normalized.ReportWindowSeconds,
    processId = begin.ProcessId, windowBeginMs = begin.TimeMs, windowEndMs = end.TimeMs,
    admissionsClosedMs = admissions.Length == 1 ? (double?)admissions[0].TimeMs : null,
    actualProfileWindowSeconds = (end.TimeMs - begin.TimeMs) / 1000,
    rawEventsLost = rawLoss, convertedEventsLost = trace.EventsLost,
    managedSamples = managed.Length,
    managedSamplesWithResolvedFrame = managed.Count(x => x.Frames.Any(Resolved)),
    managedSamplesWithSharpLinkFrame = managed.Count(x => x.Frames.Any(f => f.Contains("SharpLink", StringComparison.Ordinal))),
    noStackSamples = measured.Count(x => x.Frames.Length == 0),
    sampleTypes = measured.GroupBy(x => x.Type).ToDictionary(g => g.Key, g => g.Count()),
    measuredSamplesPerOperation = measured.Length / (double)normalized.Operations,
    managedSamplesPerOperation = managed.Length / (double)normalized.Operations,
    contentionCountPerOperation = waited.Length / (double)normalized.Operations,
    contentionWaitNsPerOperation = waited.Sum(x => x.WaitNs) / normalized.Operations,
    contentionCount = waited.Length, contentionWaitNs = waited.Sum(x => x.WaitNs),
    whollyInsideContentions = waited.Count(x => x.Entry.StartMs >= begin.TimeMs && x.Entry.StopMs <= end.TimeMs),
    unmatchedStopsOutsideWindow = unmatchedStops.Count - uncoveredStops,
    unmatchedStartsAfterWindow = starts.Values.Sum(x => x.Count) - uncoveredStarts,
    caveat = "EventPipe managed thread samples are not scheduler on-CPU samples. Samples/op and samples/item are normalized observations, not nanoseconds or registration costs. Process CPU us/op is a separate whole-process counter. Contention events cover CLR monitor contention, not every async wait, interlocked operation, spin, socket or SHM wait. Boundary-overlapping wait duration is proportionally clipped. Instrumented results never decide throughput acceptance.",
    allocationTickSamples = measuredAllocations.Length,
    allocationTicksWithResolvedStack = measuredAllocations.Count(x => x.Frames.Any(Resolved)),
    measuredAllocatedBytes = normalized.AllocatedBytes,
    measuredAllocatedBytesPerOperation = normalized.AllocatedBytes / (double)normalized.Operations,
    measuredAllocatedBytesPerItem = normalized.AllocatedBytes / (double)normalized.Items,
    sampledAllocationIntervalBytes = measuredAllocations.Sum(x => x.IntervalBytes),
    allocationSampleCoverageRatio = normalized.AllocatedBytes > 0 ? measuredAllocations.Sum(x => x.IntervalBytes) / (double)normalized.AllocatedBytes : (double?)null,
    allocationCaveat = "GCAllocationTick weights assign the allocation interval to its triggering sampled type/stack. These are sampled estimates, not exact per-type or per-registration byte costs. Boundary ticks can straddle markers. Sparse types need more evidence; actual process allocation delta is reported separately.",
    methods, stacks = stackRows, contentionStacks = contentionRows, allocationTypes, allocationStacks
};
File.WriteAllText(output, JsonSerializer.Serialize(result, jsonOptions) + "\n");
Console.WriteLine($"PROFILE workload={normalized.Workload} ops={normalized.Operations} items={normalized.Items} managedSamples={managed.Length} contentions={waited.Length} loss={rawLoss}/{trace.EventsLost}");
foreach (var row in methods.Take(20)) Console.WriteLine($"METHOD samples/op={row.inclusiveSamplesPerOperation:F6} hits={row.inclusiveSamples} {row.method}");

static bool InWindow(double t, double begin, double end) => t > begin && t < end;
static bool Resolved(string frame) => !frame.Contains("!<unresolved>", StringComparison.Ordinal) && frame != "<no-stack>";
static string StackText(string[] frames) => frames.Length == 0 ? "<no-stack>" : string.Join(";", frames.Reverse());
static string[] Frames(TraceEvent data)
{
    var frames = new List<string>();
    for (var stack = data.CallStack(); stack != null; stack = stack.Caller)
    {
        if (frames.Count == 4096) throw new InvalidOperationException("Unexpected cyclic/deep call stack.");
        var code = stack.CodeAddress;
        var module = string.IsNullOrEmpty(code.ModuleName) ? "<unknown-module>" : code.ModuleName;
        var method = code.FullMethodName;
        // Preserve unresolved frames as an explicit shared bucket; ASLR addresses cannot be compared across arms.
        frames.Add(module + "!" + (string.IsNullOrEmpty(method) ? "<unresolved>" : method));
    }
    return frames.ToArray();
}
static double ClippedNs(double start, double stop, double durationNs, double begin, double end)
{
    if (stop <= start) return 0;
    var overlap = Math.Max(0, Math.Min(stop, end) - Math.Max(start, begin));
    return durationNs * (overlap / (stop - start));
}
static void RequireNoLoss(long raw, long converted)
{
    if (raw != 0 || converted != 0) throw new InvalidOperationException($"Incomplete trace: raw events lost={raw}; converted events lost={converted}.");
}
static void ExpectFailure(Action action, string test)
{
    try { action(); } catch (InvalidOperationException) { Console.WriteLine("PASS " + test); return; }
    throw new Exception("Expected rejection: " + test);
}
static Normalized NormalizeReport(JsonElement r, bool calibration)
{
    var workload = r.GetProperty("workload").GetString()!;
    if ((calibration && workload != "calibration") ||
        (!calibration && workload is not ("rpc-Server1x16" or "c8-s2c-10000")))
        throw new InvalidOperationException("Unexpected normalized workload.");
    var transport = r.GetProperty("transport").GetString()!;
    var allocatedBytes = r.GetProperty("allocatedBytes").GetInt64();
    if ((!calibration && transport is not ("sharedmemory" or "tcp")) ||
        (workload == "rpc-Server1x16" && transport != "sharedmemory") || allocatedBytes < 0)
        throw new InvalidOperationException("Unexpected transport or allocated-byte denominator.");
    var operations = r.GetProperty("operations").GetInt64();
    var items = r.GetProperty("items").GetInt64();
    if (new[] { "validationFailures", "failure", "cancelled" }.Any(k => r.GetProperty(k).GetInt64() != 0) ||
        r.GetProperty("operationsStarted").GetInt64() != operations ||
        items != checked(operations * (workload == "c8-s2c-10000" ? 10000 : 1)))
        throw new InvalidOperationException("Incomplete/invalid normalized completed-operation denominators.");
    var n = new Normalized(r.GetProperty("commit").GetString()!,
        workload == "c8-s2c-10000" ? "c8" : calibration ? "calibration" : "generated",
        workload, transport, operations, items, allocatedBytes,
        r.GetProperty("processCpuMs").GetDouble() * 1000 / operations,
        r.GetProperty("profileWindowSeconds").GetDouble());
    if (n.Operations <= 0 || n.Items <= 0 || n.Identity is null or "" or "unknown" ||
        !double.IsFinite(n.ProcessCpuUsPerOperation) || n.ProcessCpuUsPerOperation <= 0 ||
        !double.IsFinite(n.ReportWindowSeconds) || n.ReportWindowSeconds <= 0)
        throw new InvalidOperationException("Invalid report denominators/CPU/identity/window.");
    return n;
}
internal sealed record Normalized(string Identity, string Kind, string Workload, string Transport, long Operations, long Items, long AllocatedBytes, double ProcessCpuUsPerOperation, double ReportWindowSeconds);
internal sealed record Marker(double TimeMs, int ProcessId, int Kind, string Workload, long Operations, long Items);
internal sealed record Sample(double TimeMs, int ThreadId, string Type, string[] Frames);
internal sealed record ContentionStart(double TimeMs, int ThreadId, string Flags, string[] Frames);
internal sealed record Contention(double StartMs, double StopMs, double DurationNs, int ThreadId, string Flags, string[] Frames);

internal sealed record Allocation(double TimeMs, string Type, long IntervalBytes, long ObjectBytes, string Kind, string[] Frames);
