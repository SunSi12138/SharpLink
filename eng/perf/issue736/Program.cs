using SharpLink.Runtime;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
const int N = 1000000;
var results = new List<ProbeRow>(); var payload = new ReadOnlySequence<byte>(new byte[1]); var noop = new NoOp();
#if !NATIVE_AOT
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var routeType = typeof(StreamManager).GetNestedType("RequestDispatchers", System.Reflection.BindingFlags.NonPublic)!;
var entryType = typeof(StreamManager).GetNestedType("DispatcherEntry", System.Reflection.BindingFlags.NonPublic)!;
var method = new System.Reflection.Emit.DynamicMethod("AcquireRelease", typeof(int), new[] { typeof(object), typeof(ushort) }, typeof(StreamManager), true);
var il = method.GetILGenerator(); var entry = il.DeclareLocal(entryType); var miss = il.DefineLabel();
il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Castclass, routeType); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Ldloca, entry); il.Emit(System.Reflection.Emit.OpCodes.Call, routeType.GetMethod("TryAcquire", flags)!); il.Emit(System.Reflection.Emit.OpCodes.Brfalse, miss); il.Emit(System.Reflection.Emit.OpCodes.Ldloc, entry); il.Emit(System.Reflection.Emit.OpCodes.Call, entryType.GetMethod("Release", flags)!); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1); il.Emit(System.Reflection.Emit.OpCodes.Ret); il.MarkLabel(miss); il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0); il.Emit(System.Reflection.Emit.OpCodes.Ret);
foreach (int arity in new[] { 0, 1, 2, 4, 8, 127 })
{
    var route = Activator.CreateInstance(routeType, true)!; var ids = arity == 0 ? new ushort[] { 0 } : Enumerable.Range(1, arity).Select(x => (ushort)x).ToArray();
    foreach (var id in ids) routeType.GetMethod("TryRegister", flags)!.Invoke(route, new object[] { id, noop });
    var call = (Func<ushort, int>)method.CreateDelegate(typeof(Func<ushort, int>), route); int hit = 0;
    for (int i = 0; i < 200000; i++) hit += call(ids[i % ids.Length]);
    for (int repeat = 0; repeat < 7; repeat++) { long b = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp(); for (int i = 0; i < N; i++) hit += call(ids[i % ids.Length]); var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / N; var allocated = GC.GetAllocatedBytesForCurrentThread() - b; results.Add(new ProbeRow { layer = "known-route", arity = arity, repeat = repeat, ns = elapsed, bytes = allocated }); }
    if (hit != 200000 + 7 * N)
        throw new InvalidOperationException("Known-route lookup missed a registered stream.");
    GC.KeepAlive(hit);
}

#endif
foreach (int requests in new[] { 1, 8, 32, 128 }) foreach (int arity in new[] { 0, 1, 2, 4, 8, 127 })
    {
        var manager = new StreamManager(); var ids = arity == 0 ? new ushort[] { 0 } : Enumerable.Range(1, arity).Select(x => (ushort)x).ToArray();
        for (int r = 1; r <= requests; r++) foreach (var id in ids) manager.Register(r, id, noop);
        for (int warm = 0; warm < 200000; warm++) manager.DispatchChunkAsync(warm % requests + 1, ids[warm % ids.Length], payload).GetAwaiter().GetResult();
        for (int repeat = 0; repeat < 5; repeat++)
        {
            long b = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < N; i++) manager.DispatchChunkAsync(i % requests + 1, ids[i % ids.Length], payload).GetAwaiter().GetResult();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / N; var allocated = GC.GetAllocatedBytesForCurrentThread() - b; results.Add(new ProbeRow { requests = requests, arity = arity, repeat = repeat, ns = elapsed, bytes = allocated });
        }
        if (manager.DroppedStreamFrames != 0 || manager.ActiveStreamCount != requests * ids.Length)
            throw new InvalidOperationException("Measured route workload did not preserve its registered identities.");
        manager.CompleteAll(null);
    }
foreach (int arity in new[] { 0, 1, 2, 4, 8, 127 })
{
    var manager = new StreamManager(); var ids = arity == 0 ? new ushort[] { 0 } : Enumerable.Range(1, arity).Select(x => (ushort)x).ToArray();
    manager.Register(-1, 0, noop); // initialize shared manager and map before request allocation measurement
    long b = GC.GetAllocatedBytesForCurrentThread(); for (int r = 1; r <= 1000; r++) foreach (var id in ids) manager.Register(r, id, noop);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - b; results.Add(new ProbeRow { arity = arity, registrationBytesPerRequest = allocated / 1000.0 });
    b = GC.GetAllocatedBytesForCurrentThread(); manager.CompleteAll(null); results.Add(new ProbeRow { arity = arity, cleanupBytesPerRequest = (GC.GetAllocatedBytesForCurrentThread() - b) / 1000.0 });
}
foreach (int arity in new[] { 0, 1, 2, 4, 8, 127 })
{
    var manager = new StreamManager(); var ids = arity == 0 ? new ushort[] { 0 } : Enumerable.Range(1, arity).Select(x => (ushort)x).ToArray();
    manager.Register(-1, 0, noop);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int r = 1; r <= 1000; r++) foreach (var id in ids) manager.Register(r, id, noop);
    for (int r = 1; r <= 1000; r++) foreach (var id in ids) manager.CompleteStream(r, id, null);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    results.Add(new ProbeRow { arity = arity, individualTerminalLifecycleBytesPerRequest = allocated / 1000.0 });
    manager.CompleteAll(null);
}
Console.WriteLine(JsonSerializer.Serialize(results, ProbeJsonContext.Default.ListProbeRow));
class NoOp : IStreamDispatcher { public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask; public void Complete(bool error, string? message) { } public void Complete(Exception? error) { } }

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
