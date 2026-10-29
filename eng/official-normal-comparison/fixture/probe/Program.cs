using System.Diagnostics;
using System.Globalization;
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

if (args.Length is not (7 or 8))
    throw new ArgumentException("mode itemCount workers warmupSeconds seconds output strict-cleanup|normal-validate | mode itemCount workers warmupSeconds seconds output normal-measure rawTicksPath");
var mode = args[0];
var itemCount = int.Parse(args[1], CultureInfo.InvariantCulture);
var workers = int.Parse(args[2], CultureInfo.InvariantCulture);
var warmupSeconds = double.Parse(args[3], CultureInfo.InvariantCulture);
var seconds = double.Parse(args[4], CultureInfo.InvariantCulture);
var output = args[5];
var phase = args[6];
var dynamic = mode == "dynamic-intercepted";
if ((!dynamic && mode != "static-intercepted") || itemCount is not (1 or 100) || workers != 1)
    throw new ArgumentException("Unsupported cell or worker count.");
var strictCleanupExecuted = phase == "strict-cleanup";
var measuring = phase == "normal-measure";
if (phase is not ("strict-cleanup" or "normal-validate" or "normal-measure") ||
    (measuring ? args.Length != 8 || warmupSeconds != 3 || seconds != 6
        : args.Length != 7 || warmupSeconds != 0 || seconds != 0))
    throw new ArgumentException("Cold phases require zero durations; normal-measure requires exactly 3s warmup and 6s timing.");
if (strictCleanupExecuted && ArmAdapter.Name != "candidate-3d76a3a8")
    throw new InvalidOperationException("Strict cleanup belongs only to the candidate; the official known-negative is historical, not a timed preflight.");
if (Environment.Version.ToString() != "10.0.14")
    throw new InvalidOperationException($"Runtime must be 10.0.14, got {Environment.Version}.");

Assembly? module = null;
Assembly LoadModule()
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "ScalarStreamModule.dll"));
    RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
    return assembly;
}
if (!dynamic) module = LoadModule();
var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback)
    .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
await using var server = serverBuilder.Build();
var interceptor = new PassThrough();
await using var client = SharpClientBuilder.Create().UseTcp("127.0.0.1", port)
    .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
    .DisableRequestTimeout().UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
    .AddInterceptor(interceptor).Build();
if (dynamic)
{
    module = LoadModule();
    var serverRegistration = server.RegisterAssembly(module);
    var clientRegistration = client.RegisterAssembly(module);
    if (!serverRegistration.Succeeded || !clientRegistration.Succeeded)
        throw new InvalidOperationException($"Dynamic registration failed: {serverRegistration.Error}; {clientRegistration.Error}.");
}
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
var correctnessInvoke = (Func<int, CancellationToken, IAsyncEnumerable<int>>)contract.GetMethod("CorrectnessAsync")!
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
// Only candidate-only processes enter the failure/abandonment suite. A measured
// process, including the official baseline, can never perform this leaking preflight.
var correctness = strictCleanupExecuted
    ? await ColdChecks.Run(correctnessInvoke, Operation, serviceType, LogicalCount)
    : new Dictionary<string, bool>(StringComparer.Ordinal);
var (normalCorrectness, beforeWarmupDrain) = await normalChecks.Run(normalInvoke, itemCount);


long[][] samples = [];
int[] counts = [];
long started = 0, ended = 0, elapsedTicks = 0, allocated = 0;
double cpuUs = 0;
int gc0 = 0, gc1 = 0, gc2 = 0;
if (measuring)
{
    async Task Warmup()
    {
        var until = Stopwatch.GetTimestamp() + (long)(warmupSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until) await Operation();
    }
    await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(Warmup)));
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    samples = Enumerable.Range(0, workers).Select(_ => new long[1_000_000]).ToArray();
    counts = new int[workers];
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    long deadline = 0;
    var jobs = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
    {
        await gate.Task;
        while (Stopwatch.GetTimestamp() < Volatile.Read(ref deadline))
        {
            if (counts[worker] == samples[worker].Length) throw new InvalidOperationException("Raw sample capacity exceeded.");
            var tick = Stopwatch.GetTimestamp();
            await Operation();
            samples[worker][counts[worker]++] = Stopwatch.GetTimestamp() - tick;
        }
    })).ToArray();
    using var process = Process.GetCurrentProcess(); process.Refresh();
    var cpuStart = process.TotalProcessorTime;
    var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
    var allocationStart = GC.GetTotalAllocatedBytes(true);
    started = Stopwatch.GetTimestamp();
    deadline = started + (long)(seconds * Stopwatch.Frequency);
    gate.SetResult();
    await Task.WhenAll(jobs);
    ended = Stopwatch.GetTimestamp();
    elapsedTicks = ended - started;
    allocated = GC.GetTotalAllocatedBytes(true) - allocationStart;
    process.Refresh(); cpuUs = (process.TotalProcessorTime - cpuStart).TotalMicroseconds;
    gc0 = GC.CollectionCount(0) - g0; gc1 = GC.CollectionCount(1) - g1; gc2 = GC.CollectionCount(2) - g2;
}
// Excluded from timing: all logical and physical call/stream owners must drain
// before endpoint shutdown can conceal a leak.
var afterWorkDrain = await normalChecks.WaitForDrain();
if (ReadyCount() != 1) throw new InvalidOperationException("TCP connection missing after work.");
var latencies = samples.SelectMany((sample, worker) => sample.Take(counts[worker])).Order().ToArray();
var operations = latencies.Length;
if (measuring && (operations == 0 || latencies.Any(tick => tick <= 0)))
    throw new InvalidOperationException("Empty/nonpositive raw measurements.");
