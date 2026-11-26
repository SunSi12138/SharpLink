namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    /// <summary>
    /// Continues two-way dispatch without resolving the service before the execution gate.
    /// </summary>
    private ValueTask ContinueRpcDispatch(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ServerRequestEnvelope request,
        CancellationToken moduleCancellation,
        NStudyMethodFacts facts,
        NStudyServerBindings.Use binding,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? callState,
        ServerRequestPermit requestOwner,
        IRpcByteBufferWriter? decodedRequestOwner)
    {
        var session = connection.Session;
        var method = facts.Descriptor;
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var hasReturnPayload = (flags & ProtocolV2FrameFlags.HasReturn) != 0;

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
                responseSend,
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner);
        }

        if (serverLoopToken.IsCancellationRequested)
        {
            session.ReturnDecodedPayload(decodedRequestOwner);
            decodedRequestOwner = null;
            var exception = new SharpLinkException(
                SharpLinkErrorCode.ConnectionClosed,
                "Connection closed before dispatch.");
            CompleteFailedRequestStreams(session, requestId, exception);
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner);
            return ValueTask.FromException(exception);
        }

        if (callState is not null)
        {
            // Cancellation and Reserved -> Active share one terminal gate. If cancellation wins
            // before activation, no generated stub or user code may run even when provider decode
            // completed successfully. Once activation wins, later cancellation keeps the existing
            // cooperative/non-cooperative handler semantics below.
            if (!callState.TryActivateRequest(requestOwner))
            {
                session.ReturnDecodedPayload(decodedRequestOwner);
                decodedRequestOwner = null;
                var exception = MapServerCancellationException(callState, request.RpcDeadline);
                CompleteFailedRequestStreams(session, requestId, exception);
                _ = TryClaimCallCompletion(callState);
                var responseSend = callState.Reason == ServerCallCancellationReason.ModuleDraining
                    ? TrySendModuleDrainError(
                        callState,
                        session,
                        requestId,
                        connection.ConnectionToken)
                    : session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        exception,
                        connection.ConnectionToken);
                return ReleaseDispatchResourcesAfterResponseAsync(
                    responseSend,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
            }
        }
        else
        {
            requestOwner.Activate();
        }

        var supportsCooperativeCancellation =
            (isCancellable || moduleCancellation.CanBeCanceled) &&
            method.SupportsCancellation;
        callState ??= CreateTrackedCallState(
            connection,
            requestId,
            request.RpcDeadline,
            serverLoopToken,
            moduleCancellation,
            supportsCooperativeCancellation,
            requestCancellationMap);
        if (method.HasClientStreams || method.Kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming)
        {
            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.RpcDeadline,
                serverLoopToken, moduleCancellation, requestCancellationMap);
            if (method.HasClientStreams)
                callState.RequireNStudyStreamAcceptance();
        }
        if (decodedRequestOwner is not null)
        {
            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.RpcDeadline,
                serverLoopToken, moduleCancellation, requestCancellationMap);
            callState.AttachPayloadOwner(_runtimeContext.Buffers, decodedRequestOwner);
            decodedRequestOwner = null;
        }
        var invokeToken = supportsCooperativeCancellation
            ? callState!.InvocationToken
            : serverLoopToken;

        if (!hasReturnPayload)
        {
            var callContext = CreateCallContext(
                connection, method, requestId,
                request.RpcDeadline, request.Metadata, invokeToken);
            try
            {
                using var callContextScope = SharpLinkCallContext.Push(callContext);
                var invokeTask = InvokeServiceAsync(
                    connection, session, facts, binding, callState, requestId,
                    request.Arguments, output: null, invokeToken, callContext);
                if (!invokeTask.IsCompletedSuccessfully)
                {
                    callState = EnsureTrackedCallState(
                        connection, callState, requestId, request.RpcDeadline,
                        serverLoopToken, moduleCancellation, requestCancellationMap);
                    return AwaitDispatchRpcNoReturnAsync(
                        invokeTask,
                        session,
                        requestId,
                        callState,
                        requestCancellationMap,
                        connection,
                        callContext,
                        method,
                        invokeToken,
                        requestOwner);
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
                    responseSend,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
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
                    responseSend,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
            }
            catch (Exception exception)
            {
                CompleteFailedRequestStreams(session, requestId, exception);
                var responseSend = ValueTask.CompletedTask;
                if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
                {
                    responseSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        MapServiceException(
                            exception,
                            callContext,
                            session,
                            method,
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
                    responseSend,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
            }
        }

        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        var token = writer.BeginPacket(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((ulong)requestId));
        var responseCallContext = CreateCallContext(
            connection, method, requestId,
            request.RpcDeadline, request.Metadata, invokeToken);
        try
        {
            using var callContextScope = SharpLinkCallContext.Push(responseCallContext);
            var invokeTask = InvokeServiceAsync(
                connection, session, facts, binding, callState, requestId,
                request.Arguments, writer, invokeToken, responseCallContext);
            if (!invokeTask.IsCompletedSuccessfully)
            {
                callState = EnsureTrackedCallState(
                    connection, callState, requestId, request.RpcDeadline,
                    serverLoopToken, moduleCancellation, requestCancellationMap);
                return AwaitDispatchRpcAsync(
                    invokeTask,
                    session,
                    requestId,
                    writer,
                    token,
                    callState,
                    requestCancellationMap,
                    connection,
                    responseCallContext,
                    method,
                    invokeToken,
                    requestOwner);
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
                    drainErrorSend,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
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
                connection,
                requestOwner);
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
                responseSend,
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner);
        }
        catch (Exception exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            if (!ownsWriter)
            {
                if (exception is SharpLinkCompressionProviderException compressionException)
                {
                    var compressionErrorSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId, compressionException, connection.ConnectionToken);
                    return ReleaseDispatchResourcesAfterResponseAsync(
                        compressionErrorSend,
                        callState,
                        requestId,
                        requestCancellationMap,
                        connection,
                        requestOwner);
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
                        exception,
                        responseCallContext,
                        session,
                        method,
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
                responseSend,
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestOwner);
        }
    }
}
