using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// Decode only; do not conflate threshold-crossing type/stack with exact allocation ownership.
if (args.Length != 3) throw new ArgumentException("trace.nettrace sample output-prefix");
string input = Path.GetFullPath(args[0]), sample = args[1], prefix = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var lossNotifications = new List<object>();
string etlx = TraceLog.CreateFromEventPipeDataFile(input, prefix + ".etlx", new TraceLogOptions
{
    OnLostEvents = (truncated, lost, total) => lossNotifications.Add(new { truncated, lost, total })
});
using var log = new TraceLog(etlx);
var starts = new List<(int Pid, double Time)>();
var stops = new List<(int Pid, double Time, long Bytes, long Operations)>();
foreach (TraceEvent e in log.Events)
{
    if (e.ProviderName != "SharpLink-Issue739" || ((int)e.ID != 1 && (int)e.ID != 2)) continue;
    if (Convert.ToString(e.PayloadByName("sample"), CultureInfo.InvariantCulture) != sample) continue;
    if ((int)e.ID == 1) starts.Add((e.ProcessID, e.TimeStampRelativeMSec));
    if ((int)e.ID == 2) stops.Add((e.ProcessID, e.TimeStampRelativeMSec,
        Convert.ToInt64(e.PayloadByName("bytes"), CultureInfo.InvariantCulture),
        Convert.ToInt64(e.PayloadByName("operations"), CultureInfo.InvariantCulture)));
}
if (starts.Count != 1 || stops.Count != 1) throw new InvalidDataException($"Need exactly one start and stop for {sample}; found {starts.Count}/{stops.Count}");
var start = starts[0]; var stop = stops[0];
if (start.Pid != stop.Pid || stop.Time <= start.Time || stop.Bytes < 0 || stop.Operations <= 0)
    throw new InvalidDataException("Mismatched PID or invalid marker window/counters");
long inWindow = 0, outsideWindow = 0, otherProcess = 0;
var versions = new SortedSet<int>();
using (var writer = new StreamWriter(prefix + ".ticks.jsonl"))
{
    foreach (TraceEvent e in log.Events)
    {
        if (e is not GCAllocationTickTraceData tick) continue;
        if (e.ProcessID != start.Pid) { otherProcess++; continue; }
        if (e.TimeStampRelativeMSec <= start.Time || e.TimeStampRelativeMSec >= stop.Time) { outsideWindow++; continue; }
        versions.Add(e.Version);
        var frames = new List<string>();
        int unresolvedFrames = 0;
        // Preserve unresolved addresses; no external symbol server/network lookup occurs here.
        for (var stack = e.CallStack(); stack != null; stack = stack.Caller)
        {
            string method = stack.CodeAddress.FullMethodName;
            if (string.IsNullOrWhiteSpace(method)) { unresolvedFrames++; method = $"[unresolved:0x{stack.CodeAddress.Address:x}]"; }
            frames.Add(method);
        }
        // Version <2 lacks a true 64-bit payload. Do not silently invent or repair weights.
        long? weight = e.Version >= 2 ? tick.AllocationAmount64 : null;
        long? objectSize = e.Version >= 4 ? tick.ObjectSize : null;
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, sample, pid = e.ProcessID, threadId = e.ThreadID,
            timeRelativeMilliseconds = e.TimeStampRelativeMSec, eventVersion = e.Version,
            allocationAmount64 = weight, allocationAmount32Raw = unchecked((uint)tick.AllocationAmount),
            objectSize, typeName = e.Version >= 2 ? tick.TypeName : null,
            allocationKind = tick.AllocationKind.ToString(), heapIndex = e.Version >= 2 ? (int?)tick.HeapIndex : null,
            frames, unresolvedFrames
        }));
        inWindow++;
    }
}
string traceHash;
using (var stream = File.OpenRead(input)) traceHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
File.WriteAllText(prefix + ".decode.json", JsonSerializer.Serialize(new
{
    schemaVersion = 1, sample, pid = start.Pid, traceFile = Path.GetFileName(input), traceSha256 = traceHash,
    decoderVersion = typeof(TraceLog).Assembly.GetName().Version?.ToString(),
    decoderRuntime = Environment.Version.ToString(),
    markerProvider = "SharpLink-Issue739", markerStartMilliseconds = start.Time, markerStopMilliseconds = stop.Time,
    markerDurationMilliseconds = stop.Time - start.Time, markerBytes = stop.Bytes, operations = stop.Operations,
    markerBoundary = "Start precedes precise byte baseline; Stop follows precise endpoint. Marker window includes a small unmatched boundary; tick weights can straddle both boundaries.",
    allThreadsIncluded = true, tickEvents = inWindow, excludedSamePidTickEvents = outsideWindow,
    excludedOtherPidTickEvents = otherProcess, allocationTickVersions = versions,
    eventsLostReported = log.EventsLost, lossCounterSource = "TraceLog.EventsLost (EventPipe sequence-number accounting); zero is reported, not proof of allocation completeness",
    lossNotifications, conversionCompleted = true,
    exactObjectsPerOperation = (double?)null,
    attributionStatus = "sampled threshold-crossing objects only; no exact owner attribution"
}, jsonOptions));
