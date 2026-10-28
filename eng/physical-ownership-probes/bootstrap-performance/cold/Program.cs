using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

// A separate fixed-work process: no elapsed-time, allocation or throughput measurement.
var arm = args[0];
var deadlineOn = args[1] == "on";
var output = args[2];
if (arm is not ("dev" or "a" or "b") || args[1] is not ("on" or "off")) throw new ArgumentException("Unknown cold proof cell");
var module = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "ScalarStreamModule.dll"));
RuntimeHelpers.RunModuleConstructor(module.ManifestModule.ModuleHandle);
var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback).UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
await using var server = serverBuilder.Build();
var interceptor = new ProofInterceptor();
await using var client = SharpClientBuilder.Create().UseTcp("127.0.0.1", port)
    .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; }).DisableRequestTimeout()
    .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2)).AddInterceptor(interceptor).Build();
await server.StartAsync();
await client.ConnectAsync();
var contract = module.GetType("PhysicalTcp.IScalarStream", true)!;
var proxy = typeof(ISharpLinkClient).GetMethod("Get")!.MakeGenericMethod(contract).Invoke(client, null)!;
var invoke = (Func<int, CancellationToken, IAsyncEnumerable<int>>)contract.GetMethod("DownloadAsync")!
    .CreateDelegate(typeof(Func<int, CancellationToken, IAsyncEnumerable<int>>), proxy);
var supervisor = client.GetType().GetField("_frameworkTasks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
var gate = (System.Threading.Lock)supervisor.GetType().GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(supervisor)!;
var active = (IDictionary)supervisor.GetType().GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(supervisor)!;
var observed = new HashSet<string>();
var bootstrapSnapshots = 0;
object? Property(object instance, string name)
{
    for (var type = instance.GetType(); type is not null; type = type.BaseType)
        if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } property)
            return property.GetValue(instance);
    return null;
}
int Inspect()
{
    var count = 0;
    using (gate.EnterScope())
        foreach (DictionaryEntry entry in active)
        {
            var registration = entry.Value!;
            if ((string?)Property(registration, "Operation") != "ServerStreamingInvoker") continue;
            count++;
            observed.Add(registration.GetType().FullName!);
            if (Property(registration, "Task") is Task task) observed.Add(task.GetType().FullName!);
        }
    return count;
}
for (var i = 0; i < 128; i++)
{
    await using var reader = invoke(1, CancellationToken.None).GetAsyncEnumerator();
    var first = reader.MoveNextAsync();
    // Natural TCP pending work only: no scheduling mutation, forced pump gate or timed observer.
    bootstrapSnapshots += Inspect();
    if (!await first || reader.Current != 0 || await reader.MoveNextAsync()) throw new InvalidOperationException("Cold stream content/order mismatch");
}
var descriptor = interceptor.Method ?? throw new InvalidOperationException("Generated descriptor not observed");
var failures = new List<string>();
if (interceptor.Calls != 128 || descriptor.HasMethodTimeout != deadlineOn ||
    descriptor.MethodTimeout != (deadlineOn ? TimeSpan.FromSeconds(30) : null))
    failures.Add("Generated descriptor deadline proof mismatch");
await client.StopAsync();
await server.StopAsync(TimeSpan.Zero);
var remainingBootstrapOwners = Inspect();
if (remainingBootstrapOwners != 0) failures.Add("Bootstrap ownership did not drain");
var actualPending = observed.Any(x => x.Contains(arm == "b" ? "ServerStreamBootstrapCompletion" : "StartServerStreamingInvokerAsync", StringComparison.Ordinal));
if (deadlineOn && !actualPending) failures.Add("No actual expected pending bootstrap observed; no timing may proceed");
if (!deadlineOn && bootstrapSnapshots != 0) failures.Add("Unexpected pending bootstrap in synchronous no-deadline control");
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var proof = new {
    kind = "untimed-natural-tcp-bootstrap-proof", arm, deadlineOn, operations = 128,
    generatedHasMethodTimeout = descriptor.HasMethodTimeout, generatedMethodTimeoutSeconds = descriptor.MethodTimeout?.TotalSeconds,
    actualPending, bootstrapSnapshots, observedRegistrationAndTaskTypes = observed.Order().ToArray(), drained = remainingBootstrapOwners == 0, failures,
    sourceRevision = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA"),
    runtimeVersion = Environment.Version.ToString(), frameworkLocations = new[] { typeof(object).Assembly.Location, typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location },
    loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic &&
        (a.GetName().Name!.StartsWith("SharpLink.", StringComparison.Ordinal) || a.GetName().Name is "ColdBootstrapProof" or "ScalarStreamModule"))
        .Select(a => new { name = a.GetName().Name, location = a.Location, productVersion = FileVersionInfo.GetVersionInfo(a.Location).ProductVersion, mvid = a.ManifestModule.ModuleVersionId }).ToArray(),
    binaryHashes = Directory.GetFiles(AppContext.BaseDirectory, "*.dll").ToDictionary(p => Path.GetFileName(p)!, Hash),
    boundary = "Fixed-work cold observation only, not pending frequency during timing or release acceptance."
};
File.WriteAllText(output, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
sealed class ProofInterceptor : ISharpLinkClientInterceptor
{
    public int Calls;
    public RpcMethodDescriptor? Method;
    public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
    { Calls++; Method = context.Method; return next(context); }
}
