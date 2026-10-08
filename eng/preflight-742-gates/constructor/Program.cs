using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Runtime;

var arm = args[0];
var iterations = int.Parse(args[1]);
MeasureConstructors(2048);
MeasureLockConstructors(2048);
var controllerBytes = MeasureConstructors(10000) / 10000.0;
var lockBytes = MeasureLockConstructors(10000) / 10000.0;
var results = new List<object>();
foreach (var mode in new[] { "sequential-mixed", "parallel-mixed", "parallel-send", "parallel-receive" })
{
    Run(mode, 10000);
    for (var repeat = 0; repeat < 3; repeat++)
        results.Add(Run(mode, iterations));
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    Arm = arm,
    Runtime = Environment.Version.ToString(),
    Environment.ProcessorCount,
    ServerGC = GCSettings.IsServerGC,
    DynamicCode = RuntimeFeature.IsDynamicCodeSupported,
    ControllerConstructionBytes = controllerBytes,
    LockConstructionBytes = lockBytes,
    RuntimeAssembly = typeof(StreamFlowController).Assembly.Location,
    RuntimeSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(StreamFlowController).Assembly.Location))),
    Rows = results
}, new JsonSerializerOptions { WriteIndented = true }));

static long MeasureConstructors(int count)
{
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < count; i++)
        GC.KeepAlive(new StreamFlowController(1, 2, 1024));
    return GC.GetAllocatedBytesForCurrentThread() - before;
}

static long MeasureLockConstructors(int count)
{
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < count; i++)
        Keep(new Lock());
    return GC.GetAllocatedBytesForCurrentThread() - before;
}

static object Run(string mode, int iterations)
{
    var controller = new StreamFlowController(1, 2, 1024);
    var sends = new StreamFlowController.ResolvedSendCreditLease[2];
    var receives = new StreamFlowController.ResolvedReceiveCreditLease[2];
    for (var i = 0; i < 2; i++)
    {
        if (!controller.TryAcquireSendCreditLease(i, 0, 1, out sends[i]))
            throw new InvalidOperationException("setup reservation failed");
        controller.ReturnUnsentCredit(in sends[i], 1);
        receives[i] = controller.ResolveReceiveCreditLease(i, 0);
    }
    using var ready = new CountdownEvent(2);
    using var start = new ManualResetEventSlim();
    var bytes = new long[2];
    var checksums = new long[2];
    var failures = new Exception?[2];
    void Work(int index, bool send)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            long checksum = 0;
            for (var i = 0; i < iterations; i++)
            {
                if (send)
                {
                    if (!controller.TryAcquireSendCredit(in sends[index], 1))
                        throw new InvalidOperationException("unexpected credit exhaustion");
                    controller.ReturnUnsentCredit(in sends[index], 1);
                    checksum++;
                }
                else
                {
                    controller.AcceptReceived(in receives[index], 1);
                    checksum += controller.RecordConsumed(in receives[index], 1);
                }
            }
            checksums[index] = checksum;
        }
        catch (Exception error) { failures[index] = error; }
        bytes[index] = GC.GetAllocatedBytesForCurrentThread() - before;
    }
    Thread Worker(int index, bool send) => new(() =>
    {
        ready.Signal();
        start.Wait();
        Work(index, send);
    });
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var contentionBefore = Monitor.LockContentionCount;
    var timer = Stopwatch.StartNew();
    if (mode == "sequential-mixed")
    {
        Work(0, true);
        Work(1, false);
    }
    else
    {
        var first = Worker(0, mode != "parallel-receive");
        var second = Worker(1, mode == "parallel-send");
        first.Start();
        second.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("worker startup timeout");
        cpuBefore = process.TotalProcessorTime;
        contentionBefore = Monitor.LockContentionCount;
        timer.Restart();
        start.Set();
        if (!first.Join(TimeSpan.FromSeconds(60)) || !second.Join(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException("worker timeout");
    }
    timer.Stop();
    var cpu = process.TotalProcessorTime - cpuBefore;
    var contentions = Monitor.LockContentionCount - contentionBefore;
    if (failures.Any(e => e is not null))
        throw new AggregateException(failures.OfType<Exception>());
    if (checksums.Any(v => v != iterations) || controller.SendConnectionCredit != 2)
        throw new InvalidOperationException("invalid checksum/credit balance");
    return new
    {
        Mode = mode,
        IterationsPerWorker = iterations,
        Operations = iterations * 2L,
        Checksum = checksums.Sum(),
        ElapsedNsPerOperation = timer.Elapsed.TotalNanoseconds / (iterations * 2L),
        CpuNsPerOperation = cpu.TotalNanoseconds / (iterations * 2L),
        LoopAllocatedBytes = bytes.Sum(),
        LockContentions = contentions
    };
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void Keep<T>(T value) => GC.KeepAlive(value);
