using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace Issue739;

[RpcContract]
public interface ITiny : IService
{
    [NonCancellable] ValueTask<int> AddAsync(int left, int right);
    [Oneway, NonCancellable] ValueTask PublishAsync(int value);
}

[RpcService]
public sealed class TinyService : ITiny
{
    private long _received;
    internal ReceiveCredits? Credits { get; set; }
    public long Received => Interlocked.Read(ref _received);
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);
    public ValueTask PublishAsync(int value)
    {
        if (value != 42) throw new InvalidOperationException("Corrupt OneWay value");
        Interlocked.Increment(ref _received);
        Credits?.ReleaseAfterReceive();
        return ValueTask.CompletedTask;
    }
}

[EventSource(Name = "SharpLink-Issue739")]
internal sealed class Markers : EventSource
{
    internal static readonly Markers Log = new();
    [Event(1)] public void Start(string sample) => WriteEvent(1, sample);
    [Event(2)] public void Stop(string sample, long bytes, long operations) => WriteEvent(2, sample, bytes, operations);
}

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length != 7) throw new ArgumentException("transport rpc concurrency operations warmup sample output");
        var transport = args[0]; var rpcKind = args[1];
        if (transport is not ("tcp" or "shm") || rpcKind is not ("add" or "oneway" or "control" or "oneway-bounded" or "credit-control")) throw new ArgumentException("Unknown shape");
        int concurrency = int.Parse(args[2]), operations = int.Parse(args[3]), warmup = int.Parse(args[4]);
        if (concurrency is not (1 or 8 or 32 or 128) || operations < concurrency || operations % concurrency != 0 || warmup % concurrency != 0) throw new ArgumentException("Invalid counts");
        ThreadPool.SetMinThreads(132, 132);
        var name = "issue739-" + Guid.NewGuid().ToString("N");
        IServerTransportListener listener = transport == "tcp" ? new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0)) : new SharedMemoryServerTransportListener(name);
        IClientTransportFactory factory = transport == "tcp" ? new SocketClientTransportFactory(listener.LocalEndPoint!) : new SharedMemoryClientTransportFactory(name);
        bool diagnostic = Environment.GetEnvironmentVariable("ISSUE739_DIAGNOSTIC") == "1";
        var clientReads = diagnostic ? new ReadDiagnostics() : null;
        var serverReads = diagnostic ? new ReadDiagnostics() : null;
        if (diagnostic) { factory = clientReads!.Wrap(factory); listener = serverReads!.Wrap(listener); }
        bool bounded = rpcKind is "oneway-bounded" or "credit-control";
        using var credits = bounded ? new ReceiveCredits(concurrency) : null;
        var service = new TinyService { Credits = credits };
        await using var server = SharpLinkServerBuilder.Create().UseTransport(listener)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .AllowUnencrypted().AllowUnauthenticated().ReplaceService<ITiny>(service).Build();
        using var shutdown = new CancellationTokenSource();
        await server.StartAsync();
        var serverTask = server.WaitForShutdownAsync();
        var clientBuilder = SharpClientBuilder.Create().UseTransport(factory)
            .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; })
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        clientBuilder.DisableRequestTimeout();
        await using var client = clientBuilder.Build();
        await client.ConnectAsync();
        var rpc = client.Get<ITiny>();
        var concreteServer = (SharpLinkServer)server;
        var latency = new long[operations];
        var creditLatency = bounded ? new long[operations] : null;
        await Run(warmup, false);
        await Drain();
        // Pre-JIT control and reporting paths, then allow tiering to settle before the measured window.
        _ = Process.GetCurrentProcess().TotalProcessorTime;
        _ = GC.GetTotalAllocatedBytes(precise: true);
        await Task.Delay(1000);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        using var start = new ManualResetEventSlim(false);
        var ready = new CountdownEvent(concurrency);
        var workers = new Task[concurrency];
        long receivedStart = service.Received;
        long acquiredStart = credits?.Acquired ?? 0, releasedStart = credits?.Released ?? 0, incompleteWaitsStart = credits?.IncompleteWaits ?? 0;
        credits?.ResetPeak();
        for (int worker = 0; worker < concurrency; worker++)
        {
            int index = worker;
            workers[worker] = Task.Run(async () => { ready.Signal(); start.Wait(); await Worker(index, operations, true); });
        }
        ready.Wait();
        var all = Task.WhenAll(workers);
        long clientSyncStart = clientReads?.Sync ?? 0, clientIncompleteStart = clientReads?.Incomplete ?? 0;
        long serverSyncStart = serverReads?.Sync ?? 0, serverIncompleteStart = serverReads?.Incomplete ?? 0;
        Markers.Log.Start(args[5]);
        var cpuStart = process.TotalProcessorTime;
        int gen0Start = GC.CollectionCount(0);
        long bytesStart = GC.GetTotalAllocatedBytes(precise: true), ticksStart = Stopwatch.GetTimestamp();
        start.Set();
        await all;
        await Drain();
        long ticksEnd = Stopwatch.GetTimestamp(), bytesEnd = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0) - gen0Start;
        double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        Markers.Log.Stop(args[5], bytesEnd - bytesStart, operations);
        if (rpcKind is "oneway" or "oneway-bounded" or "credit-control" && service.Received - receivedStart != operations) throw new InvalidOperationException("Receive count mismatch");
        if (credits is not null && (credits.Acquired-acquiredStart != operations || credits.Released-releasedStart != operations || credits.Outstanding != 0 || credits.Gate.CurrentCount != concurrency || credits.Peak > concurrency)) throw new InvalidOperationException("Receive-credit accounting mismatch");
        Array.Sort(latency);
        if (creditLatency is not null) Array.Sort(creditLatency);
        var result = new {
            schemaVersion = 2, processId = Environment.ProcessId, stopwatchFrequency = Stopwatch.Frequency, ticksStart, ticksEnd,
            cpuBoundary = "includes-two-precise-snapshot-costs; compare-empty-control-without-subtraction",
            sourceSha = Environment.GetEnvironmentVariable("ISSUE739_SOURCE_SHA") ?? "UNVERIFIED",
            sample = args[5], transport, rpc = rpcKind, concurrency, operations, warmup,
            runtime = Environment.Version.ToString(), framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            tailWindowMilliseconds = 20, boundaryStatus = "sentinel+admitted-zero+20ms-tail; no formal dispatch-cleanup fence; raw controls and operation-count scaling required",
            threadPoolMinimumWorkers = 132, threadPoolMinimumIo = 132,
            processorCount = Environment.ProcessorCount, serverGc = System.Runtime.GCSettings.IsServerGC,
            tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "runtime-default",
            bytes = bytesEnd - bytesStart, bytesPerOperation = (bytesEnd - bytesStart) / (double)operations,
            objectsPerOperation = (double?)null, objectsStatus = "unavailable-without-profiler",
            gen0, cpuMilliseconds = cpuMs, cpuNanosecondsPerOperation = cpuMs * 1e6 / operations,
            elapsedSeconds = (ticksEnd - ticksStart) / (double)Stopwatch.Frequency,
            qpsBoundary = "includes fixed20ms tail and sentinel; not pure steady-state throughput",
            markerBoundary = "Start precedes CPU/GC snapshots; Stop follows snapshots; trace window is wider than precise byte window",
            qps = operations * (double)Stopwatch.Frequency / (ticksEnd - ticksStart),
            p50Nanoseconds = latency[(operations - 1) / 2] * 1e9 / Stopwatch.Frequency,
            p99Nanoseconds = latency[(int)Math.Ceiling(operations * .99) - 1] * 1e9 / Stopwatch.Frequency,
            latencyMeaning = rpcKind is "oneway" or "oneway-bounded" ? "client-send-completion; final receive and admitted-call quiescence included in totals; sentinel tail is a fixed-boundary limitation" : "client-completion",
            measurement = "combined-client-and-server-managed-bytes; precise=true; uncorrected-harness-inclusive",
            received = service.Received - receivedStart,
            telemetry = "no listeners/exporters; defaults; no interceptors/deadline/retry/compression",
            receiveCreditBounded = bounded, receiveCreditScope = "receiver-handler work only; not final dispatcher/transport cleanup",
            creditAcquired = credits is null ? (long?)null : credits.Acquired-acquiredStart,
            creditReleased = credits is null ? (long?)null : credits.Released-releasedStart,
            creditWaitIncompleteAtProbe = credits is null ? (long?)null : credits.IncompleteWaits-incompleteWaitsStart,
            creditWaitObservation = "incomplete at IsCompleted probe, not proven true suspension",
            creditPeakOutstanding = credits?.Peak, creditFinalAvailable = credits?.Gate.CurrentCount,
            creditWaitP50Nanoseconds = creditLatency is null ? (double?)null : creditLatency[(operations-1)/2] * 1e9 / Stopwatch.Frequency,
            creditWaitP99Nanoseconds = creditLatency is null ? (double?)null : creditLatency[(int)Math.Ceiling(operations*.99)-1] * 1e9 / Stopwatch.Frequency,
            creditCost = "SemaphoreSlim.WaitAsync and counter/driver work included gross; direct control can complete synchronously, not subtractable",
            diagnostic, readCompletionStatus = diagnostic ? "separate-observer-run; transport-ReadAsync-IsCompleted-at-probe; not proven true suspension; completion may race" : "not-instrumented",
            clientReadSync = diagnostic ? clientReads!.Sync-clientSyncStart : (long?)null, clientReadIncomplete = diagnostic ? clientReads!.Incomplete-clientIncompleteStart : (long?)null,
            serverReadSync = diagnostic ? serverReads!.Sync-serverSyncStart : (long?)null, serverReadIncomplete = diagnostic ? serverReads!.Incomplete-serverIncompleteStart : (long?)null,
            traceEnabled = Markers.Log.IsEnabled()
        };
        await File.WriteAllTextAsync(args[6], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        await client.StopAsync(); await server.StopAsync(TimeSpan.Zero); shutdown.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }

        async Task Worker(int worker, int count, bool measure)
        {
            for (int i = worker; i < count; i += concurrency)
            {
                if (credits is not null)
                {
                    long waitBegin = measure ? Stopwatch.GetTimestamp() : 0;
                    var creditWait = credits.Gate.WaitAsync();
                    if (!creditWait.IsCompleted) credits.ObserveIncompleteWait();
                    await creditWait.ConfigureAwait(false);
                    credits.OnAcquired();
                    if (measure) creditLatency![i] = Stopwatch.GetTimestamp()-waitBegin;
                }
                long begin = measure ? Stopwatch.GetTimestamp() : 0;
                if (rpcKind == "add") { if (await rpc.AddAsync(20, 22).ConfigureAwait(false) != 42) throw new InvalidOperationException("Bad Add"); }
                else if (rpcKind is "oneway" or "oneway-bounded") await rpc.PublishAsync(42).ConfigureAwait(false);
                else if (rpcKind == "credit-control") await service.PublishAsync(42).ConfigureAwait(false);
                else await ValueTask.CompletedTask;
                if (measure) latency[i] = Stopwatch.GetTimestamp() - begin;
            }
        }
        async Task Run(int count, bool measure)
        {
            var tasks = new Task[concurrency];
            for (int i = 0; i < concurrency; i++) tasks[i] = Worker(i, count, measure);
            await Task.WhenAll(tasks);
        }
        async Task Drain()
        {
            // Same-connection sentinel orders preceding synchronous OneWay handlers.
            // ActiveCallCount is admitted-call quiescence, not a full AsyncLocal cleanup fence.
            // Sentinel cleanup tail is bounded by separate control and N/2N runs; never subtract blindly.
            if (await rpc.AddAsync(20, 22).ConfigureAwait(false) != 42) throw new InvalidOperationException("Bad drain sentinel");
            var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
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
