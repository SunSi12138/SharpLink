using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Runtime;

var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
var options = new RpcSessionCreationOptions(RpcSessionRole.Client, context);
Console.WriteLine("scenario|phase|owner|operation|site|attempts|cas_successes");
foreach (var scenario in new[] { "default-busy", "named-busy", "named-mailbox-retained", "default-mailbox-retained" })
{
    PooledAsyncStreamDispatcher<byte[]>.ClearPoolForTests();
    var input = new Pipe(); var output = new Pipe();
    await using var session = new RpcSession(new Transport(input.Reader, output.Writer), options);
    if (!session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl, context.Protocol.MaxFramePayloadBytes, 8192, 16384, null)))
        throw new Exception("handshake failed");
    ushort streamId = scenario.StartsWith("default", StringComparison.Ordinal) ? (ushort)0 : (ushort)1;
    var items = scenario.StartsWith("named100", StringComparison.Ordinal) ? 100 : 1;
    var payload = new ReadOnlySequence<byte>(new byte[16]);
    LifecycleDiagnosticCounters.Reset();
    LifecycleDiagnosticCounters.Phase = "rent";
    var dispatcher = PooledAsyncStreamDispatcher<byte[]>.Rent(default, BytesCodec.Instance);
    LifecycleDiagnosticCounters.Phase = "register";
    var mailbox = scenario.EndsWith("mailbox-retained", StringComparison.Ordinal);
    if (mailbox)
    {
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        if (streamId == 0) session.StreamManager.Register(1, 0,
            new PreAdmissionStreamDispatcher(buffers, static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true));
        else session.StreamManager.ReservePreAdmissionStreams(1, 1,
            buffers, static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true);
    }
    session.StreamManager.Register(1, streamId, dispatcher);
    LifecycleDiagnosticCounters.Phase = "enumerator";
    var consumer = dispatcher.GetAsyncEnumerator();
    if (scenario.EndsWith("-busy", StringComparison.Ordinal))
    {
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        BytesCodec.BeforeDeserialize = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new Exception("missing busy release"); };
        LifecycleDiagnosticCounters.Phase = "busy-debit";
        var dispatch = Task.Run(async () => await session.StreamManager.DispatchChunkAsync(1, streamId, payload));
        if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new Exception("no owned DATA");
        LifecycleDiagnosticCounters.Phase = "complete-stream";
        session.StreamManager.CompleteStream(1, streamId, null);
        LifecycleDiagnosticCounters.Phase = "busy-release";
        resume.Set(); await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
        BytesCodec.BeforeDeserialize = null;
    }
    else
    {
        LifecycleDiagnosticCounters.Phase = "first-data-consume";
        await session.StreamManager.DispatchChunkAsync(1, streamId, payload);
        if (!await consumer.MoveNextAsync()) throw new Exception("missing mailbox item");
        LifecycleDiagnosticCounters.Phase = "complete-stream";
        session.StreamManager.CompleteStream(1, streamId, null);
        if (session.StreamManager.ActiveStreamCount != 1) throw new Exception("retained mailbox must remain registered");
        LifecycleDiagnosticCounters.Phase = "mailbox-unregister";
        session.StreamManager.Unregister(1, streamId);
    }
    LifecycleDiagnosticCounters.Phase = "consumer-dispose";
    if (await consumer.MoveNextAsync()) throw new Exception("unexpected final item");
    await consumer.DisposeAsync();
    LifecycleDiagnosticCounters.Phase = null;
    if (session.StreamManager.ActiveStreamCount != 0 || dispatcher.HasRetainedReferencesForTests ||
        PooledAsyncStreamDispatcher<byte[]>.RetainedCountForTests != 1)
        throw new Exception("route/dispatcher must retire and return cleanly");
    LifecycleDiagnosticCounters.Dump(scenario);
    await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    var result = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    var buffer = result.Buffer;
    var credit = 0;
    while (ProtocolV2FrameParser.TryReadFrame(ref buffer, context.Protocol, out var header, out var frame))
    {
        if (header.Type != ProtocolV2FrameType.WindowUpdate || header.RequestId != 1) throw new Exception("unexpected frame");
        var update = ProtocolV2PayloadCodec.ReadWindowUpdate(frame);
        if (update.StreamId != streamId) throw new Exception("wrong stream credit");
        credit += checked((int)update.Credit);
    }
    if (!buffer.IsEmpty || credit != items * 16) throw new Exception("credit did not balance");
    output.Reader.AdvanceTo(result.Buffer.End);
    await session.DisposeAsync(); await output.Reader.CompleteAsync(); await input.Writer.CompleteAsync();
}
sealed class Transport(PipeReader input, PipeWriter output) : ITransportConnection
{
    public string Id => "known-entry-lifecycle-counter";
    public PipeReader Input => input; public PipeWriter Output => output;
    public EndPoint? LocalEndPoint => null; public EndPoint? RemoteEndPoint => null;
    public async ValueTask DisposeAsync() { await Output.CompleteAsync(); await Input.CompleteAsync(); }
}
sealed class BytesCodec : IRpcCodec<byte[]>
{
    internal static readonly BytesCodec Instance = new();
    internal static Action? BeforeDeserialize;
    public byte[] Deserialize(in ReadOnlySequence<byte> payload) { BeforeDeserialize?.Invoke(); return Array.Empty<byte>(); }
    public void Serialize(in byte[] value, IBufferWriter<byte> writer) { writer.GetSpan(16)[..16].Clear(); writer.Advance(16); }
}
