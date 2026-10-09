using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace SharpLink.MethodFactsEvidence;
internal static class Program
{
    public static async Task Main(string[] args)
    {
        if(args[0]=="counts")
        {
            foreach(var kind in Enum.GetValues<RpcMethodKind>())
            foreach(var intercepted in new[]{false,true})
            foreach(var dynamic in new[]{false,true})
            foreach(var admission in new[]{false,true})
            foreach(var cancellable in new[]{false,true})
            {
                var stub=new CountingStub(kind);
                await using var harness=new LocalHarness(intercepted,dynamic,admission,stub);
                await harness.Dispatch(kind==RpcMethodKind.OneWay,cancellable);
                Console.WriteLine(JsonSerializer.Serialize(new CountRow(kind.ToString(),intercepted,dynamic,admission,cancellable,stub.Descriptors,stub.Cancellations,stub.Invocations), EvidenceJson.Default.CountRow));
            }
            return;
        }
        var shape=args[1];var iterations=int.Parse(args[2]);
        await using var h=new LocalHarness(shape.Contains("intercept"),shape.Contains("dynamic"));
        var include=shape.Contains("context");
        void Run(int count) { for(int i=0;i<count;i++)h.Invoke(i,include); }
        Run(20000);await Task.Delay(200);Run(20000);await Task.Delay(200);Run(20000);await Task.Delay(200);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var allocated=GC.GetTotalAllocatedBytes(true);var sw=Stopwatch.StartNew();
        Run(iterations);sw.Stop();cpu=process.TotalProcessorTime-cpu;
        Console.WriteLine(JsonSerializer.Serialize(new TimingRow(shape,iterations,sw.Elapsed.TotalNanoseconds/iterations,cpu.TotalNanoseconds/iterations,(GC.GetTotalAllocatedBytes(true)-allocated)/(double)iterations,h.Service.Calls,Unsafe.SizeOf<RpcMethodDescriptor>(),Unsafe.SizeOf<RpcMethodDescriptor?>()), EvidenceJson.Default.TimingRow));
    }
}

internal sealed record CountRow(string kind,bool intercepted,bool dynamic,bool admission,bool cancellable,int Descriptors,int Cancellations,int Invocations);
internal sealed record TimingRow(string shape,int iterations,double ns,double cpuNs,double bytes,int Calls,int descriptorSize,int nullableSize);
[JsonSerializable(typeof(CountRow))]
[JsonSerializable(typeof(TimingRow))]
internal partial class EvidenceJson : JsonSerializerContext { }
