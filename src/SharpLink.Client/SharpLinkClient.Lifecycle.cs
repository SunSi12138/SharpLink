namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        LastConnectionException = null;
        try
        {
            _session = await transport.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            LastConnectionException = ex;
            throw;
        }
        _session.OnDisconnected += HandleDisconnected;
        var handshakeException = await ProcessHandshakeAsync(_session, ct);
        if (handshakeException is not null)
        {
            LastConnectionException = handshakeException;
            await _session.DisposeAsync();
            _session = null;
            if (handshakeException is OperationCanceledException operationCanceledException && ct.IsCancellationRequested)
                throw operationCanceledException;
            return false;
        }

        var loopToken = _lifecycleCts.Token;
        TrackBackgroundTask(RunHeartbeatSendLoopAsync(_session, loopToken));
        TrackBackgroundTask(RunProcessRequestLoopAsync(_session, loopToken));
        return true;
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private static bool IsTransportFault(Exception ex)
        => ex is IOException or ObjectDisposedException or SocketException;

    private async Task RunHeartbeatSendLoopAsync(IRpcSession session, CancellationToken ct)
    {
        try
        {
            await HeartbeatSendLoop(session, ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            using var sessionScope = BeginSessionLogScope(_logger, session.Id);
            LogClientBackgroundLoopUnhandledException(_logger, nameof(HeartbeatSendLoop), ex);
            HandleDisconnected(IsTransportFault(ex)
                ? CreateConnectionClosedException("Transport closed.", ex)
                : ex);
        }
    }

    private async Task RunProcessRequestLoopAsync(IRpcSession session, CancellationToken ct)
    {
        try
        {
            await ProcessRequestLoop(session, ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            using var sessionScope = BeginSessionLogScope(_logger, session.Id);
            LogClientBackgroundLoopUnhandledException(_logger, nameof(ProcessRequestLoop), ex);
            HandleDisconnected(IsTransportFault(ex)
                ? CreateConnectionClosedException("Transport closed.", ex)
                : ex);
        }
    }

    public T Get<T>() where T : IService
    {
        if (GeneratedProxyRegistry.TryCreate(typeof(T), this, out var proxy))
            return (T)proxy!;

        throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
    }

    private async Task<Exception?> ProcessHandshakeAsync(IRpcSession session, CancellationToken ct)
    {
        session.SendStringPacketAsync(PacketType.Handshake, PacketFlags.None, 0, _handshakeMessage);

        var reader = session.Input;
        Exception? handshakeException = null;
        var handshakeCompleted = false;
        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            while (PacketHelper.TryReadMessage(ref buffer, out var header, out var payload))
            {
                if (header.Type != PacketType.Handshake)
                    handshakeException = CreateProtocolViolationException("Received unexpected packet during handshake.");
                else if ((header.Flags & PacketFlags.IsError) == 0)
                    handshakeException = null;
                else
                {
                    var message = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Authentication rejected.";
                    handshakeException = SharpLinkAuthenticationResult.TryParsePayloadMessage(message, out var authResult)
                        ? new SharpLinkException(authResult.ErrorCode, authResult.ErrorMessage ?? "Authentication rejected.")
                        : CreateAuthenticationRejectedException(message);
                }

                handshakeCompleted = true;
                break;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (handshakeCompleted)
                return handshakeException;

            if (result.IsCompleted)
                break;
        }

        return ct.IsCancellationRequested
            ? new OperationCanceledException(ct)
            : CreateConnectionClosedException("Server disconnected during handshake.");
    }

    private async Task ProcessRequestLoop(IRpcSession session, CancellationToken ct)
    {
        var reader = session.Input;
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);

        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            try
            {
                while (PacketHelper.TryReadMessage(ref buffer, out var header, out var payload))
                {
                    session.LastActive = DateTime.UtcNow;
                    switch (header.Type)
                    { 
                        case PacketType.Heartbeat:
                            DebugLogServerHeartbeatReceived(_logger);
                            break;
                        case PacketType.Cancel:
                            DebugLogServerCancelIgnored(_logger);
                            break;
                        case PacketType.RpcResponse:
                            DispatchRpc(header.RequestId, header.Flags, ref payload);
                            break;
                        case PacketType.StreamChunk:
                            var dispatchTask = DispatchStreamChunkAsync(session, header.RequestId, payload);
                            if (!dispatchTask.IsCompletedSuccessfully)
                                await dispatchTask;
                            break;
                        case PacketType.StreamComplete:
                            DispatchStreamComplete(session, header.RequestId, payload);
                            break;
                        case PacketType.StreamError:
                            DispatchStreamError(session, header.RequestId, payload);
                            break;
                        case PacketType.RpcCall:
                        case PacketType.Error:
                        case PacketType.DisConnect:
                        case PacketType.Handshake:
                        default:
                            await session.DisposeAsync();
                            HandleDisconnected(CreateProtocolViolationException("Received unexpected packet from server."));
                            break;
                    }
                }

                if (result.IsCompleted)
                    break;
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        HandleDisconnected(CreateConnectionClosedException("Server disconnected."));
    }

    private async Task HeartbeatSendLoop(IRpcSession session, CancellationToken ct)
    {
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        while (!ct.IsCancellationRequested)
        {
            session.SendPacketAsync(PacketType.Heartbeat, PacketFlags.None, 0);
            await Task.Delay(_heartbeatInterval, ct);
            var now = DateTime.UtcNow;
            if (now - session.LastActive <= _heartbeatTimeout && session.IsConnected)
                continue;

            LogServerHeartbeatTimeout(_logger);

            await session.DisposeAsync();
            HandleDisconnected(CreateHeartbeatTimeoutException("Server heartbeat timeout."));
            break;
        }
    }

    private void DispatchRpc(long requestId, PacketFlags flags, ref ReadOnlySequence<byte> payload)
    {
        var isError = (flags & PacketFlags.IsError) != 0;

        if (isError)
        {
            var message = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Remote Error";
            var remoteException = CreateRemoteErrorException(message);
            if (_requestManager.DispatchError(requestId, remoteException))
                return;

            if (_serverStreamRequestIds.Remove(requestId))
            {
                _session?.StreamManager.CompleteStream(requestId, remoteException);
                return;
            }
        }
        else
        {
            if (_requestManager.Dispatch(requestId, ref payload))
                return;

            // Server-stream receives a terminal RpcResponse ACK; swallow it.
            if (_serverStreamRequestIds.Remove(requestId))
                return;
        }

        if (_locallyCanceledRequestIds.Remove(requestId))
            return;

        using var requestScope = BeginRequestLogScope(_logger, requestId);
        LogUnknownOrTimedOutResponse(_logger);
    }
}
