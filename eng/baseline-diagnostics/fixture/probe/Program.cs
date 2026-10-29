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

if (args.Length != 4 || args[0] is not ("control" or "trace"))
    throw new ArgumentException("control|trace outputJson readyJson goFile");
var variant = args[0];
var output = Path.GetFullPath(args[1]);
var readyFile = Path.GetFullPath(args[2]);
var goFile = Path.GetFullPath(args[3]);
var pendingReadyFile = readyFile + ".pending";
if (new[] { output, readyFile, goFile, pendingReadyFile }.Distinct(StringComparer.Ordinal).Count() != 4 ||
    File.Exists(output) || File.Exists(readyFile) || File.Exists(goFile) || File.Exists(pendingReadyFile))
    throw new InvalidOperationException("Diagnostic outputs/handshake must be distinct, new local paths.");
if (Environment.Version.ToString() != "10.0.14" ||
    Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") != ArmAdapter.Revision)
    throw new InvalidOperationException("Diagnostic runtime/source identity differs.");
var pid = Environment.ProcessId;
using (var ready = new FileStream(pendingReadyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    JsonSerializer.Serialize(ready, new { schema = 1, mode = variant, pid, sourceRevision = ArmAdapter.Revision });
File.Move(pendingReadyFile, readyFile, overwrite: false);
using (var handshakeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
{
    while (!File.Exists(goFile))
        await Task.Delay(50, handshakeDeadline.Token);
}
if (File.ReadAllText(goFile) != $"GO {variant} {pid}\n")
    throw new InvalidOperationException("Diagnostic GO handshake differs from this fresh process.");
const string mode = "static-intercepted";
const int itemCount = 100;
const int workers = 1;
const int warmupSeconds = 3;
const int workSeconds = 20;
const string phase = "diagnostic-work";
var dynamic = false;
var phaseEvents = DiagnosticPhase.Log;

Assembly? module = null;
Assembly LoadModule()
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "ScalarStreamModule.dll"));
    RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
    return assembly;
}
module = LoadModule();
var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback)
    .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
await using var server = serverBuilder.Build();
var interceptor = new PassThrough();
await using var client = SharpClientBuilder.Create().UseTcp("127.0.0.1", port)
    .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
    .DisableRequestTimeout().UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
    .AddInterceptor(interceptor).Build();
var generatedManifest = ArmAdapter.VerifyManifest(module!);
await server.StartAsync();
await client.ConnectAsync();
const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var readyCountProperty = client.GetType().GetProperty("ReadyConnectionCount", instanceFlags)
    ?? throw new InvalidOperationException("Ready connection inspector missing.");
var logicalCountField = client.GetType().GetField("_activeLogicalInvocations", instanceFlags)
    ?? throw new InvalidOperationException("Logical invocation inspector missing.");
int ReadyCount() => (int)readyCountProperty.GetValue(client)!;
int LogicalCount() => (int)logicalCountField.GetValue(client)!;
if (ReadyCount() != 1) throw new InvalidOperationException("Exactly one ready TCP connection required.");
var contract = module!.GetType("PhysicalTcp.IScalarStream", true)!;
var serviceType = module.GetType("PhysicalTcp.ScalarStreamService", true)!;
var proxy = typeof(ISharpLinkClient).GetMethod("Get")!.MakeGenericMethod(contract).Invoke(client, null)!;
var channel = proxy.GetType().GetField("_channel", instanceFlags)!.GetValue(proxy)!;
var channelType = channel.GetType().FullName!;
var invoke = (Func<int, CancellationToken, IAsyncEnumerable<int>>)contract.GetMethod("DownloadAsync")!
    .CreateDelegate(typeof(Func<int, CancellationToken, IAsyncEnumerable<int>>), proxy);
var normalInvoke = (Func<int, CancellationToken, IAsyncEnumerable<int>>)contract.GetMethod("NormalCompletionAsync")!
    .CreateDelegate(typeof(Func<int, CancellationToken, IAsyncEnumerable<int>>), proxy);
