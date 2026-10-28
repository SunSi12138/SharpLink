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

if (args.Length != 2 || args[0] is not ("static-none" or "static-cancellable" or "dynamic-none"))
    throw new ArgumentException("condition output; conditions static-none/static-cancellable/dynamic-none");
var condition = args[0];
var output = args[1];
var dynamic = condition == "dynamic-none";
var mode = dynamic ? "dynamic-intercepted" : "static-intercepted";
const int itemCount = 1;
if (Environment.Version.ToString() != "10.0.14")
    throw new InvalidOperationException($"Runtime must be10.0.14, got {Environment.Version}.");
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
var server = serverBuilder.Build();
var interceptor = new PassThrough();
var client = SharpClientBuilder.Create().UseTcp("127.0.0.1", port)
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
int ReadyCount() => (int)readyCountProperty.GetValue(client)!;
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
var expected = (long)itemCount * (itemCount - 1) / 2;
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
    generalLogicalObserved = true, physicalDispatcherObserved = true, observedBeforeDiagnosticCases = true };
using var observer = new OwnerObserver(client, server, serviceType, output);
serviceType.GetField("Observe", BindingFlags.Public | BindingFlags.Static)!.SetValue(null,
    (Action<int, string, CancellationToken, CancellationToken>)observer.ServiceEvent);
interceptor.Observer = observer;
var readyConnectionsBeforeTeardown = ReadyCount();
var checks = new Dictionary<string, bool> { ["orderedValues"] = true, ["count"] = true };
string? firstFailure = null;
var teardownErrors = new List<string>();
bool serverStopCompleted = false, clientStopCompleted = false, disposeCompleted = false;
try
{
    observer.Write("diagnostic-start", new { condition, itemCount, mode, channelType, streamType, wrapperTypes });
    await ColdChecks.Run(correctnessInvoke, observer, condition == "static-cancellable", checks);
    if (observer.ObservationFailure is not null)
        throw new InvalidOperationException("Owner observation failed: " + observer.ObservationFailure);
    observer.Write("cold-checks-passed");
}
catch (Exception error)
{
    firstFailure = error.ToString();
    observer.Write("first-failure", new { error = firstFailure });
}
finally
{
    // Teardown is diagnostic observation after the unchanged gate has already decided.
    // A service finally reached only by stopping the server cannot convert failure to success.
    readyConnectionsBeforeTeardown = ReadyCount();
    observer.Write("before-server-stop", new { firstFailure, readyConnectionsBeforeTeardown });
    try { await server.StopAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(20)); serverStopCompleted = true; }
    catch (Exception error) { teardownErrors.Add("serverStop: " + error); }
    observer.Write("after-server-stop", new { serverStopCompleted });
    try { await client.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); clientStopCompleted = true; }
    catch (Exception error) { teardownErrors.Add("clientStop: " + error); }
    observer.Write("after-client-stop", new { clientStopCompleted });
    try
    {
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        disposeCompleted = true;
    }
    catch (Exception error) { teardownErrors.Add("dispose: " + error); }
    observer.Write("post-stop-cleanup", new { disposeCompleted, teardownErrors });
    serviceType.GetField("Observe", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, null);
    interceptor.Observer = null;
}
long ReadAffinity()
{
    if (OperatingSystem.IsLinux())
    {
        using var process = Process.GetCurrentProcess();
        return process.ProcessorAffinity.ToInt64();
    }
    throw new PlatformNotSupportedException("Linux affinity evidence required.");
}
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var dlls = Directory.GetFiles(AppContext.BaseDirectory, "*.dll");
var names = dlls.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
    .Where(assembly => !assembly.IsDynamic && (names.Contains(assembly.GetName().Name) || assembly.GetName().Name!.StartsWith("SharpLink.", StringComparison.Ordinal)))
    .Select(assembly => new { name = assembly.GetName().Name, location = assembly.Location,
        productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion,
        mvid = assembly.ManifestModule.ModuleVersionId, sha256 = Hash(assembly.Location) }).ToArray();
var status = firstFailure is null && teardownErrors.Count == 0 && observer.ObservationFailure is null ? "Passed" : "Failed";
var result = new
{
    schemaVersion = 1, status, condition, arm = ArmAdapter.Name, sourceRevision = ArmAdapter.Revision,
    expectedSource = ArmAdapter.Revision, apiVersion = ArmAdapter.GeneratedApi,
    generatedManifest, phase = "cleanup-diagnostic-only", itemCount, workers = 1, mode,
    firstFailure, observationFailure = observer.ObservationFailure, teardownErrors,
    coldCheckResults = checks, serverStopCompleted, clientStopCompleted, disposeCompleted,
    finalCounters = new { observer.Started, observer.Completed, observer.Active, clientLogical = observer.LogicalCount },
    channelType, streamType, wrapperTypes, logicalPathProof, interceptorObserved = interceptor.Observed,
    transport = "tcp-loopback", sameVersionServerClient = true, connectionPoolMin = 1, connectionPoolMax = 1,
    readyConnections = readyConnectionsBeforeTeardown, cleanupGuardSeconds = 15, warmupSeconds = 0, requestedSeconds = 0,
    requestTimeout = "disabled", heartbeatIntervalSeconds = 3600, heartbeatTimeoutSeconds = 7200,
    clientInterceptors = 1, serverInterceptors = 0, runtimeVersion = Environment.Version.ToString(),
    processorCount = Environment.ProcessorCount, processorAffinity = ReadAffinity(),
    runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), serverGc = GCSettings.IsServerGC,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
    tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default",
    readyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "default",
    frameworkLocations = new[] { typeof(object).Assembly.Location, typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location },
    loadedAssemblies, binaryHashes = dlls.ToDictionary(path => Path.GetFileName(path)!, Hash),
    wireObservation = new { status = "not-observed", sourceInference = "StartServerStreamingInvokerAsync chooses Cancellable iff original effective invocation token.CanBeCanceled or deadline; request timeout disabled. RequestLoop maps negotiated Cancel reason into call-state Reason. Neither frame flags nor Cancel payload are claimed observed.",
        callerTokenControl = condition == "static-cancellable" ? "fresh CTS never explicitly cancelled, case4 only" : "CancellationToken.None, case4",
        actualClientInvocationTokenReceipts = "client-interceptor stage", actualServerOwnershipReceipts = "stage owners.callState" },
    stages = Path.GetFileName(output + ".stages.jsonl"),
    originalRun = "40747822819", originalAttempt = 1, preservesOriginalFailure = true,
    timingExecuted = false, operations = 0, timestampUtc = DateTimeOffset.UtcNow
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(output, json);
Console.WriteLine(json);
return status == "Passed" ? 0 : 1;

sealed class PassThrough : ISharpLinkClientInterceptor
{
    public bool Observed;
    public OwnerObserver? Observer;
    public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
    {
        Observed = true;
        Observer?.SafeWrite("client-interceptor", new
        {
            invocationTokenCanBeCanceled = context.CancellationToken.CanBeCanceled,
            invocationTokenCancelled = context.CancellationToken.IsCancellationRequested
        });
        return next(context);
    }
}
