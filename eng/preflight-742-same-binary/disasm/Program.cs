using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Runtime;

var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
var options = new RpcSessionCreationOptions(RpcSessionRole.Client, context);
Console.WriteLine("MODE=" + QuiescentCompletionDiagnostic.IsEnabled());
foreach (var scenario in new[] { "default-buffered", "default-waiting-terminal", "named-buffered", "named100-buffered" })
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
    var dispatcher = PooledAsyncStreamDispatcher<byte[]>.Rent(default, BytesCodec.Instance);
    session.StreamManager.Register(1, streamId, dispatcher);
    var consumer = dispatcher.GetAsyncEnumerator();
    for (var item = 0; item < items; item++)
    {
        await session.StreamManager.DispatchChunkAsync(1, streamId, payload);
        if (!await consumer.MoveNextAsync()) throw new Exception("missing item");
    }
    var pendingTerminal = scenario == "default-waiting-terminal";
    ValueTask<bool> pending = default;
    if (pendingTerminal)
    {
        pending = consumer.MoveNextAsync();
        if (pending.IsCompleted) throw new Exception("terminal wait must be pending");
    }
    session.StreamManager.CompleteStream(1, streamId, exception: null);
    if (pendingTerminal && await pending) throw new Exception("unexpected item");
    if (!pendingTerminal && await consumer.MoveNextAsync()) throw new Exception("unexpected final item");
    await consumer.DisposeAsync();
    if (session.StreamManager.ActiveStreamCount != 0 || dispatcher.HasRetainedReferencesForTests ||
        PooledAsyncStreamDispatcher<byte[]>.RetainedCountForTests != 1)
        throw new Exception("route/dispatcher must retire and return cleanly");
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
    Console.WriteLine("CASE_OK " + scenario);
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
    public byte[] Deserialize(in ReadOnlySequence<byte> payload) => Array.Empty<byte>();
    public void Serialize(in byte[] value, IBufferWriter<byte> writer) { writer.GetSpan(16)[..16].Clear(); writer.Advance(16); }
}