var normalChecks = new NormalChecks(client, server, serviceType, LogicalCount);
var expected = (long)itemCount * (itemCount - 1) / 2;
async ValueTask Operation()
{
    long sum = 0;
    int count = 0;
    await foreach (var item in invoke(itemCount, CancellationToken.None).ConfigureAwait(false))
    {
        if (item != count) throw new InvalidOperationException("Item order mismatch.");
        sum += item;
        count++;
    }
    if (count != itemCount || sum != expected) throw new InvalidOperationException($"Bad stream: {count}/{sum}.");
}

// This graph inspection is outside the measurement window. Traverse actual wrapper
// instances, including compiler-generated configured-enumerable structs, not names
// inferred from the product version. Reference identity avoids skipping repeated types.
var wrapperTypes = new HashSet<string>(StringComparer.Ordinal);
var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
void TraceWrappers(object value, int depth = 0)
{
    if (depth > 12 || !visited.Add(value)) return;
    wrapperTypes.Add(value.GetType().FullName!);
    foreach (var field in value.GetType().GetFields(instanceFlags))
    {
        var nested = field.GetValue(value);
        if (nested is IAsyncEnumerable<int> or IAsyncEnumerator<int> ||
            nested?.GetType().FullName?.StartsWith("System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal) == true)
            TraceWrappers(nested!, depth + 1);
    }
}
var firstStream = invoke(itemCount, CancellationToken.None);
var streamType = firstStream.GetType().FullName!;
await using (var reader = firstStream.GetAsyncEnumerator())
{
    if (!await reader.MoveNextAsync() || reader.Current != 0) throw new InvalidOperationException("Initial stream failed.");
    TraceWrappers(firstStream);
    TraceWrappers(reader);
    int count = 1;
    long sum = 0;
    while (await reader.MoveNextAsync())
    {
        if (reader.Current != count) throw new InvalidOperationException("First stream order mismatch.");
        sum += reader.Current;
        count++;
    }
    if (count != itemCount || sum != expected) throw new InvalidOperationException("First stream count mismatch.");
}
ArmAdapter.VerifyPath(dynamic, channelType, streamType, wrapperTypes);
var clientActivityListeners = SharpLinkTelemetry.ClientActivitySource.HasListeners();
var clientCallsEnabled = (bool)(typeof(SharpLinkTelemetry).GetProperty("ClientCallsEnabled", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Telemetry inspector unavailable.")).GetValue(null)!;
if (clientActivityListeners || clientCallsEnabled || !interceptor.Observed)
    throw new InvalidOperationException("Expected actual telemetry-off, intercepted path.");
var logicalPathProof = new { clientActivityListeners, clientCallsEnabled,
    telemetryWrapperObserved = false, completionOwnedObserved = false,
    generalLogicalObserved = true, physicalDispatcherObserved = true, observedBeforeWarmup = true };
// Only normal-completion health controls, outside the two-marker work window.
var (normalCorrectness, beforeWorkDrain) = await normalChecks.Run(normalInvoke, itemCount, expectedCalls: 1);
async Task Warmup()
{
    var until = Stopwatch.GetTimestamp() + warmupSeconds * Stopwatch.Frequency;
    while (Stopwatch.GetTimestamp() < until) await Operation();
}
await Task.Run(Warmup);
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
long operations = 0;
var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
long deadline = 0;
var job = Task.Run(async () =>
{
    await gate.Task;
    while (Stopwatch.GetTimestamp() < Volatile.Read(ref deadline))
    {
        await Operation();
        operations++;
    }
});
using var process = Process.GetCurrentProcess();
var markerStartTick = Stopwatch.GetTimestamp();
phaseEvents.WorkStart(ArmAdapter.Revision, variant, mode, itemCount, workSeconds, "work20s", markerStartTick);
process.Refresh();
var cpuStart = process.TotalProcessorTime;
var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
var allocationStart = GC.GetTotalAllocatedBytes(true);
var started = Stopwatch.GetTimestamp();
deadline = started + workSeconds * Stopwatch.Frequency;
gate.SetResult();
await job;
var ended = Stopwatch.GetTimestamp();
var elapsedTicks = ended - started;
var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocationStart;
process.Refresh();
var cpuTicks = (process.TotalProcessorTime - cpuStart).Ticks;
var gc0 = GC.CollectionCount(0) - g0;
var gc1 = GC.CollectionCount(1) - g1;
var gc2 = GC.CollectionCount(2) - g2;
var markerStopTick = Stopwatch.GetTimestamp();
phaseEvents.WorkStop(ArmAdapter.Revision, variant, mode, itemCount, workSeconds, "work20s", markerStopTick);
if (operations == 0 || elapsedTicks < workSeconds * Stopwatch.Frequency)
    throw new InvalidOperationException("Diagnostic work window was empty or truncated.");
var (postNormalCorrectness, afterWorkDrain) = await normalChecks.Run(normalInvoke, itemCount, expectedCalls: 2);
if (ReadyCount() != 1)
    throw new InvalidOperationException("TCP connection missing after diagnostic work.");
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
string CanonicalAssemblyFile(string location)
{
    if (string.IsNullOrEmpty(location) || !Path.IsPathFullyQualified(location))
        throw new InvalidOperationException("Every non-dynamic assembly must have an absolute disk location.");
    var fullPath = Path.GetFullPath(location);
    if (!File.Exists(fullPath))
        throw new InvalidOperationException("Loaded assembly file is missing.");
    // Reject links on the file AND every ancestor. Merely normalizing '..' does
    // not prove an assembly stayed inside its declared package/runtime root.
    for (string? component = fullPath; component is not null; component = Path.GetDirectoryName(component))
        if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Loaded assembly path contains a symbolic link or reparse point.");
    return fullPath;
}
// Establish shutdown/disposal before publishing a success artifact. Identity
// capture is after the timed window and shutdown, never in the measured path.
await client.StopAsync();
await server.StopAsync(TimeSpan.Zero);
await client.DisposeAsync();
await server.DisposeAsync();
var dlls = Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Select(CanonicalAssemblyFile).ToArray();
// Force both framework representatives to load before taking the inventory.
var frameworkLocations = new[]
{
    CanonicalAssemblyFile(typeof(object).Assembly.Location),
    CanonicalAssemblyFile(typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location)
};
var snapshotAssemblyNames = new HashSet<string>(StringComparer.Ordinal)
{
    "SharpLink.Abstractions", "SharpLink.Client", "SharpLink.Runtime", "SharpLink.Sdk", "SharpLink.Server",
    "PhysicalTcpProbe", "ScalarStreamModule", "Microsoft.Extensions.DependencyInjection.Abstractions",
    "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Logging.Abstractions", "System.Threading.RateLimiting"
};
var allLoaded = new Dictionary<Assembly, LoadedAssemblyIdentity>();
// Reflection, metadata and JSON helpers can load assemblies. Rebuild only the
// identity/metadata record until serialization leaves the exact non-dynamic set
// unchanged. Workload, timing and endpoint shutdown above are never repeated.
for (var inventoryRound = 0; ; inventoryRound++)
{
    if (inventoryRound == 16)
        throw new InvalidOperationException("Loaded assembly inventory did not stabilize.");
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
        if (assembly.IsDynamic || allLoaded.ContainsKey(assembly))
            continue;
        var name = assembly.GetName().Name
            ?? throw new InvalidOperationException("Loaded assembly has no simple name.");
        var location = CanonicalAssemblyFile(assembly.Location);
        allLoaded.Add(assembly, new LoadedAssemblyIdentity(name, location,
            FileVersionInfo.GetVersionInfo(location).ProductVersion,
            assembly.ManifestModule.ModuleVersionId, Hash(location)));
    }
    var completeInventory = allLoaded.Values.OrderBy(identity => identity.name, StringComparer.Ordinal).ToArray();
    if (completeInventory.Select(identity => identity.name).Distinct(StringComparer.Ordinal).Count() != completeInventory.Length)
        throw new InvalidOperationException("Duplicate simple names across loaded assembly identity sets.");
    var loadedAssemblies = completeInventory.Where(identity => snapshotAssemblyNames.Contains(identity.name)).ToArray();
    // Intentionally retain unknown names here. The independent validator must reject
    // anything outside the SHA512-archive-derived private-framework allowlist.
    var frameworkAssemblies = completeInventory.Where(identity => !snapshotAssemblyNames.Contains(identity.name))
        .Select(identity => new { identity.name, identity.location, identity.sha256 }).ToArray();
    var loadedAssemblyCount = completeInventory.Length;
    if (loadedAssemblyCount != loadedAssemblies.Length + frameworkAssemblies.Length)
        throw new InvalidOperationException("Loaded assembly partition is incomplete.");
    long ReadAffinity()
    {
        if (OperatingSystem.IsLinux())
        {
            using var currentProcess = Process.GetCurrentProcess();
            return currentProcess.ProcessorAffinity.ToInt64();
        }
        throw new PlatformNotSupportedException("This fixture requires Linux affinity evidence.");
    }
    var result = new
    {
        schemaVersion = 1, purpose = "DiagnosticOnly", variant, pid,
        arm = ArmAdapter.Name, expectedSource = ArmAdapter.Revision,
        sourceRevision = ArmAdapter.Revision, apiVersion = ArmAdapter.GeneratedApi, phase,
        mode, itemCount, workers, generatedManifest, normalCorrectness, postNormalCorrectness,
        beforeWorkDrain, afterWorkDrain, logicalPathProof,
        shutdownCompleted = true, disposeCompleted = true,
        transport = "tcp-loopback", sameVersionServerClient = true,
        connectionPoolMin = 1, connectionPoolMax = 1, readyConnections = 1,
        requestTimeout = "disabled", heartbeatIntervalSeconds = 3600, heartbeatTimeoutSeconds = 7200,
        serverInterceptors = 0, clientInterceptors = 1, channelType, streamType, wrapperTypes,
        interceptorObserved = interceptor.Observed, port, warmupSeconds, workSeconds,
        workStartTick = started, workEndTick = ended, markerStartTick, markerStopTick,
        elapsedTicks, stopwatchFrequency = Stopwatch.Frequency, operations,
        cpuTicks, cpuTickUnit = "100ns", allocatedBytes, gc0, gc1, gc2,
        validationFailures = 0, runtimeVersion = Environment.Version.ToString(),
        runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount,
        processorAffinity = ReadAffinity(), serverGc = GCSettings.IsServerGC,
        tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
        tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
        readyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "default",
        frameworkLocations, loadedAssemblies, frameworkAssemblies, loadedAssemblyCount,
        binaryHashes = dlls.ToDictionary(path => Path.GetFileName(path)!, Hash),
        timestampUtc = DateTimeOffset.UtcNow
    };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    var resultBytes = System.Text.Encoding.UTF8.GetBytes(json);
    // The final check is after all metadata and the JSON serializer have run.
    // Exact equality catches omissions as well as an unexpected unload; records are
    // never dropped to make an unstable inventory pass.
    var actualLoaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).ToArray();
    if (actualLoaded.Length != allLoaded.Count || actualLoaded.Any(assembly => !allLoaded.ContainsKey(assembly)))
        continue;
    using (var resultFile = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        resultFile.Write(resultBytes);
    break;
}
Console.WriteLine($"DIAGNOSTIC_OK variant={variant} pid={pid}");

sealed class PassThrough : ISharpLinkClientInterceptor
{
    public bool Observed;
    public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
    {
        Observed = true;
        return next(context);
    }
}

internal sealed record LoadedAssemblyIdentity(
    string name, string location, string? productVersion, Guid mvid, string sha256);
