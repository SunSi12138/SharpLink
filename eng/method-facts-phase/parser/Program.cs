using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length != 3) { Console.Error.WriteLine("Usage: dotnet TraceParser.dll INPUT.nettrace OUTPUT_DIR PHASE_SIDECAR.jsonl"); return 2; }
Directory.CreateDirectory(args[1]);
var diagnostic = new Diagnostic(args[2]);
try
{
    using var source = new EventPipeEventSource(args[0]);
    source.AllEvents += diagnostic.Cover;
    source.Clr.All += diagnostic.Runtime;
    new ClrRundownTraceEventParser(source).All += diagnostic.Runtime;
    source.Dynamic.All += e => { if (e.ProviderName == Diagnostic.PhaseProvider) diagnostic.PhaseEvent(e); };
    source.Process();
    diagnostic.Finish(args[1], source.EventsLost, null);
    return diagnostic.PhasesComplete ? 0 : 3;
}
catch (Exception ex)
{
    // Preserve partial evidence, but never treat a truncated/unsupported trace as a success.
    diagnostic.Finish(args[1], null, ex.GetType().Name + ": " + ex.Message);
    Console.Error.WriteLine("Trace parse incomplete; inspect summary.json. " + ex.GetType().Name);
    return 1;
}

sealed class Diagnostic(string sidecar)
{
    public const string PhaseProvider = "SharpLink-MethodFacts-Phases";
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly string[] Categories = ["facts-proxy", "facts-stub", "server-dispatch", "server-pump", "runtime-pump", "to-descriptor"];
    readonly List<Marker> phases = [];
    readonly List<MethodRow> methods = [];
    readonly List<RuntimeRow> runtime = [];
    readonly List<SpanRow> spans = [];
    readonly Dictionary<string, SpanRow> collections = [];
    readonly Dictionary<string, SpanRow> suspensions = [];
    readonly Dictionary<string, Coverage> providers = [];
    readonly List<string> warnings = [];
    public bool PhasesComplete { get; private set; }
    long eventCount;
    double? first, last;

