#if SHARPLINK_ALLOCATION_PATH_OBSERVATION
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class AllocationPathCalibration
{
    private const int Operations = 4_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static void RunSelfTests(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected output JSON path and original benchmark DLL.");
        var unchangedShapes = CompareStateMachines(args[1]);
        for (var i = 0; i < 512; i++) NestedAllocation();
        var before = AllocationPathObservation.Capture();
        var actualBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Operations; i++) NestedAllocation();
        var actual = GC.GetAllocatedBytesForCurrentThread() - actualBefore;
        var after = AllocationPathObservation.Capture();
        var outer = Delta(before, after, AllocationPathObservation.Path.CalibrationOuter);
        var inner = Delta(before, after, AllocationPathObservation.Path.CalibrationInner);
        Ensure(actual >= 2L * 128 * Operations && outer.RootBytes == actual &&
            outer.RootCalls == Operations && inner.Calls == Operations && inner.RootBytes == 0 && inner.RootCalls == 0,
            "Nested 128-byte payloads must be counted exactly once by independent same-thread allocation accounting.");
        for (var i = 0; i < 512; i++) EmptyObservation();
        var emptyBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Operations; i++) EmptyObservation();
        var emptyBytes = GC.GetAllocatedBytesForCurrentThread() - emptyBefore;
        Ensure(emptyBytes == 0, "Warmed synchronous observer must not itself allocate.");

        var expected = new InvalidOperationException("known synchronous failure");
        try
        {
            _ = AllocationPathObservation.StartRpc(() => throw expected);
            throw new InvalidOperationException("Original exception was swallowed.");
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, expected)) { }
        Ensure(AllocationPathObservation.Capture().ActiveAfter == 0, "Throw must release observation scope.");
        var source = new SingleUseSource();
        var original = new ValueTask(source, 0);
        var returned = AllocationPathObservation.StartRpc(() => original);
        Ensure(returned.Equals(original) && source.Consumptions == 0, "Observer must return the original unconsumed ValueTask.");
        source.Completed = true;
        returned.GetAwaiter().GetResult();
        Ensure(source.Consumptions == 1, "Original source must be consumed exactly once.");
        Write(args[0], new { diagnosticOnly = true, passed = true, operations = Operations,
            injectedPayloadBytes = 128, allocationObjectsPerOperation = 2, actualBytes = actual,
            outer, inner, emptyObserverBytes = emptyBytes, asyncStateMachineShapes = unchangedShapes, valueTaskIdentityPreserved = true,
            synchronousExceptionPreserved = true, snapshotBefore = before, snapshotAfter = after });
    }

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Expected output JSON path.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var name = $"allocation-path-proof-{Guid.NewGuid():N}";
        await using var listener = new SharedMemoryServerTransportListener(name);
        await using var factory = new SharedMemoryClientTransportFactory(name);
        var accepting = listener.AcceptAsync(deadline.Token);
        await using var client = await factory.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await using var server = await accepting.ConfigureAwait(false);
        var reader = server.Input;
        var writer = client.Output;
        var synchronous = await MeasureAsync(reader, writer, false, deadline.Token).ConfigureAwait(false);
        var suspended = await MeasureAsync(reader, writer, true, deadline.Token).ConfigureAwait(false);
        Ensure(synchronous.Read.Calls == Operations && synchronous.Read.Completed == Operations &&
            synchronous.Read.Incomplete == 0 && synchronous.Read.RootBytes == 0,
            "Write-before-read control must have zero allocation at the synchronous read-call boundary.");
        Ensure(suspended.Read.Calls == Operations && suspended.Read.Incomplete == Operations &&
            suspended.Read.Completed == 0 && suspended.Read.RootBytes > synchronous.Read.RootBytes,
            "Empty-ring read-before-write control must force suspension and observed read-call allocation.");
        Write(args[0], new { diagnosticOnly = true, passed = true, runtime = Environment.Version.ToString(),
            operations = Operations, warmup = 512, payloadBytes = 8, synchronous, suspended,
            limitations = "Forced controls establish call-boundary path costs only, not a full RPC allocation model. Other-thread/later allocations and boundary-straddling work remain unattributed." });
    }

    private static async Task<Result> MeasureAsync(PipeReader reader, PipeWriter writer, bool suspend, CancellationToken token)
    {
        await ExecuteAsync(reader, writer, suspend, 512, token).ConfigureAwait(false);
        var before = AllocationPathObservation.Capture();
        var processBefore = GC.GetTotalAllocatedBytes(precise: true);
        await ExecuteAsync(reader, writer, suspend, Operations, token).ConfigureAwait(false);
        var processBytes = GC.GetTotalAllocatedBytes(precise: true) - processBefore;
        var after = AllocationPathObservation.Capture();
        return new Result(suspend, processBytes, Delta(before, after, AllocationPathObservation.Path.ServerRead), before, after);
    }

    private static async Task ExecuteAsync(PipeReader reader, PipeWriter writer, bool suspend, int operations, CancellationToken token)
    {
        for (var i = 0; i < operations; i++)
        {
            ValueTask<ReadResult> pending;
            if (suspend)
            {
                pending = reader.ReadAsync(token);
                Ensure(!pending.IsCompleted, "Empty ring unexpectedly completed synchronously.");
                await WriteAsync(writer, i, token).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(writer, i, token).ConfigureAwait(false);
                pending = reader.ReadAsync(token);
                Ensure(pending.IsCompletedSuccessfully, "Published payload did not yield a synchronous read.");
            }
            var result = await pending.ConfigureAwait(false);
            Ensure(!result.IsCanceled && !result.IsCompleted && result.Buffer.Length == 8 &&
                BinaryPrimitives.ReadInt64LittleEndian(result.Buffer.FirstSpan) == i, "Control payload/operation mismatch.");
            reader.AdvanceTo(result.Buffer.End);
        }
    }

    private static ValueTask<FlushResult> WriteAsync(PipeWriter writer, long value, CancellationToken token)
    {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
        return writer.FlushAsync(token);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateKnownPayload() => GC.KeepAlive(new byte[128]);

    private static void NestedAllocation()
    {
        var outer = AllocationPathObservation.Enter(AllocationPathObservation.Path.CalibrationOuter);
        try
        {
            AllocateKnownPayload();
            var inner = AllocationPathObservation.Enter(AllocationPathObservation.Path.CalibrationInner);
            try { AllocateKnownPayload(); inner.Observe(true); }
            finally { inner.Dispose(); }
            outer.Observe(true);
        }
        finally { outer.Dispose(); }
    }

    private static void EmptyObservation()
    {
        var scope = AllocationPathObservation.Enter(AllocationPathObservation.Path.CalibrationOuter);
        try { scope.Observe(true); }
        finally { scope.Dispose(); }
    }

    private static AllocationPathObservation.Row Delta(AllocationPathObservation.Snapshot before, AllocationPathObservation.Snapshot after, AllocationPathObservation.Path path)
    {
        var a = before.Paths.Single(row => row.Name == path.ToString());
        var b = after.Paths.Single(row => row.Name == path.ToString());
        return new(path.ToString(), b.Calls - a.Calls, b.Completed - a.Completed, b.Incomplete - a.Incomplete,
            b.Throws - a.Throws, b.RootCalls - a.RootCalls, b.RootBytes - a.RootBytes);
    }

    private static string[] CompareStateMachines(string baselinePath)
    {
        var context = new AssemblyLoadContext("allocation-path-baseline-inspection", isCollectible: true);
        var resolver = new AssemblyDependencyResolver(Path.GetFullPath(baselinePath));
        context.Resolving += (_, name) => resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        try
        {
            var benchmark = context.LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
            var runtime = context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(baselinePath))!, "SharpLink.Runtime.dll"));
            Ensure(runtime.GetType("SharpLink.Runtime.AllocationPathObservation") is null, "Baseline contains observation code.");
            var comparisons = new[]
            {
                (runtime.GetType("SharpLink.Runtime.SharedMemoryPipeReader", throwOnError: true)!, typeof(SharedMemoryPipeReader), "ReadAsync", "ReadAsync"),
                (benchmark.GetType("SharpLink.Benchmarks.AllocationGateRunner", throwOnError: true)!, typeof(AllocationGateRunner), "MeasureAsync", "MeasureAsync"),
                (benchmark.GetType("SharpLink.Benchmarks.AllocationGateRunner", throwOnError: true)!, typeof(AllocationGateRunner), "RunWorkerAsync", "RunWorkerAsync")
            };
            foreach (var (original, candidate, oldMethod, newMethod) in comparisons)
                Ensure(Fields(original, oldMethod).SequenceEqual(Fields(candidate, newMethod)), $"Observation changed async state-machine fields for {oldMethod}.");
            return comparisons.Select(item => item.Item3).ToArray();
        }
        finally { context.Unload(); }
    }

    private static string[] Fields(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        return stateMachine.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.Name + ":" + field.FieldType.FullName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Write(string path, object result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
    }

    private sealed record Result(bool ForcedSuspension, long ProcessAllocatedBytes, AllocationPathObservation.Row Read,
        AllocationPathObservation.Snapshot Before, AllocationPathObservation.Snapshot After);

    private sealed class SingleUseSource : IValueTaskSource
    {
        internal bool Completed;
        internal int Consumptions;
        public void GetResult(short token)
        {
            Ensure(token == 0 && Completed && ++Consumptions == 1, "Invalid source consumption.");
        }
        public ValueTaskSourceStatus GetStatus(short token) => Completed ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => throw new InvalidOperationException("Self-test source must be completed before consumption.");
    }
}
#endif
