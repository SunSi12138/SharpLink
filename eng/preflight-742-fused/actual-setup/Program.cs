using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Runtime;

const int Warmup = 256;
const int Count = 4096;
var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
var options = new RpcSessionCreationOptions(RpcSessionRole.Client, context);
Console.WriteLine("scenario,repeat,count,bytes_per_operation");
foreach (var route in new[] { false, true })
for (var repeat = 0; repeat < 3; repeat++)
{
    long total = 0;
    for (var i = -Warmup; i < Count; i++)
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = new Transport(input.Reader, output.Writer);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var session = new RpcSession(transport, options);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (route)
        {
            var sink = new Sink();
            before = GC.GetAllocatedBytesForCurrentThread();
            session.StreamManager.Register(1, sink);
            session.StreamManager.Unregister(1);
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        if (i >= 0) total += bytes;
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        output.Reader.Complete();
        input.Writer.Complete();
    }
    Console.WriteLine($"{(route ? "fresh-manager-empty-route" : "actual-session-constructor")},{repeat},{Count},{(double)total / Count:F2}");
}
var entryType = typeof(StreamManager).GetNestedType("DispatcherEntry", BindingFlags.NonPublic)!;
var holderType = entryType.GetNestedType("DispatcherEntryCompletions", BindingFlags.NonPublic)!;
foreach (var type in new[] { typeof(StreamManager), entryType, holderType })
{
    for (var i = 0; i < Warmup; i++) GC.KeepAlive(RuntimeHelpers.GetUninitializedObject(type));
    var samples = new long[Count];
    for (var i = 0; i < Count; i++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = RuntimeHelpers.GetUninitializedObject(type);
        samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(value);
    }
    Array.Sort(samples);
    Console.WriteLine($"layout-{type.Name},0,{Count},{samples[Count / 2]:F2}");
}
sealed class Transport(PipeReader input, PipeWriter output) : ITransportConnection
{
    public string Id => "actual-setup-allocation";
    public PipeReader Input => input;
    public PipeWriter Output => output;
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;
    public async ValueTask DisposeAsync() { await Output.CompleteAsync(); await Input.CompleteAsync(); }
}
sealed class Sink : IStreamReceiveCreditBoundDispatcher
{
    public void SetReceiveCreditBinding(StreamManager.DispatcherEntry? binding) { }
    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int count) => ValueTask.CompletedTask;
    public void Complete(bool isError, string? errorMessage) { }
    public void Complete(Exception? exception) { }
}
