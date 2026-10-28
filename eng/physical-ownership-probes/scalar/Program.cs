using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

[assembly: SharpLinkGeneratedAssemblyManifest(typeof(EmptyManifest), SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol, "probe", SharpLinkGeneratedManifestVersions.AbiIdentity)]

var output = args[0];
Directory.CreateDirectory(output);
var clientType = typeof(SharpClientBuilder).Assembly.GetType("SharpLink.Client.SharpLinkClient")!;
var streamType = clientType.GetNestedType("InterceptedAsyncEnumerable`1", BindingFlags.NonPublic)!.MakeGenericType(typeof(int));
var entry = streamType.GetMethod("GetAsyncEnumerator")!;
var machine = entry.GetCustomAttribute<AsyncIteratorStateMachineAttribute>()!.StateMachineType;
if (machine.ContainsGenericParameters) machine = machine.MakeGenericType(typeof(int));
var move = machine.GetMethod("MoveNext", BindingFlags.NonPublic|BindingFlags.Instance)!;
long ObjectBytes(Type type) { for(var i=0;i<100;i++) GC.KeepAlive(RuntimeHelpers.GetUninitializedObject(type)); var before=GC.GetAllocatedBytesForCurrentThread(); for(var i=0;i<10000;i++) GC.KeepAlive(RuntimeHelpers.GetUninitializedObject(type)); return (GC.GetAllocatedBytesForCurrentThread()-before)/10000; }
object Describe(Type type) => new { Type=type.FullName, ObjectBytes=ObjectBytes(type), Fields=type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Select(f=>new {f.Name,Type=f.FieldType.ToString()}) };
File.WriteAllText(Path.Combine(output,"layout.json"),JsonSerializer.Serialize(new { Wrapper=Describe(streamType), Iterator=Describe(machine), EntryILBytes=entry.GetMethodBody()!.GetILAsByteArray()!.Length, MoveNextILBytes=move.GetMethodBody()!.GetILAsByteArray()!.Length, EH=move.GetMethodBody()!.ExceptionHandlingClauses.Count },new JsonSerializerOptions {WriteIndented=true}));
var dynamicType=clientType.Assembly.GetType("SharpLink.Client.SharpLinkModuleRpcChannel")!;
var dynamicEntry=dynamicType.GetMethod("InvokeServerStreamingDeferred",BindingFlags.NonPublic|BindingFlags.Instance)!.MakeGenericMethod(typeof(RpcEmptyRequest),typeof(int));
var dynamicMachine=dynamicEntry.GetCustomAttribute<AsyncIteratorStateMachineAttribute>()!.StateMachineType.MakeGenericType(typeof(RpcEmptyRequest),typeof(int));
var dynamicMove=dynamicMachine.GetMethod("MoveNext",BindingFlags.NonPublic|BindingFlags.Instance)!;
File.WriteAllText(Path.Combine(output,"dynamic-layout.json"),JsonSerializer.Serialize(new {Iterator=Describe(dynamicMachine),EntryILBytes=dynamicEntry.GetMethodBody()!.GetILAsByteArray()!.Length,MoveNextILBytes=dynamicMove.GetMethodBody()!.GetILAsByteArray()!.Length,EH=dynamicMove.GetMethodBody()!.ExceptionHandlingClauses.Count},new JsonSerializerOptions {WriteIndented=true}));
var results = new List<object>();
foreach(var dynamic in new[]{false,true}) foreach(var timed in new[]{false,true}) foreach(var items in new[]{0,1,100}) {
 var interceptor = new Interceptor(items);
 await using var client = SharpClientBuilder.Create().UseTransport(new NoTransport()).DisableRequestTimeout().AddInterceptor(interceptor).Build();
 using var moduleRuntime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
 var manifest=new EmptyManifest();
 using var registration=moduleRuntime.PrepareGeneratedManifest(manifest);
 var module=new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, registration);
 IRpcChannel channel = dynamic ? new SharpLinkModuleRpcChannel((SharpLinkClient)client,module) : (IRpcChannel)client;
 var codec = channel.RuntimeContext.Codecs.GetCodec<int>();
 var method=new RpcMethodDescriptor(1,929,RpcMethodKind.ServerStreaming,true,false,timed,timed?TimeSpan.FromMinutes(5):null);
 for(var i=0;i<2000;i++) await Invoke(channel,method,codec);
 var start=GC.GetAllocatedBytesForCurrentThread();
 for(var i=0;i<20000;i++) await Invoke(channel,method,codec);
 results.Add(new {dynamic,timed,items,bytesPerCall=(GC.GetAllocatedBytesForCurrentThread()-start)/20000.0});
}
File.WriteAllText(Path.Combine(output,"allocations.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
File.WriteAllText(Path.Combine(output,"environment.json"),JsonSerializer.Serialize(new {
 runtimeVersion=Environment.Version.ToString(),
 frameworkLocations=new[]{typeof(object).Assembly.Location,typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location},
 assemblies=Directory.GetFiles(AppContext.BaseDirectory,"*.dll").Select(path=>new {name=AssemblyName.GetAssemblyName(path).FullName,fileVersion=System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion,productVersion=System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion})
},new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(results));
static async ValueTask Invoke(IRpcChannel channel,RpcMethodDescriptor method,IRpcCodec<int> codec) {
 var request=default(RpcEmptyRequest);
 await foreach(var item in channel.InvokeServerStreamingAsync(method,request,RpcEmptyRequestCodec.Instance,codec,null)) Sink.Value = item;
}
sealed class Interceptor(int items):ISharpLinkClientInterceptor {
 public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context,SharpLinkClientInvocationDelegate next) => ValueTask.FromResult(new SharpLinkClientInvocationResult(new Stream(items)));
}
sealed class Stream(int items):IAsyncEnumerable<int>,IAsyncEnumerator<int> {
 int count;
 public int Current=>count;
 public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken token=default)=>this;
 public ValueTask<bool> MoveNextAsync()=>ValueTask.FromResult(count++<items);
 public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
sealed class NoTransport:IClientTransportFactory {
 public ValueTask<ITransportConnection> ConnectAsync(CancellationToken token=default)=>throw new InvalidOperationException("No network path permitted in local allocation probe.");
 public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

static class Sink { public static int Value; }

public sealed class EmptyManifest:ISharpLinkGeneratedAssemblyManifest {
 public int ApiVersion=>SharpLinkGeneratedManifestVersions.Api;
 public int ProtocolVersion=>SharpLinkGeneratedManifestVersions.Protocol;
 public string GeneratorVersion=>"probe";
 public Assembly OwnerAssembly=>typeof(EmptyManifest).Assembly;
 public RpcHash128 RpcAssemblyHash=>new(1,929);
 public string CompileTimeDescriptor=>"probe";
 public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts=>[];
 public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services=>[];
 public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs=>[];
 public IReadOnlyList<string> Dependencies=>[];
}
