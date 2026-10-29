namespace BaselineDiagnostics;
internal sealed record MarkerCensus(long MatchedProviderEvents=0,long StartIdCount=0,long StopIdCount=0,
    long MalformedPhaseEvents=0,long OtherPhaseEvents=0,long RecognizedStarts=0,long RecognizedStops=0)
{
    internal MarkerCensus Observe(string provider,int id,string? kind)
    {
        if(provider!=TraceAdapter.PhaseProvider)return this;
        return this with { MatchedProviderEvents=MatchedProviderEvents+1,
            StartIdCount=StartIdCount+(id==1?1:0),StopIdCount=StopIdCount+(id==2?1:0),
            MalformedPhaseEvents=MalformedPhaseEvents+(id is 1 or 2&&kind=="malformed"?1:0),
            OtherPhaseEvents=OtherPhaseEvents+(id is not (1 or 2)?1:0),
            RecognizedStarts=RecognizedStarts+(kind=="work-start"?1:0),RecognizedStops=RecognizedStops+(kind=="work-stop"?1:0) };
    }
    internal static MarkerCensus FromNormalized(IReadOnlyList<Observation> events)
    {
        var count=new MarkerCensus();
        foreach(var e in events.Where(e=>e.Kind is "work-start" or "work-stop"))
            count=count.Observe(TraceAdapter.PhaseProvider,e.Kind=="work-start"?1:2,e.Kind);
        return count;
    }
}
internal sealed record MarkerSummary(int Schema,string Kind,string Status,string[] Failures,MarkerCensus MarkerCensus,
    long ObservedSampleCount,long LostEvents,double WindowMilliseconds,bool CanonicalStartRecognized,bool CanonicalStopRecognized);
internal static class MarkerCheck
{
    internal static readonly string[] EventKinds=["work-start","work-stop","managed-sample","external-sample","sample-error","allocation",
        "gc-start","gc-stop","pause-start","pause-stop","contention-start","contention-stop","wait-start","wait-stop","malformed","other"];
    internal static Dictionary<string,long> EventCounts(IReadOnlyList<Observation> events)
    {
        var counts=EventKinds.ToDictionary(k=>k,_=>0L);
        foreach(var e in events)counts[counts.ContainsKey(e.Kind)?e.Kind:"other"]++;
        return counts;
    }
    internal static MarkerSummary Analyze(TraceAdapter.ParsedTrace parsed,int pid,ExpectedTiming timing)
    {
        var errors=new HashSet<string>();
        if(pid<=0)errors.Add("invalid-pid");
        if(parsed.Lost!=0||parsed.Truncated)errors.Add("lost-or-truncated");
        var starts=parsed.Events.Where(e=>e.Kind=="work-start").ToArray();
        var stops=parsed.Events.Where(e=>e.Kind=="work-stop").ToArray();
        if(starts.Length!=1||stops.Length!=1||parsed.Census.MatchedProviderEvents!=2||parsed.Census.StartIdCount!=1||parsed.Census.StopIdCount!=1)
            errors.Add("marker-cardinality");
        if(parsed.Census.MalformedPhaseEvents!=0||parsed.Census.OtherPhaseEvents!=0||parsed.Events.Any(e=>e.Kind=="malformed"))errors.Add("malformed-event");
        bool Identity(Observation e)=>e.Pid==pid&&e.Marker is {Source:Aggregator.Source,Variant:"trace",Mode:"synthetic-marker",Items:100,Seconds:1,Window:"marker-smoke",Tick:>0};
        if(starts.Concat(stops).Any(e=>!Identity(e)))errors.Add("marker-identity");
        if(!timing.IsValidFor(500,5000)||starts.Length!=1||stops.Length!=1||starts[0].Marker?.Tick!=timing.StartTick||stops[0].Marker?.Tick!=timing.StopTick)errors.Add("marker-measurement");
        double span=starts.Length==1&&stops.Length==1?stops[0].Milliseconds-starts[0].Milliseconds:double.NaN;
        if(!double.IsFinite(span)||span<500||span>5000)errors.Add("window-boundary");
        if(parsed.Events.Any(e=>!double.IsFinite(e.Milliseconds)||e.Milliseconds<0)||
            parsed.Events.Zip(parsed.Events.Skip(1)).Any(p=>p.First.Milliseconds>p.Second.Milliseconds))errors.Add("malformed-or-reversed-event");
        if(parsed.Events.Any(e=>!EventKinds.Contains(e.Kind)))errors.Add("unknown-coverage");
        // This check proves only marker transport/decoding, not stack classification
        // or a product workload. Profiler samples are counted across the whole trace.
        long samples=parsed.Events.LongCount(e=>e.Pid==pid&&e.Kind is "managed-sample" or "external-sample" or "sample-error");
        return new(1,"synthetic-marker-check",errors.Count==0?"Complete":"Incomplete",errors.Order().ToArray(),parsed.Census,samples,
            Math.Max(0,parsed.Lost),double.IsFinite(span)&&span>0?span:0,starts.Length==1,stops.Length==1);
    }
}
