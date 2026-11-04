using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text.Json;

Directory.CreateDirectory(args[0]);
using var sidecar = new StreamWriter(Path.Combine(args[0],"phases.jsonl"));
Console.Error.WriteLine("Phase provider enabled: "+Phases.Log.IsEnabled());
int sequence=0;
void Phase(string cell,string boundary,int chunk,int completed)
{
    var stamp=Stopwatch.GetTimestamp();var freq=Stopwatch.Frequency;
    Phases.Log.Phase(sequence,cell,boundary,chunk,completed,stamp,freq);
    sidecar.WriteLine(JsonSerializer.Serialize(new { sequence,cell,boundary,chunk,completed,timestamp=stamp,frequency=freq })); sequence++;
}
Phase("process","process-start",-1,0);
foreach(var cell in new[]{"I/T","P/T","I/U","P/U"})
{
    Phase(cell,"cell-start",-1,0); MethodFactsExperiment.IFactsRpc_Proxy.Touch(); Phase(cell,"warmup-end",-1,1000);
    for(int i=0;i<5;i++) { Phase(cell,"chunk-start",i,1000+i*2000); SharpLink.RpcMethodFacts.ToDescriptor(); await Task.Run(()=>GC.Collect()); Phase(cell,"chunk-end",i,1000+(i+1)*2000); }
    Phase(cell,"cell-end",-1,11000);
}
Phase("process","process-end",-1,44000);
sidecar.Flush();
[EventSource(Name="SharpLink-MethodFacts-Phases")]
sealed class Phases:EventSource
{
    public static readonly Phases Log=new();
    [Event(1,Level=EventLevel.Informational)] public void Phase(int sequence,string cell,string boundary,int chunk,int completed,long timestamp,long frequency)=>WriteEvent(1,sequence,cell,boundary,chunk,completed,timestamp,frequency);
}
namespace MethodFactsExperiment { static class IFactsRpc_Proxy { [MethodImpl(MethodImplOptions.NoInlining)] public static void Touch()=>Console.Write(""); } }
namespace SharpLink { static class RpcMethodFacts { [MethodImpl(MethodImplOptions.NoInlining)] public static void ToDescriptor()=>Console.Write(""); } }
