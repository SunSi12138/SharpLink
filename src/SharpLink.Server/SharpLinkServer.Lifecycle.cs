namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public async Task Start(CancellationToken ct = default)
    {
        _ = RunHeartbeatCheckLoopAsync(ct);
        
        while (!ct.IsCancellationRequested)
        {
            var session = await transport.ConnectAsync(ct);
            await ReplaceSessionAsync(session);
            _ = HandleSessionLifecycleAsync(session, ct);
        }
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private async Task RunHeartbeatCheckLoopAsync(CancellationToken ct)
    {
        try
        {
            await HeartbeatCheckLoop(ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            LogServerBackgroundLoopUnhandledException(_logger, nameof(HeartbeatCheckLoop), ex);
        }
    }

    private async Task HandleSessionLifecycleAsync(IRpcSession session, CancellationToken ct)
    {
        var hasConnected = false;
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        try
        {
            var res = await ProcessHandshakeAsync(session, ct);
            if (!res)
            {
                LogHandshakeFailed(_logger);
                return;
            }

            hasConnected = true;
            LogClientConnected(_logger);
            await ProcessRequestLoop(session, ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            LogServerBackgroundLoopUnhandledException(_logger, nameof(ProcessRequestLoop), ex);
        }
        finally
        {
            if (hasConnected)
                LogClientDisconnected(_logger);
            await DisconnectSessionAsync(session.Id);
        }
    }

    private async ValueTask ReplaceSessionAsync(IRpcSession session)
    {
        if (_sessions.TryGetValue(session.Id, out var oldSession))
            await oldSession.DisposeAsync();
        _sessions[session.Id] = session;
    }

    private async ValueTask DisconnectSessionAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out var rpcSession);
        if (rpcSession is not null)
            await rpcSession.DisposeAsync();
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
                
                using var sessionScope = BeginSessionLogScope(_logger, session.Id);
                LogClientHeartbeatTimeout(_logger);
                
                if(_sessions.TryRemove(id,out var oldSession))
                    await oldSession.DisposeAsync();
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
                
                session.SendStringPacketAsync(PacketType.Handshake,verified?PacketFlags.None:PacketFlags.IsError,header.RequestId,"handshake fail");
            
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
        var requestCancellationMap = new StripedLongMap<CancellationTokenSource>();
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
                                DebugLogClientHeartbeatReceived(_logger);
                                session.LastActive = DateTime.UtcNow;
                                session.SendPacketAsync(PacketType.Heartbeat,PacketFlags.None,header.RequestId);
                                break;
                            case PacketType.RpcCall:
                            {
                                using var requestScope = BeginRequestLogScope(_logger, header.RequestId);
                                if ((header.Flags & PacketFlags.IsOneWay) != 0)
                                {
                                    DispatchOneWayRpc(session, header.RequestId, header.Flags, payload, requestCancellationMap, ct);
                                    break;
                                }

                                var dispatchTask = DispatchRpcAsync(session, header.RequestId, header.Flags, payload, requestCancellationMap, ct);
                                if (!dispatchTask.IsCompletedSuccessfully)
                                    _ = AwaitDispatchAsync(dispatchTask, header.RequestId);
                                break;
                            }
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
                                await session.DisposeAsync();
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
            foreach (var cts in requestCancellationMap.DrainValues())
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
        }
    }

    private async Task AwaitDispatchAsync(ValueTask dispatchTask, long requestId)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await dispatchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogRpcDispatchUnhandledException(_logger, ex);
        }
    }

    private void DispatchOneWayRpc(
        IRpcSession session,
        long requestId,
        PacketFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        CancellationToken serverLoopToken)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        var isCancellable = (flags & PacketFlags.IsCancellable) != 0;
        if (payload.Length < ProtocolConstants.RequestHeaderLength)
            return;

        var reader = new SequenceReader<byte>(payload);
        reader.TryReadLittleEndian(out long interfaceHash);
        reader.TryReadLittleEndian(out long methodHash);

        var argsPayload = payload.Slice(ProtocolConstants.RequestHeaderLength);
        if (!services.TryGetValue(interfaceHash, out var serviceInfo))
            return;

        CancellationTokenSource? linkedCts = null;
        var invokeToken = serverLoopToken;
        if (isCancellable)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverLoopToken);
            requestCancellationMap.Set(requestId, linkedCts);
            invokeToken = linkedCts.Token;
        }

        try
        {
            var invokeTask = isCancellable
                ? serviceInfo.stub.InvokeNoReturnCancellableAsync(
                    serviceInfo.service,
                    session,
                    methodHash,
                    requestId,
                    argsPayload,
                    invokeToken)
                : serviceInfo.stub.InvokeNoReturnAsync(
                    serviceInfo.service,
                    session,
                    methodHash,
                    requestId,
                    argsPayload);
            if (invokeTask.IsCompletedSuccessfully)
            {
                ReleaseOneWayDispatchResources(linkedCts, requestId, requestCancellationMap);
                return;
            }

            _ = AwaitOneWayDispatchAsync(invokeTask, linkedCts, requestId, requestCancellationMap);
        }
        catch (Exception ex)
        {
            LogOnewayRpcDispatchFailed(_logger, ex);
            ReleaseOneWayDispatchResources(linkedCts, requestId, requestCancellationMap);
        }
    }

    private async Task AwaitOneWayDispatchAsync(
        ValueTask invokeTask,
        CancellationTokenSource? linkedCts,
        long requestId,
        StripedLongMap<CancellationTokenSource> requestCancellationMap)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOnewayRpcDispatchFailed(_logger, ex);
        }
        finally
        {
            ReleaseOneWayDispatchResources(linkedCts, requestId, requestCancellationMap);
        }
    }

    private static void ReleaseOneWayDispatchResources(
        CancellationTokenSource? linkedCts,
        long requestId,
        StripedLongMap<CancellationTokenSource> requestCancellationMap)
    {
        linkedCts?.Dispose();
        if (linkedCts is not null)
            requestCancellationMap.TryRemove(requestId, out _);
    }

    private ValueTask DispatchRpcAsync(
        IRpcSession session,
        long requestId,
        PacketFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        CancellationToken serverLoopToken)
    {
        var isCancellable = (flags & PacketFlags.IsCancellable) != 0;
        var hasReturnPayload = (flags & PacketFlags.HasReturn) != 0;
        
        if(payload.Length<ProtocolConstants.RequestHeaderLength) return ValueTask.CompletedTask;
        

        var reader = new SequenceReader<byte>(payload);
        reader.TryReadLittleEndian(out long interfaceHash);
        reader.TryReadLittleEndian(out long methodHash);
        
        var argsPayload = payload.Slice(ProtocolConstants.RequestHeaderLength);
        if (!services.TryGetValue(interfaceHash, out var serviceInfo))
        {
            session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,$"Service {interfaceHash} not found.");
            return ValueTask.CompletedTask;
        }

        CancellationTokenSource? linkedCts = null;
        var invokeToken = serverLoopToken;
        if (isCancellable)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverLoopToken);
            requestCancellationMap.Set(requestId, linkedCts);
            invokeToken = linkedCts.Token;
        }

        if (!hasReturnPayload)
        {
            try
            {
                var invokeTask = isCancellable
                    ? serviceInfo.stub.InvokeNoReturnCancellableAsync(serviceInfo.service, session, methodHash, requestId, argsPayload, invokeToken)
                    : serviceInfo.stub.InvokeNoReturnAsync(serviceInfo.service, session, methodHash, requestId, argsPayload);
                if (!invokeTask.IsCompletedSuccessfully)
                    return AwaitDispatchRpcNoReturnAsync(invokeTask, session, requestId, linkedCts, requestCancellationMap);
                session.SendPacketAsync(PacketType.RpcResponse, PacketFlags.None, requestId);
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
                return ValueTask.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,"Request canceled.");
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
                return ValueTask.CompletedTask;
            }
            catch (Exception e)
            {
                session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,e.Message);
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
                return ValueTask.CompletedTask;
            }
        }

        var writer = BufferWriterPool.Get();
        var token = writer.BeginPacket(PacketType.RpcResponse, PacketFlags.None, requestId);
        try
        {
            var invokeTask = isCancellable
                ? serviceInfo.stub.InvokeCancellableAsync(serviceInfo.service, session, methodHash, requestId, argsPayload, writer, invokeToken)
                : serviceInfo.stub.InvokeAsync(serviceInfo.service, session, methodHash, requestId, argsPayload, writer);
            if (!invokeTask.IsCompletedSuccessfully)
                return AwaitDispatchRpcAsync(invokeTask, session, requestId, writer, token, linkedCts,
                    requestCancellationMap);
            writer.EndPacket(token);
            session.SendPacket(writer);
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
            return ValueTask.CompletedTask;

        }
        catch (OperationCanceledException)
        {
            BufferWriterPool.Return(writer);
            session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,"Request canceled.");
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
            return ValueTask.CompletedTask;
        }
        catch (Exception e)
        {
            BufferWriterPool.Return(writer);
            session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,e.Message);
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
            return ValueTask.CompletedTask;
        }
    }

    private async ValueTask AwaitDispatchRpcNoReturnAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        CancellationTokenSource? linkedCts,
        StripedLongMap<CancellationTokenSource> requestCancellationMap)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
            session.SendPacketAsync(PacketType.RpcResponse, PacketFlags.None, requestId);
        }
        catch (OperationCanceledException)
        {
            session.SendStringPacketAsync(PacketType.RpcResponse, PacketFlags.IsError, requestId, "Request canceled.");
        }
        catch (Exception e)
        {
            session.SendStringPacketAsync(PacketType.RpcResponse, PacketFlags.IsError, requestId, e.Message);
        }
        finally
        {
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
        }
    }

    private async ValueTask AwaitDispatchRpcAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        ArrayBufferWriter<byte> writer,
        PacketToken token,
        CancellationTokenSource? linkedCts,
        StripedLongMap<CancellationTokenSource> requestCancellationMap)
    {
        try
        {
            await invokeTask.ConfigureAwait(false);
            writer.EndPacket(token);
            session.SendPacket(writer);
        }
        catch (OperationCanceledException)
        {
            BufferWriterPool.Return(writer);
            session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,"Request canceled.");
        }
        catch (Exception e)
        {
            BufferWriterPool.Return(writer);
            session.SendStringPacketAsync(PacketType.RpcResponse,PacketFlags.IsError,requestId,e.Message);
        }
        finally
        {
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap);
        }
    }

    private static void ReleaseDispatchResources(
        CancellationTokenSource? linkedCts,
        long requestId,
        StripedLongMap<CancellationTokenSource> requestCancellationMap)
    {
        linkedCts?.Dispose();
        if (linkedCts is not null)
            requestCancellationMap.TryRemove(requestId, out _);
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


}
