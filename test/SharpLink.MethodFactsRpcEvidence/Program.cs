using System.Diagnostics;
using System.Net;
using SharpLink.Client;
namespace SharpLink.MethodFactsEvidence;
internal static class Program
{
    public static async Task Main(string[] args)
    {
        var mode=args[0]; var shape=args[1];var count=int.Parse(args[2]);
        var service=new FactsService();
        var builder=SharpLinkServerBuilder.Create().UseHeartbeat(TimeSpan.FromMinutes(5),TimeSpan.FromMinutes(10));
        if(mode.Contains("intercept"))builder.AddInterceptor(new PassThrough());
        if(mode.Contains("admission"))builder.UseAdmissionControl(o=>o.Global.UseConcurrency(16));
        using var listener=new ActivityListener { ShouldListenTo=static _=>true, Sample=static (ref ActivityCreationOptions<ActivityContext> _) =>ActivitySamplingResult.AllDataAndRecorded };
        if(mode.Contains("telemetry"))ActivitySource.AddActivityListener(listener);
        builder.UseTcp(0,IPAddress.Loopback.ToString());
        var port=((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        builder.ReplaceService<IFactsRpc>(service);
        await using var server=builder.Build();await server.StartAsync();
        await using var client=SharpClientBuilder.Create().UseTcp(IPAddress.Loopback.ToString(),port).DisableRequestTimeout().UseHeartbeat(TimeSpan.FromMinutes(5),TimeSpan.FromMinutes(10)).Build();
        await client.ConnectAsync();var proxy=client.Get<IFactsRpc>();
        async Task Once()
        {
            if(shape=="unary")await proxy.Method00();
            else if(shape=="oneway")await proxy.OneWay();
            else if(shape=="cancellable") { using var cts=new CancellationTokenSource();await proxy.Cancel(cts.Token); }
            else if(shape=="upload") { if(await proxy.Upload(Items())!=6)throw new Exception("Upload result"); }
            else if(shape=="download") { int n=0;await foreach(var x in proxy.Download()){if(x!=7)throw new Exception("Download item");n++;}if(n!=1)throw new Exception("Download count"); }
            else if(shape=="duplex") { int sum=0;await foreach(var x in proxy.Duplex(Items()))sum+=x;if(sum!=6)throw new Exception("Duplex result"); }
            else throw new ArgumentException("shape");
        }
        for(int i=0;i<64;i++)await Once();
        if(shape=="oneway")await WaitForCalls(service,64);
        await Task.Delay(250);GC.Collect();var bytes=GC.GetTotalAllocatedBytes(true);var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var sw=Stopwatch.StartNew();
        for(int i=0;i<count;i++)await Once();
        if(shape=="oneway")await WaitForCalls(service,count+64);
        sw.Stop();cpu=process.TotalProcessorTime-cpu;
        Console.WriteLine(FormattableString.Invariant($"{{\"mode\":\"{mode}\",\"shape\":\"{shape}\",\"count\":{count},\"ns\":{sw.Elapsed.TotalNanoseconds/count},\"cpuNs\":{cpu.TotalNanoseconds/count},\"bytes\":{(GC.GetTotalAllocatedBytes(true)-bytes)/(double)count}}}"));
    }
    private static async Task WaitForCalls(FactsService service,int expected)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(Volatile.Read(ref service.Calls)<expected)await Task.Delay(1,deadline.Token);
    }
    private static async IAsyncEnumerable<int> Items() { yield return 1;yield return 2;yield return 3;await Task.CompletedTask; }
    private sealed class PassThrough : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context,SharpLinkServerInvocationDelegate next)=>next(context);
    }
}
