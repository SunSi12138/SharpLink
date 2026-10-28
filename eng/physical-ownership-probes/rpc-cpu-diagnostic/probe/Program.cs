using System.Diagnostics;
using System.Diagnostics.Tracing;
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

if(args.Length != 3) throw new ArgumentException("mode itemCount new-output-directory");
var mode=args[0]; var itemCount=int.Parse(args[1]); const int workers=1;
if(itemCount is not (1 or 100) || (mode=="dynamic-intercepted" && itemCount!=1))throw new ArgumentException("Unplanned case");
var output=Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output,"pid.tmp"),JsonSerializer.Serialize(new {pid=Environment.ProcessId}));
File.Move(Path.Combine(output,"pid.tmp"),Path.Combine(output,"pid.json"));
using var ledger=new StreamWriter(new FileStream(Path.Combine(output,"phases.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
Phase.Output=ledger;
Phase.Mark("process-start",0);
const int batch=256, maxOperations=1_000_000;
const double warmupSeconds=10, seconds=20;
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

if(!interceptor.Observed || !wrapperTypes.Any(x=>x.Contains("InterceptedAsyncEnumerable")))throw new InvalidOperationException("Intercepted stream wrapper was not reached");
if(dynamic != streamType!.Contains("InvokeServerStreamingDeferred"))throw new InvalidOperationException($"Unexpected outer stream {streamType}");
// Cold semantic proof is outside observation; unchanged real RPC, module and wrapper.
File.WriteAllText(Path.Combine(output,"cold-proof.json"),JsonSerializer.Serialize(new {mode,itemCount,readyConnections,channelType=channel.GetType().FullName,streamType,wrapperTypes,interceptorObserved=interceptor.Observed,operations=1,checksum=expected}));
async Task<int> RunPhase(string name,double duration){
    var count=0; var start=Phase.Mark(name+"-start",0);
    var until=start.MonotonicNs+(long)(duration*1e9);
    do {
        for(var i=0;i<batch;i++){await Operation();count++;if(count>=maxOperations)throw new InvalidOperationException("Operation cap hit; no shortened phase");}
    }while(Clock.Now()<until);
    Phase.Mark(name+"-end",count);return count;
}
await Task.Run(()=>RunPhase("warmup",warmupSeconds));
var operations=await Task.Run(()=>RunPhase("observation",seconds));
if((int)readyCountProperty.GetValue(client)! !=1)throw new InvalidOperationException("Ready connection count changed");
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var binaryHashes=Directory.GetFiles(AppContext.BaseDirectory,"*.dll").ToDictionary(x=>Path.GetFileName(x)!,Hash);
var snapshotNames=binaryHashes.Keys.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
var loadedAssemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic && (snapshotNames.Contains(a.GetName().Name) || a.GetName().Name!.StartsWith("SharpLink.",StringComparison.Ordinal)))
.Select(a=>new {name=a.GetName().Name,location=a.Location,productVersion=FileVersionInfo.GetVersionInfo(a.Location).ProductVersion,mvid=a.ManifestModule.ModuleVersionId}).ToArray();
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {complete=true,diagnosticOnly=true,processId=Environment.ProcessId,mode,itemCount,workers,operations,warmupSeconds,requestedSeconds=seconds,batch,maxOperations,transport="tcp-loopback",readyConnections,requestTimeout="disabled",clientInterceptors=1,serverInterceptors=0,channelType=channel.GetType().FullName,streamType,wrapperTypes,interceptorObserved=interceptor.Observed,loadedAssemblies,binaryHashes,runtimeVersion=Environment.Version.ToString(),frameworkLocations=new[]{typeof(object).Assembly.Location,typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location},serverGc=GCSettings.IsServerGC,sourceRevision=Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA"),tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default",tieredPgo=Environment.GetEnvironmentVariable("DOTNET_TieredPGO")??"default",readyToRun=Environment.GetEnvironmentVariable("DOTNET_ReadyToRun")??"default"},new JsonSerializerOptions{WriteIndented=true}));
await client.StopAsync();await server.StopAsync(TimeSpan.Zero);
Phase.Mark("process-end",operations);
sealed class PassThrough:ISharpLinkClientInterceptor {
    public bool Observed;
    public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context,SharpLinkClientInvocationDelegate next){Observed=true;return next(context);}
}
internal static class Clock {
    [StructLayout(LayoutKind.Sequential)]private struct Timespec{public long Seconds;public long Nanoseconds;}
    [DllImport("libc",SetLastError=true)]private static extern int clock_gettime(int id,out Timespec value);
    public static long Now(){if(clock_gettime(1,out var t)!=0)throw new InvalidOperationException("CLOCK_MONOTONIC unavailable");return checked(t.Seconds*1_000_000_000+t.Nanoseconds);}
}
internal sealed record Boundary(string Name,int Completed,long MonotonicNs,long CpuReadEndNs,double UserUs,double SystemUs,double TotalUs,int Pid);
internal static class Phase {
    internal static StreamWriter Output=null!;
    internal static Boundary Mark(string name,int count){
        var ns=Clock.Now();var cpu=Environment.CpuUsage;
        var user=cpu.UserTime.TotalMicroseconds;var system=cpu.PrivilegedTime.TotalMicroseconds;var total=user+system;
        var end=Clock.Now();var b=new Boundary(name,count,ns,end,user,system,total,Environment.ProcessId);
        PhaseEvents.Log.Boundary(name,count,ns,end);Output.WriteLine(JsonSerializer.Serialize(b));return b;
    }
}
[EventSource(Name="SharpLink-RpcCpu-Phase")]
internal sealed class PhaseEvents:EventSource {
    internal static readonly PhaseEvents Log=new();
    [Event(1,Level=EventLevel.Informational)]public void Boundary(string name,int count,long monotonicNs,long cpuReadEndNs)=>WriteEvent(1,name,count,monotonicNs,cpuReadEndNs);
}
