using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace BaselineDiagnostics;
internal static class TraceAdapter
{
    internal const string PhaseProvider="SharpLink-DiagnosticPhase";
    internal static DiagnosticSummary Read(string trace, string etlx, int pid, ExpectedTiming expectedTiming)
    {
        var parsed=ReadEvents(trace,etlx);
        return Aggregator.Analyze(parsed.Events,pid,parsed.Lost,expectedTiming,parsed.Truncated,parsed.Census);
    }
    internal sealed record ParsedTrace(List<Observation> Events,long Lost,bool Truncated,MarkerCensus Census);
    internal static ParsedTrace ReadEvents(string trace,string etlx)
    {
        long lost=0; bool truncated=false;
        // No SymbolReader, symbol server, native PDB lookup or external download.
        var options=new TraceLogOptions { KeepAllEvents=true, ContinueOnError=false, LocalSymbolsOnly=true,
            ShouldResolveSymbols=_=>false, AlwaysResolveSymbols=false, ConversionLog=TextWriter.Null,
            MaxEventCount=5_000_000, OnLostEvents=(cut,count,_)=> { truncated|=cut; lost=Math.Max(lost,count); } };
        TraceLog.CreateFromEventPipeDataFile(trace,etlx,options);
        using var log=new TraceLog(etlx);
        lost=Math.Max(lost,log.EventsLost);
        var normalized=new List<Observation>();
        var census=new MarkerCensus();
        foreach(var e in log.Events)
        {
            var value=Normalize(e);
            if(value is not null)normalized.Add(value);
            census=census.Observe(e.ProviderName,(int)e.ID,value?.Kind);
        }
        return new(normalized,lost,truncated,census);
    }
    internal static Observation? Normalize(TraceEvent e)
    {
        Observation Make(string kind,int instance=0,int number=0,long amount=0,string category="unknown",Marker? marker=null)
            =>new(kind,e.TimeStampRelativeMSec,e.ProcessID,e.ThreadID,instance,number,amount,category,marker);
        try
        {
            if(e.ProviderName==PhaseProvider)
            {
                return NormalizePhase(e.ProviderName,(int)e.ID,e.EventName,e.PayloadNames,
                    e.PayloadNames.Select((_,i)=>e.PayloadValue(i)).ToArray(),e.TimeStampRelativeMSec,e.ProcessID,e.ThreadID);
            }
            if(e is ClrThreadSampleTraceData sample)
            {
                if(sample.Type==ClrThreadSampleType.External)return Make("external-sample");
                if(sample.Type!=ClrThreadSampleType.Managed)return Make("sample-error");
                var frames=new List<string>();
                var stack=e.CallStack(); int depth=0;
                for(;stack is not null && depth<1024;stack=stack.Caller,depth++)frames.Add(stack.CodeAddress.FullMethodName);
                if(stack is not null)return Make("malformed");
                return Make("managed-sample",category:Aggregator.Classify(frames));
            }
            if(e.ProviderName!="Microsoft-Windows-DotNETRuntime")return null;
            return e switch {
                GCAllocationTickTraceData a=>Make("allocation",a.ClrInstanceID,amount:a.AllocationAmount64>0?a.AllocationAmount64:a.AllocationAmount),
                GCStartTraceData g=>Make("gc-start",g.ClrInstanceID,g.Depth,g.Count),
                GCEndTraceData g=>Make("gc-stop",g.ClrInstanceID,amount:g.Count),
                GCSuspendEETraceData g when (int)e.ID==9=>Make("pause-start",g.ClrInstanceID,(int)g.Reason),
                GCNoUserDataTraceData g when (int)e.ID==3=>Make("pause-stop",g.ClrInstanceID),
                ContentionStartTraceData c=>Make("contention-start",c.ClrInstanceID),
                ContentionStopTraceData c=>Make("contention-stop",c.ClrInstanceID),
                WaitHandleWaitStartTraceData w=>Make("wait-start",w.ClrInstanceID),
                WaitHandleWaitStopTraceData w=>Make("wait-stop",w.ClrInstanceID),
                // Metadata/rundown/JIT and unrelated runtime events are deliberately excluded.
                _ when (int)e.ID is 1 or 2 or 3 or 9 or 10 or 81 or 91 or 301 or 302=>Make("malformed"),
                _=>null
            };
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidCastException or IndexOutOfRangeException or FormatException or OverflowException)
        { return Make("malformed"); }
    }
    internal static Observation NormalizePhase(string provider,int id,string name,string[] fields,object[] values,double ms,int pid,int thread)
    {
        Observation Fail()=>new("malformed",ms,pid,thread);
        string[] expected=["sourceRevision","variant","mode","itemCount","workSeconds","window","timestamp"];
        if(provider!=PhaseProvider || !fields.SequenceEqual(expected) || values.Length!=7 ||
            values[0] is not string source || values[1] is not string variant || values[2] is not string mode ||
            values[3] is not int items || values[4] is not int seconds || values[5] is not string window || values[6] is not long tick)return Fail();
        // TraceEvent 3.1.23 infers Start/Stop even for explicit Info(0), strips the
        // suffix, then emits these canonical task/opcode names. Do not accept aliases.
        string kind=(id,name) switch {(1,"Work/Start")=>"work-start",(2,"Work/Stop")=>"work-stop",_=>"malformed"};
        return new(kind,ms,pid,thread,Marker:new(source,variant,mode,items,seconds,window,tick));
    }

}
