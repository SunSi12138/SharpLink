using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.N1.StaticBindEvidence;

/// <summary>Hostile-input peer talking to the actual product server over TCP; no in-process Bind dispatch.</summary>
internal sealed class RawPeer : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private RawPeer(TcpClient tcp) { _tcp = tcp; _stream = tcp.GetStream(); }
    internal static async Task<RawPeer> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
        return new(tcp);
    }
    internal async Task<ProtocolV2HandshakeResponse?> HandshakeAsync(ushort minor = 6)
    {
        var writer = new ArrayBufferWriter<byte>();
        ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, new(minor,
            ProtocolV2Capabilities.ContractManifest | ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.CancellationReason,
            ProtocolV2Capabilities.ContractManifest, 4 * 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024, ReadOnlyMemory<byte>.Empty),
            new SharpLinkProtocolOptions());
        await SendAsync(ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0, writer.WrittenMemory).ConfigureAwait(false);
        var response = await ReadAsync().ConfigureAwait(false);
        Program.Check(response.Header.Type == ProtocolV2FrameType.HandshakeResponse, "raw-handshake-frame");
        if ((response.Header.Flags & ProtocolV2FrameFlags.Error) != 0) return null;
        var handshake = ProtocolV2PayloadCodec.ReadHandshakeResponse(new(response.Payload), new SharpLinkProtocolOptions());
        var manifest = await ReadAsync().ConfigureAwait(false);
        Program.Check(manifest.Header.Type == ProtocolV2FrameType.ContractManifest, "raw-bootstrap-manifest");
        var parsed = ProtocolV2ContractManifestCodec.Read(new(manifest.Payload), new SharpLinkProtocolOptions());
        Program.Check(parsed.ServerInstanceId == handshake.ServerInstanceId && parsed.RegistryEpoch == handshake.RegistryEpoch,
            "real-handshake-manifest-authority");
        return handshake;
    }
    internal async Task<ContractBindAck> BindAsync(ulong operation, ContractBindRequest request)
    {
        await SendAsync(ProtocolV2FrameType.BindContract, ProtocolV2FrameFlags.None, operation, ContractBindingWire.WriteRequest(request)).ConfigureAwait(false);
        var response = await ReadAsync().ConfigureAwait(false);
        Program.Check(response.Header.Type == ProtocolV2FrameType.BindAck && response.Header.RequestId == operation, "raw-bind-correlation-" + operation);
        return ContractBindingWire.ReadAck(new(response.Payload));
    }
    internal Task RequestAsync(ulong requestId, ulong token, long methodId, ProtocolV2FrameFlags flags)
    {
        var bytes = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, token); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), methodId);
        return SendAsync(ProtocolV2FrameType.Request, flags, requestId, bytes);
    }
    internal async Task SendAsync(ProtocolV2FrameType type, ProtocolV2FrameFlags flags, ulong id, ReadOnlyMemory<byte> payload)
    {
        var bytes = new byte[15 + payload.Length]; bytes[0] = 0x89;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), payload.Length); bytes[5] = (byte)type; bytes[6] = (byte)flags;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(7), id); payload.Span.CopyTo(bytes.AsSpan(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
    }
    internal async Task<ObservedFrame> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var header = new byte[15]; await _stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
            if (length < 0 || length > 4 * 1024 * 1024) throw new InvalidDataException("FIXTURE bounded frame length.");
            var payload = new byte[length]; await _stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
            var bytes = new byte[15 + length]; header.CopyTo(bytes, 0); payload.CopyTo(bytes, 15);
            var sequence = new ReadOnlySequence<byte>(bytes);
            if (!ProtocolV2FrameParser.TryReadFrame(ref sequence, new SharpLinkProtocolOptions(), out var parsed, out _))
                throw new InvalidDataException("FIXTURE incomplete complete frame.");
            if (parsed.Type == ProtocolV2FrameType.Ping)
            { await SendAsync(ProtocolV2FrameType.Pong, ProtocolV2FrameFlags.None, 0, payload).ConfigureAwait(false); continue; }
            return new("raw", false, parsed, payload);
        }
    }
    public ValueTask DisposeAsync() { _tcp.Dispose(); return ValueTask.CompletedTask; }
}
