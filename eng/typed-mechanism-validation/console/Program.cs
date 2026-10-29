using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Runtime;

internal static class Program
{
    internal static readonly CancellationTokenSource Deadline = new(TimeSpan.FromSeconds(100));
    private static readonly Process Process = Process.GetCurrentProcess();
    private static readonly List<object> Results = [];
    private static string Stage = "arguments";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--arm" || args[1] is not ("baseline" or "candidate") ||
            args[2] != "--pair" || !int.TryParse(args[3], out var pair) || pair is < 1 or > 6 || args[4] != "--output")
        {
            Console.Error.WriteLine("Usage: SharpLink.Benchmarks --arm baseline|candidate --pair 1..6 --output new-local-result.json");
            return 2;
        }
        var output = Path.GetFullPath(args[5]);
        if (File.Exists(output)) { Console.Error.WriteLine("FAIL output_exists"); return 2; }
        var status = "pass";
        string? failure = null;
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(x => x.Key, x => x.Value);
        try
        {
            Check(metadata["Arm"] == args[1], "arm_mismatch");
            await WarmCase("wu1", 1, 1, 0, false);
            await WarmCase("wu100", 1, 100, 0, false);
            await WarmCase("burst513", 1, 513, 0, true);
            await ColdCase();
            await WarmCase("shared4", 4, 1, 0, false);
            await WarmCase("normal21", 1, 1, 21, false);
            await WarmCase("normal1024", 1, 1, 1024, false);
        }
        catch (Exception ex)
        {
            status = "fail";
            failure = ex is OperationCanceledException ? "deadline" :
                ex is TimeoutException ? "cleanup_timeout" :
                ex is InvalidOperationException ? "invariant" : "exception";
            // Bounded classification only, never unbounded exception/stack output.
            Console.Error.WriteLine($"FAIL {failure} stage={Stage} type={ex.GetType().Name}");
        }
        var report = new
        {
            schemaVersion = 1, status, failure, stage = Stage, pair,
            arm = metadata["Arm"], reviewedSourceCommit = metadata["ExpectedProductCommit"], sourceTree = metadata["SourceTree"],
            runtimeVersion = Environment.Version.ToString(),
            productAssembly = Identity(typeof(RpcSession).Assembly), consoleAssembly = Identity(Assembly.GetExecutingAssembly()),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount, profile = "Balanced_unmodified",
            stopwatchFrequency = Stopwatch.Frequency, cpuTicksPerSecond = TimeSpan.TicksPerSecond,
            boundary = "zero_queued_bytes_then_one_zero_byte_marker_per_batch",
            scope = "mechanism_only_not_TCP_or_QPS_or_p99", cases = Results
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > 65536) throw new InvalidOperationException("bounded_result");
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) file.Write(json);
        Console.WriteLine($"{status.ToUpperInvariant()} mechanism cases={Results.Count} pair={pair}");
        return status == "pass" ? 0 : 1;
    }

    private static async Task WarmCase(string name, int sessionCount, int batchSize, int ordinaryBytes, bool hold)
    {
        Stage = name;
        var fixture = new Fixture(sessionCount);
        try
        {
            await Phase(fixture, batchSize, ordinaryBytes, hold, measure: false);
            var beforeBytes = fixture.Bytes;
            var beforeFlushes = fixture.Flushes;
            var start = Sample.Take();
            var ops = await Phase(fixture, batchSize, ordinaryBytes, hold, measure: true);
            var costs = start.Finish(ops);
            var bytes = fixture.Bytes - beforeBytes;
            Check(bytes == ops * (ordinaryBytes == 0 ? 21 : ordinaryBytes), "wire_byte_conservation");
            fixture.CheckSettled();
            var pool = PoolSnapshot.Capture(fixture.Context);
            if (hold && pool.Present) Check(pool.Count == 512, "overflow_pool_cap_not_reached");
            var validation = await ValidateWire(fixture, batchSize, ordinaryBytes, hold);
            await fixture.Close();
            Results.Add(new { @case = name, passed = true, batchSize, sessions = sessionCount, ordinaryBytes,
                forcedHeldFlushStress = hold, warmupSeconds = 1, measureSeconds = 1,
                ops, bytes, allocatedBytes = costs.AllocatedBytes, cpuTicks = costs.CpuTicks, elapsedTicks = costs.ElapsedTicks,
                framesFromConservedFixedSize = ops,
                flushes = fixture.Flushes - beforeFlushes - validation.Flushes,
                finalQueuedBytes = 0, retainedNodes = pool.Present ? (int?)pool.Count : null, validation,
                poolDisposedAndEmpty = PoolSnapshot.DisposedAndEmpty(fixture.Context) });
        }
        finally { await fixture.Close(); }
    }

    private static async Task<long> Phase(Fixture fixture, int count, int ordinaryBytes, bool hold, bool measure)
    {
        _ = measure; // Both phases have identical fixed duration and batch boundaries.
        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        var workers = new Task<long>[fixture.Sessions.Length];
        for (var index = 0; index < workers.Length; index++)
        {
            var slot = index;
            workers[index] = Task.Run(async () =>
            {
                long ops = 0;
                do
                {
                    await Batch(fixture, slot, count, ordinaryBytes, hold);
                    ops += count;
                } while (Stopwatch.GetTimestamp() < until);
                return ops;
            });
        }
        var totals = await Task.WhenAll(workers);
        return totals.Sum();
    }

    private static async Task Batch(Fixture fixture, int slot, int count, int ordinaryBytes, bool hold)
    {
        Deadline.Token.ThrowIfCancellationRequested();
        var session = fixture.Sessions[slot];
        var writer = fixture.Outputs[slot];
        if (hold) writer.ArmHold();
        for (var i = 0; i < count; i++)
        {
            if (ordinaryBytes == 0) session.SendWindowUpdate(123, 7, 4096);
            else
            {
                var packet = fixture.Context.Buffers.Rent();
                var token = packet.BeginPacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 123);
                var length = ordinaryBytes - ProtocolV2Constants.HeaderBytes;
                packet.GetSpan(length)[..length].Fill(0x5a);
                packet.Advance(length);
                packet.EndPacket(token);
                session.SendPacket(packet);
            }
            if (hold && i == 0) await writer.WaitHeld();
        }
        if (hold) Check(session.QueuedSendBytes == 513 * 21, "held_513_reservations");
        if (hold) writer.ReleaseHold();
        // Wait for all credit reservations/owners first: a normal-queue marker
        // otherwise can pass progress frames. The following single marker forces
        // the pump past their remaining telemetry/capacity callback tails too.
        while (session.QueuedSendBytes != 0)
        {
            Deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        await session.FlushSendQueueAsync(Deadline.Token);
    }

    private static async Task ColdCase()
    {
        Stage = "cold128";
        var fixtures = new Fixture[128];
        var created = 0;
        try
        {
            var constructionStart = Sample.Take();
            for (; created < fixtures.Length; created++) fixtures[created] = new Fixture(1);
            var construction = constructionStart.Finish(128);
            foreach (var f in fixtures) Check(!PoolSnapshot.Capture(f.Context).Present, "pool_not_lazy");
            var firstStart = Sample.Take();
            foreach (var f in fixtures) await Batch(f, 0, 1, 0, false);
            var firstUse = firstStart.Finish(128);
            long bytes = 0;
            var maximumRetained = 0;
            foreach (var f in fixtures)
            {
                f.CheckSettled(); bytes += f.Bytes;
                maximumRetained = Math.Max(maximumRetained, PoolSnapshot.Capture(f.Context).Count);
            }
            Check(bytes == 128 * 21, "cold_wire_conservation");
            // One independent captured validation frame per context, untimed.
            foreach (var f in fixtures) await ValidateWire(f, 1, 0, false);
            foreach (var f in fixtures) await f.Close();
            Results.Add(new { @case = Stage, passed = true, contexts = 128, construction, ops = 128, bytes,
                allocatedBytes = firstUse.AllocatedBytes, cpuTicks = firstUse.CpuTicks, elapsedTicks = firstUse.ElapsedTicks,
                warmupSeconds = 0, measureSeconds = 0, fixedOperationCount = true,
                framesFromConservedFixedSize = 128, finalQueuedBytes = 0,
                retainedNodes = PoolSnapshot.Capture(fixtures[0].Context).Present ? (int?)maximumRetained : null,
                validatedFrames = 128, poolDisposedAndEmpty = fixtures.All(f => PoolSnapshot.DisposedAndEmpty(f.Context)) });
        }
        finally { for (var i = 0; i < created; i++) await fixtures[i].Close(); }
    }

    private static async Task<Validation> ValidateWire(Fixture f, int count, int ordinaryBytes, bool hold)
    {
        var beforeFlushes = f.Flushes;
        foreach (var w in f.Outputs) w.BeginCapture();
        for (var i = 0; i < f.Sessions.Length; i++) await Batch(f, i, count, ordinaryBytes, hold);
        long frames = 0, bytes = 0;
        foreach (var w in f.Outputs)
        {
            var captured = w.EndCapture();
            bytes += captured.Length;
            var cursor = 0;
            while (cursor < captured.Length)
            {
                var size = ordinaryBytes == 0 ? 21 : ordinaryBytes;
                Check(captured.Length - cursor >= size, "truncated_frame");
                var frame = captured.Span.Slice(cursor, size);
                Check(frame[0] == 0x89 && BinaryPrimitives.ReadInt32LittleEndian(frame[1..]) == size - 15 &&
                    frame[5] == (ordinaryBytes == 0 ? 9 : 5) && frame[6] == 0 &&
                    BinaryPrimitives.ReadUInt64LittleEndian(frame[7..]) == 123, "wire_header");
                if (ordinaryBytes == 0)
                    Check(BinaryPrimitives.ReadUInt16LittleEndian(frame[15..]) == 7 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(frame[17..]) == 4096, "wire_credit");
                else foreach (var b in frame[15..]) Check(b == 0x5a, "ordinary_payload");
                frames++; cursor += size;
            }
        }
        Check(frames == (long)count * f.Sessions.Length, "wire_frame_conservation");
        f.CheckSettled();
        return new Validation(frames, bytes, f.Flushes - beforeFlushes);
    }

    private static object Identity(Assembly assembly) => new
    {
        path = assembly.Location, mvid = assembly.ManifestModule.ModuleVersionId,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()
    };

    internal static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private sealed record Validation(long Frames, long Bytes, long Flushes);
    private sealed record Costs(long Operations, long AllocatedBytes, long ElapsedTicks, long CpuTicks);
    private readonly record struct Sample(long Alloc, long Time, TimeSpan Cpu)
    {
        internal static Sample Take()
        {
            Process.Refresh();
            return new Sample(GC.GetTotalAllocatedBytes(precise: true), Stopwatch.GetTimestamp(), Process.TotalProcessorTime);
        }
        internal Costs Finish(long ops)
        {
            var end = Take();
            return new Costs(ops, end.Alloc - Alloc, end.Time - Time, (end.Cpu - Cpu).Ticks);
        }
    }
}

