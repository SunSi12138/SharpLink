namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task ProcessRequestLoop(ServerConnectionState connection)
    {
        var session = connection.Session;
        var ct = connection.ConnectionToken;
        var reader = session.Input;
        var requestCancellationMap = connection.CallCancellations;
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
                    while (session.IsConnected &&
                           !ct.IsCancellationRequested &&
                           ProtocolV2FrameParser.TryReadFrame(
                               ref buffer, _protocolOptions, out var header, out var payload))
                    {
                        SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
                        session.MarkActive();
                        session.EnsureInboundFrameAllowed(
                            header.Type,
                            allowRequestWhileDraining: true);
                        IRpcByteBufferWriter? decodedOwner = null;
                        try
                        {
                            if (header.Type == ProtocolV2FrameType.StreamData &&
                                (header.Flags & ProtocolV2FrameFlags.Compressed) != 0)
                            {
                                var preAdmissionStreams = session.StreamManager;
                                session.ValidateInboundPayloadEnvelope(
                                    header.Type, header.Flags, payload);
                                var requestId = unchecked((long)header.RequestId);
                                var streamId = RpcSession.ReadCompressedStreamId(payload);
                                var originalLength = RpcSession.ReadCompressedOriginalLength(
                                    header.Type, header.Flags, payload);
                                if (preAdmissionStreams.TryDispatchPreAdmissionCompressed(
                                        requestId,
                                        streamId,
                                        payload,
                                        originalLength,
                                        out var preAdmissionDispatch))
                                {
                                    await preAdmissionDispatch.ConfigureAwait(false);
                                    continue;
                                }
                            }
                            if (header.Type == ProtocolV2FrameType.Request &&
                                (header.Flags & ProtocolV2FrameFlags.Compressed) != 0)
                            {
                                session.ValidateInboundPayloadEnvelope(
                                    header.Type, header.Flags, payload);
                            }
                            else
                            {
                                payload = session.DecodeInboundPayload(
                                    header.Type, header.Flags, payload, ct, out decodedOwner);
                            }
                        }
                        catch (SharpLinkException exception) when (
                            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
                        {
                            var failedRequestId = unchecked((long)header.RequestId);
                            if (header.Type == ProtocolV2FrameType.Request)
                            {
                                if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                {
                                    Interlocked.Increment(ref _rejectedOneWayCalls);
                                    DrainRejectedOneWayStreams(
                                        session,
                                        failedRequestId,
                                        ResolveRawRequestClientStreamCount(payload));
                                }
                                else
                                {
                                    var errorSend = session.SendRpcErrorWithBackpressureAsync(
                                        failedRequestId, exception, connection.ConnectionToken);
                                    if (!errorSend.IsCompletedSuccessfully)
                                        ObserveUserCall(errorSend, failedRequestId);
                                }
                            }
                            else if (header.Type == ProtocolV2FrameType.StreamData)
                            {
                                session.StreamManager.CompleteStream(
                                    failedRequestId,
                                    RpcSession.ReadCompressedStreamId(payload),
                                    exception);
                            }
                            continue;
                        }
                        // 3. 处理完整的消息 (这里不需要 await 阻塞网络读取，最好由 Task.Run 处理业务)
                        // 注意：messagePayload 在 Advance 之后就会失效，如果需要异步处理，必须 Copy
                        try
                        {
                            switch (header.Type)
                            {
                                case ProtocolV2FrameType.Ping:
                                    DebugLogClientHeartbeatReceived(_logger);
                                    await session.SendPongWithBackpressureAsync(
                                        ReadMonotonicTimestamp(payload), ct).ConfigureAwait(false);
                                    break;
                                case ProtocolV2FrameType.Pong:
                                    DebugLogClientHeartbeatReceived(_logger);
                                    break;
                                case ProtocolV2FrameType.Request:
                                    {
                                        var requestId = unchecked((long)header.RequestId);
                                        using var requestScope = BeginRequestLogScope(_logger, requestId);
                                        if (!TryAcceptRequest(connection, requestId))
                                        {
                                            if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                            {
                                                Interlocked.Increment(ref _rejectedOneWayCalls);
                                                LogOnewayRpcResourceExhausted(_logger, "server_unavailable");
                                            }
                                            else
                                            {
                                                var errorSend = session.SendRpcErrorWithBackpressureAsync(
                                                    requestId,
                                                    new SharpLinkException(
                                                        SharpLinkErrorCode.Unavailable,
                                                        "Server is draining."),
                                                    connection.ConnectionToken);
                                                if (!errorSend.IsCompletedSuccessfully)
                                                    ObserveUserCall(errorSend, requestId);
                                            }
                                            break;
                                        }

                                        var admissionProgramUse = CaptureAdmissionProgram(
                                            requestId,
                                            out var admissionProgram);
                                        if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                        {
                                            DispatchOneWayRpc(
                                                connection,
                                                requestId,
                                                header.Flags,
                                                payload,
                                                requestCancellationMap,
                                                ct,
                                                admissionProgram,
                                                admissionProgramUse);
                                            break;
                                        }

                                        var dispatchTask = DispatchRpcAsync(
                                            connection,
                                            requestId,
                                            header.Flags,
                                            payload,
                                            requestCancellationMap,
                                            ct,
                                            admissionProgram,
                                            admissionProgramUse);
                                        if (!dispatchTask.IsCompletedSuccessfully)
                                            ObserveUserCall(dispatchTask, requestId);
                                        break;
                                    }
                                case ProtocolV2FrameType.Cancel:
                                    var cancelRequestId = unchecked((long)header.RequestId);
                                    var cancelReason = session.ReadNegotiatedCancelReason(payload);
                                    session.AbortSendStreams(
                                        cancelRequestId,
                                        ServerCallTerminationMapper.CreateRemoteCancellationException(cancelReason));
                                    if (requestCancellationMap.TryCapture(
                                            cancelRequestId,
                                            static (requestId, state) => state.CaptureLease(requestId),
                                            out var callLease) &&
                                        callLease.TryAcquire())
                                    {
                                        try
                                        {
                                            callLease.State.TryCancel(
                                                ServerCallTerminationMapper.MapRemoteCancellationReason(cancelReason));
                                        }
                                        finally
                                        {
                                            callLease.ReleaseUse();
                                        }
                                    }
                                    break;
                                case ProtocolV2FrameType.StreamData:
                                    await DispatchStreamChunkAsync(session, unchecked((long)header.RequestId), payload);
                                    break;
                                case ProtocolV2FrameType.StreamComplete:
                                    DispatchStreamComplete(
                                        session, unchecked((long)header.RequestId), header.Flags, payload, _protocolOptions);
                                    break;
                                case ProtocolV2FrameType.WindowUpdate:
                                    session.ApplyWindowUpdate(
                                        unchecked((long)header.RequestId),
                                        ProtocolV2PayloadCodec.ReadWindowUpdate(payload));
                                    break;
                                case ProtocolV2FrameType.GoAway:
                                    return;
                                case ProtocolV2FrameType.HealthCheck:
                                    if ((session.NegotiatedCapabilities &
                                         ProtocolV2Capabilities.HealthCheck) == 0)
                                    {
                                        throw new SharpLinkProtocolViolationException(
                                            ProtocolViolationReason.ProtocolState,
                                            "HealthCheck was not negotiated for this session.");
                                    }
                                    await session.SendHealthResponseWithBackpressureAsync(
                                        unchecked((long)header.RequestId),
                                        HealthStatus,
                                        ct).ConfigureAwait(false);
                                    break;
                                case ProtocolV2FrameType.HandshakeRequest:
                                case ProtocolV2FrameType.HandshakeResponse:
                                case ProtocolV2FrameType.Response:
                                case ProtocolV2FrameType.HealthResponse:
                                default:
                                    {
                                        SharpLinkTelemetry.RecordProtocolFailure("server");
                                        LogProtocolViolationRateLimited(
                                            ProtocolViolationReason.ProtocolState);
                                        return;
                                    }
                            }
                        }
                        finally
                        {
                            session.ReturnDecodedPayload(decodedOwner);
                        }
                    }

                    // 4. 告诉 Pipe 我们消费到了哪里
                    if (result.IsCompleted) break;
                }
                finally
                {
                    // 移动游标：buffer.Start 是我们没处理完的起始位置
                    try
                    {
                        reader.AdvanceTo(buffer.Start, buffer.End);
                    }
                    catch (InvalidOperationException) when (
                        !session.IsConnected || ct.IsCancellationRequested)
                    {
                        // Transport teardown can complete a StreamPipeReader after ReadAsync
                        // returns. The buffer is already terminal and has no remaining owner.
                    }
                }
            }
        }
        finally
        {
            // Outstanding observers retain the map and remove their own state after user work ends.
            // Cooperative methods already observe serverLoopToken; non-cooperative methods suppress
            // their response because the token is checked before response ownership is claimed.
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
        catch (SharpLinkException exception) when (
            exception.Code == SharpLinkErrorCode.ConnectionClosed)
        {
        }
        catch (Exception ex)
        {
            LogRpcDispatchUnhandledException(_logger, ex);
        }
    }

    private void ObserveUserCall(ValueTask dispatchTask, long requestId)
        => _ = AwaitDispatchAsync(dispatchTask, requestId);

    private static async Task DispatchStreamChunkAsync(RpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame, "StreamData stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        var streamPayload = payload.Slice(sizeof(ushort));
        await session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
    }

    private static void DispatchStreamComplete(
        RpcSession session,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        var streamId = TryReadStreamId(ref payload);
        if ((flags & ProtocolV2FrameFlags.Error) == 0)
        {
            session.StreamManager.CompleteStream(requestId, streamId, exception: null);
            return;
        }
        var error = ProtocolV2PayloadCodec.ReadError(payload, flags, limits.MaxErrorMessageBytes);
        session.StreamManager.CompleteStream(
            requestId, streamId, new SharpLinkException(error.Code, error.Message));
    }

    private static ushort TryReadStreamId(ref ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame, "StreamComplete stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        payload = payload.Slice(sizeof(ushort));
        return streamId;
    }

    private static long ReadMonotonicTimestamp(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long timestamp))
            throw new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame, "Heartbeat timestamp is truncated.");
        return timestamp;
    }

}
