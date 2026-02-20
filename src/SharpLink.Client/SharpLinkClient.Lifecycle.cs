namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        _session = await transport.ConnectAsync(ct);
        _session.OnDisconnected += HandleDisconnected;
        var res = await ProcessHandshakeAsync(_session, ct);
        if (!res)
            return false;

        _ = RunHeartbeatSendLoopAsync(_session, ct);
        _ = RunProcessRequestLoopAsync(_session, ct);
        return true;
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

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
            HandleDisconnected(ex);
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
            HandleDisconnected(ex);
        }
    }

    public T Get<T>() where T : IService
    {
        if (GeneratedProxyRegistry.TryCreate(typeof(T), this, out var proxy))
            return (T)proxy!;

        throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
    }

    private static async Task<bool> ProcessHandshakeAsync(IRpcSession session, CancellationToken ct)
    {
        session.SendStringPacketAsync(PacketType.Handshake, PacketFlags.None, 0, "Password");

        var reader = session.Input;
        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            bool? handshakeResult = null;
            while (PacketHelper.TryReadMessage(ref buffer, out var header, out var _))
            {
                handshakeResult = header.Type == PacketType.Handshake && (header.Flags & PacketFlags.IsError) == 0;
                break;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (handshakeResult.HasValue)
                return handshakeResult.Value;

            if (result.IsCompleted)
                break;
        }
        return false;
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
                            HandleDisconnected(new IOException("Received unexpected packet from server."));
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

        HandleDisconnected(new IOException("Server disconnected."));
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
            HandleDisconnected(new IOException("Server heartbeat timeout."));
            break;
        }
    }

    private void DispatchRpc(long requestId, PacketFlags flags, ref ReadOnlySequence<byte> payload)
    {
        var isError = (flags & PacketFlags.IsError) != 0;

        if (isError)
        {
            var message = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Remote Error";
            if (_requestManager.DispatchError(requestId, new Exception(message)))
                return;

            if (_serverStreamRequestIds.Remove(requestId))
            {
                _session?.StreamManager.CompleteStream(requestId, 0, true, message);
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