internal sealed class Fixture
{
    internal readonly SharpLinkRuntimeContext Context;
    internal readonly RpcSession[] Sessions;
    internal readonly Sink[] Outputs;
    private readonly Pipe[] Inputs;
    private Task? _close;
    internal Fixture(int count)
    {
        Context = new SharpLinkRuntimeContextBuilder().Configure(o => o.PerformanceProfile = SharpLinkPerformanceProfile.Balanced)
            .Build(includeGeneratedAssemblyCatalog: false);
        Sessions = new RpcSession[count]; Outputs = new Sink[count]; Inputs = new Pipe[count];
        for (var i = 0; i < count; i++)
        {
            Inputs[i] = new Pipe(); Outputs[i] = new Sink();
            Sessions[i] = new RpcSession(new Connection("mechanism", Inputs[i].Reader, Outputs[i]),
                new RpcSessionCreationOptions(RpcSessionRole.Client, Context));
            Program.Check(Sessions[i].TryCompleteHandshake(new NegotiatedSessionOptions(
                ProtocolV2Constants.MinorVersion, ProtocolV2Capabilities.FlowControl, Context.Protocol.MaxFramePayloadBytes,
                Context.FlowControl.StreamReceiveWindowBytes, Context.FlowControl.ConnectionReceiveWindowBytes)), "handshake");
            Program.Check(Sessions[i].HasStreamFlowControl, "flow_control_not_enabled");
        }
    }
    internal long Bytes => Outputs.Sum(w => w.Bytes);
    internal long Flushes => Outputs.Sum(w => w.Flushes);
    internal void CheckSettled() { foreach (var s in Sessions) Program.Check(s.QueuedSendBytes == 0, "queue_not_empty"); }
    internal Task Close() => _close ??= CloseCore().WaitAsync(TimeSpan.FromSeconds(10));
    private async Task CloseCore()
    {
        foreach (var w in Outputs) w.ReleaseHold();
        try
        {
            foreach (var s in Sessions) await s.DisposeAsync();
            foreach (var p in Inputs) await p.Writer.CompleteAsync();
            foreach (var w in Outputs) await w.CompleteAsync();
            CheckSettled();
        }
        finally { Context.Dispose(); }
        Program.Check(PoolSnapshot.DisposedAndEmpty(Context), "pool_disposal");
    }
    private sealed class Connection(string id, PipeReader input, PipeWriter output) : ITransportConnection
    {
        public string Id => id;
        public PipeReader Input => input;
        public PipeWriter Output => output;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        public async ValueTask DisposeAsync() { await Output.CompleteAsync(); await Input.CompleteAsync(); }
    }
}

