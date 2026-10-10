using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace Issue739Net11;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length is not (7 or 8)) throw new ArgumentException("transport rpc operations warmup sample output expected-runtime [concurrency]");
        string transport = args[0], kind = args[1];
        int operations = int.Parse(args[2]), warmup = int.Parse(args[3]);
        int concurrency = args.Length == 8 ? int.Parse(args[7]) : 1;
        if (transport is not ("tcp" or "shm") || kind is not ("add" or "control") || operations < 1 || operations > 1048576 || warmup < 1 || warmup > 65536)
            throw new ArgumentException("Invalid bounded shape");
        if (concurrency is not (1 or 32 or 128) || operations % concurrency != 0 || warmup % concurrency != 0)
            throw new ArgumentException("Invalid concurrency/count divisibility");
        if (Environment.Version.ToString() != args[6]) throw new InvalidOperationException("Runtime mismatch");
        string sourceSha = Environment.GetEnvironmentVariable("ISSUE739_SOURCE_SHA") ?? "UNVERIFIED";
        if (sourceSha != "eb99fe887cf2129d9b88441245ca0a4a6406b6c2") throw new InvalidOperationException("Source mismatch");
        if (!ThreadPool.SetMinThreads(132, 132)) throw new InvalidOperationException("Thread-pool minimum rejected");
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        if (minimumWorkers != 132 || minimumIo != 132) throw new InvalidOperationException("Thread-pool minimum mismatch");
        string name = "issue739-net11-" + Guid.NewGuid().ToString("N");
        IServerTransportListener listener = transport == "tcp"
            ? new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0))
            : new SharedMemoryServerTransportListener(name);
        IClientTransportFactory factory = transport == "tcp"
            ? new SocketClientTransportFactory(listener.LocalEndPoint!) : new SharedMemoryClientTransportFactory(name);
        var service = new TinyService();
        await using var server = SharpLinkServerBuilder.Create().UseTransport(listener)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .AllowUnencrypted().AllowUnauthenticated().ReplaceService<ITiny>(service).Build();
        await server.StartAsync();
        var serverTask = server.WaitForShutdownAsync();
        var builder = SharpClientBuilder.Create().UseTransport(factory)
            .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; })
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)).DisableRequestTimeout();
        await using var client = builder.Build();
        await client.ConnectAsync();
        var rpc = client.Get<ITiny>();
        var concreteServer = (SharpLinkServer)server;
        var latency = new long[operations];
        long checks = 0;
        var warmupWorkers = new Task[concurrency];
        for (int i = 0; i < concurrency; i++) warmupWorkers[i] = Worker(i, warmup, false);
        await Task.WhenAll(warmupWorkers);
        await Drain();
        _ = Process.GetCurrentProcess().TotalProcessorTime;
        _ = GC.GetTotalAllocatedBytes(precise: true);
        await Task.Delay(1000);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        using var start = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(concurrency);
        checks = 0;
        long receivedStart = service.Received;
        var workers = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            int index = i;
            workers[i] = Task.Run(async () => { ready.Signal(); start.Wait(); await Worker(index, operations, true); });
        }
        ready.Wait();
        var allWorkers = Task.WhenAll(workers);
        var cpuStart = process.TotalProcessorTime;
        int gen0Start = GC.CollectionCount(0);
        long bytesStart = GC.GetTotalAllocatedBytes(precise: true), ticksStart = Stopwatch.GetTimestamp();
        start.Set();
        await allWorkers;
        await Drain();
        long ticksEnd = Stopwatch.GetTimestamp(), bytesEnd = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0) - gen0Start;
        double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        long received = service.Received - receivedStart;
        long expectedReceived = kind == "add" ? operations + 1L : 1L;
        if (checks != operations || received != expectedReceived || concreteServer.ActiveCallCountForDiagnostics != 0)
            throw new InvalidOperationException("Exact operation/receive/quiescence mismatch");
        Array.Sort(latency);
        var result = new
        {
            schemaVersion = 1, processId = Environment.ProcessId, sample = args[4], sourceSha,
            transport, kind, concurrency, operations, warmup, checks, received, expectedReceived,
            runtime = Environment.Version.ToString(), framework = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), os = RuntimeInformation.OSDescription,
            serverGc = System.Runtime.GCSettings.IsServerGC, processorCount = Environment.ProcessorCount,
            bytes = bytesEnd - bytesStart, bytesPerOperation = (bytesEnd - bytesStart) / (double)operations,
            gen0, cpuMilliseconds = cpuMs, cpuNanosecondsPerOperation = cpuMs * 1e6 / operations,
            elapsedSeconds = (ticksEnd - ticksStart) / (double)Stopwatch.Frequency,
            qps = operations * (double)Stopwatch.Frequency / (ticksEnd - ticksStart),
            p50Nanoseconds = latency[(operations - 1) / 2] * 1e9 / Stopwatch.Frequency,
            p99Nanoseconds = latency[(int)Math.Ceiling(operations * .99) - 1] * 1e9 / Stopwatch.Frequency,
            ticksStart, ticksEnd, stopwatchFrequency = Stopwatch.Frequency,
            precise = true, driverIncluded = true, subtractionApplied = false,
            interpretation = "combined client/server gross managed bytes; repeatability determined by stage runner; no cross-runtime or nonregression inference",
            fixture = "Add-only; NonCancellable; synchronous ValueTask result; Interlocked receive counter in both variants",
            boundary = "includes sentinel and fixed20ms tail; admitted-zero is not formal dispatcher cleanup fence; raw controls retained",
            connectionCount = 1, threadPoolMinimumWorkers = minimumWorkers, threadPoolMinimumIo = minimumIo,
            diagnostic = false, traceEnabled = false,
            executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))).ToLowerInvariant()
        };
        await File.WriteAllTextAsync(args[5], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        await client.StopAsync(); await server.StopAsync(TimeSpan.Zero);
        try { await serverTask; } catch (OperationCanceledException) { }

        async Task Worker(int worker, int count, bool measure)
        {
            for (int i = worker; i < count; i += concurrency)
            {
                long begin = measure ? Stopwatch.GetTimestamp() : 0;
                if (kind == "add")
                {
                    if (await rpc.AddAsync(20, 22).ConfigureAwait(false) != 42) throw new InvalidOperationException("Bad Add");
                }
                else await ValueTask.CompletedTask;
                if (measure) { latency[i] = Stopwatch.GetTimestamp() - begin; if (concurrency == 1) checks++; else Interlocked.Increment(ref checks); }
            }
        }
        async Task Drain()
        {
            if (await rpc.AddAsync(20, 22).ConfigureAwait(false) != 42) throw new InvalidOperationException("Bad sentinel");
            long deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            var spin = new SpinWait();
            while (concreteServer.ActiveCallCountForDiagnostics != 0)
            {
                if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("Server did not quiesce");
                spin.SpinOnce();
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }
}
