using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace Issue739.Features;
internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length != 11) throw new ArgumentException("transport mode concurrency calls warmup sample output feature items producer control");
        string transport = args[0], mode = args[1], sample = args[5], feature = args[7], producer = args[9], control = args[10];
        int concurrency = int.Parse(args[2]), calls = int.Parse(args[3]), warmup = int.Parse(args[4]), items = int.Parse(args[8]);
        if (transport is not ("tcp" or "shm") || mode is not ("add" or "cancellable" or "echo" or "upload" or "download" or "duplex" or "oneway-stream") || concurrency is not (1 or 32) || calls < concurrency || calls % concurrency != 0 || warmup < concurrency || warmup % concurrency != 0 || items < 1 || producer is not ("sync" or "yield") || control is not ("rpc" or "fixture" or "idle")) throw new ArgumentException("Invalid matrix cell");
        if (feature is not ("none" or "client-interceptor" or "server-interceptor" or "both-interceptors" or "allocating-plugin" or "metrics" or "tracing" or "context-read" or "authentication" or "deadline" or "cancellation" or "compression" or "common")) throw new ArgumentException("Unknown feature");
        if ((feature is "deadline" or "cancellation") && mode != "cancellable") throw new ArgumentException("Needs matched cancellable contract");
        if (feature == "compression" && mode != "echo") throw new ArgumentException("Compression requires matched 4096-byte echo payload");
        if (control != "rpc" && feature != "none") throw new ArgumentException("Controls must be feature=none; they diagnose fixture overhead, not corrected framework costs");
        ThreadPool.SetMinThreads(132, 132);
        bool useClientHook = feature is "client-interceptor" or "both-interceptors" or "allocating-plugin" or "common";
        bool useServerHook = feature is "server-interceptor" or "both-interceptors" or "common";
        bool metrics = feature is "metrics" or "common", tracing = feature == "tracing";
        bool auth = feature is "authentication" or "common";
        var service = new FeatureService { Suspended = producer == "yield", CheckContext = feature == "context-read", CheckAuthentication = auth, CheckDeadline = feature == "deadline", CheckCancellation = feature == "cancellation" };
        var clientHook = new ClientHook(feature == "allocating-plugin"); var serverHook = new ServerHook();
        using var telemetry = new TelemetryScope(metrics, tracing);
        var clientCodec = new CountingZstd(); var serverCodec = new CountingZstd();
        long clientAuthentications = 0, serverAuthentications = 0;
        var identity = new SharpLinkAuthenticationContext(subject: "issue739-fixture");
        var name = "issue739-features-" + Guid.NewGuid().ToString("N");
        IServerTransportListener listener = transport == "tcp" ? new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0)) : new SharedMemoryServerTransportListener(name);
        IClientTransportFactory factory = transport == "tcp" ? new SocketClientTransportFactory(listener.LocalEndPoint!) : new SharedMemoryClientTransportFactory(name);
        var serverBuilder = SharpLinkServerBuilder.Create().UseTransport(listener).UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)).AllowUnencrypted().ReplaceService<IFeatureRpc>(service);
        if (auth) serverBuilder.RequireAuthentication().UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) => { Interlocked.Increment(ref serverAuthentications); if (!request.Payload.Span.SequenceEqual("fixture"u8)) throw new InvalidOperationException("Bad auth fixture"); return ValueTask.FromResult(SharpLinkAuthenticationResult.Authenticate(identity)); }));
        else serverBuilder.AllowUnauthenticated();
        if (useServerHook) serverBuilder.AddInterceptor(serverHook);
        if (feature == "compression") serverBuilder.UseRuntime(o => o.Compression.Providers.Add(serverCodec));
        await using var server = serverBuilder.Build();
        using var shutdown = new CancellationTokenSource();
        await server.StartAsync();
        var serverTask = server.WaitForShutdownAsync();
        var clientBuilder = SharpClientBuilder.Create().UseTransport(factory).UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; }).UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        if (feature == "deadline") clientBuilder.UseRequestTimeout(TimeSpan.FromSeconds(30)); else clientBuilder.DisableRequestTimeout();
        if (auth) clientBuilder.UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => { Interlocked.Increment(ref clientAuthentications); return ValueTask.FromResult<ReadOnlyMemory<byte>>("fixture"u8.ToArray()); }));
        if (useClientHook) clientBuilder.AddInterceptor(clientHook);
        if (feature == "compression") clientBuilder.UseRuntime(o => o.Compression.Providers.Add(clientCodec));
        await using var client = clientBuilder.Build();
        await client.ConnectAsync();
        var wireRpc = client.Get<IFeatureRpc>();
        IFeatureRpc rpc = control == "fixture" ? service : wireRpc;
        var concreteServer = (SharpLinkServer)server;
        var input = new IntegerProducer(items, producer == "yield");
        var payload = new byte[4096]; Array.Fill(payload, (byte)42);
        using var callerCancellation = new CancellationTokenSource();
        CancellationToken callerToken = feature == "cancellation" ? callerCancellation.Token : CancellationToken.None;
        var latency = new long[calls];
        long clientReceivedItems = 0, successfulCalls = 0;
        long warmupCompletedStart = service.CompletedCalls;
        await Run(warmup, false); await Drain(warmupCompletedStart + (control == "idle" ? 0 : warmup));
        if (control == "rpc")
        {
            if (useClientHook && clientHook.Calls < warmup) throw new InvalidOperationException("Client hook inactive");
            if (useServerHook && serverHook.Calls < warmup) throw new InvalidOperationException("Server hook inactive");
            if (metrics && telemetry.Measurements == 0) throw new InvalidOperationException("Meter listener inactive");
            if (tracing && (telemetry.ClientActivities == 0 || telemetry.ServerActivities == 0)) throw new InvalidOperationException("Activity listener inactive");
            if (auth && (clientAuthentications != 1 || serverAuthentications != 1 || service.Observations < warmup)) throw new InvalidOperationException("Authentication/context inactive");
            if (feature == "compression" && (clientCodec.Compressed == 0 || serverCodec.Compressed == 0 || clientCodec.Decoded == 0 || serverCodec.Decoded == 0)) throw new InvalidOperationException("Compression inactive");
        }
        var activation = new { clientHooks = clientHook.Calls, serverHooks = serverHook.Calls, metrics = telemetry.Measurements, clientActivities = telemetry.ClientActivities, serverActivities = telemetry.ServerActivities, clientAuthentications, serverAuthentications, contextObservations = service.Observations, clientCompressed = clientCodec.Compressed, serverCompressed = serverCodec.Compressed, clientDecoded = clientCodec.Decoded, serverDecoded = serverCodec.Decoded };
        clientHook.Observe = serverHook.Observe = telemetry.Observe = clientCodec.Observe = serverCodec.Observe = false;
        // Activation assertions stay outside the measurement. Same service completion/item counters in every cell.
        service.CheckContext = service.CheckAuthentication = service.CheckDeadline = service.CheckCancellation = false;
        await Task.Delay(1000); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); using var start = new ManualResetEventSlim(false); using var ready = new CountdownEvent(concurrency);
        var workers = new Task[concurrency];
        for (int worker = 0; worker < concurrency; worker++) { int index = worker; workers[worker] = Task.Run(async () => { ready.Signal(); start.Wait(); await Worker(index, calls, true); }); }
        ready.Wait(); var all = Task.WhenAll(workers);
        long completedStart = service.CompletedCalls, inputStart = service.InputItems, outputStart = service.OutputItems, receivedStart = clientReceivedItems, successStart = successfulCalls;
        var cpuStart = process.TotalProcessorTime; int gen0Start = GC.CollectionCount(0);
        long bytesStart = GC.GetTotalAllocatedBytes(precise: true), ticksStart = Stopwatch.GetTimestamp();
        start.Set(); await all; long workloadEndTicks = Stopwatch.GetTimestamp();
        await Drain(completedStart + (control == "idle" ? 0 : calls));
        long ticksEnd = Stopwatch.GetTimestamp(), bytesEnd = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0) - gen0Start; double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        long completed = service.CompletedCalls - completedStart, sentItems = service.InputItems - inputStart, outputItems = service.OutputItems - outputStart, receivedItems = clientReceivedItems - receivedStart;
        if (successfulCalls - successStart != calls || (control != "idle" && completed != calls)) throw new InvalidOperationException("Call mismatch");
        long expectedInput = control != "idle" && mode is "upload" or "duplex" or "oneway-stream" ? (long)calls * items : 0;
        long expectedOutput = control != "idle" && mode is "download" or "duplex" ? (long)calls * items : 0;
        if (sentItems != expectedInput || outputItems != expectedOutput || receivedItems != expectedOutput) throw new InvalidOperationException("Item count mismatch");
        Array.Sort(latency);
        var result = new {
            schemaVersion = 1, processId = Environment.ProcessId, processStartUtc = process.StartTime.ToUniversalTime().ToString("O"),
            tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "runtime-default",
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
            sourceSha = Environment.GetEnvironmentVariable("ISSUE739_SOURCE_SHA") ?? "UNVERIFIED", sample, transport, mode, concurrency, calls, warmup, feature, items, producer, control,
            runtime = Environment.Version.ToString(), framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount, serverGc = System.Runtime.GCSettings.IsServerGC,
            bytes = bytesEnd - bytesStart, bytesPerCall = (bytesEnd - bytesStart) / (double)calls,
            bytesPerInputItem = sentItems == 0 ? (double?)null : (bytesEnd - bytesStart) / (double)sentItems,
            bytesPerOutputItem = outputItems == 0 ? (double?)null : (bytesEnd - bytesStart) / (double)outputItems,
            bytesPerTotalItem = sentItems + outputItems == 0 ? (double?)null : (bytesEnd - bytesStart) / (double)(sentItems + outputItems),
            completedCalls = completed, clientCompletedCalls = successfulCalls - successStart, serverReceivedInputItems = sentItems, serverProducedOutputItems = outputItems, clientReceivedOutputItems = receivedItems,
            workloadSeconds = (workloadEndTicks - ticksStart) / (double)Stopwatch.Frequency,
            drainAndTailSeconds = (ticksEnd - workloadEndTicks) / (double)Stopwatch.Frequency,
            timingMeaning = "latency is per-call client completion; aggregate includes fixed20ms tail and cannot be read as pure transport throughput",
            objectsPerCall = (double?)null, objectsStatus = "unavailable-without-profiler", gen0, cpuMilliseconds = cpuMs, elapsedSeconds = (ticksEnd - ticksStart) / (double)Stopwatch.Frequency,
            p50Nanoseconds = latency[(calls - 1) / 2] * 1e9 / Stopwatch.Frequency, p99Nanoseconds = latency[(int)Math.Ceiling(calls * .99) - 1] * 1e9 / Stopwatch.Frequency,
            activation, measurement = "combined-client-server-all-thread-GC.GetTotalAllocatedBytes(precise:true); gross fixture-inclusive; no subtraction; connection/auth/setup/disposal outside; bounded20ms-tail-inside",
            tailWindowMilliseconds = 20, minWorkerThreads = 132, minIoThreads = 132,
            drain = "wire sentinel, expected service completion count, ActiveCallCount==0, fixed20ms tail then assert inactive; bounded observation not proof all transport/AsyncLocal cleanup completed",
            latencyMeaning = control != "rpc" ? "direct fixture/idle worker completion" : mode == "oneway-stream" ? "client-send-completion; receiver completion and tail inside aggregate totals" : "fully consumed successful call",
            contextReadMeaning = feature == "context-read" ? "activation-only existing context observation; no separate context-enable setting; measured same as none" : "not-applicable",
            cancellationMeaning = feature == "cancellation" ? "precreated caller token, never cancelled; successful cancellable contract only" : "no cancellation exercised"
        };
        await File.WriteAllTextAsync(args[6], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        await client.StopAsync(); await server.StopAsync(TimeSpan.Zero); shutdown.Cancel(); try { await serverTask; } catch (OperationCanceledException) { }

        async Task Worker(int worker, int count, bool measure)
        {
            for (int i = worker; i < count; i += concurrency)
            {
                long begin = measure ? Stopwatch.GetTimestamp() : 0;
                if (control != "idle") switch (mode)
                {
                    case "add": if (await rpc.AddAsync(20, 22).ConfigureAwait(false) != 42) throw new InvalidOperationException(); break;
                    case "cancellable": if (await rpc.CancellableAsync(20, 22, callerToken).ConfigureAwait(false) != 42) throw new InvalidOperationException(); break;
                    case "echo": if (!(await rpc.EchoAsync(payload).ConfigureAwait(false)).AsSpan().SequenceEqual(payload)) throw new InvalidOperationException(); break;
                    case "upload": if (await rpc.UploadAsync(input).ConfigureAwait(false) != items * (items - 1) / 2) throw new InvalidOperationException(); break;
                    case "oneway-stream": await rpc.NotifyAsync(input).ConfigureAwait(false); break;
                    case "download": await Consume(rpc.DownloadAsync(items)); break;
                    case "duplex": await Consume(rpc.DuplexAsync(input)); break;
                }
                Interlocked.Increment(ref successfulCalls);
                if (measure) latency[i] = Stopwatch.GetTimestamp() - begin;
            }
        }
        async Task Consume(IAsyncEnumerable<int> values)
        {
            int count = 0;
            await foreach (int value in values.ConfigureAwait(false)) { if (value != count++) throw new InvalidOperationException("Bad stream payload/order"); }
            if (count != items) throw new InvalidOperationException("Bad stream length");
            Interlocked.Add(ref clientReceivedItems, count);
        }
        async Task Run(int count, bool measure) { var tasks = new Task[concurrency]; for (int i = 0; i < concurrency; i++) tasks[i] = Worker(i, count, measure); await Task.WhenAll(tasks); }
        async Task Drain(long expectedCompleted)
        {
            if (await wireRpc.FenceAsync().ConfigureAwait(false) != 42) throw new InvalidOperationException("Bad wire fence");
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            var spin = new SpinWait();
            while (Interlocked.Read(ref service.CompletedCalls) < expectedCompleted || concreteServer.ActiveCallCountForDiagnostics != 0)
            { if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("Did not drain"); spin.SpinOnce(); }
            await Task.Delay(20);
            if (concreteServer.ActiveCallCountForDiagnostics != 0 || service.CompletedCalls != expectedCompleted) throw new InvalidOperationException("Late or excess completion");
        }
    }
}
