using System.Diagnostics;

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
                        IRpcByteBufferWriter? decodedOwner = null;
                        try
                        {
                            if (header.Type == ProtocolV2FrameType.StreamData &&
                                (header.Flags & ProtocolV2FrameFlags.Compressed) != 0 &&
                                session.StreamManager is StreamManager preAdmissionStreams)
                            {
                                var rpcSession = (RpcSession)session;
                                rpcSession.ValidateInboundPayloadEnvelope(
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
                                _admissionController is not null)
                            {
                                ((RpcSession)session).ValidateInboundPayloadEnvelope(
                                    header.Type, header.Flags, payload);
                            }
                            else
                            {
                                payload = ((RpcSession)session).DecodeInboundPayload(
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

                                        if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                        {
                                            DispatchOneWayRpc(
                                                connection, requestId, header.Flags, payload, requestCancellationMap, ct);
                                            break;
                                        }

                                        var dispatchTask = DispatchRpcAsync(
                                            connection, requestId, header.Flags, payload, requestCancellationMap, ct);
                                        if (!dispatchTask.IsCompletedSuccessfully)
                                            ObserveUserCall(dispatchTask, requestId);
                                        break;
                                    }
                                case ProtocolV2FrameType.Cancel:
                                    var cancelRequestId = unchecked((long)header.RequestId);
                                    var cancelReason = session.ReadNegotiatedCancelReason(payload);
                                    ((RpcSession)session).AbortSendStreams(
                                        cancelRequestId,
                                        CreateRemoteCancellationException(cancelReason));
                                    if (requestCancellationMap.TryGetValue(cancelRequestId, out var callState) &&
                                        callState.TryAcquire(cancelRequestId))
                                    {
                                        try
                                        {
                                            callState.TryCancel(MapRemoteCancellationReason(cancelReason));
                                        }
                                        finally
                                        {
                                            callState.ReleaseUse();
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
                                    ((RpcSession)session).ApplyWindowUpdate(
                                        unchecked((long)header.RequestId),
                                        ProtocolV2PayloadCodec.ReadWindowUpdate(payload));
                                    break;
                                case ProtocolV2FrameType.GoAway:
                                    return;
                                case ProtocolV2FrameType.HealthCheck:
                                    if ((((RpcSession)session).NegotiatedCapabilities &
                                         ProtocolV2Capabilities.HealthCheck) == 0)
                                    {
                                        throw new SharpLinkException(
                                            SharpLinkErrorCode.ProtocolViolation,
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
                                        return;
                                    }
                            }
                        }
                        finally
                        {
                            ((RpcSession)session).ReturnDecodedPayload(decodedOwner);
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

    private void DispatchOneWayRpc(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState = null,
        bool admissionGranted = false,
        int admittedClientStreamCount = 0)
    {
        var session = connection.Session;
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var request = ReadRequestEnvelope(session, payload, flags);
        if (IsDeadlineExceeded(request.DeadlineTimestamp))
        {
            if (admittedCallState is not null)
            {
                DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            }
            return;
        }
        if (!Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo))
        {
            if (admittedCallState is not null)
            {
                DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            }
            return;
        }
        if (!serviceInfo.AcceptsCalls)
        {
            if (admittedCallState is not null)
            {
                DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            }
            return;
        }

        var descriptor = GetMethodDescriptor(serviceInfo.Stub, request.MethodHash);

        if (_admissionController is not null && !admissionGranted)
        {
            admittedCallState = CreateAdmissionWaitState(
                connection,
                requestId,
                request.Deadline,
                request.DeadlineTimestamp,
                serverLoopToken,
                serviceInfo.ModuleCancellation,
                requestCancellationMap);
            ValueTask<AdmissionDecision> admissionTask;
            try
            {
                admissionTask = _admissionController.AcquireAsync(
                    CreateAdmissionContext(connection, descriptor, request),
                    checked((int)payload.Length),
                    _admissionController.QueueOneWayCalls,
                    admittedCallState.InvocationToken);
            }
            catch (Exception exception)
            {
                LogOnewayRpcDispatchFailed(_logger, exception);
                DrainRejectedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                _ = RejectAdmission(
                    session,
                    requestId,
                    AdmissionDecision.Reject(
                        "partition_selector", "partition", SharpLinkErrorCode.Internal),
                    oneWay: true);
                ReleaseAdmissionCallState(
                    requestCancellationMap, requestId, admittedCallState);
                return;
            }
            if (!admissionTask.IsCompletedSuccessfully)
            {
                ReservePreAdmissionRequestStreams(
                    session,
                    requestId,
                    descriptor.ClientStreamCount,
                    admittedCallState);
                var retainedPayload = CopyAdmissionPayload(payload);
                ObserveUserCall(
                    new ValueTask(AwaitOneWayAdmissionAsync(
                        admissionTask,
                        retainedPayload,
                        connection,
                        requestId,
                        flags,
                        requestCancellationMap,
                        serverLoopToken,
                        descriptor.ClientStreamCount,
                        admittedCallState)),
                    requestId);
                return;
            }

            var decision = admissionTask.Result;
            if (!decision.IsAcquired)
            {
                DrainRejectedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                _ = RejectAdmission(connection.Session, requestId, decision, oneWay: true);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
                return;
            }
            admittedCallState.AttachAdmissionLease(decision.Lease!);
        }

        var admission = TryAcquireCall(connection);
        if (admission != ServerCallAdmissionResult.Acquired)
        {
            DrainRejectedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            if (admittedCallState is not null)
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            Interlocked.Increment(ref _rejectedOneWayCalls);
            if (admission is ServerCallAdmissionResult.PerConnectionCapacityExhausted or
                ServerCallAdmissionResult.ServerCapacityExhausted)
            {
                var reason = GetCallCapacityExhaustionReason(admission);
                SharpLinkTelemetry.RecordResourceExhausted("server", reason);
                LogOnewayRpcResourceExhausted(_logger, reason);
            }
            return;
        }

        IRpcByteBufferWriter? decodedRequestOwner = null;
        try
        {
            if (_admissionController is not null)
            {
                payload = ((RpcSession)session).DecodeInboundPayload(
                    ProtocolV2FrameType.Request,
                    flags,
                    payload,
                    admittedCallState?.InvocationToken ?? serverLoopToken,
                    out decodedRequestOwner);
                request = ReadRequestEnvelope(session, payload, flags);
            }
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
        {
            Interlocked.Increment(ref _rejectedOneWayCalls);
            LogOnewayRpcDispatchFailed(_logger, exception);
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            return;
        }
        catch (OperationCanceledException)
        {
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            return;
        }
        catch
        {
            ((RpcSession)session).ReturnDecodedPayload(decodedRequestOwner);
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            throw;
        }

        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&
            serviceInfo.Stub.SupportsCancellation(request.MethodHash);
        var callState = admittedCallState ?? CreateTrackedCallState(
            connection,
            requestId,
            request.Deadline,
            request.DeadlineTimestamp,
            serverLoopToken,
            serviceInfo.ModuleCancellation,
            supportsCooperativeCancellation,
            requestCancellationMap);
        if (decodedRequestOwner is not null)
        {
            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
            callState.AttachPayloadOwner(_runtimeContext.Buffers, decodedRequestOwner);
            decodedRequestOwner = null;
        }
        var invokeToken = supportsCooperativeCancellation
            ? callState!.InvocationToken
            : serverLoopToken;

        var callContext = CreateCallContext(
            connection, serviceInfo.Stub, request.MethodHash, requestId,
            request.Deadline, request.Metadata, invokeToken);
        try
        {
            using var callContextScope = SharpLinkCallContext.Push(callContext);
            var invokeTask = InvokeServiceAsync(
                serviceInfo,
                connection,
                session,
                request.MethodHash,
                requestId,
                request.Arguments,
                output: null,
                invokeToken,
                callContext);
            if (invokeTask.IsCompletedSuccessfully)
            {
                if (callContext is SharpLinkServerInvocationContext interceptorContext)
                    interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
                TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken);
                ReleaseOneWayDispatchResources(callState, requestId, requestCancellationMap, connection);
                return;
            }

            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
            ObserveUserCall(
                new ValueTask(AwaitOneWayDispatchAsync(
                    invokeTask,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    callContext,
                    session,
                    serviceInfo.Stub,
                    request.MethodHash,
                    invokeToken)),
                requestId);
        }
        catch (Exception ex)
        {
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
            {
                LogOnewayRpcDispatchFailed(_logger, MapServiceException(
                    ex, callContext, session, serviceInfo.Stub, request.MethodHash, requestId, invokeToken));
            }
            ReleaseOneWayDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private async Task AwaitOneWayDispatchAsync(
        ValueTask invokeTask,
        ServerCallCancellationState callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        IRpcSession session,
        IRpcStub stub,
        long methodId,
        CancellationToken cancellationToken)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
            if (callContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            TryClaimCallCompletion(callState);
        }
        catch (Exception ex)
        {
            if (TryClaimCallCompletion(callState))
            {
                LogOnewayRpcDispatchFailed(_logger, MapServiceException(
                    ex, callContext, session, stub, methodId, requestId, cancellationToken));
            }
        }
        finally
        {
            ReleaseOneWayDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private async Task AwaitOneWayAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        var transferred = false;
        try
        {
            AdmissionDecision decision;
            try
            {
                decision = await admissionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = CreateAdmissionCancellationDecision(callState);
            }
            if (!decision.IsAcquired)
            {
                DrainRejectedOneWayStreams(
                    connection.Session, requestId, clientStreamCount);
                _ = RejectAdmission(connection.Session, requestId, decision, oneWay: true);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, callState);
                transferred = true;
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            DispatchOneWayRpc(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                admittedClientStreamCount: clientStreamCount);
            transferred = true;
        }
        finally
        {
            _runtimeContext.Buffers.Return(retainedPayload);
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

    private async ValueTask AwaitRpcAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState)
    {
        var transferred = false;
        try
        {
            AdmissionDecision decision;
            try
            {
                decision = await admissionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = CreateAdmissionCancellationDecision(callState);
            }
            if (!decision.IsAcquired)
            {
                await RejectAdmission(
                    connection.Session,
                    requestId,
                    decision,
                    oneWay: false,
                    connection.ConnectionToken).ConfigureAwait(false);
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            var dispatchTask = DispatchRpcAsync(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true);
            transferred = true;
            if (!dispatchTask.IsCompletedSuccessfully)
                await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            _runtimeContext.Buffers.Return(retainedPayload);
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

    private ServerCallCancellationState CreateAdmissionWaitState(
        ServerConnectionState connection,
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap)
    {
        var callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            deadlineTimestamp,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation: true);
        requestCancellationMap.Set(requestId, callState);
        connection.DeadlineScheduler.Register(callState);
        return callState;
    }

    private static SharpLinkAdmissionContext CreateAdmissionContext(
        ServerConnectionState connection,
        RpcMethodDescriptor descriptor,
        RpcRequestEnvelope request)
        => new(
            descriptor.ContractId,
            descriptor.MethodId,
            descriptor.Kind,
            connection.Session.Id,
            connection.AuthenticationContext,
            request.Metadata,
            request.Deadline);

    private IRpcByteBufferWriter CopyAdmissionPayload(ReadOnlySequence<byte> payload)
    {
        var owner = _runtimeContext.Buffers.Rent(checked((int)payload.Length));
        foreach (var segment in payload)
            owner.Write(segment.Span);
        return owner;
    }

    private ValueTask RejectAdmission(
        IRpcSession session,
        long requestId,
        AdmissionDecision decision,
        bool oneWay,
        CancellationToken cancellationToken = default)
    {
        var scope = decision.Scope ?? "server";
        var reason = decision.Reason ?? "unknown";
        var resourceExhaustionReason = GetAdmissionResourceExhaustionReason(reason);
        SharpLinkTelemetry.RecordAdmissionRejected(scope, reason);
        if (decision.ErrorCode == SharpLinkErrorCode.ResourceExhausted)
            SharpLinkTelemetry.RecordResourceExhausted(
                "server",
                resourceExhaustionReason);
        if (oneWay)
        {
            Interlocked.Increment(ref _rejectedOneWayCalls);
            SharpLinkTelemetry.RecordAdmissionOneWayDropped(scope, reason);
            if (ShouldLogOneWayAdmissionRejection())
                LogOnewayRpcResourceExhausted(
                    _logger,
                    resourceExhaustionReason);
            return ValueTask.CompletedTask;
        }

        var rejection = decision.ErrorCode == SharpLinkErrorCode.ResourceExhausted
            ? SharpLinkResourceExhaustion.CreateWire(
                resourceExhaustionReason,
                $"Server admission rejected the call ({resourceExhaustionReason}; {scope}/{reason}).")
            : new SharpLinkException(
                decision.ErrorCode,
                "Server stopped accepting new calls.");
        return session.SendRpcErrorWithBackpressureAsync(
            requestId,
            rejection,
            cancellationToken);
    }

    private bool ShouldLogOneWayAdmissionRejection()
    {
        var now = Stopwatch.GetTimestamp();
        var minimumInterval = Stopwatch.Frequency * 5L;
        while (true)
        {
            var previous = Volatile.Read(ref _oneWayAdmissionLogTimestamp);
            if (previous != 0 && now - previous < minimumInterval)
                return false;
            if (Interlocked.CompareExchange(ref _oneWayAdmissionLogTimestamp, now, previous) == previous)
                return true;
        }
    }

    private static string GetAdmissionResourceExhaustionReason(string reason)
        => reason switch
        {
            "concurrency" => SharpLinkResourceExhaustion.AdmissionConcurrency,
            "queue_count" or "queue_bytes" => SharpLinkResourceExhaustion.AdmissionQueue,
            "rate" => SharpLinkResourceExhaustion.AdmissionRate,
            "partition_capacity" => SharpLinkResourceExhaustion.AdmissionPartitionCapacity,
            _ => SharpLinkResourceExhaustion.AdmissionOther
        };

    private static AdmissionDecision CreateAdmissionCancellationDecision(
        ServerCallCancellationState callState)
        => callState.Reason switch
        {
            ServerCallCancellationReason.DeadlineExceeded => AdmissionDecision.Reject(
                "deadline", SharpLinkErrorCode.DeadlineExceeded),
            ServerCallCancellationReason.ConnectionClosed => AdmissionDecision.Reject(
                "disconnect", SharpLinkErrorCode.ConnectionClosed),
            ServerCallCancellationReason.AdmissionResourceExhausted => AdmissionDecision.Reject(
                "queue_bytes", SharpLinkErrorCode.ResourceExhausted),
            ServerCallCancellationReason.ServerStopping or ServerCallCancellationReason.ModuleDraining =>
                AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable),
            _ => AdmissionDecision.Reject("cancelled", SharpLinkErrorCode.Cancelled)
        };

    private static void ReleasePendingAdmissionState(
        IRpcSession session,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId,
        ServerCallCancellationState callState)
    {
        if (session.StreamManager is StreamManager streamManager)
        {
            streamManager.CompleteRequestStreams(
                requestId,
                new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "Call ended before stream admission completed."));
        }
        ReleaseAdmissionCallState(requestCancellationMap, requestId, callState);
    }

    private static void ReleaseAdmissionCallState(
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId,
        ServerCallCancellationState callState)
    {
        requestCancellationMap.TryRemove(requestId, callState);
        callState.Dispose();
    }

    private void ReleaseOneWayDispatchResources(
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        if (callState is not null)
        {
            requestCancellationMap.TryRemove(requestId, callState);
            callState.Dispose();
        }
        ReleaseCall(connection);
    }

    private ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState = null,
        bool admissionGranted = false)
    {
        var session = connection.Session;
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var hasReturnPayload = (flags & ProtocolV2FrameFlags.HasReturn) != 0;

        var request = ReadRequestEnvelope(session, payload, flags);
        if (IsDeadlineExceeded(request.DeadlineTimestamp))
        {
            ValueTask responseSend;
            try
            {
                responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    new SharpLinkException(
                        SharpLinkErrorCode.DeadlineExceeded,
                        "Request deadline exceeded before dispatch."),
                    connection.ConnectionToken);
            }
            finally
            {
                if (admittedCallState is not null)
                    ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            }
            return responseSend;
        }
        if (!Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo))
        {
            ValueTask responseSend;
            try
            {
                responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    new SharpLinkException(
                        SharpLinkErrorCode.Unimplemented,
                        $"Service {request.InterfaceHash} is not implemented."),
                    connection.ConnectionToken);
            }
            finally
            {
                if (admittedCallState is not null)
                    ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            }
            return responseSend;
        }
        if (!serviceInfo.AcceptsCalls)
        {
            ValueTask responseSend;
            try
            {
                responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    new SharpLinkException(
                        SharpLinkErrorCode.Unavailable,
                        "RPC module is draining"),
                    connection.ConnectionToken);
            }
            finally
            {
                if (admittedCallState is not null)
                    ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            }
            return responseSend;
        }

        if (_admissionController is not null && !admissionGranted)
        {
            admittedCallState = CreateAdmissionWaitState(
                connection,
                requestId,
                request.Deadline,
                request.DeadlineTimestamp,
                serverLoopToken,
                serviceInfo.ModuleCancellation,
                requestCancellationMap);
            var descriptor = GetMethodDescriptor(serviceInfo.Stub, request.MethodHash);
            ValueTask<AdmissionDecision> admissionTask;
            try
            {
                admissionTask = _admissionController.AcquireAsync(
                    CreateAdmissionContext(connection, descriptor, request),
                    checked((int)payload.Length),
                    allowQueue: true,
                    admittedCallState.InvocationToken);
            }
            catch (Exception exception)
            {
                ValueTask responseSend;
                try
                {
                    responseSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        new SharpLinkException(
                            SharpLinkErrorCode.Internal,
                            "The admission partition selector failed.",
                            exception),
                        connection.ConnectionToken);
                    SharpLinkTelemetry.RecordAdmissionRejected("partition", "partition_selector");
                }
                finally
                {
                    ReleasePendingAdmissionState(
                        session, requestCancellationMap, requestId, admittedCallState);
                }
                return responseSend;
            }
            if (!admissionTask.IsCompletedSuccessfully)
            {
                ReservePreAdmissionRequestStreams(
                    session,
                    requestId,
                    descriptor.ClientStreamCount,
                    admittedCallState);
                var retainedPayload = CopyAdmissionPayload(payload);
                return AwaitRpcAdmissionAsync(
                    admissionTask,
                    retainedPayload,
                    connection,
                    requestId,
                    flags,
                    requestCancellationMap,
                    serverLoopToken,
                    admittedCallState);
            }

            var decision = admissionTask.Result;
            if (!decision.IsAcquired)
            {
                ValueTask rejectionSend;
                try
                {
                    rejectionSend = RejectAdmission(
                        connection.Session,
                        requestId,
                        decision,
                        oneWay: false,
                        connection.ConnectionToken);
                }
                finally
                {
                    ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
                }
                return rejectionSend;
            }
            admittedCallState.AttachAdmissionLease(decision.Lease!);
        }

        var admission = TryAcquireCall(connection);
        if (admission != ServerCallAdmissionResult.Acquired)
        {
            if (admittedCallState is not null)
                ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            SharpLinkException rejection;
            if (admission is ServerCallAdmissionResult.PerConnectionCapacityExhausted or
                ServerCallAdmissionResult.ServerCapacityExhausted)
            {
                var reason = GetCallCapacityExhaustionReason(admission);
                SharpLinkTelemetry.RecordResourceExhausted("server", reason);
                rejection = SharpLinkResourceExhaustion.CreateWire(
                    reason,
                    $"Server call capacity is exhausted ({reason}).");
            }
            else
            {
                rejection = new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    "Server is draining.");
            }
            return session.SendRpcErrorWithBackpressureAsync(
                requestId, rejection, connection.ConnectionToken);
        }

        IRpcByteBufferWriter? decodedRequestOwner = null;
        try
        {
            if (_admissionController is not null)
            {
                payload = ((RpcSession)session).DecodeInboundPayload(
                    ProtocolV2FrameType.Request,
                    flags,
                    payload,
                    admittedCallState?.InvocationToken ?? serverLoopToken,
                    out decodedRequestOwner);
                request = ReadRequestEnvelope(session, payload, flags);
            }
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            var responseSend = session.SendRpcErrorWithBackpressureAsync(
                requestId, exception, connection.ConnectionToken);
            return ReleaseDispatchResourcesAfterResponseAsync(
                responseSend, admittedCallState, requestId, requestCancellationMap, connection);
        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            var responseSend = session.SendRpcErrorWithBackpressureAsync(
                requestId,
                CreateServerCancellationException(admittedCallState, request.DeadlineTimestamp),
                connection.ConnectionToken);
            return ReleaseDispatchResourcesAfterResponseAsync(
                responseSend, admittedCallState, requestId, requestCancellationMap, connection);
        }
        catch (Exception exception)
        {
            ((RpcSession)session).ReturnDecodedPayload(decodedRequestOwner);
            CompleteFailedRequestStreams(session, requestId, exception);
            ReleaseDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            throw;
        }

        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&
            serviceInfo.Stub.SupportsCancellation(request.MethodHash);
        var callState = admittedCallState ?? CreateTrackedCallState(
            connection,
            requestId,
            request.Deadline,
            request.DeadlineTimestamp,
            serverLoopToken,
            serviceInfo.ModuleCancellation,
            supportsCooperativeCancellation,
            requestCancellationMap);
        if (decodedRequestOwner is not null)
        {
            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
            callState.AttachPayloadOwner(_runtimeContext.Buffers, decodedRequestOwner);
            decodedRequestOwner = null;
        }
        var invokeToken = supportsCooperativeCancellation
            ? callState!.InvocationToken
            : serverLoopToken;

        if (!hasReturnPayload)
        {
            var callContext = CreateCallContext(
                connection, serviceInfo.Stub, request.MethodHash, requestId,
                request.Deadline, request.Metadata, invokeToken);
            try
            {
                using var callContextScope = SharpLinkCallContext.Push(callContext);
                var invokeTask = InvokeServiceAsync(
                    serviceInfo, connection, session, request.MethodHash, requestId,
                    request.Arguments, output: null, invokeToken, callContext);
                if (!invokeTask.IsCompletedSuccessfully)
                {
                    callState = EnsureTrackedCallState(
                        connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                        serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
                    return AwaitDispatchRpcNoReturnAsync(
                        invokeTask, session, requestId, callState, requestCancellationMap, connection,
                        callContext, serviceInfo.Stub, request.MethodHash, invokeToken);
                }
                if (callContext is SharpLinkServerInvocationContext interceptorContext)
                    interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
                var responseSend = ValueTask.CompletedTask;
                if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
                {
                    responseSend = session.SendPacketWithBackpressureAsync(
                        ProtocolV2FrameType.Response,
                        ProtocolV2FrameFlags.None,
                        requestId,
                        connection.ConnectionToken);
                }
                else
                {
                    responseSend = TrySendModuleDrainError(
                        callState, session, requestId, connection.ConnectionToken);
                }
                return ReleaseDispatchResourcesAfterResponseAsync(
                    responseSend, callState, requestId, requestCancellationMap, connection);
            }
            catch (OperationCanceledException exception)
            {
                CompleteFailedRequestStreams(session, requestId, exception);
                var responseSend = ValueTask.CompletedTask;
                if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
                {
                    responseSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        CreateServerCancellationException(callState, request.DeadlineTimestamp),
                        connection.ConnectionToken);
                }
                else
                {
                    responseSend = TrySendModuleDrainError(
                        callState, session, requestId, connection.ConnectionToken);
                }
                return ReleaseDispatchResourcesAfterResponseAsync(
                    responseSend, callState, requestId, requestCancellationMap, connection);
            }
            catch (Exception e)
            {
                CompleteFailedRequestStreams(session, requestId, e);
                var responseSend = ValueTask.CompletedTask;
                if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
                {
                    responseSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        MapServiceException(
                            e,
                            callContext,
                            session,
                            serviceInfo.Stub,
                            request.MethodHash,
                            requestId,
                            invokeToken),
                        connection.ConnectionToken);
                }
                else
                {
                    responseSend = TrySendModuleDrainError(
                        callState, session, requestId, connection.ConnectionToken);
                }
                return ReleaseDispatchResourcesAfterResponseAsync(
                    responseSend, callState, requestId, requestCancellationMap, connection);
            }
        }

        var writer = ((RpcSession)session).RentFrameWriter();
        var ownsWriter = true;
        var token = writer.BeginPacket(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((ulong)requestId));
        var responseCallContext = CreateCallContext(
            connection, serviceInfo.Stub, request.MethodHash, requestId,
            request.Deadline, request.Metadata, invokeToken);
        try
        {
            using var callContextScope = SharpLinkCallContext.Push(responseCallContext);
            var invokeTask = InvokeServiceAsync(
                serviceInfo, connection, session, request.MethodHash, requestId,
                request.Arguments, writer, invokeToken, responseCallContext);
            if (!invokeTask.IsCompletedSuccessfully)
            {
                callState = EnsureTrackedCallState(
                    connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                    serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
                return AwaitDispatchRpcAsync(invokeTask, session, requestId, writer, token, callState,
                    requestCancellationMap, connection, responseCallContext,
                    serviceInfo.Stub, request.MethodHash, invokeToken);
            }
            if (responseCallContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            if (!TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
            {
                _runtimeContext.Buffers.Return(writer);
                ownsWriter = false;
                var drainErrorSend = TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken);
                return ReleaseDispatchResourcesAfterResponseAsync(
                    drainErrorSend, callState, requestId, requestCancellationMap, connection);
            }
            writer.EndPacket(token);
            ownsWriter = false;
            var responseSend = ((RpcSession)session)
                .SendPacketWithBackpressureAsync(writer, connection.ConnectionToken);
            return CompletePayloadResponseAndReleaseDispatchResourcesAsync(
                responseSend,
                session,
                callState,
                requestId,
                requestCancellationMap,
                connection);

        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            if (!ownsWriter)
                throw;

            _runtimeContext.Buffers.Return(writer);
            var responseSend = ValueTask.CompletedTask;
            if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
            {
                responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    CreateServerCancellationException(callState, request.DeadlineTimestamp),
                    connection.ConnectionToken);
            }
            else
            {
                responseSend = TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken);
            }
            return ReleaseDispatchResourcesAfterResponseAsync(
                responseSend, callState, requestId, requestCancellationMap, connection);
        }
        catch (Exception e)
        {
            CompleteFailedRequestStreams(session, requestId, e);
            if (!ownsWriter)
            {
                if (e is SharpLinkCompressionProviderException)
                {
                    var compressionErrorSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId, e, connection.ConnectionToken);
                    return ReleaseDispatchResourcesAfterResponseAsync(
                        compressionErrorSend, callState, requestId, requestCancellationMap, connection);
                }
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            var responseSend = ValueTask.CompletedTask;
            if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
            {
                responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    MapServiceException(
                        e,
                        responseCallContext,
                        session,
                        serviceInfo.Stub,
                        request.MethodHash,
                        requestId,
                        invokeToken),
                    connection.ConnectionToken);
            }
            else
            {
                responseSend = TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken);
            }
            return ReleaseDispatchResourcesAfterResponseAsync(
                responseSend, callState, requestId, requestCancellationMap, connection);
        }
    }

    private void ReservePreAdmissionRequestStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        if (clientStreamCount == 0 || session.StreamManager is not StreamManager streamManager)
            return;

        var admissionController = _admissionController ?? throw new InvalidOperationException(
            "Pre-admission streams require an admission controller.");
        streamManager.ReservePreAdmissionStreams(
            requestId,
            clientStreamCount,
            _runtimeContext.Buffers,
            admissionController.TryReserveAdditionalQueuedBytes,
            admissionController.ReleaseAdditionalQueuedBytes,
            () => callState.TryCancel(
                ServerCallCancellationReason.AdmissionResourceExhausted),
            compressedPayload =>
            {
                var decodedPayload = ((RpcSession)session).DecodeInboundPayload(
                    ProtocolV2FrameType.StreamData,
                    ProtocolV2FrameFlags.Compressed,
                    compressedPayload,
                    callState.InvocationToken,
                    out var decodedOwner);
                return new PreAdmissionDecodedPayload(
                    decodedPayload.Slice(sizeof(ushort)),
                    decodedOwner ?? throw new InvalidOperationException(
                        "Compressed stream decoding did not return an owner."),
                    _runtimeContext.Buffers);
            });
    }

    private static void DrainRejectedOneWayStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount != 0 && session.StreamManager is StreamManager streamManager)
            streamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

    private int ResolveRawRequestClientStreamCount(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long contractId) ||
            !reader.TryReadLittleEndian(out long methodId) ||
            !Volatile.Read(ref _services).TryGetValue(contractId, out var registration) ||
            !registration.Stub.TryGetMethodDescriptor(methodId, out var descriptor))
        {
            return 0;
        }

        return descriptor.ClientStreamCount;
    }

    private static void CompleteFailedRequestStreams(
        IRpcSession session,
        long requestId,
        Exception exception)
    {
        if (session.StreamManager is StreamManager streamManager)
            streamManager.CompleteRequestStreams(requestId, exception);
    }

    private static void DrainFailedOneWayStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount == 0 || session.StreamManager is not StreamManager streamManager)
            return;

        streamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

    private async ValueTask AwaitDispatchRpcNoReturnAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        ServerCallCancellationState callState,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        IRpcStub stub,
        long methodId,
        CancellationToken cancellationToken)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
            if (callContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            if (TryClaimCallCompletion(callState))
            {
                await session.SendPacketWithBackpressureAsync(
                    ProtocolV2FrameType.Response,
                    ProtocolV2FrameFlags.None,
                    requestId,
                    connection.ConnectionToken).ConfigureAwait(false);
            }
            else
            {
                await TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            if (TryClaimCallCompletion(callState))
            {
                await session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    CreateServerCancellationException(callState, callState.DeadlineTimestamp),
                    connection.ConnectionToken).ConfigureAwait(false);
            }
            else
            {
                await TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            CompleteFailedRequestStreams(session, requestId, e);
            if (TryClaimCallCompletion(callState))
            {
                await session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    MapServiceException(
                        e,
                        callContext,
                        session,
                        stub,
                        methodId,
                        requestId,
                        cancellationToken),
                    connection.ConnectionToken).ConfigureAwait(false);
            }
            else
            {
                await TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private async ValueTask AwaitDispatchRpcAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        IRpcByteBufferWriter writer,
        PacketToken token,
        ServerCallCancellationState callState,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        IRpcStub stub,
        long methodId,
        CancellationToken cancellationToken)
    {
        var ownsWriter = true;
        try
        {
            await invokeTask.ConfigureAwait(false);
            if (callContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            if (!TryClaimCallCompletion(callState))
            {
                _runtimeContext.Buffers.Return(writer);
                ownsWriter = false;
                await TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken).ConfigureAwait(false);
                return;
            }
            writer.EndPacket(token);
            ownsWriter = false;
            await ((RpcSession)session)
                .SendPacketWithBackpressureAsync(writer, connection.ConnectionToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            if (!ownsWriter)
                throw;

            _runtimeContext.Buffers.Return(writer);
            if (TryClaimCallCompletion(callState))
            {
                await session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    CreateServerCancellationException(callState, callState.DeadlineTimestamp),
                    connection.ConnectionToken).ConfigureAwait(false);
            }
            else
            {
                await TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            CompleteFailedRequestStreams(session, requestId, e);
            if (!ownsWriter)
            {
                if (e is SharpLinkCompressionProviderException)
                {
                    await session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        e,
                        connection.ConnectionToken).ConfigureAwait(false);
                    return;
                }
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            if (TryClaimCallCompletion(callState))
            {
                await session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    MapServiceException(
                        e,
                        callContext,
                        session,
                        stub,
                        methodId,
                        requestId,
                        cancellationToken),
                    connection.ConnectionToken).ConfigureAwait(false);
            }
            else
            {
                await TrySendModuleDrainError(
                    callState, session, requestId, connection.ConnectionToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private void ReleaseDispatchResources(
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        if (callState is not null)
        {
            requestCancellationMap.TryRemove(requestId, callState);
            callState.Dispose();
        }
        ReleaseCall(connection);
    }

    private ValueTask ReleaseDispatchResourcesAfterResponseAsync(
        ValueTask responseSend,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        if (responseSend.IsCompletedSuccessfully)
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
            return ValueTask.CompletedTask;
        }

        return AwaitResponseAndReleaseDispatchResourcesAsync(
            responseSend, callState, requestId, requestCancellationMap, connection);
    }

    private async ValueTask AwaitResponseAndReleaseDispatchResourcesAsync(
        ValueTask responseSend,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        try
        {
            await responseSend.ConfigureAwait(false);
        }
        finally
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private ValueTask CompletePayloadResponseAndReleaseDispatchResourcesAsync(
        ValueTask responseSend,
        IRpcSession session,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        if (responseSend.IsCompletedSuccessfully)
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
            return ValueTask.CompletedTask;
        }

        return AwaitPayloadResponseAndReleaseDispatchResourcesAsync(
            responseSend, session, callState, requestId, requestCancellationMap, connection);
    }

    private async ValueTask AwaitPayloadResponseAndReleaseDispatchResourcesAsync(
        ValueTask responseSend,
        IRpcSession session,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        try
        {
            try
            {
                await responseSend.ConfigureAwait(false);
            }
            catch (SharpLinkCompressionProviderException exception)
            {
                await session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    exception,
                    connection.ConnectionToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private static async Task DispatchStreamChunkAsync(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "StreamData stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        var streamPayload = payload.Slice(sizeof(ushort));
        await session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
    }

    private static void DispatchStreamComplete(
        IRpcSession session,
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
            throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "StreamComplete stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        payload = payload.Slice(sizeof(ushort));
        return streamId;
    }

    private static long ReadMonotonicTimestamp(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long timestamp))
            throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Heartbeat timestamp is truncated.");
        return timestamp;
    }

    private ServerCallCancellationState? CreateTrackedCallState(
        ServerConnectionState connection,
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        bool supportsCooperativeCancellation,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        AdmissionLease? admissionLease = null)
    {
        if (!supportsCooperativeCancellation && !moduleDrainingToken.CanBeCanceled && admissionLease is null)
            return null;

        var callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            deadlineTimestamp,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation);
        if (admissionLease is not null)
            callState.AttachAdmissionLease(admissionLease);
        requestCancellationMap.Set(requestId, callState);
        connection.DeadlineScheduler.Register(callState);
        return callState;
    }

    private ServerCallCancellationState EnsureTrackedCallState(
        ServerConnectionState connection,
        ServerCallCancellationState? callState,
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap)
    {
        if (callState is not null)
            return callState;

        callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            deadlineTimestamp,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation: false);
        requestCancellationMap.Set(requestId, callState);
        connection.DeadlineScheduler.Register(callState);
        return callState;
    }

    private bool TryClaimCallCompletion(ServerCallCancellationState callState)
    {
        if (callState.TryClaimResponse())
            return true;
        if (callState.TryRecordAbandoned())
        {
            SharpLinkTelemetry.RecordAbandonedCall(
                "server",
                GetTerminationReasonTag(callState.Reason));
            LogRpcCallAbandoned(_logger, callState.Reason);
        }
        return false;
    }

    private bool TryClaimCallCompletion(
        ServerCallCancellationState? callState,
        long deadlineTimestamp,
        CancellationToken serverLoopToken)
    {
        if (callState is not null)
            return TryClaimCallCompletion(callState);

        var reason = IsDeadlineExceeded(deadlineTimestamp)
            ? ServerCallCancellationReason.DeadlineExceeded
            : serverLoopToken.IsCancellationRequested
                ? ServerCallCancellationReason.ConnectionClosed
                : ServerCallCancellationReason.None;
        if (reason == ServerCallCancellationReason.None)
            return true;

        SharpLinkTelemetry.RecordAbandonedCall("server", GetTerminationReasonTag(reason));
        LogRpcCallAbandoned(_logger, reason);
        return false;
    }

    private static SharpLinkException CreateServerCancellationException(
        ServerCallCancellationState? callState,
        long deadlineTimestamp)
        => (callState?.Reason ?? (IsDeadlineExceeded(deadlineTimestamp)
            ? ServerCallCancellationReason.DeadlineExceeded
            : ServerCallCancellationReason.RemoteCancel)) switch
        {
            ServerCallCancellationReason.DeadlineExceeded => new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."),
            ServerCallCancellationReason.ServerStopping => new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "Server is stopping."),
            ServerCallCancellationReason.ModuleDraining => new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC module is draining"),
            ServerCallCancellationReason.ConnectionClosed => new SharpLinkException(
                SharpLinkErrorCode.ConnectionClosed,
                "Connection closed."),
            ServerCallCancellationReason.AdmissionResourceExhausted => new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "Admission queue retained-byte capacity was exhausted."),
            _ => new SharpLinkException(SharpLinkErrorCode.Cancelled, "Request canceled.")
        };

    private static ValueTask TrySendModuleDrainError(
        ServerCallCancellationState? callState,
        IRpcSession session,
        long requestId,
        CancellationToken cancellationToken)
    {
        if (callState?.TryClaimModuleDrainResponse() == true)
        {
            return session.SendRpcErrorWithBackpressureAsync(
                requestId,
                new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    "RPC module is draining"),
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    private static ServerCallCancellationReason MapRemoteCancellationReason(
        ProtocolV2CancelReason reason)
        => reason switch
        {
            ProtocolV2CancelReason.DeadlineExceeded => ServerCallCancellationReason.DeadlineExceeded,
            ProtocolV2CancelReason.ConsumerAbandoned => ServerCallCancellationReason.ConsumerAbandoned,
            ProtocolV2CancelReason.Unspecified or
            ProtocolV2CancelReason.UserCancellation => ServerCallCancellationReason.RemoteCancel,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };

    private static string GetTerminationReasonTag(ServerCallCancellationReason reason)
        => reason switch
        {
            ServerCallCancellationReason.RemoteCancel => "remote_cancel",
            ServerCallCancellationReason.ConsumerAbandoned => "consumer_abandoned",
            ServerCallCancellationReason.DeadlineExceeded => "deadline_exceeded",
            ServerCallCancellationReason.ModuleDraining => "module_draining",
            ServerCallCancellationReason.ServerStopping => "server_stopping",
            ServerCallCancellationReason.ConnectionClosed => "connection_closed",
            ServerCallCancellationReason.AdmissionResourceExhausted => "admission_resource_exhausted",
            _ => "unknown"
        };

    private static SharpLinkException CreateRemoteCancellationException(
        ProtocolV2CancelReason reason)
        => reason switch
        {
            ProtocolV2CancelReason.DeadlineExceeded => new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Remote RPC deadline exceeded."),
            ProtocolV2CancelReason.ConsumerAbandoned => new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "Remote consumer abandoned the RPC stream."),
            _ => new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "Remote caller cancelled the RPC stream.")
        };

    private RpcRequestEnvelope ReadRequestEnvelope(
        IRpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long interfaceHash) ||
            !reader.TryReadLittleEndian(out long methodHash))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ProtocolViolation,
                "Request routing prefix is truncated.");
        }

        DateTimeOffset? deadline = null;
        var deadlineTimestamp = 0L;
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0)
        {
            if (!reader.TryReadLittleEndian(out long unixMilliseconds))
                throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Request deadline is truncated.");
            try
            {
                deadline = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
                var utcNow = DateTimeOffset.UtcNow;
                var monotonicNow = Stopwatch.GetTimestamp();
                deadlineTimestamp = GetMonotonicDeadlineTimestamp(
                    deadline.Value,
                    utcNow,
                    monotonicNow);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request deadline is outside the supported UTC range.",
                    exception);
            }
        }

        SharpLinkMetadata? metadata = null;
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if (session is not RpcSession runtimeSession ||
                (runtimeSession.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request metadata was not negotiated during handshake.");
            }
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength) ||
                metadataLength > _protocolOptions.MaxMetadataBytes ||
                reader.Remaining < metadataLength)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request metadata length is invalid.");
            }
            metadata = ProtocolV2PayloadCodec.ReadMetadata(
                reader.Sequence.Slice(reader.Position, metadataLength));
            reader.Advance(metadataLength);
        }

        return new RpcRequestEnvelope(
            interfaceHash,
            methodHash,
            reader.UnreadSequence,
            deadline,
            deadlineTimestamp,
            metadata);
    }

    private readonly record struct RpcRequestEnvelope(
        long InterfaceHash,
        long MethodHash,
        ReadOnlySequence<byte> Arguments,
        DateTimeOffset? Deadline,
        long DeadlineTimestamp,
        SharpLinkMetadata? Metadata);

    private static bool IsDeadlineExceeded(long deadlineTimestamp)
        => deadlineTimestamp > 0 && deadlineTimestamp <= Stopwatch.GetTimestamp();

    private static long GetMonotonicDeadlineTimestamp(
        DateTimeOffset deadline,
        DateTimeOffset utcNow,
        long monotonicNow)
    {
        var remaining = deadline - utcNow;
        if (remaining <= TimeSpan.Zero)
            return monotonicNow;
        var stopwatchTicks = remaining.TotalSeconds * Stopwatch.Frequency;
        if (stopwatchTicks >= long.MaxValue - monotonicNow)
            return long.MaxValue;
        return monotonicNow + Math.Max(1L, (long)Math.Ceiling(stopwatchTicks));
    }


}
