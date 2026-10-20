using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

// No replacement of canonical BDN/ABBA workload. This is a closed-loop, bounded
// diagnosis with dedicated sender threads and a same-process business completion fence.
internal static class ClientSendDiagnostics
{
    internal const int BatchSize = 4096;
    internal const int WarmupBatches = 32;
    internal const int Batches = 256;
    internal const int MicroOperations = 1000000;
    private const int Total = (WarmupBatches + Batches) * BatchSize;

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 4 || !OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new ArgumentException("Expected Linux 64-bit: transport concurrency pool output.json");
        var transport = args[0];
        var concurrency = int.Parse(args[1]);
        var pool = int.Parse(args[2]);
        if ((concurrency, pool) is not ((1, 1) or (8, 4)))
            throw new ArgumentException("Predeclared loads only.");
        using var receiver = new Observer(Total);
        var sb = SharpLinkServerBuilder.Create().UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var cb = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .UseConnectionPool(o => { o.MinConnections = pool; o.MaxConnections = pool; });
        if (transport == "tcp")
        {
            sb.UseTcp(0, IPAddress.Loopback.ToString());
            cb.UseTcp(IPAddress.Loopback.ToString(), ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port);
        }
        else if (transport == "sharedmemory")
        {
            var name = "issue775-client-" + Guid.NewGuid().ToString("N");
            sb.UseTransport(new SharedMemoryServerTransportListener(name));
            cb.UseSharedMemory(name);
        }
        else throw new ArgumentException("Unknown transport.");
        sb.ReplaceService<IBenchmarkRpc>(receiver);
        await using var server = sb.Build();
        await using var client = (SharpLinkClient)cb.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await server.StartAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        while (client.ReadyConnectionCount != pool) await Task.Delay(1, timeout.Token);
        var rpc = client.Get<IBenchmarkRpc>();
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
        await client.PrepareStaticBindingAsync<IBenchmarkRpc>(timeout.Token);
#endif
        Require(await rpc.AddAsync(20, 22) == 42, "Initial Unary");
        var contract = GlobalCatalogManifestSource.Instance.CreateSnapshot().SelectMany(m => m.Contracts)
            .Single(c => c.ContractType == typeof(IBenchmarkRpc));
        var generated = contract.Methods.Single(m => m.Name == nameof(IBenchmarkRpc.PublishEventAsync));
        Require(contract.StubFactory(client.RuntimeContext.Codecs).TryGetMethodDescriptor(generated.MethodId, out var method), "Method facts");
        var probe = client.CreateClientSendProbe<IBenchmarkRpc>(method);
        Require(probe.Connections == pool && probe.Reservations == 0, "Ready pool");
        var bindBefore = probe.Binds;
        // Synchronous coordinator does not run on any measured sender thread.
        var result = await Task.Run(() => Measure(rpc, receiver, probe, concurrency, timeout.Token));
        await probe.FlushAsync();
        Require(await rpc.AddAsync(17, 25) == 42, "Final Unary");
        Require(receiver.Completed == Total && receiver.Seen.All(x => x == 1), "Missing/duplicate business work");
        Require(probe.Reservations == 0 && probe.Binds == bindBefore, "Reservation leak or extra Bind");
        var micro = await Task.Run(() => new[] {
            Micro("control", probe.ControlLoop), Micro("route-admission-release", probe.RouteLoop) });
        Require(probe.Reservations == 0, "Microdiagnostic leaked admission");
        await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(new
        {
            Schema = 1, Passed = true, DiagnosticOnly = true,
            SourceSha = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA"),
            Magic = (int)ProtocolV2Constants.Magic, RoutePrefixBytes = 16,
            Transport = transport, Concurrency = concurrency, Pool = pool,
            ProcessorCount = Environment.ProcessorCount, Runtime = RuntimeInformation.FrameworkDescription,
            StopwatchFrequency = Stopwatch.Frequency, BatchSize, Batches, WarmupBatches,
            Operations = Batches * BatchSize, Warmup = WarmupBatches * BatchSize,
            ReceiverCompleted = receiver.Completed, UniqueReceived = receiver.Seen.Count(x => x == 1),
            BindBefore = bindBefore, BindAfter = probe.Binds, FinalReservations = probe.Reservations,
            ControlBytes = probe.ControlBytes, Measurement = result, Micro = micro,
            Limits = "Dedicated send threads; closed-loop batch fence in same-process service, NOT wire ACK/BDN score. Sender CPU excludes waits outside send loops; other process CPU includes runtime, transport, service, coordinator and sender synchronization. Queue gauges are sampled, not high-watermarks. Micro loops are not additive phase accounting."
        }));
    }

    private static object Measure(IBenchmarkRpc rpc, Observer receiver,
        SharpLinkClient.ClientSendProbe probe, int concurrency, CancellationToken token)
    {
        using var barrier = new Barrier(concurrency + 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var samples = new WorkerSample[Batches * concurrency];
        var rounds = new BatchSample[Batches];
        var errors = new Exception?[concurrency];
        var threads = new Thread[concurrency];
        for (var worker = 0; worker < concurrency; worker++)
        {
            var index = worker;
            threads[worker] = new Thread(() =>
            {
                try
                {
                    for (var round = 0; round < WarmupBatches + Batches; round++)
                    {
                        barrier.SignalAndWait(stop.Token);
                        var tid = Environment.CurrentManagedThreadId;
                        var started = Stopwatch.GetTimestamp();
                        var cpuStarted = CpuTime(3);
                        var asyncCalls = 0;
                        var first = round * BatchSize + index * (BatchSize / concurrency);
                        for (var i = first; i < first + BatchSize / concurrency; i++)
                        {
                            var send = rpc.PublishEventAsync(i, 42, "binding");
                            if (!send.IsCompletedSuccessfully)
                            {
                                asyncCalls++;
                                send.AsTask().GetAwaiter().GetResult();
                            }
                            else send.GetAwaiter().GetResult();
                        }
                        var cpuStopped = CpuTime(3);
                        var ended = Stopwatch.GetTimestamp();
                        Require(Environment.CurrentManagedThreadId == tid, "Sender migrated managed thread");
                        var queued = probe.QueuedBytes;
                        if (round >= WarmupBatches)
                            samples[(round - WarmupBatches) * concurrency + index] = new(
                                round - WarmupBatches, index, tid, BatchSize / concurrency,
                                started, ended, cpuStarted, cpuStopped, asyncCalls, queued);
                        barrier.SignalAndWait(stop.Token);
                    }
                }
                catch (Exception error) { errors[index] = error; stop.Cancel(); }
            }) { IsBackground = true, Name = "issue775-sender-" + worker };
            threads[worker].Start();
        }
        long startedAll = 0, endedAll = 0, cpuStart = 0, cpuEnd = 0, allocatedStart = 0, allocatedEnd = 0;
        var complete = false;
        try
        {
            for (var round = 0; round < WarmupBatches + Batches; round++)
            {
                receiver.BeginBatch((round + 1) * BatchSize);
                if (round == WarmupBatches)
                {
                    allocatedStart = GC.GetTotalAllocatedBytes(true);
                    cpuStart = CpuTime(2);
                    startedAll = Stopwatch.GetTimestamp();
                }
                var begin = Stopwatch.GetTimestamp();
                barrier.SignalAndWait(stop.Token);
                barrier.SignalAndWait(stop.Token);
                var sendEnd = Stopwatch.GetTimestamp();
                var queued = probe.QueuedBytes;
                receiver.BatchDone.Wait(stop.Token);
                var drained = Stopwatch.GetTimestamp();
                Require(receiver.Completed == (round + 1) * BatchSize, "Business batch incomplete");
                if (round >= WarmupBatches)
                    rounds[round - WarmupBatches] = new(round - WarmupBatches, begin, sendEnd, drained, queued);
            }
            endedAll = Stopwatch.GetTimestamp();
            cpuEnd = CpuTime(2);
            allocatedEnd = GC.GetTotalAllocatedBytes(true);
            complete = true;
        }
        finally
        {
            if (!complete) stop.Cancel();
            foreach (var thread in threads) Require(thread.Join(TimeSpan.FromSeconds(5)), "Sender did not terminate");
        }
        if (errors.Any(x => x is not null)) throw new AggregateException(errors.OfType<Exception>());
        var senderCpu = samples.Sum(x => x.CpuEnd - x.CpuStart);
        return new
        {
            Start = startedAll, End = endedAll, ProcessCpuStart = cpuStart, ProcessCpuEnd = cpuEnd,
            AllocatedStart = allocatedStart, AllocatedEnd = allocatedEnd,
            SenderCpuNanoseconds = senderCpu, OtherProcessCpuNanoseconds = cpuEnd - cpuStart - senderCpu,
            Samples = samples, Rounds = rounds
        };
    }

    private static object Micro(string name, Action<int> loop)
    {
        loop(100000);
        var start = Stopwatch.GetTimestamp();
        var cpu = CpuTime(3);
        loop(MicroOperations);
        var endCpu = CpuTime(3);
        var end = Stopwatch.GetTimestamp();
        return new { Name = name, Operations = MicroOperations, Start = start, End = end, CpuStart = cpu, CpuEnd = endCpu };
    }

    private readonly record struct WorkerSample(int Round, int Worker, int Thread, int Completed,
        long Start, long End, long CpuStart, long CpuEnd, int AsyncCalls, long QueuedBytes);
    private readonly record struct BatchSample(int Round, long Start, long SendEnd, long DrainEnd, long QueuedBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { internal long Seconds; internal long Nanoseconds; }
    [DllImport("libc", SetLastError = true)]
    private static extern int clock_gettime(int clock, out Timespec value);
    private static long CpuTime(int clock)
    {
        if (clock_gettime(clock, out var time) != 0)
            throw new InvalidOperationException("clock_gettime errno " + Marshal.GetLastPInvokeError());
        return checked(time.Seconds * 1000000000L + time.Nanoseconds);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Observer(int total) : BenchmarkRpcService, IBenchmarkRpc, IDisposable
    {
        private int _completed;
        private int _target;
        internal readonly int[] Seen = new int[total];
        internal readonly ManualResetEventSlim BatchDone = new(false, 0);
        internal int Completed => Volatile.Read(ref _completed);
        internal void BeginBatch(int target)
        {
            BatchDone.Reset();
            Volatile.Write(ref _target, target);
        }
        ValueTask IBenchmarkRpc.PublishEventAsync(int code, long ticks, string tag)
        {
            Require((uint)code < (uint)Seen.Length && ticks == 42 && tag == "binding", "Corrupt diagnostic payload");
            Require(Interlocked.Exchange(ref Seen[code], 1) == 0, "Duplicate OneWay");
            if (Interlocked.Increment(ref _completed) == Volatile.Read(ref _target)) BatchDone.Set();
            return ValueTask.CompletedTask;
        }
        public void Dispose() => BatchDone.Dispose();
    }
}
