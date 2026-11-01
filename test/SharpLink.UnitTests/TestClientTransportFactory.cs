using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using SharpLink.UnitTests.Client;

namespace SharpLink.UnitTests;

internal sealed class TestClientTransportFactory : IClientTransportFactory
{
    private static readonly KeyValuePair<long, RpcHash128>[] DefaultContractManifest =
    [
        new(8_101, new RpcHash128(0x6d756c7469636c75UL, 0x737465722d763031UL))
    ];

    private readonly ProtocolV2Capabilities _negotiatedCapabilities;
    private readonly KeyValuePair<long, RpcHash128>[] _contractManifest;
    private readonly string? _compressionProfile;

    internal TestClientTransportFactory(
        ProtocolV2Capabilities negotiatedCapabilities = ProtocolV2Capabilities.None,
        IEnumerable<KeyValuePair<long, RpcHash128>>? contractManifest = null,
        string? compressionProfile = null,
        bool acceptManualContractBindings = false)
    {
        if (acceptManualContractBindings)
            Connection.EnableManualContractBindings();
        _negotiatedCapabilities = negotiatedCapabilities;
        _contractManifest = contractManifest?.ToArray() ?? DefaultContractManifest;
        _compressionProfile = compressionProfile;
    }

    public TestTransportConnection Connection { get; } = new();
    public int ConnectCount => Volatile.Read(ref _connectCount);
    private int _connectCount;

    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCount);
        await Connection.InjectSuccessfulHandshakeAsync(
            _negotiatedCapabilities,
            _contractManifest,
            _compressionProfile,
            cancellationToken);
        return Connection;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

internal sealed class TestTransportConnection : ITransportConnection
{
    private static readonly SharpLinkProtocolOptions ProtocolLimits = new();
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly CallbackPipeWriter _output;
    private readonly Channel<TestSentFrame> _sentPackets = Channel.CreateUnbounded<TestSentFrame>();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _inboundWriteGate = new(1, 1);
    private readonly Task _observeOutputTask;
    private int _disposed;

    public TestTransportConnection()
    {
        _output = new CallbackPipeWriter(_outbound.Writer);
        _observeOutputTask = ObserveOutputAsync(_disposeCts.Token);
    }

    internal Protocol3BindingAuthority Authority { get; } = new(
        Guid.NewGuid(), Guid.NewGuid(), Protocol3BindingLimits.Default);
    private bool _acceptManualContractBindings;
    private ulong _manualBindingIncarnation;

    // Opt-in only. This controlled peer knows one explicitly registered fixture identity;
    // it is not a general server registry and never acknowledges request stream heads.
    internal void EnableManualContractBindings() => _acceptManualContractBindings = true;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public PipeReader Input => _inbound.Reader;
    public PipeWriter Output => _output;
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    internal void RunOnNextOutputBufferRequest(Action callback)
        => _output.RunOnNextBufferRequest(callback);

    internal async Task InjectSuccessfulHandshakeAsync(
        ProtocolV2Capabilities negotiatedCapabilities = ProtocolV2Capabilities.None,
        IEnumerable<KeyValuePair<long, RpcHash128>>? contractManifest = null,
        string? compressionProfile = null,
        CancellationToken cancellationToken = default)
    {
        negotiatedCapabilities |= ProtocolV2Negotiator.MandatoryCapabilities;
        using var responsePayload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(responsePayload, new ProtocolV2HandshakeResponse(
            ProtocolV2Constants.MinorVersion,
            negotiatedCapabilities,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            compressionProfile,
            Authority));
        await InjectFrameAsync(
            ProtocolV2FrameType.HandshakeResponse,
            ProtocolV2FrameFlags.None,
            0,
            responsePayload.WrittenMemory,
            cancellationToken);

        using var manifestPayload = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(
            manifestPayload,
            new ProtocolV2ContractManifest(1, contractManifest ?? []),
            ProtocolLimits);
        await InjectFrameAsync(
            ProtocolV2FrameType.ContractManifest,
            ProtocolV2FrameFlags.None,
            0,
            manifestPayload.WrittenMemory,
            cancellationToken);
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _inboundWriteGate.WaitAsync(linked.Token);
        try
        {
            using var writer = new PooledByteBufferWriter();
            var token = ProtocolV2FrameWriter.BeginFrame(writer, type, flags, requestId);
            writer.Write(payload.Span);
            ProtocolV2FrameWriter.EndFrame(writer, token);
            await _inbound.Writer.WriteAsync(writer.WrittenMemory, linked.Token);
            await _inbound.Writer.FlushAsync(linked.Token);
        }
        finally
        {
            _inboundWriteGate.Release();
        }
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
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>Reads the next frame the client wrote, in write order, or <c>null</c> on timeout.</summary>
    internal async Task<TestSentFrame?> TryReadNextSentFrameAsync(TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            return await _sentPackets.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var failures = new List<Exception>();
        Exception? observerFailure = null;
        try { _disposeCts.Cancel(); } catch (Exception error) { failures.Add(error); }
        await CleanupAsync(async () =>
        {
            // Join any current write before completing its writer. Cancelled external writers
            // may still unwind; do not dispose their synchronization object underneath them.
            await _inboundWriteGate.WaitAsync();
            try { await CompleteAsync(_inbound.Writer); }
            finally { _inboundWriteGate.Release(); }
        });
        await CleanupAsync(() => CompleteAsync(_output));
        try { await _observeOutputTask; }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { }
        catch (Exception error) { observerFailure = error; failures.Add(error); }
        await CleanupAsync(() => CompleteAsync(_inbound.Reader));
        await CleanupAsync(() => CompleteAsync(_outbound.Reader));
        _sentPackets.Writer.TryComplete(observerFailure);
        try { _disposeCts.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException(failures);

        async ValueTask CleanupAsync(Func<ValueTask> cleanup)
        {
            try { await cleanup(); } catch (Exception error) { failures.Add(error); }
        }
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
                {
                    _sentPackets.Writer.TryWrite(new TestSentFrame(header, payload.ToArray()));
                    if (_acceptManualContractBindings && header.Type == ProtocolV2FrameType.BindContract)
                        await ReplyToManualContractBindAsync(header, payload, cancellationToken);
                }
                if (result.IsCompleted)
                    return;
            }
            finally
            {
                _outbound.Reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
    }

    private async Task ReplyToManualContractBindAsync(ProtocolV2FrameHeader header,
        ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        var request = Protocol3BindingControlCodec.ReadBindContract(payload);
        Protocol3BindResult result;
        if (request.ContractId != ManualClientContractFixture.ContractId)
            result = Protocol3BindResult.Failure(Protocol3BindStatus.UnknownContract);
        else if (request.Hash != ManualClientContractFixture.AssemblyHash)
            result = Protocol3BindResult.Failure(Protocol3BindStatus.HashMismatch);
        else if (request.Requirement != Protocol3BindingRequirement.Compatible || request.Receipt is not null)
            result = Protocol3BindResult.Failure(Protocol3BindStatus.ScopeDenied);
        else
            result = new Protocol3BindResult(Protocol3BindStatus.Success,
                Protocol3BindingToken.Create(0, ++_manualBindingIncarnation, Authority.Limits.Slots),
                request.ContractId, request.Hash, 1, 1, Authority.ServerInstanceId, Authority.RegistryEpoch);
        using var writer = new PooledByteBufferWriter();
        Protocol3BindingControlCodec.WriteBindResult(writer, result, Authority.Limits.Slots);
        await InjectFrameAsync(ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None,
            header.RequestId, writer.WrittenMemory, cancellationToken);
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
