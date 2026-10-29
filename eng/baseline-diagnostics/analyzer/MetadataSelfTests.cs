using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
namespace BaselineDiagnostics;
internal static class MetadataSelfTests
{
    internal static int Run()
    {
        const BindingFlags all=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var metadataType=typeof(TraceEvent).Assembly.GetType("Microsoft.Diagnostics.Tracing.EventPipeMetadata",true)!;
        var ctor=metadataType.GetConstructor(all,null,[typeof(int),typeof(int)],null)!;
        var initialize=metadataType.GetMethod("InitDefaultParameters",all)!;
        var transform=metadataType.GetMethod("ApplyTransforms",all)!;
        var factory=typeof(EventPipeEventSource).GetMethod("CreateTemplate",all)!;
        // Actual pinned decoder metadata transform and template construction only.
        // No EventPipeEventSource constructor, trace, collector, IPC or symbol reader.
        var unusedSource=RuntimeHelpers.GetUninitializedObject(typeof(EventPipeEventSource));
        string[] names=["sourceRevision","variant","mode","itemCount","workSeconds","window","timestamp"];
        object[] values=[Aggregator.Source,"trace","synthetic-marker",100,1,"marker-smoke",100L];
        int count=0;
        void Assert(bool ok){if(!ok)throw new InvalidOperationException("metadata synthetic assertion");}
        void Test(Action action){action();count++;}
        foreach(var input in new[]{("WorkStart",1,(byte)0,"Work/Start","work-start"),
                                   ("WorkStop",2,(byte)0,"Work/Stop","work-stop"),
                                   ("WorkStart",1,(byte)1,"Work/Start","work-start"),
                                   ("WorkStop",2,(byte)2,"Work/Stop","work-stop"),
                                   ("WorkBegin",1,(byte)0,"WorkBegin","malformed")})
            Test(()=> {
                var metadata=ctor.Invoke([8,42]);
                void Set(string key,object value)=>metadataType.GetProperty(key,all)!.SetValue(metadata,value);
                Set("ProviderName",TraceAdapter.PhaseProvider);Set("EventName",input.Item1);Set("EventId",input.Item2);Set("Opcode",input.Item3);
                initialize.Invoke(metadata,null);transform.Invoke(metadata,null);
                var template=(TraceEvent)factory.Invoke(unusedSource,[metadata])!;
                Assert(template.EventName==input.Item4);
                var normalized=TraceAdapter.NormalizePhase(template.ProviderName,(int)template.ID,template.EventName,names,values,100,42,1);
                Assert(normalized.Kind==input.Item5);
                if(input.Item3==0&&input.Item1 is "WorkStart" or "WorkStop")Assert(template.EventName!=input.Item1);
            });
        foreach(var bad in new[]{("other",1,"Work/Start"),(TraceAdapter.PhaseProvider,9,"Work/Start"),
                                (TraceAdapter.PhaseProvider,1,"Work/Stop"),(TraceAdapter.PhaseProvider,2,"Work/Start"),
                                (TraceAdapter.PhaseProvider,1,"WorkStart"),(TraceAdapter.PhaseProvider,2,"WorkStop")})
            Test(()=>Assert(TraceAdapter.NormalizePhase(bad.Item1,bad.Item2,bad.Item3,names,values,100,42,1).Kind=="malformed"));
        for(int index=0;index<values.Length;index++)
        {
            int i=index;
            foreach(object wrong in new object[]{null!,new object(),true})
                Test(()=> {var copy=(object[])values.Clone();copy[i]=wrong;Assert(TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,1,"Work/Start",names,copy,100,42,1).Kind=="malformed");});
        }
        Test(()=> {var copy=(object[])values.Clone();copy[3]=100L;Assert(TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,1,"Work/Start",names,copy,100,42,1).Kind=="malformed");});
        Test(()=> {var copy=(object[])values.Clone();copy[6]=100;Assert(TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,1,"Work/Start",names,copy,100,42,1).Kind=="malformed");});
        Test(()=> {var copy=(string[])names.Clone();(copy[0],copy[1])=(copy[1],copy[0]);Assert(TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,1,"Work/Start",copy,values,100,42,1).Kind=="malformed");});
        Test(()=>Assert(TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,1,"Work/Start",names[..6],values,100,42,1).Kind=="malformed"));
        Test(()=>Assert(TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,1,"Work/Start",names,values[..6],100,42,1).Kind=="malformed"));
        var start=TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,1,"Work/Start",names,values,100,42,1);
        var stopValues=(object[])values.Clone();stopValues[6]=1100L;
        var stop=TraceAdapter.NormalizePhase(TraceAdapter.PhaseProvider,2,"Work/Stop",names,stopValues,1100,42,1);
        var timing=new ExpectedTiming(100,1100,1000);
        var census=new MarkerCensus().Observe(TraceAdapter.PhaseProvider,1,start.Kind).Observe(TraceAdapter.PhaseProvider,2,stop.Kind);
        TraceAdapter.ParsedTrace Good()=>new([start,stop],0,false,census);
        void Bad(TraceAdapter.ParsedTrace trace,ExpectedTiming? expected=null){Assert(MarkerCheck.Analyze(trace,42,expected??timing).Status=="Incomplete");}
        Test(()=> {var r=MarkerCheck.Analyze(Good(),42,timing);Assert(r.Status=="Complete"&&r.ObservedSampleCount==0&&r.CanonicalStartRecognized&&r.CanonicalStopRecognized);});
        Test(()=> {var p=Good();p.Events.Insert(0,new("managed-sample",50,42));p.Events.Add(new("external-sample",1200,42));p.Events.Add(new("managed-sample",1300,99));Assert(MarkerCheck.Analyze(p,42,timing).ObservedSampleCount==2);});
        Test(()=>Bad(Good() with{Events=[stop]}));
        Test(()=>Bad(Good() with{Events=[start,start,stop],Census=census.Observe(TraceAdapter.PhaseProvider,1,start.Kind)}));
        Test(()=>Bad(Good() with{Events=[stop,start]}));
        Test(()=>Bad(Good() with{Events=[start with{Pid=99},stop]}));
        Test(()=>Bad(Good() with{Census=census.Observe(TraceAdapter.PhaseProvider,3,"malformed")}));
        Test(()=>Bad(Good() with{Census=census.Observe(TraceAdapter.PhaseProvider,1,"malformed")}));
        Test(()=>Bad(Good() with{Lost=1}));
        Test(()=>Bad(Good() with{Truncated=true}));
        Test(()=>Bad(Good(),timing with{StartTick=99}));
        Test(()=>Bad(Good(),timing with{Frequency=10000}));
        Test(()=>Bad(Good() with{Events=[start,stop with{Milliseconds=200}]}));
        Test(()=>Bad(Good() with{Events=[start,stop with{Marker=stop.Marker! with{Mode="static-intercepted"}}]}));
        Test(()=> {var p=Good();p.Events.Insert(1,new("unrecognized",200,42));Bad(p);});
        Test(()=> {var c=census.Observe("unrelated",1,"work-start");Assert(c==census);Assert(census.MatchedProviderEvents==2);});
        Test(()=> {var counts=MarkerCheck.EventCounts([start,stop,new("SECRET",500,42)]);Assert(counts["other"]==1&&counts.Count==MarkerCheck.EventKinds.Length&&!counts.ContainsKey("SECRET"));});
        return count;
    }
}
