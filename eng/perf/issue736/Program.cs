using SharpLink.Runtime;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

const int Operations = 1_000_000;
var results = new List<ProbeRow>();
var payload = new ReadOnlySequence<byte>(new byte[1]);
var noop = new NoOp();
int[] arities = [0, 1, 2, 4, 8, 127];

#if !NATIVE_AOT
// The dynamic wrapper calls the actual private route-acquire/entry-release methods.
// This attribution layer is intentionally absent from NativeAOT, not substituted.
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var routeType = typeof(StreamManager).GetNestedType("RequestDispatchers", System.Reflection.BindingFlags.NonPublic)!;
var entryType = typeof(StreamManager).GetNestedType("DispatcherEntry", System.Reflection.BindingFlags.NonPublic)!;
var method = new System.Reflection.Emit.DynamicMethod("AcquireRelease", typeof(int), [typeof(object), typeof(ushort)], typeof(StreamManager), true);
var il = method.GetILGenerator();
var entry = il.DeclareLocal(entryType);
var miss = il.DefineLabel();
il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
il.Emit(System.Reflection.Emit.OpCodes.Castclass, routeType);
il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
il.Emit(System.Reflection.Emit.OpCodes.Ldloca, entry);
il.Emit(System.Reflection.Emit.OpCodes.Call, routeType.GetMethod("TryAcquire", flags)!);
il.Emit(System.Reflection.Emit.OpCodes.Brfalse, miss);
il.Emit(System.Reflection.Emit.OpCodes.Ldloc, entry);
il.Emit(System.Reflection.Emit.OpCodes.Call, entryType.GetMethod("Release", flags)!);
il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
il.Emit(System.Reflection.Emit.OpCodes.Ret);
il.MarkLabel(miss);
il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
il.Emit(System.Reflection.Emit.OpCodes.Ret);
foreach (var arity in arities)
{
    var route = Activator.CreateInstance(routeType, true)!;
    var ids = StreamIds(arity);
    foreach (var id in ids)
        routeType.GetMethod("TryRegister", flags)!.Invoke(route, [id, noop]);
    var call = (Func<ushort, int>)method.CreateDelegate(typeof(Func<ushort, int>), route);
    Warm(() => RunKnownBatch(call, ids, 200_000));
    for (int repeat = 0; repeat < 7; repeat++)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        int hits = RunKnownBatch(call, ids, Operations);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / Operations;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (hits != Operations)
            throw new InvalidOperationException("Known-route sample missed a registered stream.");
        results.Add(new ProbeRow { layer = "known-route", arity = arity, repeat = repeat, ns = elapsed, bytes = allocated });
    }
}
#endif

foreach (var requests in new[] { 1, 8, 32, 128 })
{
    foreach (var arity in arities)
    {
        var manager = new StreamManager();
        var ids = StreamIds(arity);
        for (int request = 1; request <= requests; request++)
            foreach (var id in ids)
                manager.Register(request, id, noop);
        Warm(() => RunManagerBatch(manager, requests, ids, payload, 200_000));
        for (int repeat = 0; repeat < 5; repeat++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            RunManagerBatch(manager, requests, ids, payload, Operations);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / Operations;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            results.Add(new ProbeRow { requests = requests, arity = arity, repeat = repeat, ns = elapsed, bytes = allocated });
        }
        if (manager.DroppedStreamFrames != 0 || manager.ActiveStreamCount != requests * ids.Length)
            throw new InvalidOperationException("Measured workload did not preserve registered route identities.");
        manager.CompleteAll(null);
    }
}

foreach (var arity in arities)
{
    var manager = new StreamManager();
    var ids = StreamIds(arity);
    manager.Register(-1, 0, noop);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int request = 1; request <= 1000; request++)
        foreach (var id in ids)
            manager.Register(request, id, noop);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    results.Add(new ProbeRow { arity = arity, registrationBytesPerRequest = allocated / 1000.0 });
    manager.CompleteStream(-1, 0, null); // Exclude initialization sentinel from cleanup sample.
    before = GC.GetAllocatedBytesForCurrentThread();
    manager.CompleteAll(null);
    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    results.Add(new ProbeRow { arity = arity, cleanupBytesPerRequest = allocated / 1000.0 });
}

foreach (var arity in arities)
{
    var manager = new StreamManager();
    var ids = StreamIds(arity);
    manager.Register(-1, 0, noop);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int request = 1; request <= 1000; request++)
        foreach (var id in ids)
            manager.Register(request, id, noop);
    for (int request = 1; request <= 1000; request++)
        foreach (var id in ids)
            manager.CompleteStream(request, id, null);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    if (manager.ActiveStreamCount != 1)
        throw new InvalidOperationException("Individual completion leaked a route.");
    results.Add(new ProbeRow { arity = arity, individualTerminalLifecycleBytesPerRequest = allocated / 1000.0 });
    manager.CompleteAll(null);
}
Console.WriteLine(JsonSerializer.Serialize(results, ProbeJsonContext.Default.ListProbeRow));

static ushort[] StreamIds(int arity) => arity == 0 ? [0] : Enumerable.Range(1, arity).Select(x => (ushort)x).ToArray();

static void Warm(Action batch)
{
    var milliseconds = int.Parse(Environment.GetEnvironmentVariable("ISSUE736_WARMUP_MS") ?? "0");
    var start = Stopwatch.GetTimestamp();
    do { batch(); } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds);
    if (milliseconds > 0)
    {
        // Let background tier compilation finish, then warm the identical measured helper again.
        Thread.Sleep(100);
        batch();
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void RunManagerBatch(StreamManager manager, int requests, ushort[] ids, ReadOnlySequence<byte> payload, int count)
{
    for (int i = 0; i < count; i++)
        manager.DispatchChunkAsync(i % requests + 1, ids[(i / requests) % ids.Length], payload).GetAwaiter().GetResult();
}

#if !NATIVE_AOT
[MethodImpl(MethodImplOptions.NoInlining)]
static int RunKnownBatch(Func<ushort, int> call, ushort[] ids, int count)
{
    int hits = 0;
    for (int i = 0; i < count; i++)
        hits += call(ids[i % ids.Length]);
    return hits;
}
#endif

internal sealed class NoOp : IStreamDispatcher
{
    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
    public void Complete(bool error, string? message) { }
    public void Complete(Exception? error) { }
}

internal sealed class ProbeRow
{
    public string? layer { get; init; }
    public int? requests { get; init; }
    public int? arity { get; init; }
    public int? repeat { get; init; }
    public double? ns { get; init; }
    public long? bytes { get; init; }
    public double? registrationBytesPerRequest { get; init; }
    public double? cleanupBytesPerRequest { get; init; }
    public double? individualTerminalLifecycleBytesPerRequest { get; init; }
}
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ProbeRow>))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
internal partial class ProbeJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
