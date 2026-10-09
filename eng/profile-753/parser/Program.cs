using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

const string Provider = "SharpLink-753-ProfileWindow";
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (args.Length == 1 && args[0] == "--self-test") { SelfTests(); return; }
if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "--calibration"))
    throw new ArgumentException("ProfileParser TRACE NORMALIZED_JSON OUTPUT_JSON [--calibration], or --self-test");
var calibration = args.Length == 4;
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
if (File.Exists(output)) throw new IOException("Refusing to overwrite profile output.");
using var report = JsonDocument.Parse(File.ReadAllText(args[1]));
var r = Normalize(report.RootElement, calibration);
var rawMarkers = new List<Marker>();
int rawLoss;
Console.WriteLine("RAW_BEGIN");
using (var source = new EventPipeEventSource(args[0]))
{
    source.Dynamic.All += data => { if (data.ProviderName == Provider) rawMarkers.Add(ReadMarker(data)); };
    source.Process(); rawLoss = source.EventsLost;
}
NoLoss(rawLoss, 0);
ValidateMarkers(rawMarkers, r);
Console.WriteLine($"RAW_OK markers={rawMarkers.Count} loss={rawLoss}");
var etlx = Path.ChangeExtension(output, ".etlx");
if (File.Exists(etlx)) throw new IOException("Refusing stale converted trace.");
var warnings = new List<string>();
Console.WriteLine("CONVERT_BEGIN");
using (var log = new StreamWriter(new FileStream(output + ".conversion.log", FileMode.CreateNew)))
{
    TraceLog.CreateFromEventPipeDataFile(args[0], etlx, new TraceLogOptions {
        ContinueOnError = false, KeepAllEvents = true, LocalSymbolsOnly = true, ConversionLog = log,
        OnLostEvents = (truncated, lost, total) => { if (truncated || lost != 0) warnings.Add($"truncated={truncated} lost={lost} total={total}"); }
    });
}
using var trace = new TraceLog(etlx);
NoLoss(rawLoss, trace.EventsLost);
if (warnings.Count != 0) throw new InvalidOperationException("Incomplete conversion: " + string.Join(";", warnings));
Console.WriteLine("CONVERT_OK");
var markers = new List<Marker>();
var samples = new List<Sample>();
var starts = new Dictionary<(int Tid, int Clr), Stack<Start>>();
var waits = new List<Wait>();
var unmatchedStops = new List<double>();
var gc = new List<object>();
var threadPool = new List<object>();
using (var source = trace.Events.GetSource())
{
    source.Dynamic.All += data => { if (data.ProviderName == Provider) markers.Add(ReadMarker(data)); };
    var profiler = new SampleProfilerTraceEventParser(source);
    profiler.ThreadSample += data => {
        if (data.ProcessID == r.ProcessId)
            samples.Add(new(data.TimeStampRelativeMSec, data.ThreadID, data.Type.ToString(), Frames(data)));
    };
    source.Clr.ContentionStart += data => {
        if (data.ProcessID != r.ProcessId) return;
        var key = (data.ThreadID, (int)data.ClrInstanceID);
        if (!starts.TryGetValue(key, out var stack)) starts[key] = stack = new();
        stack.Push(new(data.TimeStampRelativeMSec, data.ThreadID, data.ContentionFlags.ToString(), Frames(data)));
    };
    source.Clr.ContentionStop += data => {
        if (data.ProcessID != r.ProcessId) return;
        var key = (data.ThreadID, (int)data.ClrInstanceID);
        if (!starts.TryGetValue(key, out var stack) || stack.Count == 0)
        { unmatchedStops.Add(data.TimeStampRelativeMSec); return; }
        var start = stack.Pop();
        if (!double.IsFinite(data.DurationNs) || data.DurationNs < 0 || data.TimeStampRelativeMSec < start.TimeMs)
            throw new InvalidOperationException("Invalid contention duration/order.");
        waits.Add(new(start.TimeMs, data.TimeStampRelativeMSec, data.DurationNs, start.Tid, start.Flags, start.Frames));
    };
    source.Clr.GCStart += data => {
        if (data.ProcessID == r.ProcessId) gc.Add(new { timeMs = data.TimeStampRelativeMSec, kind = "start", data.Count, data.Depth, reason = data.Reason.ToString(), type = data.Type.ToString() });
    };
    source.Clr.GCStop += data => {
        if (data.ProcessID == r.ProcessId) gc.Add(new { timeMs = data.TimeStampRelativeMSec, kind = "stop", data.Count, data.Depth });
    };
    source.Clr.ThreadPoolWorkerThreadStart += data => {
        if (data.ProcessID == r.ProcessId) threadPool.Add(new { timeMs = data.TimeStampRelativeMSec, kind = "worker-start", data.ActiveWorkerThreadCount });
    };
    source.Clr.ThreadPoolWorkerThreadStop += data => {
        if (data.ProcessID == r.ProcessId) threadPool.Add(new { timeMs = data.TimeStampRelativeMSec, kind = "worker-stop", data.ActiveWorkerThreadCount });
    };
    source.Process();
}
ValidateMarkers(markers, r);
// All attribution uses the converted trace's OWN time origin, not raw-trace relative time.
var begin = markers.Single(x => x.Id == 1); var close = markers.Single(x => x.Id == 2); var end = markers.Single(x => x.Id == 3);
for (var i = 0; i < 3; i++)
    if (Math.Abs((markers[i].TimeMs - markers[0].TimeMs) - (rawMarkers[i].TimeMs - rawMarkers[0].TimeMs)) > 1)
        throw new InvalidOperationException("Raw/converted marker intervals disagree.");
