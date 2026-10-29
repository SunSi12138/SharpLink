namespace BaselineDiagnostics;

// Normalized input is shared by the real TraceEvent adapter and synthetic controls.
internal sealed record ExpectedTiming(long StartTick, long StopTick, long Frequency)
{
    internal bool IsValid => StartTick>0 && StopTick>StartTick && Frequency>0 &&
        1000.0*(StopTick-StartTick)/Frequency is >=20000 and <=25000;
}
internal sealed record Marker(string Source, string Variant, string Mode, int Items, int Seconds, string Window, long Tick);
internal sealed record Observation(string Kind, double Milliseconds, int Pid, int Thread = 1, int Instance = 0,
    int Number = 0, long Amount = 0, string Category = "unknown", Marker? Marker = null);
internal sealed record Interval(int Count, double DurationMilliseconds);
internal sealed record DiagnosticSummary(int Schema, string Status, string Coverage, string[] Failures,
    string SourceRevision, string Scenario, double WindowMilliseconds, long LostEvents, long UnknownStacks,
    long UnmatchedEvents, long OutsideWindowEvents, long OtherProcessEvents, long UnknownEvents,
    Dictionary<string,long> ManagedThreadTimeSamples, long ExternalThreadSamples, long SampleErrors,
    long AllocationTickSamples, long AllocationTickReportedBytes, Dictionary<string,long> GcStartsByGeneration,
    Interval GcPauses, Interval Contention, Interval WaitHandle, Dictionary<string,long> CensoredIntervals,
    string Interpretation = "Sampled managed thread-time counts, not CPU percentages. Allocation ticks are samples, not exact allocated bytes. GC pause, contention and wait durations can overlap; never add them or infer CPU time.");