double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
double? Percentile(int numerator, int denominator) => operations == 0 ? null
    : Us(latencies[checked((int)(((long)operations * numerator + denominator - 1) / denominator - 1))]);
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
// Materialize raw samples in memory only. Serialization may load assemblies;
// those must be present in the final stable inventory before any record is saved.
string? rawTicksJson = null;
if (measuring)
{
    var rawTicks = new { schemaVersion = 2, arm = ArmAdapter.Name, mode, itemCount,
        stopwatchFrequency = Stopwatch.Frequency, measurementStartTick = started,
        measurementEndTick = ended, elapsedTicks, workers, operations,
        samples = Enumerable.Range(0, workers).Select(worker => new { worker, durationTicks = samples[worker].Take(counts[worker]).ToArray() }).ToArray() };
    rawTicksJson = JsonSerializer.Serialize(rawTicks);
}

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
    var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
    var result = new
    {
        schemaVersion = 2, arm = ArmAdapter.Name, expectedSource = ArmAdapter.Revision,
        sourceRevision = ArmAdapter.Revision, apiVersion = ArmAdapter.GeneratedApi, phase,
        mode, itemCount, workers, generatedManifest, correctness, strictCleanupExecuted, normalCorrectness,
        beforeWarmupDrain, afterWorkDrain, logicalPathProof,
        shutdownCompleted = true, disposeCompleted = true,
        transport = "tcp-loopback", sameVersionServerClient = true,
        connectionPoolMin = 1, connectionPoolMax = 1, readyConnections = 1,
        requestTimeout = "disabled", heartbeatIntervalSeconds = 3600, heartbeatTimeoutSeconds = 7200,
        serverInterceptors = 0, clientInterceptors = 1, channelType, streamType, wrapperTypes,
        interceptorObserved = interceptor.Observed, port, warmupSeconds, requestedSeconds = seconds,
        elapsedTicks, stopwatchFrequency = Stopwatch.Frequency, elapsedSeconds, operations,
        operationsPerSecond = operations == 0 ? (double?)null : operations / elapsedSeconds,
        itemsPerSecond = operations == 0 ? (double?)null : (double)operations * itemCount / elapsedSeconds,
        meanUs = operations == 0 ? (double?)null : latencies.Average(Us),
        p50Us = Percentile(1, 2), p95Us = Percentile(95, 100), p99Us = Percentile(99, 100), p999Us = Percentile(999, 1000), maxUs = Percentile(1, 1),
        allocatedBytesPerOperation = operations == 0 ? (double?)null : (double)allocated / operations,
        cpuUsPerOperation = operations == 0 ? (double?)null : cpuUs / operations, gc0, gc1, gc2,
        validationFailures = 0, runtimeVersion = Environment.Version.ToString(),
        runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount,
        processorAffinity = ReadAffinity(), serverGc = GCSettings.IsServerGC,
        tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
        tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
        readyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "default",
        frameworkLocations, loadedAssemblies, frameworkAssemblies, loadedAssemblyCount, binaryHashes = dlls.ToDictionary(path => Path.GetFileName(path)!, Hash),
        timestampUtc = DateTimeOffset.UtcNow
    };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    // The final check is after all metadata and BOTH JSON serializers have run.
    // Exact equality catches omissions as well as an unexpected unload; records are
    // never dropped to make an unstable inventory pass.
    var actualLoaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).ToArray();
    if (actualLoaded.Length != allLoaded.Count || actualLoaded.Any(assembly => !allLoaded.ContainsKey(assembly)))
        continue;
    if (rawTicksJson is not null)
        File.WriteAllText(args[7], rawTicksJson);
    File.WriteAllText(output, json);
    break;
}
Console.WriteLine($"PROBE_OK arm={ArmAdapter.Name} phase={phase} mode={mode} items={itemCount}");

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
