using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using SharpLink.Sdk;


namespace SharpLink.Client;

internal sealed class SharpLinkClient(ITransport transport, ISerializer serializer) : IRpcChannel, IDisposable, ISharpLinkClient
{
    private readonly ConcurrentDictionary<long, byte> _serverStreamRequestIds = [];
    private readonly ConcurrentDictionary<long, byte> _locallyCanceledRequestIds = [];
    private readonly RequestManager _requestManager = new();
    private IRpcSession? _session;
    private int _disconnectHandled;
    private bool _disposed;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger _logger = NullLogger<SharpLinkClient>.Instance;
    private readonly LogLevel _minimumLogLevel = LogLevel.Warning;

    public SharpLinkClient(ITransport transport, ISerializer serializer, TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout)
        : this(transport, serializer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        if (heartbeatTimeout <= heartbeatInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");

        _heartbeatInterval = heartbeatInterval;
        _heartbeatTimeout = heartbeatTimeout;
    }

    public SharpLinkClient(
        ITransport transport,
        ISerializer serializer,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        SharpLinkLoggingOptions loggingOptions)
        : this(transport, serializer, heartbeatInterval, heartbeatTimeout)
    {
        ArgumentNullException.ThrowIfNull(loggingOptions);
        _logger = loggingOptions.LoggerFactory.CreateLogger<SharpLinkClient>();
        _minimumLogLevel = loggingOptions.MinimumLogLevel;
    }
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        _session = await transport.ConnectAsync(serializer,ct);
        var res = await ProcessHandshakeAsync(_session,ct);
        if(!res) return false;
        _ = HeartbeatSendLoop(_session,ct);
        _ = ProcessRequestLoop(_session,ct);
        return true;
    }
    public T Get<T>() where T : IService
    {
        if (GeneratedProxyRegistry.TryCreate(typeof(T), this, serializer, out var proxy))
            return (T)proxy!;

        throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
    }
    

    private static async Task<bool> ProcessHandshakeAsync(IRpcSession session, CancellationToken ct)
    {
        await session.SendStringPacketAsync(PacketType.Handshake, PacketFlags.None,0,"Password");
        
        var reader = session.Input;
        while (session.IsConnected && !ct.IsCancellationRequested )
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            bool? handshakeResult = null;
            while (PacketHelper.TryReadMessage(ref buffer, out var header, out var payload))
            {
                handshakeResult = header.Type == PacketType.Handshake && !header.Flags.HasFlag(PacketFlags.IsError);
                break;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (handshakeResult.HasValue)
                return handshakeResult.Value;
            
            if(result.IsCompleted)
                break;
        }
        return false;
    }
    private async Task ProcessRequestLoop(IRpcSession session, CancellationToken ct)
    {
        var reader = session.Input;
        
        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            // 1. 等待数据读取
            ReadResult result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            try 
            {
                // 2. 循环解析 buffer 中的数据包 (可能包含多个包)
                while (PacketHelper.TryReadMessage(ref buffer,out var header, out var payload))
                {
                    session.LastActive = DateTime.UtcNow;
                    switch (header.Type)
                    {
                        case PacketType.Heartbeat:
                            if (IsEnabled(LogLevel.Debug))
                                _logger.LogDebug("Receive heartbeat from server {SessionId}", session.Id);
                            session.LastActive = DateTime.UtcNow;
                            break;
                        case PacketType.RpcResponse:
                            DispatchRpc(header.RequestId,header.Flags,ref payload);
                            break;
                        case PacketType.StreamChunk:
                            await DispatchStreamChunkAsync(session, header.RequestId, payload);
                            break;
                        case PacketType.StreamComplete:
                            DispatchStreamComplete(session, header.RequestId, payload);
                            break;
                        case PacketType.StreamError:
                            DispatchStreamError(session, header.RequestId, payload);
                            break;
                        case PacketType.RpcCall:
                        case PacketType.Cancel:
                        case PacketType.Error:
                        case PacketType.DisConnect:
                        case PacketType.Handshake:
                        default:
                            session.Dispose();
                            HandleDisconnected(new IOException("Received unexpected packet from server."));
                            break;
                    }
                }
                // 4. 告诉 Pipe 我们消费到了哪里
                if (result.IsCompleted) break;
            }
            finally
            {
                // 移动游标：buffer.Start 是我们没处理完的起始位置
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        HandleDisconnected(new IOException("Server disconnected."));
    }
    
    private async Task HeartbeatSendLoop(IRpcSession session,CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await session.SendPacketAsync(PacketType.Heartbeat, PacketFlags.None, 0);
            await Task.Delay(_heartbeatInterval,ct);
            var now = DateTime.UtcNow;
            if (now - session.LastActive < _heartbeatTimeout && session.IsConnected)
                continue;
            
            if (IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Server disconnected due to heartbeat timeout.");
            
            session.Dispose();
            HandleDisconnected(new IOException("Server heartbeat timeout."));
            break;
        }
    }
    

    private void DispatchRpc(long requestId, PacketFlags flags,ref ReadOnlySequence<byte> payload)
    {
        var isError = (flags.HasFlag(PacketFlags.IsError));

        if (isError)
        {
            if (_requestManager.DispatchError(requestId, new Exception(Encoding.UTF8.GetString(payload))))
                return;

            if (_serverStreamRequestIds.TryRemove(requestId, out _))
            {
                var message = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Stream request failed.";
                _session?.StreamManager.CompleteStream(requestId, 0, true, message);
                return;
            }
        }
        else
        {
            if (_requestManager.Dispatch(requestId, ref payload, serializer))
                return;

            // Server-stream 调用会收到一个 RpcResponse ACK，这里直接吞掉。
            if (_serverStreamRequestIds.TryRemove(requestId, out _))
                return;
        }

        if (_locallyCanceledRequestIds.TryRemove(requestId, out _))
            return;

        if (IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Response for unknown or timed-out request ID: {RequestId}", requestId);
    }

    public ValueTask<T> InvokeAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false);

    public ValueTask<T> InvokeNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, null, false);

