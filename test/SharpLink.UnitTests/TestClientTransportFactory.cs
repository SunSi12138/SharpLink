using System.Net;
using System.IO.Pipelines;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Channels;

namespace SharpLink.UnitTests;

internal sealed class TestClientTransportFactory : IClientTransportFactory
{
    private readonly ProtocolV2Capabilities _negotiatedCapabilities;

    internal TestClientTransportFactory(
        ProtocolV2Capabilities negotiatedCapabilities = ProtocolV2Capabilities.None)
    {
        _negotiatedCapabilities = negotiatedCapabilities;
    }

    public TestTransportConnection Connection { get; } = new();
    public int ConnectCount => Volatile.Read(ref _connectCount);
    private int _connectCount;

    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCount);
        var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
            ProtocolV2Constants.MinorVersion,
            _negotiatedCapabilities,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024));
        await Connection.InjectFrameAsync(
            ProtocolV2FrameType.HandshakeResponse,
            ProtocolV2FrameFlags.None,
            0,
            payload.WrittenMemory,
            cancellationToken);
        return Connection;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

internal sealed class TestTransportConnection : ITransportConnection
{
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly CallbackPipeWriter _output;
    private readonly Channel<TestSentFrame> _sentPackets = Channel.CreateUnbounded<TestSentFrame>();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _observeOutputTask;
    private int _disposed;

    public TestTransportConnection()
    {
        _output = new CallbackPipeWriter(_outbound.Writer);
        _observeOutputTask = ObserveOutputAsync(_disposeCts.Token);
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public PipeReader Input => _inbound.Reader;
    public PipeWriter Output => _output;
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    internal void RunOnNextOutputBufferRequest(Action callback)
        => _output.RunOnNextBufferRequest(callback);

    public Task InjectPacketAsync(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        long requestId,
        CancellationToken cancellationToken = default)
        => InjectFrameAsync(type, flags, unchecked((ulong)requestId), ReadOnlyMemory<byte>.Empty, cancellationToken);

    public Task InjectInt32ResponseAsync(
        long requestId,
        int value = 0,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return InjectFrameAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            unchecked((ulong)requestId),
            payload,
            cancellationToken);
    }

    public async Task InjectFrameAsync(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ulong requestId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(writer, type, flags, requestId);
        writer.Write(payload.Span);
        ProtocolV2FrameWriter.EndFrame(writer, token);
        await _inbound.Writer.WriteAsync(writer.WrittenMemory, cancellationToken);
        await _inbound.Writer.FlushAsync(cancellationToken);
    }

    public async Task<ProtocolV2FrameHeader> WaitForSentPacket(ProtocolV2FrameType type)
        => (await WaitForSentFrame(type)).Header;

    public async Task<TestSentFrame> WaitForSentFrame(ProtocolV2FrameType type)
    {
        while (true)
        {
            var frame = await _sentPackets.Reader.ReadAsync();
            if (frame.Header.Type == type)
                return frame;
        }
    }

    public async Task<bool> TryWaitForSentPacket(ProtocolV2FrameType type, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var frame = await _sentPackets.Reader.ReadAsync(timeoutCts.Token);
                if (frame.Header.Type == type)
                    return true;
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _disposeCts.Cancel();
        await CompleteAsync(_inbound.Writer);
        await CompleteAsync(_output);
        try
        {
            await _observeOutputTask;
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        await CompleteAsync(_inbound.Reader);
        await CompleteAsync(_outbound.Reader);
        _sentPackets.Writer.TryComplete();
        _disposeCts.Dispose();
    }

    private async Task ObserveOutputAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _outbound.Reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            try
            {
                while (ProtocolV2FrameParser.TryReadFrame(
                    ref buffer,
                    new SharpLinkProtocolOptions(),
                    out var header,
                    out var payload))
                    _sentPackets.Writer.TryWrite(new TestSentFrame(header, payload.ToArray()));
                if (result.IsCompleted)
                    return;
            }
            finally
            {
                _outbound.Reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
    }

    private static async ValueTask CompleteAsync(PipeWriter writer)
    {
        try
        {
            await writer.CompleteAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async ValueTask CompleteAsync(PipeReader reader)
    {
        try
        {
            await reader.CompleteAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class CallbackPipeWriter(PipeWriter inner) : PipeWriter
    {
        private Action? _nextBufferRequest;

        internal void RunOnNextBufferRequest(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (Interlocked.CompareExchange(ref _nextBufferRequest, callback, null) is not null)
                throw new InvalidOperationException("an output buffer callback is already armed");
        }

        public override void Advance(int bytes) => inner.Advance(bytes);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null)
            => inner.CompleteAsync(exception);

        public override ValueTask<FlushResult> FlushAsync(
            CancellationToken cancellationToken = default)
            => inner.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            RunCallbackIfArmed();
            return inner.GetMemory(sizeHint);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            RunCallbackIfArmed();
            return inner.GetSpan(sizeHint);
        }

        private void RunCallbackIfArmed()
            => Interlocked.Exchange(ref _nextBufferRequest, null)?.Invoke();
    }
}

internal readonly record struct TestSentFrame(
    ProtocolV2FrameHeader Header,
    byte[] Payload);
