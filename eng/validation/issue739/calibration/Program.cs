using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace Issue739Calibration;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length != 6) throw new ArgumentException("case operations warmup sample output expected-runtime");
        string kind = args[0]; int operations = int.Parse(args[1]), warmup = int.Parse(args[2]);
        if (operations < 1 || operations > 1048576 || warmup < 1 || warmup > 65536) throw new ArgumentException("Bounded counts required");
        if (Environment.Version.ToString() != args[5]) throw new InvalidOperationException("Runtime version mismatch");
        string sourceSha = Environment.GetEnvironmentVariable("ISSUE739_SOURCE_SHA") ?? "UNVERIFIED";
        if (sourceSha != "eb99fe887cf2129d9b88441245ca0a4a6406b6c2") throw new InvalidOperationException("Source baseline mismatch");
        Action<int> run;
        Action<int> prepare = _ => { };
        Action verify = () => { };
        IAsyncDisposable? cleanup = null;
        long checks = 0, startedIncomplete = 0, completed = 0;
        var details = new Dictionary<string, object?>();
        if (kind.StartsWith("logical-", StringComparison.Ordinal))
        {
            var client = (SharpLinkClient)SharpClientBuilder.Create().UseTransport(new NeverConnectFactory())
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).DisableRequestTimeout().Build();
            cleanup = client;
            MethodInfo method = typeof(SharpLinkClient).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(m => m.Name == "AwaitLogicalInvocationAsync" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(int));
            var invoke = method.CreateDelegate<Func<ValueTask<int>, ValueTask<int>>>(client);
            FieldInfo active = typeof(SharpLinkClient).GetField("_activeLogicalInvocations", BindingFlags.NonPublic | BindingFlags.Instance)!;
            TaskCompletionSource<int>[] sources = [];
            bool incomplete = kind != "logical-completed-wrapper";
            bool wrapper = kind != "logical-completion-control";
            if (kind is not ("logical-incomplete-wrapper" or "logical-completed-wrapper" or "logical-completion-control")) throw new ArgumentException("Unknown logical case");
            prepare = count =>
            {
                active.SetValue(client, wrapper ? count : 0);
                sources = incomplete ? new TaskCompletionSource<int>[count] : [];
                for (int i = 0; i < sources.Length; i++) sources[i] = new TaskCompletionSource<int>();
            };
            run = count =>
            {
                for (int i = 0; i < count; i++)
                {
                    if (incomplete)
                    {
                        var input = new ValueTask<int>(sources[i].Task);
                        if (input.IsCompleted) throw new InvalidOperationException("Input must begin incomplete");
                        startedIncomplete++;
                        var output = wrapper ? invoke(input) : input;
                        if (output.IsCompleted) throw new InvalidOperationException("Output must begin incomplete");
                        sources[i].SetResult(42);
                        if (!output.IsCompletedSuccessfully || output.GetAwaiter().GetResult() != 42) throw new InvalidOperationException("Inline completion failed");
                    }
                    else if (invoke(ValueTask.FromResult(42)).GetAwaiter().GetResult() != 42) throw new InvalidOperationException("Bad completed wrapper");
                    checks++; completed++;
                }
            };
            verify = () => { if ((int)active.GetValue(client)! != 0) throw new InvalidOperationException("Logical active count not zero"); };
            details["privateMethod"] = method.ToString();
            details["driver"] = "TCS array/tasks and closed delegate precreated; default inline TCS continuation; SetResult/GetResult/checks included; active logical count initialized outside interval";
            details["scope"] = "actual private wrapper, not full logical admission or RPC; forced incomplete input is a shape control";
        }
        else if (kind is "permit-plain" or "permit-capacity-control")
        {
            var owner = await PermitEnvironment.Create(); cleanup = owner;
            bool permitCase = kind == "permit-plain";
            run = count =>
            {
                for (int i = 0; i < count; i++)
                {
                    if (permitCase)
                    {
                        if (owner.Server.TryReserveCall(owner.Connection, mayDecode: false, out var permit) != ServerCallAdmissionResult.Acquired || permit is null)
                            throw new InvalidOperationException("Reserve failed");
                        if (!permit.IsReserved || owner.Connection.ActiveCalls != 1 || owner.Server.ActiveCallCountForDiagnostics != 1)
                            throw new InvalidOperationException("Reserve accounting failed");
                        permit.Activate();
                        if (!permit.IsActive) throw new InvalidOperationException("Activate failed");
                        permit.Dispose();
                    }
                    else
                    {
                        if (owner.Server.TryAcquireCall(owner.Connection) != ServerCallAdmissionResult.Acquired)
                            throw new InvalidOperationException("Acquire failed");
                        owner.Server.ReleaseCall(owner.Connection);
                    }
                    if (owner.Connection.ActiveCalls != 0 || owner.Server.ActiveCallCountForDiagnostics != 0)
                        throw new InvalidOperationException("Permit leaked");
                    checks++; completed++;
                }
            };
            verify = () => owner.Connection.AssertStateInvariant();
            details["driver"] = "actual running idle server; standalone ready in-memory session/connection; capacity checks included; no socket or wire dispatch";
            details["scope"] = "one accepted plain admission reserve/activate/dispose per operation; no decode gate, no lifecycle field spoof";
        }
        else if (kind.StartsWith("context-", StringComparison.Ordinal))
        {
            var snapshot = new SharpLinkCallContextSnapshot("precreated-calibration", null);
            var flowState = new FlowState(kind == "context-flow-null-control" ? null : snapshot);
            prepare = _ => flowState.Checks = 0;
            var context = ExecutionContext.Capture() ?? throw new InvalidOperationException("Flow suppressed");
            ContextCallback callback = static state =>
            {
                var data = (FlowState)state!;
                using (SharpLinkCallContext.Push(data.Snapshot))
                {
                    if (!ReferenceEquals(SharpLinkCallContext.Current, data.Snapshot)) throw new InvalidOperationException("Context not visible");
                    data.Checks++;
                }
                if (SharpLinkCallContext.Current is not null) throw new InvalidOperationException("Context not restored");
            };
            bool same = kind == "context-same-snapshot";
            bool empty = kind == "context-null-control";
            bool flow = kind is "context-flow-transition" or "context-flow-null-control";
            if (!same && !empty && !flow && kind != "context-null-transition") throw new ArgumentException("Unknown context case");
            run = count =>
            {
                if (SharpLinkCallContext.Current is not null) throw new InvalidOperationException("Dirty baseline context");
                // Same-snapshot outer installation is a separately counted fixed setup cost, not N new snapshots.
                var outer = same ? SharpLinkCallContext.Push(snapshot) : default;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (flow) ExecutionContext.Run(context, callback, flowState);
                        else
                        {
                            using (SharpLinkCallContext.Push(empty ? null : snapshot))
                            {
                                if (!ReferenceEquals(SharpLinkCallContext.Current, empty ? null : snapshot)) throw new InvalidOperationException("Context mismatch");
                            }
                            if (!ReferenceEquals(SharpLinkCallContext.Current, same ? snapshot : null)) throw new InvalidOperationException("Restoration mismatch");
                        }
                        checks++; completed++;
                    }
                }
                finally { if (same) outer.Dispose(); }
                if (SharpLinkCallContext.Current is not null) throw new InvalidOperationException("Leaked context");
            };
            verify = () => { if (SharpLinkCallContext.Current is not null) throw new InvalidOperationException("Context not reset"); details["flowCallbackChecks"] = flowState.Checks; };
            details["driver"] = "snapshot, FlowState, callback, and ExecutionContext captured before interval; ExecutionContext.Run dispatch included only in flow case";
            details["sameSnapshotBoundary"] = "one outer Push/Dispose inside interval is fixed setup, explicitly not hidden or subtracted";
            details["scope"] = "direct AsyncLocal context shapes; no claim to reproduce full async RPC flow or multiplicity";
        }
        else throw new ArgumentException("Unknown calibration case");

        prepare(warmup); run(warmup); verify();
        prepare(operations); checks = startedIncomplete = completed = 0;
        _ = GC.GetTotalAllocatedBytes(true); _ = GC.GetAllocatedBytesForCurrentThread();
        Thread.Sleep(1000);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long currentStart = GC.GetAllocatedBytesForCurrentThread(), totalStart = GC.GetTotalAllocatedBytes(true);
        int threadStart = Environment.CurrentManagedThreadId;
        long ticksStart = Stopwatch.GetTimestamp();
        run(operations);
        long ticksEnd = Stopwatch.GetTimestamp(), totalEnd = GC.GetTotalAllocatedBytes(true), currentEnd = GC.GetAllocatedBytesForCurrentThread();
        int threadEnd = Environment.CurrentManagedThreadId;
        verify();
        if (checks != operations || completed != operations || threadStart != threadEnd) throw new InvalidOperationException("Operation/thread count mismatch");
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        var result = new
        {
            schemaVersion = 1, kind, sample = args[3], processId = Environment.ProcessId, sourceSha,
            runtime = Environment.Version.ToString(), expectedRuntime = args[5], framework = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), os = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount, serverGc = System.Runtime.GCSettings.IsServerGC, minimumWorkers, minimumIo,
            tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "runtime-default",
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
            readyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "runtime-default",
            operations, warmup, checks, completed, startedIncomplete,
            bytes = totalEnd - totalStart, bytesPerOperation = (totalEnd - totalStart) / (double)operations,
            currentThreadBytes = currentEnd - currentStart, currentThreadBytesPerOperation = (currentEnd - currentStart) / (double)operations,
            threadStart, threadEnd, elapsedSeconds = (ticksEnd - ticksStart) / (double)Stopwatch.Frequency,
            precise = true, driverIncluded = true, subtractionApplied = false, interpretation = "source-shape microcalibration only; no end-to-end multiplicity or owner90 closure",
            executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))).ToLowerInvariant(),
            details
        };
        await File.WriteAllTextAsync(args[4], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (cleanup is not null) await cleanup.DisposeAsync();
    }

    private sealed class FlowState(SharpLinkCallContextSnapshot? snapshot)
    {
        internal SharpLinkCallContextSnapshot? Snapshot { get; } = snapshot;
        internal long Checks;
    }
    private sealed class NeverConnectFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Calibration must not connect");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class MemoryTransport : ITransportConnection
    {
        private readonly Pipe _input = new(); private readonly Pipe _output = new();
        public string Id => "calibration-memory";
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public EndPoint? LocalEndPoint => null; public EndPoint? RemoteEndPoint => null;
        public async ValueTask DisposeAsync() { await Input.CompleteAsync(); await Output.CompleteAsync(); }
    }
    private sealed class PermitEnvironment(SharpLinkServer server, ServerConnectionState connection, RpcSession session) : IAsyncDisposable
    {
        internal SharpLinkServer Server { get; } = server;
        internal ServerConnectionState Connection { get; } = connection;
        internal static async Task<PermitEnvironment> Create()
        {
            var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseTransport(new IdleListener()).Build();
            await server.StartAsync();
            if (!server.IsRunningForCallAdmission) throw new InvalidOperationException("Server did not start");
            var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
            var session = new RpcSession(new MemoryTransport(), new RpcSessionCreationOptions(RpcSessionRole.Server, runtime));
            var options = new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion, ProtocolV2Capabilities.None,
                runtime.Protocol.MaxFramePayloadBytes, runtime.FlowControl.StreamReceiveWindowBytes, runtime.FlowControl.ConnectionReceiveWindowBytes, null);
            if (!session.TryCompleteHandshake(options)) throw new InvalidOperationException("Session handshake setup failed");
            var connection = new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
                new StripedLongMap<ServerCallCancellationState>(), CancellationToken.None, TimeProvider.System);
            if (!connection.MarkReady(null)) throw new InvalidOperationException("Connection not ready");
            return new PermitEnvironment(server, connection, session);
        }
        public async ValueTask DisposeAsync()
        {
            await Connection.CloseAsync(); await Connection.ServiceCleanupTask;
            await session.DisposeAsync(); await Server.StopAsync(TimeSpan.Zero);
        }
    }
}
