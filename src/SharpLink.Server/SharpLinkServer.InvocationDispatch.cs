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
        if (IsDeadlineExceeded(request.RpcDeadline))
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
                request.RpcDeadline,
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
                    deadline: request.RpcDeadline,
                    cancellationToken: admittedCallState.InvocationToken);
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

        if ((flags & ProtocolV2FrameFlags.Compressed) != 0 && admittedCallState is null)
        {
            admittedCallState = CreateAdmissionWaitState(
                connection,
                requestId,
                request.RpcDeadline,
                serverLoopToken,
                serviceInfo.ModuleCancellation,
                requestCancellationMap);
        }

        IRpcByteBufferWriter? decodedRequestOwner = null;
        try
        {
            if ((flags & ProtocolV2FrameFlags.Compressed) != 0)
            {
                payload = session.DecodeInboundPayload(
                    ProtocolV2FrameType.Request,
                    flags,
                    payload,
                    admittedCallState!.InvocationToken,
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
                MapServerCancellationException(admittedCallState, request.RpcDeadline),
                connection.ConnectionToken);
            return ReleaseDispatchResourcesAfterResponseAsync(
                responseSend, admittedCallState, requestId, requestCancellationMap, connection);
        }
        catch (Exception exception)
        {
            session.ReturnDecodedPayload(decodedRequestOwner);
            CompleteFailedRequestStreams(session, requestId, exception);
            ReleaseDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            throw;
        }

        if (IsDeadlineExceeded(request.RpcDeadline))
        {
            session.ReturnDecodedPayload(decodedRequestOwner);
            decodedRequestOwner = null;
            var exception = new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded before dispatch.");
            CompleteFailedRequestStreams(session, requestId, exception);
            var responseSend = session.SendRpcErrorWithBackpressureAsync(
                requestId, exception, connection.ConnectionToken);
            return ReleaseDispatchResourcesAfterResponseAsync(
                responseSend, admittedCallState, requestId, requestCancellationMap, connection);
        }

        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&
            serviceInfo.Stub.SupportsCancellation(request.MethodHash);
        var callState = admittedCallState ?? CreateTrackedCallState(
            connection,
            requestId,
            request.RpcDeadline,
            serverLoopToken,
            serviceInfo.ModuleCancellation,
            supportsCooperativeCancellation,
            requestCancellationMap);
        if (decodedRequestOwner is not null)
        {
            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.RpcDeadline,
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
                        connection, callState, requestId, request.RpcDeadline,
                        serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
                    return AwaitDispatchRpcNoReturnAsync(
                        invokeTask, session, requestId, callState, requestCancellationMap, connection,
                        callContext, serviceInfo.Stub, request.MethodHash, invokeToken);
                }
                if (callContext is SharpLinkServerInvocationContext
                    {
                        Status: SharpLinkInvocationStatus.Pending
                    } interceptorContext)
                    interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
                var responseSend = ValueTask.CompletedTask;
                if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
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
                if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
                {
                    responseSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        MapServerCancellationException(callState, request.RpcDeadline),
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
                if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
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

        var writer = session.RentFrameWriter();
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
                    connection, callState, requestId, request.RpcDeadline,
                    serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
                return AwaitDispatchRpcAsync(invokeTask, session, requestId, writer, token, callState,
                    requestCancellationMap, connection, responseCallContext,
                    serviceInfo.Stub, request.MethodHash, invokeToken);
            }
            if (responseCallContext is SharpLinkServerInvocationContext
                {
                    Status: SharpLinkInvocationStatus.Pending
                } interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            if (!TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
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
            var responseSend = session
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
            if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
            {
                responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    MapServerCancellationException(callState, request.RpcDeadline),
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
                if (e is SharpLinkCompressionProviderException compressionException)
                {
                    var compressionErrorSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId, compressionException, connection.ConnectionToken);
                    return ReleaseDispatchResourcesAfterResponseAsync(
                        compressionErrorSend, callState, requestId, requestCancellationMap, connection);
                }
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            var responseSend = ValueTask.CompletedTask;
            if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
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
        RpcSession session,
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
            if (callContext is SharpLinkServerInvocationContext
                {
                    Status: SharpLinkInvocationStatus.Pending
                } interceptorContext)
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
                    MapServerCancellationException(callState, callState.Deadline),
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
        RpcSession session,
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
            if (callContext is SharpLinkServerInvocationContext
                {
                    Status: SharpLinkInvocationStatus.Pending
                } interceptorContext)
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
            await session
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
                    MapServerCancellationException(callState, callState.Deadline),
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
                if (e is SharpLinkCompressionProviderException compressionException)
                {
                    await session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        compressionException,
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
        RpcSession session,
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
        RpcSession session,
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

}