// Mapping admission ticks to EventPipe time is approximate by the delay to emitting Close.
// Retain that uncertainty explicitly; exact outer marker window is the primary population.
var admissionStartMs = close.TimeMs - r.MeasurementSeconds * 1000;
var measured = samples.Where(x => Inside(x.TimeMs, begin.TimeMs, end.TimeMs)).ToArray();
var managed = measured.Where(x => x.Type == "Managed").ToArray();
var resolved = managed.Count(x => x.Frames.Any(Resolved));
if (managed.Length < (calibration ? 100 : 1000) || resolved < (calibration ? 100 : 1000))
    throw new InvalidOperationException($"Insufficient managed/resolved samples: {managed.Length}/{resolved}.");
var unmatchedInside = unmatchedStops.Count(x => Inside(x, begin.TimeMs, end.TimeMs));
var unmatchedBeforeEnd = starts.Values.SelectMany(x => x).Count(x => x.TimeMs < end.TimeMs);
if (unmatchedInside != 0 || unmatchedBeforeEnd != 0)
    throw new InvalidOperationException($"Unpaired waits intersect window: starts={unmatchedBeforeEnd}, stops={unmatchedInside}.");
var insideWaits = waits.Where(x => WaitInside(x, begin.TimeMs, end.TimeMs)).ToArray();
var boundaryWaits = waits.Where(x => WaitCrosses(x, begin.TimeMs, end.TimeMs)).ToArray();
if (calibration)
{
    bool Has(IEnumerable<Sample> s, string name) => s.Any(x => x.Frames.Any(f => f.Contains(name, StringComparison.Ordinal)));
    if (!Has(samples.Where(x => x.TimeMs < begin.TimeMs), "BusyBeforeWindow") ||
        !Has(samples.Where(x => x.TimeMs > end.TimeMs), "BusyAfterWindow") ||
        !Has(managed, "BusyInsideWindow") || Has(measured, "BusyBeforeWindow") || Has(measured, "BusyAfterWindow"))
        throw new InvalidOperationException("Sample window/callsite calibration failed.");
    foreach (var method in new[] { "ContendedInsideMonitor", "ContendedInsideSystemLock" })
        if (!insideWaits.Any(x => x.DurationNs > 10_000_000 && x.Frames.Any(f => f.Contains(method, StringComparison.Ordinal))))
            throw new InvalidOperationException("Missing paired/attributed positive contention: " + method);
}
var groups = Groups();
var groupRows = groups.Select(g => new {
    group = g.Key, hits = managed.Count(x => g.Value(x.Frames)),
    resolvedRelevantPathHits = managed.Count(x => g.Value(x.Frames) && ReaderPrefixResolved(x.Frames)),
    relevantPathResolvedFraction = managed.Any(x => g.Value(x.Frames))
        ? (double?)managed.Count(x => g.Value(x.Frames) && ReaderPrefixResolved(x.Frames)) / managed.Count(x => g.Value(x.Frames)) : null,
    hitsPerOperation = managed.Count(x => g.Value(x.Frames)) / (double)r.Operations,
    hitsPerSecond = managed.Count(x => g.Value(x.Frames)) / ((end.TimeMs - begin.TimeMs) / 1000),
    oneSecondBins = Enumerable.Range(0, (int)Math.Ceiling((end.TimeMs - begin.TimeMs) / 1000))
        .Select(i => managed.Count(x => g.Value(x.Frames) && x.TimeMs >= begin.TimeMs + i * 1000 && x.TimeMs < begin.TimeMs + (i + 1) * 1000)).ToArray()
}).ToArray();
var methods = managed.SelectMany(x => x.Frames.Distinct(StringComparer.Ordinal)).GroupBy(x => x, StringComparer.Ordinal)
    .Select(g => new { method = g.Key, inclusiveHits = g.Count(), hitsPerOperation = g.Count() / (double)r.Operations })
    .OrderByDescending(x => x.inclusiveHits).ToArray();
