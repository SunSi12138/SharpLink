using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string Stage = "arguments";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--arm" || args[1] is not ("baseline" or "candidate") ||
            args[2] != "--items" || !int.TryParse(args[3], out var items) || items is not (1 or 100) || args[4] != "--output")
        { Console.Error.WriteLine("Usage: --arm baseline|candidate --items 1|100 --output new-local.json"); return 2; }
        var output = Path.GetFullPath(args[5]);
        if (File.Exists(output)) { Console.Error.WriteLine("FAIL output_exists"); return 2; }
        try
        {
            var result = await Run(args[1], items);
            WriteResult(output, result);
            Console.WriteLine($"PASS count arm={args[1]} items={items}");
            return 0;
        }
        catch (Exception ex)
        {
            var failure = ex is OperationCanceledException ? "deadline" : ex is TimeoutException ? "cleanup_timeout" :
                ex is InvalidOperationException ? "invariant" : "exception";
            WriteResult(output, new { schemaVersion = 1, status = "fail", arm = args[1], itemCount = items,
                stage = Stage, failure, exceptionType = ex.GetType().Name });
            Console.Error.WriteLine($"FAIL count {failure} stage={Stage} type={ex.GetType().Name}");
            return 1;
        }
    }

    private static async Task<object> Run(string arm, int itemCount)
    {
        Stage = "setup";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(x => x.Key, x => x.Value);
        Check(metadata["Arm"] == arm, "arm_identity");
        Check(Environment.Version.ToString() == "10.0.14", "runtime_10_0_14_required");
        var initialCounts = WindowUpdateCountDiagnostic.Snapshot();
        Check(initialCounts == default, "fresh_process_counters");
        // Load and run the generated module initializer BEFORE either builder.
        var module = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "ScalarStreamModule.dll"));
        RuntimeHelpers.RunModuleConstructor(module.ManifestModule.ModuleHandle);
        var attribute = module.GetCustomAttribute<SharpLinkGeneratedAssemblyManifestAttribute>()
            ?? throw new InvalidOperationException("manifest_missing");
        var manifest = (ISharpLinkGeneratedAssemblyManifest)Activator.CreateInstance(attribute.ManifestType)!;
        Check(attribute.ApiVersion == 5 && manifest.ApiVersion == 5 && attribute.ProtocolVersion == 2 &&
            manifest.ProtocolVersion == 2 && attribute.GeneratorVersion == "3.0.0" && manifest.GeneratorVersion == "3.0.0" &&
            attribute.AbiIdentity == "sharplink-3.0-api5-owner-bound-unsafe-blit-v1", "manifest_identity");
        var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback)
            .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        ISharpLinkClient? client = null;
        var disposed = false;
        async Task DisposeEndpoints()
        {
            if (client is not null) await client.StopAsync();
            await server.StopAsync(TimeSpan.Zero);
            if (client is not null) await client.DisposeAsync();
            await server.DisposeAsync();
        }
        try
        {
            var interceptor = new PassThrough();
            client = SharpClientBuilder.Create().UseTcp("127.0.0.1", port)
                .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; })
                .DisableRequestTimeout().UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
                .AddInterceptor(interceptor).Build();
            await server.StartAsync().AsTask().WaitAsync(deadline.Token);
            await client.ConnectAsync().AsTask().WaitAsync(deadline.Token);
            Check(Read(client, "ReadyConnectionCount") == 1, "ready_connection_count");
            var ready = (Array)Field(client, "_readyConnections");
            Check(ready.Length == 1, "sole_session");
            var session = (RpcSession)Property(ready.GetValue(0)!, "Session");
            Check(session.HasStreamFlowControl, "flow_control_required");
            var contract = module.GetType("PhysicalTcp.IScalarStream", true)!;
            var proxy = typeof(ISharpLinkClient).GetMethod("Get")!.MakeGenericMethod(contract).Invoke(client, null)!;
            var channelType = Field(proxy, "_channel").GetType().FullName;
            Check(channelType == "SharpLink.Client.SharpLinkClient", "static_channel");
            var invoke = (Func<int, CancellationToken, IAsyncEnumerable<int>>)contract.GetMethod("DownloadAsync")!
                .CreateDelegate(typeof(Func<int, CancellationToken, IAsyncEnumerable<int>>), proxy);
            Check(!SharpLinkTelemetry.ClientActivitySource.HasListeners() &&
                !(bool)typeof(SharpLinkTelemetry).GetProperty("ClientCallsEnabled", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!, "telemetry_off");
            Check(WindowUpdateCountDiagnostic.Snapshot() == initialCounts, "no_setup_credits");
            var requestedRpcs = itemCount == 1 ? 1 : 256;
            var completedRpcs = 0;
            long completedItems = 0, checksum = 0;
            string? streamType = null;
            Stage = "fixed_rpc_loop";
            async Task FixedLoop()
            {
                for (var rpc = 0; rpc < requestedRpcs; rpc++)
                {
                    // No preflight, warmup, extra RPC, per-RPC counter read or drain.
                    var stream = invoke(itemCount, CancellationToken.None);
                    streamType ??= stream.GetType().FullName;
                    var count = 0;
                    long sum = 0;
                    await foreach (var value in stream.ConfigureAwait(false))
                    {
                        Check(value == count, "item_order");
                        count++; sum += value;
                    }
                    Check(count == itemCount && sum == (long)itemCount * (itemCount - 1) / 2, "item_count_checksum");
                    completedRpcs++; completedItems += count; checksum += sum;
                }
            }
            var loop = FixedLoop();
            try { await loop.WaitAsync(deadline.Token); }
            catch
            {
                // External watchdog does not inject a cancellation token into the
                // RPC. Endpoint disposal below stops work; observe any late fault.
                _ = loop.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            Check(interceptor.Observed && streamType!.Contains("+LogicalInvocationAsyncEnumerable`", StringComparison.Ordinal), "intercepted_logical_path");
            Stage = "final_quiescence";
            var flowController = Property(Field(session, "_protocolState"), "FlowController");
            var registry = Field(server, "_connectionRegistry");
            var active = (Array)(registry.GetType().GetMethod("SnapshotActive", Instance)
                ?? throw new InvalidOperationException("server_registry_snapshot_missing")).Invoke(registry, null)!;
            Check(active.Length == 1, "sole_server_session");
            var serverSession = (RpcSession)Property(active.GetValue(0)!, "Session");
            Check(serverSession.HasStreamFlowControl, "server_flow_control_required");
            var serverFlowController = Property(Field(serverSession, "_protocolState"), "FlowController");
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            drain.CancelAfter(TimeSpan.FromSeconds(15));
            Ownership ReadOwnership() => new(ReadField(client, "_activeLogicalInvocations"), Read(client, "ActiveClientCallCount"),
                Read(client, "ActiveClientStreamCount"), Read(client, "PendingCallCount"), Read(server, "ActiveCallCountForDiagnostics"), session.QueuedSendBytes);
            while (!ReadOwnership().Empty || !ReadFlow(flowController).Quiescent ||
                !ReadServerFlow(serverFlowController, serverSession).Quiescent ||
                !ArgumentsConserved(WindowUpdateCountDiagnostic.Snapshot(), requestedRpcs, itemCount))
                await Task.Delay(10, drain.Token);
            // One final marker AFTER zero queued bytes fences all credit owner/callback
            // cleanup. No per-RPC fence and no artificial credit batching window.
            await session.FlushSendQueueAsync(drain.Token);
            var beforeDisposeOwnership = ReadOwnership();
            var beforeDisposeCounts = WindowUpdateCountDiagnostic.Snapshot();
            var beforeDisposeFlowState = ReadFlow(flowController);
            var beforeDisposeServerFlowState = ReadServerFlow(serverFlowController, serverSession);
            Check(beforeDisposeOwnership.Empty && beforeDisposeFlowState.Quiescent && beforeDisposeServerFlowState.Quiescent && ArgumentsConserved(beforeDisposeCounts, requestedRpcs, itemCount), "before_dispose_conservation");
            var endReady = (Array)Field(client, "_readyConnections");
            Check(Read(client, "ReadyConnectionCount") == 1 && endReady.Length == 1 &&
                ReferenceEquals(Property(endReady.GetValue(0)!, "Session"), session), "same_ready_connection");
            Stage = "shutdown";
            await DisposeEndpoints().WaitAsync(TimeSpan.FromSeconds(10));
            disposed = true;
            var afterDisposeCounts = WindowUpdateCountDiagnostic.Snapshot();
            var afterDisposeOwnership = ReadOwnership();
            var afterDisposeFlowState = ReadFlow(flowController);
            var afterDisposeServerFlowState = ReadServerFlow(serverFlowController, serverSession);
            Check(afterDisposeOwnership.Empty && beforeDisposeFlowState == afterDisposeFlowState &&
                beforeDisposeServerFlowState == afterDisposeServerFlowState && beforeDisposeCounts == afterDisposeCounts, "post_dispose_conservation");
            Stage = "identity";
            var frameworkLocations = new[] { Canonical(typeof(object).Assembly.Location),
                Canonical(typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location) };
            var binaryHashes = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                .ToDictionary(p => Path.GetFileName(p), p => Hash(Canonical(p)));
            var inventory = new Dictionary<Assembly, AssemblyIdentity>();
            for (var round = 0; round < 16; round++)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (!assembly.IsDynamic && !inventory.ContainsKey(assembly))
                    {
                        var location = Canonical(assembly.Location);
                        inventory.Add(assembly, new(assembly.GetName().Name!, location, assembly.ManifestModule.ModuleVersionId, Hash(location)));
                    }
                var loadedAssemblies = inventory.Values.OrderBy(i => i.name, StringComparer.Ordinal).ToArray();
                Check(loadedAssemblies.Select(i => i.name).Distinct(StringComparer.Ordinal).Count() == loadedAssemblies.Length, "duplicate_loaded_name");
                var result = new
                {
                    schemaVersion = 1, diagnosticSchema = WindowUpdateCountDiagnostic.Schema, status = "pass", arm, itemCount,
                    diagnosticInstrumented = true, countersMayAffectScheduling = true,
                    receiverMeaning = "handler_completed_not_credit_granted",
                    requestedRpcs, completedRpcs, completedItems, checksum, workers = 1, mode = "static-intercepted",
                    transport = "tcp-loopback", runtimeVersion = Environment.Version.ToString(), sourceTree = metadata["SourceTree"],
                    diagnosticTree = metadata["DiagnosticTree"], diagnosticCommit = metadata["DiagnosticCommit"],
                    initialCounts, beforeDisposeCounts, afterDisposeCounts, beforeDisposeOwnership, afterDisposeOwnership,
                    beforeDisposeFlowState, afterDisposeFlowState, beforeDisposeServerFlowState, afterDisposeServerFlowState,
                    readyConnectionsBeforeDispose = 1, finalFenceCompleted = true, shutdownCompleted = true, disposeCompleted = true,
                    connectionPoolMin = 1, connectionPoolMax = 1, requestTimeout = "disabled",
                    heartbeatIntervalSeconds = 3600, heartbeatTimeoutSeconds = 7200, clientInterceptors = 1, serverInterceptors = 0,
                    channelType, streamType, interceptorObserved = interceptor.Observed, port,
                    generatedManifest = new { api = attribute.ApiVersion, protocol = attribute.ProtocolVersion,
                        generator = attribute.GeneratorVersion, abi = attribute.AbiIdentity },
                    loadedAssemblies, loadedAssemblyCount = loadedAssemblies.Length, binaryHashes, frameworkLocations,
                    scope = "instrumented_opportunity_counts_not_uninstrumented_N_or_performance"
                };
                // Serialization may load assemblies: stabilize before writing evidence.
                _ = JsonSerializer.Serialize(result, Json);
                var actual = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray();
                if (actual.Length == inventory.Count && actual.All(inventory.ContainsKey)) return result;
            }
            throw new InvalidOperationException("inventory_not_stable");
        }
        finally
        {
            if (!disposed) await DisposeEndpoints().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static ServerFlowState ReadServerFlow(object controller, RpcSession session)
    {
        if (Field(controller, "_gate") is not System.Threading.Lock gate)
            throw new InvalidOperationException("server_flow_gate_type");
        lock (gate)
        {
            return new ServerFlowState(((System.Collections.ICollection)Field(controller, "_sendStates")).Count,
                (int)Field(controller, "_activeSendStreamCount"), Read(Field(controller, "_waiters"), "Count"),
                (int)Field(controller, "_pendingSendStateWaiterCount"), (long)Field(controller, "_sendConnectionCredit"),
                (int)Field(controller, "_connectionWindow"), session.QueuedSendBytes);
        }
    }
    private sealed record ServerFlowState(int sendStates, int activeSendStreamCount, int waiters,
        int pendingSendStateWaiterCount, long sendConnectionCredit, int connectionWindow, long queuedSendBytes)
    { public bool Quiescent => sendStates == 0 && activeSendStreamCount == 0 && waiters == 0 && pendingSendStateWaiterCount == 0 && sendConnectionCredit == connectionWindow && queuedSendBytes == 0; }

    private static FlowState ReadFlow(object controller)
    {
        // Final-only inspection uses the actual System.Threading.Lock, never
        // Monitor(object). No flushing or mutation of pending credits is performed.
        if (Field(controller, "_gate") is not System.Threading.Lock gate)
            throw new InvalidOperationException("flow_gate_type");
        lock (gate)
        {
            var states = (System.Collections.ICollection)Field(controller, "_receiveStates");
            var updates = Field(controller, "_consumedCreditUpdates");
            var updateCount = updates is null ? 0 : ((System.Collections.ICollection)updates).Count;
            return new FlowState(states.Count, updateCount,
                (long)Field(controller, "_pendingConnectionConsumed"),
                (long)Field(controller, "_receiveConnectionCredit"), (int)Field(controller, "_connectionWindow"));
        }
    }
    private sealed record FlowState(int receiveStates, int consumedCreditUpdates, long pendingConnectionConsumed,
        long receiveConnectionCredit, int connectionWindow)
    { public bool Quiescent => receiveStates == 0 && consumedCreditUpdates == 0 && pendingConnectionConsumed == 0 && receiveConnectionCredit == connectionWindow; }

    private static bool ArgumentsConserved(WindowUpdateCountSnapshot s, int rpcs, int items)
        => s.InvalidCredits == 0 && s.InvalidRoles == 0 &&
           s.Client.Entered == s.Client.Accepted && s.Client.Accepted == s.Server.HandlerCompleted &&
           s.Client.EnteredCreditArgumentBytes == s.Client.AcceptedCreditArgumentBytes && s.Client.AcceptedCreditArgumentBytes == s.Server.ReceivedCreditArgumentBytes &&
           s.Client.AcceptedCreditArgumentBytes > 0 && s.Client.Accepted >= rpcs && s.Client.Accepted <= (long)rpcs * items &&
           s.Server.Entered == 0 && s.Server.Accepted == 0 && s.Client.HandlerCompleted == 0 &&
           s.Server.EnteredCreditArgumentBytes == 0 && s.Server.AcceptedCreditArgumentBytes == 0 && s.Client.ReceivedCreditArgumentBytes == 0;
    private static object Property(object owner, string name) => (owner.GetType().GetProperty(name, Instance)
        ?? throw new InvalidOperationException("missing_property_" + name)).GetValue(owner)!;
    private static object Field(object owner, string name) => (owner.GetType().GetField(name, Instance)
        ?? throw new InvalidOperationException("missing_field_" + name)).GetValue(owner)!;
    private static int Read(object owner, string name) => (int)Property(owner, name);
    private static int ReadField(object owner, string name) => (int)Field(owner, name);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Canonical(string location)
    {
        Check(Path.IsPathFullyQualified(location) && File.Exists(location), "assembly_file");
        var path = Path.GetFullPath(location);
        for (string? component = path; component is not null; component = Path.GetDirectoryName(component))
            Check((File.GetAttributes(component) & FileAttributes.ReparsePoint) == 0, "assembly_symlink");
        return path;
    }
    private static void WriteResult(string output, object result)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        Check(data.Length <= 131072, "bounded_result");
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        file.Write(data);
    }
    private sealed record Ownership(int logicalInvocations, int activeClientCalls, int activeClientStreams,
        int pendingCalls, int activeServerCalls, long queuedSendBytes)
    { public bool Empty => logicalInvocations == 0 && activeClientCalls == 0 && activeClientStreams == 0 && pendingCalls == 0 && activeServerCalls == 0 && queuedSendBytes == 0; }
    private sealed record AssemblyIdentity(string name, string location, Guid mvid, string sha256);
    private sealed class PassThrough : ISharpLinkClientInterceptor
    {
        public bool Observed;
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        { Observed = true; return next(context); }
    }
}
