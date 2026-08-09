using System.Diagnostics;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
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
