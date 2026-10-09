using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using SharpLink.Runtime;

if (args.Length != 3 || !int.TryParse(args[2], out var iterations) || iterations < 1000)
    throw new ArgumentException("Usage: <arm> <output.json> <iterations >= 1000>");
if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
    throw new InvalidOperationException("Set DOTNET_TieredCompilation=0 for isolated micro measurements.");

var rows = new List<object>();
// Each process is one observation; process order is balanced by the outer runner.
foreach (var mode in new[] { "sync", "suspend", "suspend-consumer-await" })
{
    foreach (var wrapped in new[] { false, true })
    {
        var inner = new ControlledReader(mode != "sync");
        PipeReader reader = wrapped ? new ReadOwnershipPipeReader(inner) : inner;
        Run(reader, inner, mode, 20_000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        Run(reader, inner, mode, iterations);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        process.Refresh();
        rows.Add(new
        {
            mode, wrapped, iterations,
            elapsedSeconds = elapsed.TotalSeconds,
            nanosecondsPerRead = elapsed.TotalNanoseconds / iterations,
            allocatedBytesPerRead = bytes / (double)iterations,
            cpuNanosecondsPerRead = (process.TotalProcessorTime - cpuBefore).TotalNanoseconds / iterations,
            gen0 = GC.CollectionCount(0) - gen0,
            gen1 = GC.CollectionCount(1) - gen1,
            gen2 = GC.CollectionCount(2) - gen2,
        });
        await reader.CompleteAsync();
    }
}
var construction = new[] { MeasureConstruction(false), MeasureConstruction(true) };
var document = new
{
    arm = args[0],
    sourceCommit = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT"),
    runtime = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    processorCount = Environment.ProcessorCount,
    serverGc = GCSettings.IsServerGC,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    rows,
    construction,
    notes = new[]
    {
        "Deterministic zero-allocation fake PipeReader; each suspend returns an incomplete ValueTask before controlled completion on the same thread.",
        "The consumer-await case registers an async consumer before completion; its own allocation is included in both raw and wrapped cells.",
        "Current-thread allocations are exact for these single-threaded cells. These timings are isolated read costs, not RPC throughput.",
        "Raw and wrapped cells share a process but use fixed raw-then-wrapped order; compare identical wrapped cells across externally balanced arms. Raw subtraction is allocation attribution, not a latency speedup estimate.",
        "Construction bytes are total allocations per constructed reader with fixed inner cost included; wrapped minus raw attributes fixed wrapper setup, not retained graph size.",
        "These allocation counters do not prove that result buffers or exceptions are released; separate lifetime regression tests are required."
    }
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));

static object MeasureConstruction(bool wrapped)
{
    const int count = 10_000;
    var readers = new PipeReader[count];
    _ = new ReadOwnershipPipeReader(new ControlledReader(false));
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var index = 0; index < readers.Length; index++)
    {
        var inner = new ControlledReader(false);
        readers[index] = wrapped ? new ReadOwnershipPipeReader(inner) : inner;
    }
    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    GC.KeepAlive(readers);
    return new { wrapped, count, allocatedBytesPerReader = bytes / (double)count };
}

static void Run(PipeReader reader, ControlledReader inner, string mode, int count)
{
    for (var i = 0; i < count; i++)
    {
        var pending = mode == "suspend-consumer-await" ? ConsumeAsync(reader) : reader.ReadAsync();
        if (mode != "sync")
        {
            if (pending.IsCompleted)
                throw new InvalidOperationException("The requested suspended path completed synchronously.");
            inner.Finish();
        }
        if (!pending.IsCompletedSuccessfully)
            throw new InvalidOperationException("Completion unexpectedly moved off the measurement thread.");
        var result = pending.GetAwaiter().GetResult();
        if (result.Buffer.Length != 1 || result.IsCanceled || result.IsCompleted)
            throw new InvalidOperationException("Unexpected read payload or status.");
        reader.AdvanceTo(result.Buffer.End);
    }
}

static async ValueTask<ReadResult> ConsumeAsync(PipeReader reader)
    => await reader.ReadAsync().ConfigureAwait(false);

sealed class ControlledReader(bool suspend) : PipeReader, IValueTaskSource<ReadResult>
{
    private readonly ReadOnlySequence<byte> _buffer = new(new byte[1]);
    private ManualResetValueTaskSourceCore<ReadResult> _source;
    private bool _active;

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_active)
            throw new InvalidOperationException("Missing AdvanceTo.");
        _active = true;
        if (!suspend)
            return ValueTask.FromResult(new ReadResult(_buffer, false, false));
        _source.Reset();
        return new ValueTask<ReadResult>(this, _source.Version);
    }

    public void Finish() => _source.SetResult(new ReadResult(_buffer, false, false));
    public override void AdvanceTo(SequencePosition consumed) => _active = false;
    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceTo(consumed);
    public override void CancelPendingRead() { }
    public override void Complete(Exception? exception = null) { }
    public override bool TryRead(out ReadResult result) => throw new NotSupportedException();
    public ReadResult GetResult(short token) => _source.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);
}
