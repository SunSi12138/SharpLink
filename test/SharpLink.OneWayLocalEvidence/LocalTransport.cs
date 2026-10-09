using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.OneWayLocalEvidence;

internal sealed class LocalTransport : IClientTransportFactory, ITransportConnection
{
    private readonly Pipe _input = new();
    private int _disposed;
    public CountingWriter Writer { get; } = new();
    public string Id => "oneway-local-evidence";
    public PipeReader Input => _input.Reader;
    public PipeWriter Output => Writer;
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var writer = new PooledByteBufferWriter();
        var frame = ProtocolV2FrameWriter.BeginFrame(writer, ProtocolV2FrameType.HandshakeResponse, 0, 0);
        ProtocolV2PayloadCodec.WriteHandshakeResponse(writer, new ProtocolV2HandshakeResponse(
            ProtocolV2Constants.MinorVersion, ProtocolV2Capabilities.ContractManifest,
            4 * 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024, null));
        ProtocolV2FrameWriter.EndFrame(writer, frame);
        frame = ProtocolV2FrameWriter.BeginFrame(writer, ProtocolV2FrameType.ContractManifest, 0, 0);
        var entries = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            .SelectMany(m => m.Contracts.Select(c => new KeyValuePair<long, RpcHash128>(c.ContractId, m.RpcAssemblyHash)));
        ProtocolV2ContractManifestCodec.Write(writer, new ProtocolV2ContractManifest(0, entries), new());
        ProtocolV2FrameWriter.EndFrame(writer, frame);
        await _input.Writer.WriteAsync(writer.WrittenMemory, cancellationToken);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Writer.ReleaseFlush();
        await _input.Writer.CompleteAsync();
        await _input.Reader.CompleteAsync();
    }
}

// Observable wire effects, parsed using the real protocol parser. No RPC server runs.
// Allocates only when a previously unseen batch size grows the buffer, outside steady state.
internal sealed class CountingWriter : PipeWriter
{
    private byte[] _buffer = new byte[1024 * 1024];
    private readonly SharpLinkProtocolOptions _limits = new();
    private int _written;
    private long _requests, _streams, _checksum;
    private TaskCompletionSource<FlushResult>? _flushGate;
    private int _heldFlushEntered;
    public long Requests => Volatile.Read(ref _requests);
    public long Streams => Volatile.Read(ref _streams);
    public long Checksum => Volatile.Read(ref _checksum);
    public bool HeldFlushEntered => Volatile.Read(ref _heldFlushEntered) != 0;
    public void HoldNextFlush()
    {
        Volatile.Write(ref _heldFlushEntered, 0);
        Volatile.Write(ref _flushGate, new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
    public void ReleaseFlush() => Interlocked.Exchange(ref _flushGate, null)?.TrySetResult(new(false, false));
    public override void Advance(int bytes) => _written += bytes;
    public override void CancelPendingFlush() => ReleaseFlush();
    public override void Complete(Exception? exception = null) => ReleaseFlush();
    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_buffer.Length - _written < sizeHint)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _written + sizeHint));
        return _buffer.AsMemory(_written);
    }
    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        var remaining = new ReadOnlySequence<byte>(_buffer.AsMemory(0, _written));
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, _limits, out var header, out var payload))
        {
            if (header.Type == ProtocolV2FrameType.Request)
            {
                if ((header.Flags & ProtocolV2FrameFlags.OneWay) == 0) throw new InvalidOperationException("Not a OneWay request.");
                var value = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(payload.Length - sizeof(int)).FirstSpan);
                if (value != 42) throw new InvalidOperationException("Request business payload differs.");
                Interlocked.Add(ref _checksum, checked((long)header.RequestId) + value);
                Interlocked.Increment(ref _requests);
            }
            else if (header.Type == ProtocolV2FrameType.StreamComplete)
                Interlocked.Increment(ref _streams);
            else if (header.Type is ProtocolV2FrameType.Cancel or ProtocolV2FrameType.Response or ProtocolV2FrameType.StreamData)
                throw new InvalidOperationException($"Unexpected frame: {header.Type}");
        }
        if (!remaining.IsEmpty) throw new InvalidOperationException("Partial frame in completed batch.");
        _written = 0;
        var gate = Volatile.Read(ref _flushGate);
        if (gate is not null) Volatile.Write(ref _heldFlushEntered, 1);
        return gate is null ? new(new FlushResult(false, false)) : new(gate.Task);
    }
}
