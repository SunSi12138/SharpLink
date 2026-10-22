using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

var mode=args[0]; var itemCount=int.Parse(args[1]); var workers=int.Parse(args[2]);
var warmupSeconds=double.Parse(args[3]); var seconds=double.Parse(args[4]); var output=args[5];
var dynamic=mode=="dynamic-intercepted";
if(!dynamic && mode!="static-intercepted") throw new ArgumentException("Unknown mode");
Assembly? module=null;
Assembly LoadModule(){var a=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,"ScalarStreamModule.dll")); RuntimeHelpers.RunModuleConstructor(a.ManifestModule.ModuleHandle); return a;}
if(!dynamic) module=LoadModule();
var serverBuilder=SharpLinkServerBuilder.Create().UseTcp(0,IPAddress.Loopback).UseHeartbeat(TimeSpan.FromHours(1),TimeSpan.FromHours(2));
var port=((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
await using var server=serverBuilder.Build();
var interceptor=new PassThrough();
await using var client=SharpClientBuilder.Create().UseTcp("127.0.0.1",port).UseConnectionPool(o=>{o.MinConnections=1;o.MaxConnections=1;}).DisableRequestTimeout().UseHeartbeat(TimeSpan.FromHours(1),TimeSpan.FromHours(2)).AddInterceptor(interceptor).Build();
if(dynamic) {
    module=LoadModule();
    var s=server.RegisterAssembly(module);var c=client.RegisterAssembly(module);
    if(!s.Succeeded || !c.Succeeded)throw new InvalidOperationException($"registration failed: {s.Error} {c.Error}");
}
await server.StartAsync(); await client.ConnectAsync();
var readyCountProperty=client.GetType().GetProperty("ReadyConnectionCount",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!;
var readyConnections=(int)readyCountProperty.GetValue(client)!;
if(readyConnections!=1)throw new InvalidOperationException($"Expected one ready connection, got {readyConnections}");
var contract=module!.GetType("PhysicalTcp.IScalarStream",true)!;
var proxy=typeof(ISharpLinkClient).GetMethod("Get")!.MakeGenericMethod(contract).Invoke(client,null)!;
var channel=proxy.GetType().GetField("_channel",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(proxy)!;
if(dynamic != channel.GetType().Name.Contains("ModuleRpcChannel"))throw new InvalidOperationException($"Wrong channel {channel.GetType()}");
var invoke=(Func<int,CancellationToken,IAsyncEnumerable<int>>)contract.GetMethod("DownloadAsync")!.CreateDelegate(typeof(Func<int,CancellationToken,IAsyncEnumerable<int>>),proxy);
var expected=(long)itemCount*(itemCount-1)/2;
async ValueTask Operation() {
    long sum=0;int n=0;
    await foreach(var item in invoke(itemCount,CancellationToken.None).ConfigureAwait(false)) { if(item!=n)throw new InvalidOperationException("Item order mismatch");sum+=item;n++; }
    if(n!=itemCount || sum!=expected)throw new InvalidOperationException($"Bad stream: {n}/{sum}");
}
var first=Stopwatch.GetTimestamp();
var firstStream=invoke(itemCount,CancellationToken.None);var streamType=firstStream.GetType().FullName;
var wrapperTypes=new HashSet<string>();
void TraceWrappers(object value,int depth=0){
    if(depth>8 || !wrapperTypes.Add(value.GetType().FullName!))return;
    foreach(var f in value.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)){
        var nested=f.GetValue(value);
        if(nested is IAsyncEnumerable<int> or IAsyncEnumerator<int> || nested?.GetType().FullName?.StartsWith("System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable") == true)TraceWrappers(nested!,depth+1);
    }
}
await using(var reader=firstStream.GetAsyncEnumerator()){
    if(!await reader.MoveNextAsync() || reader.Current!=0)throw new InvalidOperationException("Initial stream failed");
    TraceWrappers(reader);
    int n=1;long sum=0;
    while(await reader.MoveNextAsync()){if(reader.Current!=n)throw new InvalidOperationException("First stream order");sum+=reader.Current;n++;}
    if(n!=itemCount || sum!=expected)throw new InvalidOperationException("First stream content");
}
var firstUs=Stopwatch.GetElapsedTime(first).TotalMicroseconds;
if(!interceptor.Observed || !wrapperTypes.Any(x=>x.Contains("InterceptedAsyncEnumerable")))throw new InvalidOperationException("Intercepted stream wrapper was not reached");
if(dynamic != streamType!.Contains("InvokeServerStreamingDeferred"))throw new InvalidOperationException($"Unexpected outer stream {streamType}");
async Task Warmup(){var until=Stopwatch.GetTimestamp()+(long)(warmupSeconds*Stopwatch.Frequency);while(Stopwatch.GetTimestamp()<until)await Operation();}
await Task.WhenAll(Enumerable.Range(0,workers).Select(_=>Task.Run(Warmup)));
GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
var samples=Enumerable.Range(0,workers).Select(_=>new long[1_000_000]).ToArray();var counts=new int[workers];
var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);long deadline=0;
var jobs=Enumerable.Range(0,workers).Select(w=>Task.Run(async()=>{
    await gate.Task;
    while(Stopwatch.GetTimestamp()<Volatile.Read(ref deadline)){
        var t=Stopwatch.GetTimestamp();await Operation();samples[w][counts[w]++]=Stopwatch.GetTimestamp()-t;
        if(counts[w]==samples[w].Length)throw new InvalidOperationException("Sample limit hit");
    }
})).ToArray();
using var proc=Process.GetCurrentProcess();proc.Refresh();
var cpuStart=proc.TotalProcessorTime;var g0=GC.CollectionCount(0);var g1=GC.CollectionCount(1);var g2=GC.CollectionCount(2);
var allocStart=GC.GetTotalAllocatedBytes(true);var started=Stopwatch.GetTimestamp();deadline=started+(long)(seconds*Stopwatch.Frequency);gate.SetResult();
await Task.WhenAll(jobs);var elapsed=Stopwatch.GetElapsedTime(started).TotalSeconds;var alloc=GC.GetTotalAllocatedBytes(true)-allocStart;proc.Refresh();var cpu=(proc.TotalProcessorTime-cpuStart).TotalMicroseconds;
var gc0=GC.CollectionCount(0)-g0;var gc1=GC.CollectionCount(1)-g1;var gc2=GC.CollectionCount(2)-g2;
var latencies=samples.SelectMany((s,w)=>s.Take(counts[w])).Order().ToArray();var ops=latencies.Length;
double Us(double ticks)=>ticks*1e6/Stopwatch.Frequency;
double P(double percentile)=>Us(latencies[Math.Clamp((int)Math.Ceiling(percentile/100*ops)-1,0,ops-1)]);
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var result=new {mode,itemCount,workers,transport="tcp-loopback",connectionPoolMin=1,connectionPoolMax=1,readyConnections,requestTimeout="disabled",heartbeatIntervalSeconds=3600,serverInterceptors=0,clientInterceptors=1,channelType=channel.GetType().FullName,streamType,wrapperTypes,interceptorObserved=interceptor.Observed,port,warmupSeconds,requestedSeconds=seconds,elapsedSeconds=elapsed,operations=ops,operationsPerSecond=ops/elapsed,itemsPerSecond=(double)ops*itemCount/elapsed,meanUs=latencies.Average(x=>Us(x)),p50Us=P(50),p95Us=P(95),p99Us=P(99),p999Us=P(99.9),maxUs=P(100),allocatedBytesPerOperation=(double)alloc/ops,cpuUsPerOperation=cpu/ops,gc0,gc1,gc2,firstCallUs=firstUs,validationFailures=0,runtime=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),processorCount=Environment.ProcessorCount,serverGc=GCSettings.IsServerGC,tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default",tieredPgo=Environment.GetEnvironmentVariable("DOTNET_TieredPGO")??"default",readyToRun=Environment.GetEnvironmentVariable("DOTNET_ReadyToRun")??"default",timestampUtc=DateTimeOffset.UtcNow,sourceRevision=Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA")??"unspecified",binaryHashes=Directory.GetFiles(AppContext.BaseDirectory,"*.dll").Where(x=>Path.GetFileName(x).StartsWith("SharpLink.")||Path.GetFileName(x) is "PhysicalTcpProbe.dll" or "ScalarStreamModule.dll").ToDictionary(x=>Path.GetFileName(x)!,Hash)};
if((int)readyCountProperty.GetValue(client)! !=1)throw new InvalidOperationException("Ready connection count changed during sample");
var json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(output,json);Console.WriteLine(json);
await client.StopAsync();await server.StopAsync(TimeSpan.Zero);
sealed class PassThrough:ISharpLinkClientInterceptor {
    public bool Observed;
    public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context,SharpLinkClientInvocationDelegate next){Observed=true;return next(context);}
}