    public ValueTask<T> InvokeCancellableAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, cancellationToken);

    public ValueTask<T> InvokeCancellableNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, cancellationToken);

    public async ValueTask InvokeOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true);

    public async ValueTask InvokeOneWayNoPayloadAsync(long interfaceHash, long methodHash)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, null, true);

    public async ValueTask InvokeOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true);

    public async ValueTask InvokeOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true);

    public async ValueTask InvokeCancellableOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, cancellationToken);

    public async ValueTask InvokeCancellableOneWayNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, null, true, cancellationToken);

    public async ValueTask InvokeCancellableOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, cancellationToken);

    public async ValueTask InvokeCancellableOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, cancellationToken);

    public ValueTask<T> InvokeClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false);

    public ValueTask<T> InvokeClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false);

    public ValueTask<T> InvokeCancellableClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, cancellationToken);

    public ValueTask<T> InvokeCancellableClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, cancellationToken);

    public IAsyncEnumerable<T> InvokeServerStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null);

    public IAsyncEnumerable<T> InvokeServerStreamNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null);

    public IAsyncEnumerable<T> InvokeCancellableServerStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, cancellationToken);

    public IAsyncEnumerable<T> InvokeCancellableServerStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, cancellationToken);

    public IAsyncEnumerable<T> InvokeDuplexStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender);

    public IAsyncEnumerable<T> InvokeDuplexStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender);

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, cancellationToken);

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender, cancellationToken);

    private async ValueTask<T> InvokeCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool isOneWay)
    {
        var requestId = isOneWay ? _requestManager.AllocateRequestId() : 0;
        RpcRequestOperation<T>? op = null;
        if (!isOneWay)
        {
            op = _requestManager.Rent<T>(out requestId);
        }

        var packetFlags = isOneWay ? PacketFlags.IsOneWay : PacketFlags.None;
        await SendRpcCallAsync(interfaceHash, methodHash, requestId, packetFlags, payloadWriter);

        if (streamSender is not null)
            _ = RunStreamSenderAsync(streamSender, requestId, default);

        if (isOneWay)
            return default!;

        return await op!.AsValueTask();
    }

    private async ValueTask<T> InvokeCancellableCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool isOneWay,
        CancellationToken ct)
    {
        var requestId = isOneWay ? _requestManager.AllocateRequestId() : 0;
        RpcRequestOperation<T>? op = null;
        if (!isOneWay)
        {
            op = _requestManager.Rent<T>(out requestId);
        }

        var packetFlags = isOneWay ? PacketFlags.IsOneWay : PacketFlags.None;
        if (ct.CanBeCanceled)
            packetFlags |= PacketFlags.IsCancellable;

        await using var cancelRegistration = RegisterCancel(ct, requestId, isOneWay);
        await SendRpcCallAsync(interfaceHash, methodHash, requestId, packetFlags, payloadWriter);

        if (streamSender is not null)
            _ = RunStreamSenderAsync(streamSender, requestId, ct);

        if (isOneWay)
            return default!;

        return await op!.AsValueTask();
    }

    private IAsyncEnumerable<T> InvokeServerStreamCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender)
    {
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.TryAdd(requestId, 0);
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _session!.StreamManager.Register(requestId, 0, new TypedStreamDispatcher<T>(channel.Writer, serializer));
        _ = StartServerStreamRequestAsync(
            interfaceHash,
            methodHash,
            requestId,
            payloadWriter,
            streamSender);

        return channel.Reader.ReadAllAsync();
    }

    private IAsyncEnumerable<T> InvokeCancellableServerStreamCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        CancellationToken cancellationToken)
    {
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.TryAdd(requestId, 0);
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _session!.StreamManager.Register(requestId, 0, new TypedStreamDispatcher<T>(channel.Writer, serializer));
        _ = StartCancellableServerStreamRequestAsync(
            interfaceHash,
            methodHash,
            requestId,
            payloadWriter,
            streamSender,
            cancellationToken);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private async Task StartServerStreamRequestAsync(
        long interfaceHash,
        long methodHash,
        long requestId,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender)
    {
        try
        {
            await SendRpcCallAsync(interfaceHash, methodHash, requestId, PacketFlags.None, payloadWriter);

            if (streamSender is not null)
                await streamSender(requestId, default);
        }
        catch (Exception ex)
        {
            _serverStreamRequestIds.TryRemove(requestId, out _);
            _session!.StreamManager.CompleteStream(requestId, 0, true, ex.Message);
        }
    }

    private async Task StartCancellableServerStreamRequestAsync(
        long interfaceHash,
        long methodHash,
        long requestId,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var cancelRegistration = RegisterStreamCancel(cancellationToken, requestId);
            var packetFlags = cancellationToken.CanBeCanceled
                ? PacketFlags.IsCancellable
                : PacketFlags.None;
            await SendRpcCallAsync(interfaceHash, methodHash, requestId, packetFlags, payloadWriter);

            if (streamSender is not null)
                await streamSender(requestId, cancellationToken);
        }
        catch (Exception ex)
        {
            _serverStreamRequestIds.TryRemove(requestId, out _);
            _session!.StreamManager.CompleteStream(requestId, 0, true, ex.Message);
        }
    }

    private CancellationTokenRegistration RegisterCancel(CancellationToken ct, long requestId, bool isOneWay)
    {
        if (!ct.CanBeCanceled)
            return default;

        return ct.Register(() =>
        {
            _locallyCanceledRequestIds.TryAdd(requestId, 0);
            _ = _session?.SendCancelAsync(requestId);
            if (!isOneWay)
                _requestManager.DispatchError(requestId, new OperationCanceledException(ct));
        });
    }

    private CancellationTokenRegistration RegisterStreamCancel(CancellationToken ct, long requestId)
    {
        if (!ct.CanBeCanceled)
            return default;

        return ct.Register(() =>
        {
            _locallyCanceledRequestIds.TryAdd(requestId, 0);
            _ = _session?.SendCancelAsync(requestId);
            _session?.StreamManager.CompleteStream(requestId, 0, true, "Canceled");
        });
    }

    private async Task SendRpcCallAsync(
        long interfaceHash,
        long methodHash,
        long requestId,
        PacketFlags flags,
        Action<IBufferWriter<byte>>? payloadWriter)
    {
        var writer = BufferWriterPool.Get();
        using (writer.BeginPacketScope(PacketType.RpcCall, flags, requestId))
        {
            var span = writer.GetSpan(ProtocolConstants.RequestHeaderLength);
            BinaryPrimitives.WriteInt64LittleEndian(span, interfaceHash);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], methodHash);
            writer.Advance(ProtocolConstants.RequestHeaderLength);
            payloadWriter?.Invoke(writer);
        }

        try
        {
            await _session!.SendPacketAsync(writer);
        }
        catch
        {
            BufferWriterPool.Return(writer);
            throw;
        }
    }

    public async Task SendClientStreamAsync<T>(long requestId, sbyte streamId, IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken))
            {
                await _session!.SendStreamChunkAsync(requestId, streamId, item);
            }

            await _session!.SendStreamCompleteAsync(requestId, streamId);
        }
        catch (Exception ex)
        {
            await _session!.SendStreamErrorAsync(requestId, streamId, ex.Message);
            throw;
        }
    }

    private async Task RunStreamSenderAsync(Func<long, CancellationToken, Task> streamSender, long requestId, CancellationToken ct)
    {
        try
        {
            await streamSender(requestId, ct);
        }
        catch (Exception ex)
        {
            _requestManager.DispatchError(requestId, ex);
        }
    }

    private static async Task DispatchStreamChunkAsync(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (reader.TryRead(out var streamIdRaw))
        {
            var streamId = unchecked((sbyte)streamIdRaw);
            var streamPayload = payload.Slice(sizeof(sbyte));
            await session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
            return;
        }

        await session.StreamManager.DispatchChunkAsync(requestId, payload);
    }

    private static void DispatchStreamComplete(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var streamId = TryReadStreamId(ref payload);
        session.StreamManager.CompleteStream(requestId, streamId, false, null);
    }

    private static void DispatchStreamError(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var streamId = TryReadStreamId(ref payload);
        var message = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Remote Error";
        session.StreamManager.CompleteStream(requestId, streamId, true, message);
    }

    private static sbyte TryReadStreamId(ref ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryRead(out var streamIdRaw))
            return 0;

        var streamId = unchecked((sbyte)streamIdRaw);
        payload = payload.Slice(sizeof(sbyte));
        return streamId;
    }

    public void Dispose()
    { 
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _session?.Dispose();
        HandleDisconnected(new ObjectDisposedException(nameof(SharpLinkClient)));
        transport.Dispose();
    }

    private void HandleDisconnected(Exception ex)
    {
        if (Interlocked.Exchange(ref _disconnectHandled, 1) != 0)
            return;

        if (IsEnabled(LogLevel.Information))
            _logger.LogInformation(ex, "Client disconnected.");

        _requestManager.FailAll(ex);
        _session?.StreamManager.CompleteAll(true, ex.Message);
    }

    private bool IsEnabled(LogLevel level) => level >= _minimumLogLevel && _logger.IsEnabled(level);
}