var stacks = managed.GroupBy(x => StackText(x.Frames)).Select(g => new {
    stack = g.Key, hits = g.Count(), hitsPerOperation = g.Count() / (double)r.Operations,
    threadIds = g.Select(x => x.Tid).Distinct().Order().ToArray()
}).OrderByDescending(x => x.hits).ToArray();
var contentionStacks = insideWaits.GroupBy(x => StackText(x.Frames)).Select(g => new {
    stack = g.Key, count = g.Count(), countPerOperation = g.Count() / (double)r.Operations,
    observedWaitNs = g.Sum(x => x.DurationNs), observedWaitNsPerOperation = g.Sum(x => x.DurationNs) / r.Operations,
    durationsNs = g.Select(x => x.DurationNs).Order().ToArray(), threadIds = g.Select(x => x.Tid).Distinct().Order().ToArray()
}).OrderByDescending(x => x.observedWaitNs).ToArray();
var intervals = managed.GroupBy(x => x.Tid).SelectMany(g => {
    var times = g.Select(x => x.TimeMs).Order().ToArray();
    return times.Skip(1).Select((t, i) => t - times[i]);
}).Where(x => x > 0).Order().ToArray();
var result = new {
    schemaVersion = 1, diagnosticOnly = true, acceptanceTimingsInvalid = true, calibration,
    inputTrace = Path.GetFileName(args[0]), normalizedReport = Path.GetFileName(args[1]),
    traceEventVersion = typeof(TraceLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    identity = r.Commit, r.RunId, r.ProcessId, r.Workload, r.Operations, r.RuntimeVersion,
    r.MeasurementSeconds, r.DrainSeconds, r.ProfileWindowSeconds,
    profileMarkerSeconds = (end.TimeMs - begin.TimeMs) / 1000,
    markerMinusReportedWindowSeconds = (end.TimeMs - begin.TimeMs) / 1000 - r.ProfileWindowSeconds,
    beginMs = begin.TimeMs, admissionsCloseMs = close.TimeMs, endMs = end.TimeMs,
    admissionStartEstimateMs = admissionStartMs,
    estimatedAdmissionSamples = measured.Count(x => x.TimeMs >= admissionStartMs && x.TimeMs <= close.TimeMs),
    postAdmissionSamples = measured.Count(x => x.TimeMs > close.TimeMs),
    admissionMappingCaveat = "Close event is emitted after lifecycle stoppedTicks; mapped start/stop has that unmeasured emission delay. Outer marker interval is primary. Exact lifecycle ticks are validated against report.",
    rawEventsLost = rawLoss, convertedEventsLost = trace.EventsLost,
    totalMeasuredSamples = measured.Length, managedSamples = managed.Length, resolvedManagedSamples = resolved,
    managedWithAnyResolvedFrameFraction = resolved / (double)managed.Length,
    fullyResolvedManagedStacks = managed.Count(x => x.Frames.Length > 0 && x.Frames.All(Resolved)),
    partiallyResolvedManagedStacks = managed.Count(x => x.Frames.Any(Resolved) && x.Frames.Any(f => !Resolved(f))),
    entirelyUnresolvedManagedStacks = managed.Count(x => x.Frames.Length > 0 && !x.Frames.Any(Resolved)),
    resolvedManagedLeafSamples = managed.Count(x => x.Frames.Length > 0 && Resolved(x.Frames[0])),
    readerSamples = managed.Count(x => x.Frames.Any(Reader)),
    readerSamplesWithResolvedRelevantPrefix = managed.Count(x => x.Frames.Any(Reader) && ReaderPrefixResolved(x.Frames)),
    resolutionCaveat = "Any resolved frame may be only a ThreadPool root; it is a collection check, not usable attribution. A group lead requires >=90% resolved leaf-through-outermost-reader prefixes and >=100 such hits per trace; otherwise inconclusive.",
    noStackSamples = measured.Count(x => x.Frames.Length == 0),
    sampleTypes = measured.GroupBy(x => x.Type).ToDictionary(g => g.Key, g => g.Count()),
    managedSampleInterArrivalMs = new { scope = "Per-thread gaps between managed samples only; includes intervening external/unsampled time, not provider cadence", count = intervals.Length, min = Quantile(intervals, 0), median = Quantile(intervals, .5), p95 = Quantile(intervals, .95), max = Quantile(intervals, 1) },
    groups = groupRows, methods, stacks, contentionStacks,
    insideContentionCount = insideWaits.Length, insideContentionWaitNs = insideWaits.Sum(x => x.DurationNs),
    boundaryContentions = boundaryWaits, unmatchedStopsOutsideWindow = unmatchedStops.Count - unmatchedInside,
    unmatchedStartsAfterWindow = starts.Values.Sum(x => x.Count) - unmatchedBeforeEnd,
    gcEventsWholeTrace = gc, threadPoolEventsWholeTrace = threadPool,
    wholeProcessCpuMs = r.ProcessCpuMs, wholeProcessAllocatedBytes = r.AllocatedBytes,
    caveat = "All-thread managed sampling is not on-CPU time. Inclusive groups overlap. Hits/op cannot price CPU/method or explain a percent throughput change. Contention captures waiting after initial spin, not all locking/spinning/atomics. Wait sums are thread-blocking observations, not request latency. No causality or acceptance verdict from this trace."
};
using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(file, result, json);
Console.WriteLine($"PROFILE_OK calibration={calibration} run={r.RunId} ops={r.Operations} managed={managed.Length} resolved={resolved} waits={insideWaits.Length}");

static bool Reader(string f) => f.Contains("ReadOwnershipPipeReader", StringComparison.Ordinal);
static bool BeneathReader(string[] frames, Func<string, bool> predicate)
{
    // Frames are leaf -> root. A candidate operation must be a CALLEE of a reader frame,
    // not a generic ExecutionContext/OnCompleted caller above it.
    for (var i = 0; i < frames.Length; i++)
        if (predicate(frames[i]) && frames.Skip(i + 1).Any(Reader)) return true;
    return false;
}
static bool ReaderPrefixResolved(string[] frames)
{
    var outermost = Array.FindLastIndex(frames, Reader);
    return outermost >= 0 && frames.Take(outermost + 1).All(Resolved);
}
static Dictionary<string, Func<string[], bool>> Groups() => new() {
    ["reader-any"] = f => f.Any(Reader),
    ["reader-lock-or-spin"] = f => BeneathReader(f, x => x.Contains("System.Threading.Lock", StringComparison.Ordinal) || x.Contains("SpinWait", StringComparison.Ordinal)),
    ["reader-notification"] = f => f.Any(x => Reader(x) && (x.Contains("OnCompleted", StringComparison.Ordinal) || x.Contains("PublishRead", StringComparison.Ordinal))) ||
        BeneathReader(f, x => x.Contains("ManualResetValueTaskSourceCore", StringComparison.Ordinal)),
    ["reader-execution-context"] = f => BeneathReader(f, x => x.Contains("ExecutionContext", StringComparison.Ordinal)),
    ["reader-client-proven-stack"] = f => f.Any(Reader) && f.Any(x => x.Contains("SharpLinkClient", StringComparison.Ordinal)),
    ["reader-server-proven-stack"] = f => f.Any(Reader) && f.Any(x => x.Contains("SharpLinkServer", StringComparison.Ordinal))
};
static bool WaitInside(Wait w, double begin, double end) => w.StartMs >= begin && w.StopMs <= end;
static bool WaitCrosses(Wait w, double begin, double end) => w.StartMs < end && w.StopMs > begin && !WaitInside(w, begin, end);
static bool Inside(double t, double begin, double end) => t > begin && t < end;
static bool Resolved(string f) => !f.EndsWith("!<unresolved>", StringComparison.Ordinal) && f != "<no-stack>";
static string StackText(string[] f) => f.Length == 0 ? "<no-stack>" : string.Join(";", f.Reverse());
static string[] Frames(TraceEvent data)
{
    var frames = new List<string>();
    for (var s = data.CallStack(); s != null; s = s.Caller)
    {
        if (frames.Count >= 4096) throw new InvalidOperationException("Cyclic/deep stack.");
        var c = s.CodeAddress; var module = string.IsNullOrEmpty(c.ModuleName) ? "<unknown>" : c.ModuleName;
        frames.Add(module + "!" + (string.IsNullOrEmpty(c.FullMethodName) ? "<unresolved>" : c.FullMethodName));
    }
    return frames.ToArray();
}
static Marker ReadMarker(TraceEvent d)
{
    var id = (int)d.ID;
    if (id is < 1 or > 3) throw new InvalidOperationException("Unexpected marker/error event: " + d);
    string Text(string k) => Convert.ToString(d.PayloadByName(k), CultureInfo.InvariantCulture)!;
    long Number(string k) => Convert.ToInt64(d.PayloadByName(k), CultureInfo.InvariantCulture);
    return new(d.TimeStampRelativeMSec, d.ProcessID, id, Text("workload"), Text("runId"),
        id == 2 ? 0 : Number("boundaryTicks"), id == 2 ? Number("startedTicks") : 0,
        id == 2 ? Number("stoppedTicks") : 0, id == 3 ? Number("operations") : 0);
}
static void ValidateMarkers(IReadOnlyList<Marker> m, Report r)
{
    if (m.Count != 3 || m[0].Id != 1 || m[1].Id != 2 || m[2].Id != 3 ||
        m.Any(x => x.Pid != r.ProcessId || x.RunId != r.RunId || x.Workload != r.Workload) ||
        !(m[0].TimeMs < m[1].TimeMs && m[1].TimeMs < m[2].TimeMs) ||
        m[0].BoundaryTicks != r.BeginTicks || m[1].StartedTicks != r.StartedTicks ||
        m[1].StoppedTicks != r.StoppedTicks || m[2].BoundaryTicks != r.EndTicks || m[2].Operations != r.Operations)
        throw new InvalidOperationException("Invalid marker count/order/PID/runId/ticks/count.");
    var tickSeconds = (r.EndTicks - r.BeginTicks) / (double)r.Frequency;
    var traceSeconds = (m[2].TimeMs - m[0].TimeMs) / 1000;
    if (Math.Abs(tickSeconds - traceSeconds) > .1 || Math.Abs(traceSeconds - r.ProfileWindowSeconds) > 1)
        throw new InvalidOperationException("Trace, marker ticks and report window disagree.");
}
static Report Normalize(JsonElement e, bool calibration)
{
    string S(string k) => e.GetProperty(k).GetString()!;
    long L(string k) => e.GetProperty(k).GetInt64();
    double D(string k) => e.GetProperty(k).GetDouble();
    var r = new Report(S("workload"), S("runId"), checked((int)L("processId")), S("commit"), S("runtimeVersion"),
        L("operations"), L("stopwatchFrequency"), L("beginTicks"), L("startedTicks"), L("stoppedTicks"), L("endTicks"),
        D("measurementSeconds"), D("drainSeconds"), D("profileWindowSeconds"), D("processCpuMs"), L("allocatedBytes"));
    var ids = new[] { "072a3a13fca0aa9186d2db44e38e5fc846969b07", "ac903d63c541d972f4268bb404b3c09ce63776e4", "1a24cc07036dd2c36f269d8df66cd81f85c14f08" };
    if (r.Workload != (calibration ? "calibration" : "tcp-add-c1") || S("transport") != (calibration ? "synthetic" : "tcp") ||
        (calibration ? r.Commit != "calibration" : !ids.Contains(r.Commit)) || r.RuntimeVersion != "10.0.12" ||
        string.IsNullOrWhiteSpace(r.RunId) || r.ProcessId <= 0 || r.Operations <= 0 || r.Frequency <= 0 ||
        r.BeginTicks <= 0 || r.StartedTicks < r.BeginTicks || r.StoppedTicks <= r.StartedTicks || r.EndTicks < r.StoppedTicks ||
        L("operationsStarted") != r.Operations || L("items") != r.Operations ||
        new[] { "failure", "cancelled", "validationFailures" }.Any(k => L(k) != 0) ||
        !double.IsFinite(r.ProcessCpuMs) || r.ProcessCpuMs <= 0 || r.AllocatedBytes < 0 ||
        !double.IsFinite(r.MeasurementSeconds) || r.MeasurementSeconds <= 0 ||
        !double.IsFinite(r.DrainSeconds) || r.DrainSeconds < 0 || r.DrainSeconds > 30 ||
        !double.IsFinite(r.ProfileWindowSeconds) || Math.Abs(r.ProfileWindowSeconds - r.MeasurementSeconds - r.DrainSeconds) > .001 ||
        Math.Abs((r.StoppedTicks - r.StartedTicks) / (double)r.Frequency - r.MeasurementSeconds) > .001 ||
        (!calibration && (r.MeasurementSeconds < 15 * .99 || r.MeasurementSeconds > 16)))
        throw new InvalidOperationException("Invalid normalized identity, window, runtime or completed-operation denominator.");
    return r;
}
static void NoLoss(long raw, long converted)
{
    if (raw != 0 || converted != 0) throw new InvalidOperationException($"Event loss: raw={raw}, converted={converted}.");
}
static double? Quantile(double[] a, double q) => a.Length == 0 ? null : a[(int)Math.Round((a.Length - 1) * q)];
static void SelfTests()
{
    void Reject(Action f, string name) { try { f(); } catch (InvalidOperationException) { Console.WriteLine("PASS " + name); return; } throw new Exception("Expected rejection: " + name); }
    NoLoss(0, 0); Reject(() => NoLoss(1, 0), "raw-loss"); Reject(() => NoLoss(0, 1), "converted-loss");
    if (Inside(0, 0, 2) || !Inside(1, 0, 2) || Inside(2, 0, 2)) throw new Exception("Boundary predicate.");
    var r = new Report("tcp-add-c1", "run", 7, "test", "10.0.12", 10, 1000, 1000, 1010, 16010, 16020, 15, .01, 15.01, 5, 0);
    Marker[] m = [new(0,7,1,r.Workload,r.RunId,1000,0,0,0), new(15010,7,2,r.Workload,r.RunId,0,1010,16010,0), new(15020,7,3,r.Workload,r.RunId,16020,0,0,10)];
    ValidateMarkers(m, r);
    Reject(() => ValidateMarkers(m.Take(2).ToArray(), r), "missing-marker");
    Reject(() => ValidateMarkers(m.Append(m[2]).ToArray(), r), "duplicate-marker");
    Reject(() => ValidateMarkers(m, r with { RunId = "wrong" }), "wrong-runId");
    Reject(() => ValidateMarkers(m, r with { ProcessId = 8 }), "wrong-PID");
    Reject(() => ValidateMarkers(m, r with { Operations = 11 }), "wrong-operations");
    Reject(() => ValidateMarkers(m, r with { StartedTicks = 1011 }), "wrong-ticks");
    Reject(() => ValidateMarkers(m, r with { ProfileWindowSeconds = 1 }), "wrong-window");
    Reject(() => ValidateMarkers([m[0],m[2],m[1]], r), "wrong-order");
    var groups = Groups();
    const string reader = "Runtime!ReadOwnershipPipeReader.ReadAsync";
    foreach (var (name, operation) in new[] { ("reader-lock-or-spin", "CoreLib!System.Threading.Lock.TryEnterSlow"), ("reader-execution-context", "CoreLib!ExecutionContext.Run") })
    {
        if (!groups[name]([operation, reader]) || groups[name]([reader, operation])) throw new Exception("Reversed callee group: " + name);
    }
    if (groups["reader-notification"]([reader, "Other!Awaiter.OnCompleted"]) ||
        !groups["reader-notification"](["Runtime!ReadOwnershipPipeReader.OnCompleted"]) ||
        !groups["reader-notification"](["CoreLib!ManualResetValueTaskSourceCore.OnCompleted", reader]))
        throw new Exception("Notification group association.");
    if (ReaderPrefixResolved(["X!<unresolved>", reader]) || !ReaderPrefixResolved([reader, "X!<unresolved>"]))
        throw new Exception("Relevant prefix resolution.");
    var w = new Wait(2, 4, 2_000_000, 1, "Managed", []);
    if (!WaitInside(w, 1, 5) || WaitCrosses(w, 1, 5) || !WaitCrosses(w, 3, 5) || WaitInside(w, 3, 5) || WaitCrosses(w, 5, 6))
        throw new Exception("Wait boundary classification.");
    var valid = new Dictionary<string, object> {
        ["workload"]="tcp-add-c1", ["transport"]="tcp", ["runId"]="test", ["processId"]=7,
        ["commit"]="072a3a13fca0aa9186d2db44e38e5fc846969b07", ["runtimeVersion"]="10.0.12",
        ["operations"]=10, ["items"]=10, ["operationsStarted"]=10, ["stopwatchFrequency"]=1000,
        ["beginTicks"]=1000, ["startedTicks"]=1010, ["stoppedTicks"]=16010, ["endTicks"]=16020,
        ["failure"]=0,["cancelled"]=0,["validationFailures"]=0, ["measurementSeconds"]=15d,
        ["drainSeconds"]=.01, ["profileWindowSeconds"]=15.01, ["processCpuMs"]=5d, ["allocatedBytes"]=0
    };
    Report Parse(Dictionary<string, object> v) { using var d = JsonDocument.Parse(JsonSerializer.Serialize(v)); return Normalize(d.RootElement, false); }
    _ = Parse(valid);
    // Match the existing formal workload gate: requested15s permits >=99% actual interval.
    // Task.Delay timer precision can legitimately produce14.999s; trace the observed interval.
    var timerPrecision = new Dictionary<string, object>(valid) {
        ["measurementSeconds"]=14.999, ["stoppedTicks"]=16009, ["endTicks"]=16019,
        ["profileWindowSeconds"]=15.009
    };
    _ = Parse(timerPrecision);
    foreach (var (key, value) in new (string, object)[] { ("commit","wrong"),("runtimeVersion","10.0.2"),("operationsStarted",11),("operations",0),("measurementSeconds",14d),("stoppedTicks",16012),("drainSeconds",-1d),("failure",1) })
    {
        var bad = new Dictionary<string, object>(valid) { [key] = value };
        Reject(() => Parse(bad), "normalize-" + key);
    }
    Console.WriteLine("PASS group direction, notification ownership, resolution, wait boundaries, normalized guards; synthetic loss counts are not a real dropped-event trace test.");
}
internal sealed record Report(string Workload, string RunId, int ProcessId, string Commit, string RuntimeVersion, long Operations, long Frequency, long BeginTicks, long StartedTicks, long StoppedTicks, long EndTicks, double MeasurementSeconds, double DrainSeconds, double ProfileWindowSeconds, double ProcessCpuMs, long AllocatedBytes);
internal sealed record Marker(double TimeMs, int Pid, int Id, string Workload, string RunId, long BoundaryTicks, long StartedTicks, long StoppedTicks, long Operations);
internal sealed record Sample(double TimeMs, int Tid, string Type, string[] Frames);
internal sealed record Start(double TimeMs, int Tid, string Flags, string[] Frames);
internal sealed record Wait(double StartMs, double StopMs, double DurationNs, int Tid, string Flags, string[] Frames);
