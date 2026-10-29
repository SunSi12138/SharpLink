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
        long lost=0; bool truncated=false;
        // No SymbolReader, symbol server, native PDB lookup or external download.
        var options=new TraceLogOptions { KeepAllEvents=true, ContinueOnError=false, LocalSymbolsOnly=true,
            ShouldResolveSymbols=_=>false, AlwaysResolveSymbols=false, ConversionLog=TextWriter.Null,
            MaxEventCount=5_000_000, OnLostEvents=(cut,count,_)=> { truncated|=cut; lost=Math.Max(lost,count); } };
        TraceLog.CreateFromEventPipeDataFile(trace,etlx,options);
        using var log=new TraceLog(etlx);
        lost=Math.Max(lost,log.EventsLost);
        var normalized=new List<Observation>();
        foreach(var e in log.Events)
        {
            var value=Normalize(e);
            if(value is not null)normalized.Add(value);
        }
        return Aggregator.Analyze(normalized,pid,lost,expectedTiming,truncated);
    }
    internal static Observation? Normalize(TraceEvent e)
    {
        Observation Make(string kind,int instance=0,int number=0,long amount=0,string category="unknown",Marker? marker=null)
            =>new(kind,e.TimeStampRelativeMSec,e.ProcessID,e.ThreadID,instance,number,amount,category,marker);
        try
        {
            if(e.ProviderName==PhaseProvider)
            {
                string[] fields=["sourceRevision","variant","mode","itemCount","workSeconds","window","timestamp"];
                if(!e.PayloadNames.SequenceEqual(fields))return Make("malformed");
                if(e.PayloadValue(0) is not string source || e.PayloadValue(1) is not string variant ||
                   e.PayloadValue(2) is not string mode || e.PayloadValue(3) is not int items ||
                   e.PayloadValue(4) is not int seconds || e.PayloadValue(5) is not string window ||
                   e.PayloadValue(6) is not long timestamp)return Make("malformed");
                var marker=new Marker(source,variant,mode,items,seconds,window,timestamp);
                return ((int)e.ID,e.EventName) switch {
                    (1,"WorkStart")=>Make("work-start",marker:marker),
                    (2,"WorkStop")=>Make("work-stop",marker:marker),
                    _=>Make("malformed")};
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
}
