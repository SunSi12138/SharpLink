namespace SharpLink.Server;

public class SharpLinkServer(
    ITransport transport,
    ISerializer serializer,
    FrozenDictionary<long, (IRpcStub stub,object service)> services,
    TimeSpan heartbeatCheckInterval,
    TimeSpan heartbeatTimeout,
    SharpLinkLoggingOptions loggingOptions) : IDisposable,ISharpLinkServer
{
    private readonly ConcurrentDictionary<string, IRpcSession> _sessions = [];
    private readonly ILogger _logger = (loggingOptions ?? throw new ArgumentNullException(nameof(loggingOptions))).LoggerFactory.CreateLogger<SharpLinkServer>();
    private readonly LogLevel _minimumLogLevel = loggingOptions.MinimumLogLevel;

    //TODO:允许自定义验证
    private static bool AuthValidator(string s)
    {
        var res = !string.IsNullOrEmpty(s);
        return res;
    }
    public async Task Start(CancellationToken ct = default)
    {
        _ = Task.Run(()=>HeartbeatCheckLoop(ct), ct);
        
        while (!ct.IsCancellationRequested)
        {
            var session = await transport.ConnectAsync(serializer,ct);

            if (_sessions.TryGetValue(session.Id, out var oldSession))
            {
                oldSession.Dispose();
            }
            _sessions[session.Id] = session;
            
            _ = Task.Run(async () =>
            {
                var res = await ProcessHandshakeAsync(session, ct);
                if (!res)
                {
                    Disconnect();
                    return;
                }
                if (IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Client {SessionId} connected", session.Id);
                await ProcessRequestLoop(session, ct);
                Disconnect();
                return;

                void Disconnect()
                {
                    _sessions.TryRemove(session.Id, out var rpcSession);
                    rpcSession?.Dispose();
                }
            }, ct);
        }
    }

    private async Task HeartbeatCheckLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(heartbeatCheckInterval,ct);
            var now = DateTime.UtcNow;
            foreach (var (id,session) in _sessions)
            {
                if (now - session.LastActive <= heartbeatTimeout || !session.IsConnected)
                    continue;
                
                if (IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Client {SessionId} disconnected due to heartbeat timeout", session.Id);
                
                if(_sessions.TryRemove(id,out var oldSession))
                    oldSession.Dispose();
            }
        }
    }
    private static bool HandshakeVerify(ReadOnlySequence<byte> message)=>AuthValidator(Encoding.UTF8.GetString(message));
    private static async Task<bool> ProcessHandshakeAsync(IRpcSession session, CancellationToken ct)
    {
        
        var reader = session.Input;
        bool? handshakeResult = null;

        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer =  result.Buffer;
            while (PacketHelper.TryReadMessage(ref buffer,out var header, out var message))
            {
                var verified = header.Type == PacketType.Handshake && HandshakeVerify(message);
                
                await session.SendStringPacketAsync(PacketType.Handshake,verified?PacketFlags.None:PacketFlags.IsError,header.RequestId,"handshake fail");
            
                handshakeResult = verified;
                break;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
            
            if(handshakeResult.HasValue)
                return handshakeResult.Value;
            
            if(result.IsCompleted)
                break;
        }

        return false;
    }
    private async Task ProcessRequestLoop(IRpcSession session,CancellationToken ct)
    {
        var reader = session.Input;
        var requestCancellationMap = new ConcurrentDictionary<long, CancellationTokenSource>();
        try
        {
            //处理握手
            while (session.IsConnected && !ct.IsCancellationRequested)
            {
                // 1. 等待数据读取
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                try
                {
                    // 2. 循环解析 buffer 中的数据包 (可能包含多个包)
                    while (PacketHelper.TryReadMessage(ref buffer,out var header, out var payload))
                    {
                        session.LastActive = DateTime.UtcNow;
                        // 3. 处理完整的消息 (这里不需要 await 阻塞网络读取，最好由 Task.Run 处理业务)
                        // 注意：messagePayload 在 Advance 之后就会失效，如果需要异步处理，必须 Copy
                        switch (header.Type)
                        {
                            case PacketType.Heartbeat:
                                if (IsEnabled(LogLevel.Debug))
                                    _logger.LogDebug("Receive heartbeat from client {SessionId}", session.Id);
                                session.LastActive = DateTime.UtcNow;
                                await session.SendPacketAsync(PacketType.Heartbeat,PacketFlags.None,header.RequestId);
                                break;
                            case PacketType.RpcCall:
                                //TODO:使用Channel进行异步处理防止大量的Task频繁创建
                                _ =  DispatchRpcAsync(session, header.RequestId, header.Flags, payload, requestCancellationMap, ct); 
                                break;
                            case PacketType.Cancel:
                                if (requestCancellationMap.TryRemove(header.RequestId, out var cts))
                                {
                                    await cts.CancelAsync();
                                    cts.Dispose();
                                }
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
                            case PacketType.DisConnect:
                            case PacketType.Handshake:
                            case PacketType.Error:
                            case PacketType.RpcResponse:
                            default:
                            {
                                session.Dispose();
                                break;
                            }
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
        }
        finally
        {
            foreach (var (_, cts) in requestCancellationMap)
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
            requestCancellationMap.Clear();
        }
    }
    
    private async Task DispatchRpcAsync(
        IRpcSession session,
        long requestId,
        PacketFlags flags,
        ReadOnlySequence<byte> payload,
        ConcurrentDictionary<long, CancellationTokenSource> requestCancellationMap,
        CancellationToken serverLoopToken)
    {
        var isOneWay = flags.HasFlag(PacketFlags.IsOneWay);
        var isCancellable = flags.HasFlag(PacketFlags.IsCancellable);
        
        if(payload.Length<ProtocolConstants.RequestHeaderLength) return;
        

        var reader = new SequenceReader<byte>(payload);
        reader.TryReadLittleEndian(out long interfaceHash);
        reader.TryReadLittleEndian(out long methodHash);
        
        var argsPayload = payload.Slice(ProtocolConstants.RequestHeaderLength);
        if (!services.TryGetValue(interfaceHash, out var serviceInfo))
        {
            if (!isOneWay)
                await session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,$"Service {interfaceHash} not found.");
            return;
        }

        CancellationTokenSource? linkedCts = null;
        var invokeToken = serverLoopToken;
        if (isCancellable)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverLoopToken);
            requestCancellationMap[requestId] = linkedCts;
            invokeToken = linkedCts.Token;
        }

        try
        {
            if (isOneWay)
            {
                var discard = BufferWriterPool.Get();
                try
                {
                    await serviceInfo.stub.InvokeAsync(serviceInfo.service, session, methodHash,requestId, argsPayload, discard, invokeToken);
                }
                finally
                {
                    BufferWriterPool.Return(discard);
                }
            }
            else
            {
                var writer = BufferWriterPool.Get();
                var token = writer.BeginPacket(PacketType.RpcResponse, PacketFlags.None, requestId);
                await serviceInfo.stub.InvokeAsync(serviceInfo.service, session, methodHash,requestId, argsPayload, writer, invokeToken);
                writer.EndPacket(token);
                await session.SendPacketAsync(writer);
            }
        }
        catch (OperationCanceledException)
        {
            if (!isOneWay)
                await session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,"Request canceled.");
        }
        catch (Exception e)
        {
            if (!isOneWay)
                await session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,e.Message);
        }
        finally
        {
            linkedCts?.Dispose();
            requestCancellationMap.TryRemove(requestId, out _);
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
        transport.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool IsEnabled(LogLevel level) => level >= _minimumLogLevel && _logger.IsEnabled(level);

}