    public void Cover(TraceEvent e)
    {
        eventCount++;
        first = Math.Min(first ?? e.TimeStampRelativeMSec, e.TimeStampRelativeMSec);
        last = Math.Max(last ?? e.TimeStampRelativeMSec, e.TimeStampRelativeMSec);
        string key = e.ProcessID + ":" + e.ProviderName;
        if (!providers.TryGetValue(key, out var c)) providers[key] = c = new Coverage(e.ProcessID, e.ProviderName);
        c.Count++; c.FirstMs = Math.Min(c.FirstMs ?? e.TimeStampRelativeMSec, e.TimeStampRelativeMSec);
        c.LastMs = Math.Max(c.LastMs ?? e.TimeStampRelativeMSec, e.TimeStampRelativeMSec);
    }
    public void PhaseEvent(TraceEvent e)
    {
        if (!e.EventName.StartsWith("Phase", StringComparison.Ordinal)) return;
        var p = new Marker(e.ProcessID, e.TimeStampRelativeMSec, Number(e, "sequence"), Str(e, "cell"), Str(e, "boundary"), Number(e, "chunk"), Number(e, "completed"), Number(e, "timestamp"), Number(e, "frequency"));
        phases.Add(p);
        if (p.Sequence is null || p.Cell is null || p.Boundary is null || p.Chunk is null || p.Completed is null || p.Timestamp is null || p.Frequency is null or <= 0)
            warnings.Add("Malformed phase payload at " + e.TimeStampRelativeMSec.ToString(CultureInfo.InvariantCulture));
    }
    public void Runtime(TraceEvent e)
    {
        bool rundown = e.ProviderName.Contains("Rundown", StringComparison.Ordinal);
        string name = e.EventName;
        bool jit = e is MethodJittingStartedTraceData;
        bool load = e is MethodLoadUnloadTraceDataBase && !name.Contains("Unload", StringComparison.Ordinal);
        bool threadPool = name.Contains("ThreadPool", StringComparison.OrdinalIgnoreCase);
        bool tiering = name.Contains("TieredCompilation", StringComparison.Ordinal);
        bool gc = name.StartsWith("GC/", StringComparison.Ordinal);
        if (jit || load || threadPool || tiering || gc)
            runtime.Add(new(e.ProcessID, e.TimeStampRelativeMSec, name, rundown, jit, load, threadPool, tiering));
        if (jit || load)
        {
            var row = new MethodRow
            {
                Pid = e.ProcessID, TimeMs = e.TimeStampRelativeMSec, Event = name, EventVersion = e.Version,
                Observation = rundown ? "rundown-observation-not-compilation-time" : jit ? "jit-start" : "live-method-load",
                MethodId = Str(e, "MethodID"), ModuleId = Str(e, "ModuleID"), ClrInstanceId = e.Version >= 1 ? Str(e, "ClrInstanceID") : null,
                Namespace = Str(e, "MethodNamespace"), Name = Str(e, "MethodName"), Signature = Str(e, "MethodSignature"),
                Address = Str(e, "MethodStartAddress"), Size = Number(e, "MethodSize"),
                RejitId = e is MethodLoadUnloadVerboseTraceData && e.Version >= 2 ? Str(e, "ReJITID") : null,
                Tier = e is MethodLoadUnloadTraceDataBase m ? m.OptimizationTier.ToString() : null
            };
            if (row.Tier == "Unknown") row.Tier = null;
            methods.Add(row);
        }
        if (rundown) return;
        string clr = Str(e, "ClrInstanceID") ?? "unknown";
        string key = e.ProcessID + ":" + clr;
        if (name == "GC/Start")
        {
            string id = Str(e, "Count") ?? "unknown";
            string gcKey = key + ":" + id;
            var s = new SpanRow(e.ProcessID, clr, "gc-collection-not-pause", id, e.TimeStampRelativeMSec, Str(e, "Reason"), Str(e, "Type"), Number(e, "Depth"));
            if (collections.ContainsKey(gcKey)) warnings.Add("Repeated GC start without end: " + gcKey);
            spans.Add(s); collections[gcKey] = s;
        }
        else if (name == "GC/Stop")
        {
            string id = Str(e, "Count") ?? "unknown";
            if (collections.Remove(key + ":" + id, out var s)) s.EndMs = e.TimeStampRelativeMSec;
            else spans.Add(new(e.ProcessID, clr, "gc-collection-not-pause", id, null, null, null, null) { EndMs = e.TimeStampRelativeMSec });
        }
        else if (name == "GC/SuspendEEStart")
        {
            var s = new SpanRow(e.ProcessID, clr, "runtime-suspension", Str(e, "Count"), e.TimeStampRelativeMSec, Str(e, "Reason"), null, null);
            if (suspensions.ContainsKey(key)) warnings.Add("Repeated runtime suspend without restart: " + key);
            spans.Add(s); suspensions[key] = s;
        }
        else if (name is "GC/SuspendEEStop" or "GC/RestartEEStart" or "GC/RestartEEStop")
        {
            if (!suspensions.TryGetValue(key, out var s))
            {
                s = new(e.ProcessID, clr, "runtime-suspension", null, null, null, null, null);
                spans.Add(s); suspensions[key] = s;
            }
            if (name == "GC/SuspendEEStop") s.FullySuspendedMs = e.TimeStampRelativeMSec;
            if (name == "GC/RestartEEStart") s.RestartStartMs = e.TimeStampRelativeMSec;
            if (name == "GC/RestartEEStop") { s.EndMs = e.TimeStampRelativeMSec; suspensions.Remove(key); }
        }
    }
    public void Finish(string directory, int? lost, string? error)
    {
        phases.Sort((a,b) => a.TimeMs.CompareTo(b.TimeMs));
        var named = methods.Where(m => m.Name != null).GroupBy(m => m.Identity).ToDictionary(g => g.Key, g => g.First());
        foreach (var m in methods)
        {
            if (m.Name == null && named.TryGetValue(m.Identity, out var n))
            { m.Namespace = n.Namespace; m.Name = n.Name; m.Signature = n.Signature; m.NameSource = "same-method-identity-other-event"; }
            m.Category = Classify(m.FullName);
        }
        var targets = methods.Where(m => m.Category != null).ToList();
        if (lost is null) warnings.Add("Lost event count unavailable because parsing did not complete.");
        else if (lost > 0) warnings.Add("Trace reports lost events: " + lost);
        if (error != null) warnings.Add("Parsing failed: " + error);
        if (phases.Count == 0) warnings.Add("No phase markers observed; phase coverage unavailable.");
        var expected = new List<(string Cell,string Boundary,int Chunk,int Completed)> { ("process","process-start",-1,0) };
        foreach (var cell in new[]{"I/T","P/T","I/U","P/U"})
        {
            expected.Add((cell,"cell-start",-1,0)); expected.Add((cell,"warmup-end",-1,1000));
            for(int chunk=0;chunk<5;chunk++) { expected.Add((cell,"chunk-start",chunk,1000+chunk*2000)); expected.Add((cell,"chunk-end",chunk,1000+(chunk+1)*2000)); }
            expected.Add((cell,"cell-end",-1,11000));
        }
        expected.Add(("process","process-end",-1,44000));
        PhasesComplete = phases.Count==54 && phases.Select(p=>p.Pid).Distinct().Count()==1;
        if (PhasesComplete)
            for(int i=0;i<phases.Count;i++)
            {
                var p=phases[i]; var x=expected[i];
                if (p.Sequence!=i || p.Cell!=x.Cell || p.Boundary!=x.Boundary || p.Chunk!=x.Chunk || p.Completed!=x.Completed || p.Timestamp is null || p.Frequency is null or <=0 ||
                    (i>0 && (p.Timestamp<phases[i-1].Timestamp || p.Frequency!=phases[i-1].Frequency))) PhasesComplete=false;
            }
        try
        {
            var sidecarMarkers = File.ReadLines(sidecar).Where(line=>!string.IsNullOrWhiteSpace(line)).Select(line=>JsonSerializer.Deserialize<Marker>(line,new JsonSerializerOptions { PropertyNameCaseInsensitive=true })!).ToArray();
            if (sidecarMarkers.Length!=phases.Count) PhasesComplete=false;
            else for(int i=0;i<phases.Count;i++)
            {
                var p=phases[i]; var s=sidecarMarkers[i];
                if(p.Sequence!=s.Sequence || p.Cell!=s.Cell || p.Boundary!=s.Boundary || p.Chunk!=s.Chunk || p.Completed!=s.Completed || p.Timestamp!=s.Timestamp || p.Frequency!=s.Frequency) PhasesComplete=false;
            }
        }
        catch(Exception ex) { PhasesComplete=false; warnings.Add("Sidecar validation failed: "+ex.GetType().Name); }
        if (!PhasesComplete) warnings.Add("Phase coverage incomplete: expected one process, 54 ordered markers, four fixed cells, five complete chunks per cell and 44000 final completed calls, exactly matching the sidecar payloads.");
        if (spans.Any(s=>!s.Complete)) warnings.Add("One or more GC collection/runtime-suspension spans have missing or out-of-order endpoints; overlapping phase totals remain unavailable.");
        var windows = new List<Window>();
        foreach (var process in phases.GroupBy(p => p.Pid))
        {
            var pp = process.ToArray();
            for (int i = 0; i < pp.Length - 1; i++)
            {
                var a = pp[i]; var b = pp[i+1];
                var rr = runtime.Where(r => r.Pid == a.Pid && r.TimeMs >= a.TimeMs && r.TimeMs < b.TimeMs && !r.Rundown).ToArray();
                var mm = targets.Where(m => m.Pid == a.Pid && m.TimeMs >= a.TimeMs && m.TimeMs < b.TimeMs && m.Observation != "rundown-observation-not-compilation-time").ToArray();
                windows.Add(new(a.Pid, a.Sequence, a.Cell, a.Chunk, a.Boundary, b.Boundary, a.TimeMs, b.TimeMs,
                    rr.Count(r=>r.Jit), rr.Count(r=>r.Load), rr.Count(r=>r.ThreadPool), rr.Count(r=>r.Tiering), rr.Count(r=>r.Event=="GC/Start"), rr.Count(r=>r.Event=="GC/SuspendEEStart"),
                    Overlap(a.Pid, a.TimeMs,b.TimeMs,"gc-collection-not-pause"), Overlap(a.Pid,a.TimeMs,b.TimeMs,"runtime-suspension"),
                    mm.GroupBy(m=>m.Category!).ToDictionary(g=>g.Key,g=>new TargetCounts(g.Count(m=>m.Observation=="jit-start"),g.Count(m=>m.Observation=="live-method-load"),g.Where(m=>m.Observation=="live-method-load").Select(m=>m.CodeIdentity).Distinct().Count(),g.Where(m=>m.Tier!=null).GroupBy(m=>m.Tier!).ToDictionary(t=>t.Key,t=>t.Count()), g.Count(m=>m.Observation=="live-method-load" && m.Tier==null)))));
            }
        }
        var coverage = Categories.ToDictionary(c => c, c =>
        {
            var ms = targets.Where(m=>m.Category==c).ToArray();
            var live = ms.Where(m=>m.Observation=="live-method-load").ToArray();
            return new { status = ms.Length==0 ? "not-observed-not-proof-of-absence" : "observed-heuristic-name-match", eventCount = ms.Length, namedMethods = ms.Select(m=>m.FullName).Distinct().Count(), liveLoads = live.Length, jitStarts = ms.Count(m=>m.Observation=="jit-start"), rundownObservations = ms.Count(m=>m.Observation=="rundown-observation-not-compilation-time"), tierStatus = live.Length==0 ? "unavailable-no-live-load-observations" : live.Any(m=>m.Tier==null) ? "partially-or-fully-unavailable" : "observed", rejitIdStatus = live.Length==0 ? "unavailable-no-live-load-observations" : live.Any(m=>m.RejitId==null) ? "partially-or-fully-unavailable" : "observed", loadsMissingTier = live.Count(m=>m.Tier==null), loadsMissingRejitId = live.Count(m=>m.RejitId==null), tiers = live.Where(m=>m.Tier!=null).GroupBy(m=>m.Tier!).ToDictionary(g=>g.Key,g=>g.Count()), methodNamePreviews = ms.Select(m=>m.FullName).Distinct().Order().Take(6).Select(n=>n.Length<=240?n:n[..240]+"...").ToArray(), methodListTruncated = ms.Select(m=>m.FullName).Distinct().Count()>6 || ms.Any(m=>m.FullName.Length>240) };
        });
        foreach (var c in coverage.Where(kv=>kv.Value.eventCount==0)) warnings.Add("Target method category not observed: " + c.Key);
        var summary = new
        {
            schemaVersion = 1, tool = "TraceEvent 4.0.0", purpose = "phase-runtime-diagnostic-only-no-performance-acceptance",
            schedulingLimit = "EventPipe runtime events do not establish off-CPU time or CPU scheduling causality.", sidecarMatched = PhasesComplete,
            parseStatus = error == null ? "completed" : "incomplete", coverageStatus = warnings.Count==0 ? "observed-expected-markers-no-reported-loss" : "incomplete-or-limited",
            eventCount, firstEventMs = first, lastEventMs = last, lostEvents = lost, phaseCount = phases.Count,
            phaseClock = "TraceEvent.TimeStampRelativeMSec; Stopwatch payload retained, never cross-process synchronized",
            zeroMeaning = "Zero means no matching event observed in this capture, not proof no activity occurred.",
            codeVersionMeaning = "Distinct observed (pid, CLR instance, module, method, code address, ReJITID); ReJITID is not a tier/version ordinal. Missing tier/ID remains null. Rundown timestamp is observation only.",
            providers = providers.Values.OrderBy(p=>p.Pid).ThenBy(p=>p.Provider).ToArray(), targetCoverage = coverage,
            unresolvedMethodEvents = methods.Count(m=>m.Name==null),
            incompleteGcCollections = spans.Count(s=>s.Kind=="gc-collection-not-pause" && !s.Complete), incompleteSuspensions = spans.Count(s=>s.Kind=="runtime-suspension" && !s.Complete),
            runtimeEventCounts = runtime.GroupBy(r=>(r.Rundown ? "rundown:" : "live:")+r.Event).OrderBy(g=>g.Key).ToDictionary(g=>g.Key,g=>g.Count()),
            phaseTargetColumns = new[]{"jitStarts","liveLoads","observedCodeIdentities","tiers","liveLoadsMissingTier"},
            phaseColumns = new[]{"pid","sequence","cell","chunk","from","to","startMs","endMs","jitStarts","liveMethodLoads","threadPoolEvents","tieringEvents","gcStarts","suspendStarts","gcCollectionOverlapMsNotPause","runtimeSuspensionOverlapMs","targets"},
            phases = windows.Select(w=>new object?[]{w.Pid,w.Sequence,w.Cell,w.Chunk,w.From,w.To,w.StartMs,w.EndMs,w.JitStarts,w.LiveMethodLoads,w.ThreadPoolEvents,w.TieringEvents,w.GcStarts,w.SuspendStarts,w.GcCollectionOverlapMs,w.RuntimeSuspensionOverlapMs,w.Targets.ToDictionary(kv=>kv.Key,kv=>new object[]{kv.Value.JitStarts,kv.Value.LiveLoads,kv.Value.ObservedCodeIdentities,kv.Value.Tiers,kv.Value.LiveLoadsMissingTier})}).ToArray(),
            warnings = warnings.Distinct().ToArray()
        };
        File.WriteAllText(Path.Combine(directory,"summary.json"), JsonSerializer.Serialize(summary,Json)+"\n");
        File.WriteAllLines(Path.Combine(directory,"phases.jsonl"), phases.Select(p=>JsonSerializer.Serialize(p,Json)));
        WriteCsv(Path.Combine(directory,"method-code-versions.csv"),
            ["pid","time_ms","event","event_version","observation","category","clr_instance_id","module_id","method_id","method","signature","name_source","code_address","code_size","rejit_id","optimization_tier","observed_code_identity"],
            targets.Select(m=>new object?[]{m.Pid,m.TimeMs,m.Event,m.EventVersion,m.Observation,m.Category,m.ClrInstanceId,m.ModuleId,m.MethodId,m.FullName,m.Signature,m.NameSource,m.Address,m.Size,m.RejitId,m.Tier,m.CodeIdentity}));
        WriteCsv(Path.Combine(directory,"gc-pauses.csv"),["pid","clr_instance_id","kind","gc_count","start_ms","fully_suspended_ms","restart_start_ms","end_ms","observed_span_ms","reason","gc_type","generation","complete"],
            spans.Select(s=>new object?[]{s.Pid,s.ClrInstanceId,s.Kind,s.Count,s.StartMs,s.FullySuspendedMs,s.RestartStartMs,s.EndMs,s.Complete?s.EndMs-s.StartMs:null,s.Reason,s.GcType,s.Generation,s.Complete}));
        WriteCsv(Path.Combine(directory,"phase-runtime-summary.csv"),["pid","sequence","cell","chunk","from_boundary","to_boundary","start_ms","end_ms","jit_starts","live_method_loads","threadpool_events","tiering_events","gc_starts","suspend_starts","gc_collection_overlap_ms_not_pause","runtime_suspension_overlap_ms","targets_json"],
            windows.Select(w=>new object?[]{w.Pid,w.Sequence,w.Cell,w.Chunk,w.From,w.To,w.StartMs,w.EndMs,w.JitStarts,w.LiveMethodLoads,w.ThreadPoolEvents,w.TieringEvents,w.GcStarts,w.SuspendStarts,w.GcCollectionOverlapMs,w.RuntimeSuspensionOverlapMs,JsonSerializer.Serialize(w.Targets,Json)}));
    }
    double? Overlap(int pid,double start,double end,string kind)
    {
        var ss = spans.Where(s=>s.Pid==pid&&s.Kind==kind).ToArray();
        if (ss.Any(s=>!s.Complete && (s.StartMs==null || s.StartMs<end) && (s.EndMs==null || s.EndMs>start))) return null;
        return ss.Where(s=>s.Complete).Sum(s=>Math.Max(0,Math.Min(end,s.EndMs!.Value)-Math.Max(start,s.StartMs!.Value)));
    }
    static string? Classify(string name)
    {
        if (name.Contains("RpcMethodFacts",StringComparison.Ordinal) && name.Contains("ToDescriptor",StringComparison.Ordinal)) return "to-descriptor";
        if ((name.Contains("MethodFactsExperiment",StringComparison.Ordinal)||name.Contains("IFactsRpc",StringComparison.Ordinal))&&name.Contains("Proxy",StringComparison.Ordinal)) return "facts-proxy";
        if ((name.Contains("MethodFactsExperiment",StringComparison.Ordinal)||name.Contains("IFactsRpc",StringComparison.Ordinal))&&name.Contains("Stub",StringComparison.Ordinal)) return "facts-stub";
        if (name.Contains("PumpOutboundStreamAsync",StringComparison.Ordinal)) return name.Contains("ServerGeneratedBridge",StringComparison.Ordinal) ? "server-pump" : "runtime-pump";
        if (name.Contains("SharpLink",StringComparison.Ordinal)&&(name.Contains("ResolveMethodCall",StringComparison.Ordinal)||name.Contains("DispatchRpcAsync",StringComparison.Ordinal)||name.Contains("InvokeServiceAsync",StringComparison.Ordinal))) return "server-dispatch";
        return null;
    }
    static object? Value(TraceEvent e,string name)
    {
        for(int i=0;i<e.PayloadNames.Length;i++) if(string.Equals(e.PayloadNames[i],name,StringComparison.OrdinalIgnoreCase)) return e.PayloadValue(i);
        return null;
    }
    static string? Str(TraceEvent e,string name) => Convert.ToString(Value(e,name),CultureInfo.InvariantCulture) is {Length:>0} v ? v : null;
    static long? Number(TraceEvent e,string name) => long.TryParse(Str(e,name),NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)?n:null;
    static void WriteCsv(string path,string[] header,IEnumerable<object?[]> rows)
    {
        static string Escape(object? v) => "\""+(Convert.ToString(v,CultureInfo.InvariantCulture)??"").Replace("\"","\"\"")+"\"";
        using var f = new StreamWriter(path); f.WriteLine(string.Join(',',header.Select(Escape)));
        foreach(var row in rows) f.WriteLine(string.Join(',',row.Select(Escape)));
    }
}
sealed record Marker(int Pid,double TimeMs,long? Sequence,string? Cell,string? Boundary,long? Chunk,long? Completed,long? Timestamp,long? Frequency);
sealed record RuntimeRow(int Pid,double TimeMs,string Event,bool Rundown,bool Jit,bool Load,bool ThreadPool,bool Tiering);
sealed record TargetCounts(int JitStarts,int LiveLoads,int ObservedCodeIdentities,Dictionary<string,int> Tiers,int LiveLoadsMissingTier);
sealed record Window(int Pid,long? Sequence,string? Cell,long? Chunk,string? From,string? To,double StartMs,double EndMs,int JitStarts,int LiveMethodLoads,int ThreadPoolEvents,int TieringEvents,int GcStarts,int SuspendStarts,double? GcCollectionOverlapMs,double? RuntimeSuspensionOverlapMs,Dictionary<string,TargetCounts> Targets);
sealed class Coverage(int pid,string provider)
{
    public int Pid {get;}=pid; public string Provider {get;}=provider;
    public long Count {get;set;} public double? FirstMs {get;set;} public double? LastMs {get;set;}
}
sealed class SpanRow(int pid,string clr,string kind,string? count,double? start,string? reason,string? gcType,long? generation)
{
    public int Pid {get;}=pid; public string ClrInstanceId {get;}=clr; public string Kind {get;}=kind; public string? Count {get;}=count;
    public double? StartMs {get;}=start; public double? FullySuspendedMs {get;set;} public double? RestartStartMs {get;set;} public double? EndMs {get;set;}
    public string? Reason {get;}=reason; public string? GcType {get;}=gcType; public long? Generation {get;}=generation;
    public bool Complete=>StartMs.HasValue&&EndMs>=StartMs && (Kind!="runtime-suspension" || (FullySuspendedMs>=StartMs && RestartStartMs>=FullySuspendedMs && EndMs>=RestartStartMs));
}
sealed class MethodRow
{
    public int Pid; public double TimeMs; public int EventVersion; public string? Event,Observation,Category,ClrInstanceId,ModuleId,MethodId,Namespace,Name,Signature,Address,RejitId,Tier;
    public long? Size; public string NameSource="event-payload";
    public string Identity=>$"{Pid}:{ClrInstanceId??"missing"}:{ModuleId??"missing"}:{MethodId??"missing"}";
    public string FullName=>(Namespace??"")+"."+(Name??"");
    public string CodeIdentity=>$"{Identity}:{Address??"missing"}:{RejitId??"missing"}";
}