// Fixed-size sink: normal timed writes only account bytes/flushes; wire parsing is untimed.
internal sealed class Sink : PipeWriter
{
    private readonly byte[] _buffer = new byte[65536];
    private readonly byte[] _capture = new byte[65536];
    private int _written, _captured;
    private bool _capturing;
    private int _armed;
    private TaskCompletionSource? _held;
    private TaskCompletionSource<FlushResult>? _release;
    internal long Bytes, Flushes;
    public override void Advance(int bytes)
    {
        Program.Check(bytes >= 0 && bytes <= _buffer.Length - _written, "sink_capacity");
        _written += bytes;
    }
    public override Memory<byte> GetMemory(int sizeHint = 0) { Capacity(sizeHint); return _buffer.AsMemory(_written); }
    public override Span<byte> GetSpan(int sizeHint = 0) { Capacity(sizeHint); return _buffer.AsSpan(_written); }
    private void Capacity(int hint) => Program.Check(hint >= 0 && hint <= _buffer.Length - _written, "sink_capacity");
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capturing)
        {
            Program.Check(_written <= _capture.Length - _captured, "capture_capacity");
            _buffer.AsSpan(0, _written).CopyTo(_capture.AsSpan(_captured)); _captured += _written;
        }
        Bytes += _written; Flushes++; _written = 0;
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _held!.TrySetResult();
            return new ValueTask<FlushResult>(_release!.Task);
        }
        return ValueTask.FromResult(new FlushResult(false, false));
    }
    internal void ArmHold()
    {
        _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _armed, 1);
    }
    internal Task WaitHeld() => _held!.Task.WaitAsync(Program.Deadline.Token);
    internal void ReleaseHold() => _release?.TrySetResult(new FlushResult(false, false));
    internal void BeginCapture() { _captured = 0; _capturing = true; }
    internal ReadOnlyMemory<byte> EndCapture() { _capturing = false; return _capture.AsMemory(0, _captured); }
    public override void CancelPendingFlush() => ReleaseHold();
    public override void Complete(Exception? exception = null) => ReleaseHold();
}

internal sealed record PoolSnapshot(bool Present, int Count)
{
    private static readonly FieldInfo? PoolField = typeof(SharpLinkRuntimeContext).GetField("_windowUpdatePool", BindingFlags.Instance | BindingFlags.NonPublic);
    private static object? Pool(SharpLinkRuntimeContext context) => PoolField?.GetValue(context);
    internal static PoolSnapshot Capture(SharpLinkRuntimeContext context)
    {
        var pool = Pool(context);
        if (pool is null) return new(false, 0);
        var count = (int)pool.GetType().GetProperty("RetainedCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        Program.Check(count is >= 0 and <= 512, "pool_cap");
        return new(true, count);
    }
    internal static bool DisposedAndEmpty(SharpLinkRuntimeContext context)
    {
        var pool = Pool(context);
        return pool is null || (Capture(context).Count == 0 &&
            (bool)pool.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!);
    }
}