internal static class Aggregator
{
    internal const string Source = "3d76a3a8b55a37e24c62a073fe588cff3d3867be";
    internal static readonly string[] Categories = ["sharplink-stream", "sharplink-dispatch", "sharplink-transport", "sharplink-other", "serialization", "task-scheduling", "runtime-wait", "runtime-other", "other-managed", "unknown"];
    internal static DiagnosticSummary Analyze(IReadOnlyList<Observation> events, int pid, long lost, ExpectedTiming expectedTiming, bool truncated = false)
    {
        var failures = new HashSet<string>();
        if (pid <= 0) failures.Add("invalid-pid");
        if (lost < 0 || lost > 0 || truncated) failures.Add("lost-or-truncated");
        var starts = events.Where(e => e.Kind == "work-start").ToArray();
        var stops = events.Where(e => e.Kind == "work-stop").ToArray();
        if (starts.Length != 1 || stops.Length != 1) failures.Add("marker-cardinality");
        bool identity(Observation e) => e.Pid == pid && e.Marker is {Source: Source, Variant: "trace", Mode: "static-intercepted", Items: 100, Seconds: 20, Window: "work20s", Tick: > 0};
        if (starts.Concat(stops).Any(e => !identity(e))) failures.Add("marker-identity");
        if(!expectedTiming.IsValid || starts.Length!=1 || stops.Length!=1 ||
            starts[0].Marker?.Tick!=expectedTiming.StartTick || stops[0].Marker?.Tick!=expectedTiming.StopTick)
            failures.Add("marker-measurement");
        var begin = starts.Length == 1 ? starts[0].Milliseconds : double.NaN;
        var end = stops.Length == 1 ? stops[0].Milliseconds : double.NaN;
        var span = end - begin;
        if (!double.IsFinite(span) || span < 20000 || span > 25000 ||
            (starts.Length == 1 && stops.Length == 1 && starts[0].Marker?.Tick >= stops[0].Marker?.Tick)) failures.Add("window-boundary");
        var categories = Categories.ToDictionary(k => k, _ => 0L);
        var generations = new[] {"gen0","gen1","gen2"}.ToDictionary(k => k,_ => 0L);
        long outside=0, otherPid=0, unknown=0, unmatched=0, external=0, errors=0, allocations=0, bytes=0;
        var active = new Dictionary<(string Kind,int Thread,int Instance,int Number),Observation>();
        var intervals = new Dictionary<string,Interval> { ["pause"] = new(0,0), ["contention"] = new(0,0), ["wait"] = new(0,0) };
        var censored = new[] {"gc","pause","contention","wait"}.ToDictionary(k=>k,_=>0L);
        double previous=double.NegativeInfinity;
        foreach (var e in events)
        {
            if (!double.IsFinite(e.Milliseconds) || e.Milliseconds < 0 || e.Milliseconds < previous)
            { failures.Add("malformed-or-reversed-event"); continue; }
            previous=e.Milliseconds;
            if (e.Kind is "work-start" or "work-stop") continue;
            if (e.Kind == "malformed") { failures.Add("malformed-event"); continue; }
            if (e.Pid != pid) { otherPid++; continue; }
            bool inside=double.IsFinite(begin)&&double.IsFinite(end)&&e.Milliseconds>=begin&&e.Milliseconds<=end;
            if(!inside)outside++;
            if (e.Thread <= 0 || e.Instance < 0) { failures.Add("malformed-event"); continue; }
            bool paired=e.Kind is "gc-start" or "gc-stop" or "pause-start" or "pause-stop" or "contention-start" or "contention-stop" or "wait-start" or "wait-stop";
            if(paired)
            {
                string kind=e.Kind.Split('-')[0];
                if(kind=="gc" && (e.Amount<0||e.Amount>int.MaxValue)) {failures.Add("malformed-event");continue;}
                var key=(kind,kind is "gc" or "pause"?0:e.Thread,e.Instance,kind=="gc"?(int)e.Amount:0);
                if(e.Kind.EndsWith("start",StringComparison.Ordinal))
                {
                    if(!active.TryAdd(key,e)){unmatched++;failures.Add("unmatched-events");}
                    if(kind=="gc" && inside)
                    { if(e.Number is <0 or >2)failures.Add("malformed-generation");else generations["gen"+e.Number]++; }
                }
                else if(active.Remove(key,out var start))
                {
                    bool whollyInside=start.Milliseconds>=begin&&e.Milliseconds<=end;
                    bool overlaps=start.Milliseconds<=end&&e.Milliseconds>=begin;
                    if(!whollyInside&&overlaps)censored[kind]++;
                    if(whollyInside&&kind!="gc"&&(kind!="pause"||start.Number is 1 or 6))
                    {var old=intervals[kind];intervals[kind]=new(old.Count+1,old.DurationMilliseconds+e.Milliseconds-start.Milliseconds);}
                }
                else {unmatched++;failures.Add("unmatched-events");}
                continue;
            }
            if(!inside)continue;
            switch (e.Kind)
            {
                case "managed-sample":
                    if (!categories.ContainsKey(e.Category)) { failures.Add("malformed-category"); categories["unknown"]++; }
                    else categories[e.Category]++;
                    break;
                case "external-sample": external++; break;
                case "sample-error": errors++; failures.Add("sample-error"); break;
                case "allocation":
                    if(e.Amount <= 0) failures.Add("malformed-allocation");
                    else { allocations++; bytes=checked(bytes+e.Amount); }
                    break;
                default: unknown++; break;
            }
        }
        unmatched+=active.Count;
        if(unmatched>0)failures.Add("unmatched-events");
        if(categories.Values.Sum()+external+errors==0)failures.Add("missing-samples");
        if(categories["unknown"]>0 || unknown>0)failures.Add("unknown-coverage");
        return new(1,failures.Count==0?"Complete":"Incomplete",categories["unknown"]+unknown>0?"Partial":"Classified",
            failures.Order().ToArray(),Source,"baseline-static-n100-work20s",double.IsFinite(span)&&span>0?span:0,
            Math.Max(0,lost),categories["unknown"],unmatched,outside,otherPid,unknown,categories,external,errors,allocations,bytes,
            generations,intervals["pause"],intervals["contention"],intervals["wait"],censored);
    }
    internal static string Classify(IEnumerable<string> frames)
    {
        // First matching leafward managed frame, exactly one category per sample.
        foreach(var frame in frames)
        {
            var f=frame.Trim();
            int bang=f.IndexOf('!');if(bang>=0)f=f[(bang+1)..];
            if(f.Length==0||f.Contains("UNKNOWN",StringComparison.OrdinalIgnoreCase)||f.Contains("UNRESOLVED",StringComparison.OrdinalIgnoreCase)||f.Contains('?')||
               System.Text.RegularExpressions.Regex.IsMatch(f,@"^(0x)?[0-9a-fA-F]+$"))continue;
            if(f.Contains("SharpLink") && (f.Contains("Stream")||f.Contains("OutboundGeneratedCall")))return "sharplink-stream";
            if(f.Contains("SharpLink") && (f.Contains("Dispatch")||f.Contains("Invoke")))return "sharplink-dispatch";
            if(f.Contains("SharpLink") && (f.Contains("Tcp")||f.Contains("Socket")||f.Contains("Transport")))return "sharplink-transport";
            if(f.Contains("SharpLink"))return "sharplink-other";
            if(f.Contains("SharpPack")||f.Contains("MemoryPack")||f.Contains("MessagePack")||f.Contains("Serializer"))return "serialization";
            if(f.StartsWith("System.Threading.Tasks.",StringComparison.Ordinal)||f.Contains("ThreadPool"))return "task-scheduling";
            if(f.Contains("WaitHandle")||f.Contains("Monitor.")||f.Contains("SemaphoreSlim"))return "runtime-wait";
            if(f.StartsWith("System.",StringComparison.Ordinal)||f.StartsWith("Microsoft.",StringComparison.Ordinal))return "runtime-other";
            return "other-managed";
        }
        return "unknown";
    }
}
