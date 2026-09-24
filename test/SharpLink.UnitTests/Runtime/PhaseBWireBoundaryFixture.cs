using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.FlowStatePhaseB;

namespace SharpLink.UnitTests.Runtime;

// Real StreamData -> negotiated receiver -> actual WindowUpdate bytes -> model
// owner. This is a wire/accounting boundary fixture, not the production request loop.
internal sealed class PhaseBWireBoundaryFixture : IAsyncDisposable
{
    private readonly Pipe _receiverInput = new();
    private readonly Pipe _creditOutput = new();
    internal readonly PhaseBWriterBoundaryFixture Sender;
    internal readonly RpcSession Receiver;
    internal readonly record struct ReturnedCredit(long RequestId, ProtocolV2WindowUpdate Update,
        GrantAuthority.WireObservation Observation);

    internal PhaseBWireBoundaryFixture(int window = 64)
    {
        Sender = new PhaseBWriterBoundaryFixture(window);
        Receiver = RpcSessionTestFixture.CreateSessionOverTestTransport("phase-b-wire-receiver",
            _receiverInput.Reader, _creditOutput.Writer, RpcSessionTestFixture.ServerOptions(
                flushOptions: new RpcSessionFlushOptions(1, TimeSpan.MaxValue)), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(Receiver, ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: window, connectionReceiveWindowBytes: window);
    }

    internal async Task<GrantAuthority.Lease> OpenAsync(long requestId, ushort streamId = 1)
    {
        var lease = await Sender.Credits.OpenAsync(requestId, streamId);
        Receiver.StreamManager.Register(requestId, streamId, new Consumer());
        return lease;
    }

    internal async Task ReceiveAsync(PhaseBWriterBoundaryFixture.Ticket ticket)
    {
        // This is a copy of bytes read from the actual sender transport. ExpectedWire
        // is used only by the existing sender assertion, never as receiver input.
        var frame = new ReadOnlySequence<byte>(await Sender.ReadFrameBytesAsync(ticket));
        if (!ProtocolV2FrameParser.TryReadFrame(ref frame, Receiver.RuntimeContext.Protocol,
                out var header, out var payload) || header.Type != ProtocolV2FrameType.StreamData || !frame.IsEmpty)
            throw new InvalidDataException("Expected exactly one actual StreamData frame.");
        var streamId = ReadStreamId(payload);
        await Receiver.StreamManager.DispatchChunkAsync(unchecked((long)header.RequestId),
            streamId, payload.Slice(sizeof(ushort)));
    }

    private static ushort ReadStreamId(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short bits)) throw new InvalidDataException("Missing stream id.");
        return unchecked((ushort)bits);
    }

    internal async Task<List<ReturnedCredit>> DrainCreditsAsync()
    {
        // Completion is a deterministic pump barrier; absence of bytes after it
        // means batching retained credit, not a timing-dependent empty read.
        await Receiver.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (!_creditOutput.Reader.TryRead(out var read)) return [];
        try { return await ReplayCreditsAsync(Sender.Credits, read.Buffer); }
        finally { _creditOutput.Reader.AdvanceTo(read.Buffer.End); }
    }

    internal static async Task<List<ReturnedCredit>> ReplayCreditsAsync(GrantAuthority owner,
        ReadOnlySequence<byte> frames)
    {
        var result = new List<ReturnedCredit>();
        while (!frames.IsEmpty)
        {
            if (!ProtocolV2FrameParser.TryReadFrame(ref frames, RpcSessionTestFixture.RuntimeContext.Protocol,
                    out var header, out var payload))
                throw new InvalidDataException("Truncated completed wire batch.");
            if (header.Type != ProtocolV2FrameType.WindowUpdate)
                throw new InvalidDataException("The receiver credit lane must contain only WindowUpdate.");
            var update = ProtocolV2PayloadCodec.ReadWindowUpdate(payload);
            var requestId = unchecked((long)header.RequestId);
            var observed = await owner.ObserveWindowUpdateAsync(requestId, update.StreamId, checked((int)update.Credit));
            result.Add(new ReturnedCredit(requestId, update, observed));
        }
        return result;
    }

    internal static byte[] CreditFrame(long requestId, ushort streamId, int credit)
    {
        using var packet = new PooledByteBufferWriter();
        using (packet.BeginPacketScope(ProtocolV2FrameType.WindowUpdate, ProtocolV2FrameFlags.None,
                   unchecked((ulong)requestId)))
            ProtocolV2PayloadCodec.WriteWindowUpdate(packet, new ProtocolV2WindowUpdate(streamId, checked((uint)credit)));
        return packet.WrittenMemory.ToArray();
    }

    private sealed class Consumer : IStreamConsumptionAwareDispatcher
    {
        private Action<long, ushort, int>? _consumed;
        private long _requestId;
        private ushort _streamId;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => DispatchAsync(payload, checked((int)payload.Length));
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            if (payload.Length != encodedByteCount) throw new InvalidDataException("Actual payload/credit size mismatch.");
            _consumed?.Invoke(_requestId, _streamId, encodedByteCount);
            return ValueTask.CompletedTask;
        }
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId)
        { _consumed = callback; _requestId = requestId; _streamId = streamId; }
        public void Complete(bool isError, string? message) { }
        public void Complete(Exception? exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await Sender.DisposeAsync();
        await Receiver.DisposeAsync();
        await _creditOutput.Reader.CompleteAsync();
        await _receiverInput.Writer.CompleteAsync();
    }
}
