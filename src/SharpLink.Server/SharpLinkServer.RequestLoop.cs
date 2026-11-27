namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static bool TryAcceptInboundStreamProgress(
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId,
        out bool hasOwningCallState,
        out StreamTerminalOwner terminalOwner)
    {
        if (!requestCancellationMap.TryCapture(
                requestId,
                static (id, state) => state.CaptureLease(id),
                out var callLease))
        {
            // No owning state yet: preserve pre-admission buffering.
            hasOwningCallState = false;
            terminalOwner = default;
            return true;
        }

        return TryAcceptCapturedInboundStreamProgress(
            requestCancellationMap, requestId, callLease, out hasOwningCallState, out terminalOwner);
    }

    private static bool TryAcceptCapturedInboundStreamProgress(
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId,
        ServerCallCancellationLease callLease,
        out bool hasOwningCallState,
        out StreamTerminalOwner terminalOwner)
    {
        // Losing a pooled generation is not a terminal decision. Retry current ownership;
        // removal falls back to ordinary existing-route handling, never terminal abandonment.
        var retries = 0;
        while (!callLease.TryAcquire())
        {
            if (++retries > 3)
                throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                    "Inbound stream call ownership could not be acquired safely.");
            if (!requestCancellationMap.TryCapture(
                    requestId, static (id, state) => state.CaptureLease(id), out callLease))
            {
                hasOwningCallState = false;
                terminalOwner = default;
                return true;
            }
        }
        hasOwningCallState = true;

        var releaseLease = true;
        try
        {
            var accepted = callLease.State.TryAcceptStreamDataDeferredCancellation(
                out var notifyCancellation);
            terminalOwner = accepted ? default : callLease.TerminalOwner;
            if (notifyCancellation)
            {
                // The connection read loop owns frame parsing for every call on this transport.
                // Never execute application token callbacks inline here. Retain this generation
                // until the best-effort notification completes so the pooled state cannot be
                // recycled underneath the queued work.
                releaseLease = false;
                _ = Task.Run(() =>
                {
                    try
                    {
                        callLease.State.NotifyInvocationCancellation();
                    }
                    finally
                    {
                        callLease.ReleaseUse();
                    }
                });
            }
            return accepted;
        }
        finally
        {
            if (releaseLease)
                callLease.ReleaseUse();
        }
    }

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
                           session.TryReadInboundFrame(
                               ref buffer, _protocolOptions, out var header, out var payload))
                    {
                        SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
                        session.MarkActive();
                        session.EnsureInboundFrameAllowed(
                            header.Type,
                            allowRequestWhileDraining: true);

                        var oneWayInputCount = 0;
                        if (header.Type == NStudyWire.OneWayInputRequest)
                        {
                            // Count is independent of the registry. Check the entire
                            // compressed envelope before stripping the count byte.
                            oneWayInputCount = NStudyWire.ReadOneWayInputCount(payload);
                            session.ValidateInboundPayloadEnvelope(header.Type, header.Flags, payload);
                            payload = payload.Slice(1);
                            header = header with { Type = ProtocolV2FrameType.Request };
                        }

                        var isStreamProgress = header.Type is
                            ProtocolV2FrameType.StreamData or ProtocolV2FrameType.StreamComplete;
                        var streamRequestId = isStreamProgress
                            ? unchecked((long)header.RequestId)
                            : 0;
                        var hasOwningStreamCallState = false;
                        if (isStreamProgress &&
                            !TryAcceptInboundStreamProgress(
                                requestCancellationMap,
                                streamRequestId,
                                out hasOwningStreamCallState,
                                out var terminalOwner))
                        {
                            await DrainTerminalStreamProgressAsync(
                                session, header.Type, header.Flags, streamRequestId, payload, decoded: false, terminalOwner)
                                .ConfigureAwait(false);
                            continue;
                        }

                        IRpcByteBufferWriter? decodedOwner = null;
                        var compressedMailboxOwnedFrame = false;
                        try
                        {
                            if (header.Type == ProtocolV2FrameType.StreamData &&
                                (header.Flags & ProtocolV2FrameFlags.Compressed) != 0)
                            {
                                var preAdmissionStreams = session.StreamManager;
                                session.ValidateInboundPayloadEnvelope(
                                    header.Type, header.Flags, payload);
                                var streamId = RpcSession.ReadCompressedStreamId(payload);
                                var originalLength = RpcSession.ReadCompressedOriginalLength(
                                    header.Type, header.Flags, payload);
                                if (preAdmissionStreams.TryDispatchPreAdmissionCompressed(
                                        streamRequestId,
                                        streamId,
                                        payload,
                                        originalLength,
                                        out var preAdmissionDispatch,
                                        out var hasRoute))
                                {
                                    compressedMailboxOwnedFrame = true;
                                    await preAdmissionDispatch.ConfigureAwait(false);
                                    continue;
                                }
                                if (!hasRoute)
                                {
                                    // Rejected input needs neither a route nor a decompressor.
                                    preAdmissionStreams.RecordUnroutedStreamData();
                                    if (streamId != 0)
                                        session.RefundDiscardedStreamData(streamRequestId, streamId, originalLength);
                                    continue;
                                }
                            }
                            if (header.Type == ProtocolV2FrameType.Request &&
                                (_admissionController is not null ||
                                 (header.Flags & ProtocolV2FrameFlags.Compressed) != 0))
                            {
                                // Request compression preserves the routing/metadata/TimeBudget
                                // prefix. Never perform potentially blocking decompression here:
                                // dispatch must first resolve that relative budget into one local
                                // RpcDeadline and then carry the same boundary through decode.
                                if (oneWayInputCount == 0)
                                    session.ValidateInboundPayloadEnvelope(header.Type, header.Flags, payload);
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
                            try
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
                                            ResolveRejectedNStudyOneWayCount(connection, payload, oneWayInputCount));
                                    }
                                    else
                                    {
                                        var errorSend = session.SendRpcErrorWithBackpressureAsync(
                                            failedRequestId, exception, connection.ConnectionToken);
                                        if (!errorSend.IsCompletedSuccessfully)
                                            _ = ObserveDecodedRequestErrorSend(errorSend, failedRequestId);
                                    }
                                }
                                else if (header.Type == ProtocolV2FrameType.StreamData)
                                {
                                    // The decompressor is allowed to fail only while the call still
                                    // owns progress. Re-arbitrate here because an active call may
                                    // cross its monotonic boundary inside the decompressor and this
                                    // catch path would otherwise bypass the normal post-decode claim.
                                    if (hasOwningStreamCallState &&
                                        !TryAcceptInboundStreamProgress(
                                            requestCancellationMap,
                                            failedRequestId,
                                            out _,
                                            out var failedTerminalOwner))
                                    {
                                        if (!compressedMailboxOwnedFrame)
                                            await DrainTerminalStreamProgressAsync(
                                                session, header.Type, header.Flags, failedRequestId, payload, decoded: false, failedTerminalOwner)
                                                .ConfigureAwait(false);
                                        continue;
                                    }

                                    session.StreamManager.CompleteStream(
                                        failedRequestId,
                                        RpcSession.ReadCompressedStreamId(payload),
                                        exception);
                                }
                            }
                            finally
                            {
                                session.ReturnDecodedPayload(decodedOwner);
                                decodedOwner = null;
                            }
                            continue;
                        }

                        if (isStreamProgress &&
                            !TryAcceptInboundStreamProgress(
                                requestCancellationMap,
                                streamRequestId,
                                out _,
                                out var decodedTerminalOwner))
                        {
                            try
                            {
                                await DrainTerminalStreamProgressAsync(
                                    session, header.Type, header.Flags, streamRequestId, payload, decoded: true, decodedTerminalOwner)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                session.ReturnDecodedPayload(decodedOwner);
                                decodedOwner = null;
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
                                case NStudyWire.Bind:
                                    await HandleNStudyBindAsync(connection, header.RequestId,
                                        NStudyWire.ReadBind(payload), ct).ConfigureAwait(false);
                                    break;
                                case NStudyWire.TargetDeclaration:
                                    await HandleNStudyTargetDeclarationAsync(connection, header.RequestId,
                                        NStudyWire.ReadTargetDeclaration(payload), ct).ConfigureAwait(false);
                                    break;
                                case NStudyWire.InventoryQuery:
                                    if (!payload.IsEmpty)
                                        throw RpcSession.InvalidNStudy("A server inventory query does not carry a Session scope.");
                                    await HandleNStudyInventoryAsync(connection, header.RequestId, ct).ConfigureAwait(false);
                                    break;
                                case NStudyWire.InventoryReply:
                                    session.OnNStudyInventoryReply(header.RequestId, payload);
                                    break;
                                case NStudyWire.Release:
                                    session.EnsureNStudyReady(RpcSessionRole.Server);
                                    connection.NStudyBindings.Release(NStudyWire.ReadRelease(payload));
                                    break;
                                case NStudyWire.Coordination:
                                    // Client-to-server coordination messages are queued synchronously.
                                    _ = session.OnNStudyCoordination(header.RequestId, payload);
                                    break;
                                case ProtocolV2FrameType.ResponseCompressionPreferenceUpdate:
                                    var responseCompressionPreference =
                                        ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceUpdate(payload);
                                    var appliedResponseCompressionGeneration =
                                        session.ApplyServerResponseCompressionPreferenceUpdate(responseCompressionPreference);
                                    await session.SendResponseCompressionPreferenceAckWithBackpressureAsync(
                                        appliedResponseCompressionGeneration,
                                        ct).ConfigureAwait(false);
                                    break;
                                case ProtocolV2FrameType.Request:
                                    {
                                        var requestId = unchecked((long)header.RequestId);
                                        _ = DispatchRequestAsync(
                                            connection,
                                            requestId,
                                            header.Flags,
                                            payload,
                                            requestCancellationMap,
                                            ct,
                                            oneWayInputCount);
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
                                    await DispatchStreamChunkAsync(
                                        session, streamRequestId, payload);
                                    break;
                                case ProtocolV2FrameType.StreamComplete:
                                    DispatchStreamComplete(
                                        session, streamRequestId, header.Flags, payload, _protocolOptions);
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
                                case ProtocolV2FrameType.ResponseCompressionPreferenceAck:
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

    private async Task DispatchRequestAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        int oneWayInputCount)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        NStudyServerBindings.Use bindingUse = default;
        try
        {
            if (!TryAcceptRequest(connection, requestId))
            {
                if ((flags & ProtocolV2FrameFlags.OneWay) != 0)
                {
                    Interlocked.Increment(ref _rejectedOneWayCalls);
                    LogOnewayRpcResourceExhausted(_logger, "server_unavailable");
                    DrainRejectedOneWayStreams(
                        connection.Session,
                        requestId,
                        ResolveRejectedNStudyOneWayCount(connection, payload, oneWayInputCount));
                    return;
                }

                var rejectionSend = connection.Session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    new SharpLinkException(
                        SharpLinkErrorCode.Unavailable,
                        "Server is draining."),
                    connection.ConnectionToken);
                payload = default;
                if (!rejectionSend.IsCompletedSuccessfully)
                    await rejectionSend.ConfigureAwait(false);
                return;
            }

            ValueTask dispatchTask;
            try
            {
                var request = ReadRequestEnvelope(connection.Session, payload, flags);
                var preparation = connection.NStudyBindings.BorrowDispatch(request.InterfaceHash, request.MethodHash,
                    out bindingUse);
                var admissionProgram = CaptureAdmissionProgram(requestId);
                dispatchTask = (flags & ProtocolV2FrameFlags.OneWay) != 0
                    ? DispatchOneWayRpc(
                        connection,
                        requestId,
                        flags,
                        payload,
                        requestCancellationMap,
                        serverLoopToken,
                        admissionProgram,
                        request,
                        in preparation,
                        bindingUse,
                        oneWayInputCount)
                    : DispatchRpcAsync(
                        connection,
                        requestId,
                        flags,
                        payload,
                        requestCancellationMap,
                        serverLoopToken,
                        admissionProgram,
                        request,
                        in preparation,
                        bindingUse);
            }
            catch (SharpLinkException exception) when (
                exception.Code == SharpLinkErrorCode.ProtocolViolation)
            {
                SharpLinkTelemetry.RecordProtocolFailure("server");
                var reason = SharpLinkProtocolViolationException.Classify(exception);
                if (reason == ProtocolViolationReason.InternalState)
                {
                    LogServerBackgroundLoopUnhandledException(
                        _logger,
                        nameof(ProcessRequestLoop),
                        exception);
                }
                else
                {
                    LogProtocolViolationRateLimited(reason);
                }

                payload = default;
                var closeTask = connection.CloseAsync();
                if (!closeTask.IsCompletedSuccessfully)
                    await closeTask.ConfigureAwait(false);
                return;
            }

            payload = default;
            if (!dispatchTask.IsCompletedSuccessfully)
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
        finally
        {
            bindingUse.Dispose();
        }
    }

    private async Task AwaitDispatchAsync(ValueTask dispatchTask, long requestId)
    {
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



    private async Task ObserveDecodedRequestErrorSend(ValueTask dispatchTask, long requestId)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        await AwaitDispatchAsync(dispatchTask, requestId).ConfigureAwait(false);
    }

    private static ValueTask DispatchStreamChunkAsync(RpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame, "StreamData stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        var streamPayload = payload.Slice(sizeof(ushort));
        if (session.StreamManager.TryDispatchChunk(requestId, streamId, streamPayload, out var dispatch))
            return dispatch;
        // A rejected request may have no input route at all. Its sender already
        // debited the frame, and the receiver retains nothing.
        if (streamId != 0)
            session.RefundDiscardedStreamData(requestId, streamId, Math.Max(1, checked((int)streamPayload.Length)));
        return ValueTask.CompletedTask;
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
            session.StreamManager.CompletePeerStream(requestId, streamId, exception: null);
            return;
        }
        var error = ProtocolV2PayloadCodec.ReadError(payload, flags, limits.MaxErrorMessageBytes);
        session.StreamManager.CompletePeerStream(
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
