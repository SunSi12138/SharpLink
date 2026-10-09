using System.Diagnostics;
using System.Net;
using SharpLink.Client;
using SharpLink.Server;
namespace SharpLink.NullabilityEvidence;
internal static class FullRpc
{
    internal static async Task Run(string shape,int count,int repetitions)
    {
        var builder=SharpLinkServerBuilder.Create().UseHeartbeat(TimeSpan.FromMinutes(5),TimeSpan.FromMinutes(10));
        builder.UseTcp(0,IPAddress.Loopback.ToString());var port=((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        builder.ReplaceService<INullabilityEvidence>(new StreamService());await using var server=builder.Build();await server.StartAsync();
        await using var client=SharpClientBuilder.Create().UseTcp(IPAddress.Loopback.ToString(),port).DisableRequestTimeout().UseHeartbeat(TimeSpan.FromMinutes(5),TimeSpan.FromMinutes(10)).Build();
        await client.ConnectAsync();var proxy=client.Get<INullabilityEvidence>();
        var upload=shape.StartsWith("in-");var kind=shape[shape.IndexOf('-')..][1..];var size=kind=="realistic"?512:1;var nullEvery=kind=="all-null"?1:kind=="half-null"?2:0;
        async Task Once(int itemCount)
        {
            int n;
            if(upload)
            {
                if(kind=="value")n=await proxy.UploadValues(new Items<int>(42,itemCount,0));
                else if(kind is "required" or "realistic")n=await proxy.UploadRequired(new Items<string>(new string('x',size),itemCount,0));
                else
                {
                    var result=await proxy.UploadNullable(new Items<string?>(new string('x',size),itemCount,nullEvery));
                    n=(int)result;var nulls=result>>32;
                    if(nulls!=(nullEvery==0?0:itemCount/nullEvery))throw new InvalidOperationException("Upload nullable itemCount changed");
                }
            }
            else
            {
                n=0;
                if(kind=="value"){await foreach(var v in proxy.Values(itemCount)){if(v!=42)throw new InvalidOperationException("Wrong value");n++;}}
                else if(kind is "required" or "realistic"){await foreach(var v in proxy.Required(itemCount,size)){if(v.Length!=size)throw new InvalidOperationException("Wrong required item");n++;}}
                else{await foreach(var v in proxy.Nullable(itemCount,size,nullEvery)){n++;if((v is null)!=(nullEvery!=0&&n%nullEvery==0))throw new InvalidOperationException("Wrong nullable item");}}
            }
            if(n!=itemCount)throw new InvalidOperationException($"Item itemCount {n}/{itemCount}");
        }
        for(int i=0;i<64;i++)await Once(1000);await Task.Delay(500);for(int i=0;i<64;i++)await Once(1000);await Task.Delay(300);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var bytes=GC.GetTotalAllocatedBytes(true);var sw=Stopwatch.StartNew();
        for(int i=0;i<repetitions;i++)await Once(count);sw.Stop();
        Program.Write("rpc-tcp",shape,count,repetitions,sw.Elapsed.TotalNanoseconds,GC.GetTotalAllocatedBytes(true)-bytes);
    }
}
