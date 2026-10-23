namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Dedicated queued continuation: admission identity never enters immediate dispatch.
    // The post-admission resource skeleton mirrors the immediate method and requires parity tests.
    private ValueTask DispatchAdmittedOneWayRpc(
        QueuedServiceSelection serviceSelection,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        AdmissionProgram? admissionProgram,
        ServerCallCancellationState? admittedCallState = null,
        bool admissionGranted = false,
        int admittedClientStreamCount = 0,
        ServerRetainedAdmissionPayload? retainedAdmissionPayload = null)
    {
        var ownsAdmissionProgramUse = admissionProgram is not null && !admissionGranted;
        try
        {
            var session = connection.Session;
            var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
            var isCompressed = (flags & ProtocolV2FrameFlags.Compressed) != 0;
            var request = ReadRequestEnvelope(
                session, payload, flags, admittedCallState?.Deadline ?? default);
            if (!serviceSelection.TryGetService(out var serviceInfo))
            {
                if (admittedCallState is not null)
                {
                    DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                    ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
                    return ValueTask.CompletedTask;
                }
                return TerminateUnresolvableOneWayRequest(session, requestId);
            }

            // Resolve the method shape before pre-invocation rejection. A rejected OneWay call with
            // client streams still needs a receive route so the peer can finish sending and recover
            // its receive credit even though no user invocation will run. If the method shape cannot
            // be resolved on the immediate path, terminate the connection rather than guess a stream
            // count; an admission-resume path already owns the exact reserved stream count and can
            // safely drain those routes instead.
            if (!serviceInfo.Stub.TryGetMethodDescriptor(request.MethodHash, out var descriptor))
            {
                if (admittedCallState is not null)
                {
                    DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                    ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
                    return ValueTask.CompletedTask;
                }
                return TerminateUnresolvableOneWayRequest(session, requestId);
            }
            if (IsDeadlineExceeded(request.RpcDeadline))
            {
                DrainRejectedOneWayStreams(
                    session,
                    requestId,
                    admittedCallState is null
                        ? descriptor.ClientStreamCount
                        : admittedClientStreamCount);
                if (admittedCallState is not null)
                    ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
                return ValueTask.CompletedTask;
            }
            if (!serviceInfo.AcceptsCalls)
            {
                DrainRejectedOneWayStreams(
                    session,
                    requestId,
                    admittedCallState is null
                        ? descriptor.ClientStreamCount
                        : admittedClientStreamCount);
                if (admittedCallState is not null)
                    ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
                return ValueTask.CompletedTask;
            }

            var admission = TryReserveCall(connection, mayDecode: isCompressed, out var requestPermit);
            if (admission != ServerCallAdmissionResult.Acquired || requestPermit is null)
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
                return ValueTask.CompletedTask;
            }
            var requestOwner = requestPermit;

            IRpcByteBufferWriter? decodedRequestOwner = null;
            try
            {
                if (isCompressed)
                {
                    admittedCallState = EnsurePreDecodeCallState(
                        connection,
                        admittedCallState,
                        requestId,
                        request.RpcDeadline,
                        serverLoopToken,
                        serviceInfo.ModuleCancellation,
                        requestCancellationMap);
                    if (!TryPrepareCompressedRequestDecode(
                            requestOwner,
                            retainedAdmissionPayload?.RetainedPermit,
                            flags,
                            payload,
                            out var decodePermit,
                            out var resourceRejection))
                    {
                        retainedAdmissionPayload?.Dispose();
                        requestOwner.ReleaseDecodeResources();
                        var rejection = resourceRejection ?? throw new InvalidOperationException(
                            "Compressed one-way decode resource rejection is missing its error.");
                        var reason = SharpLinkResourceExhaustion.GetReason(rejection);
                        Interlocked.Increment(ref _rejectedOneWayCalls);
                        LogOnewayRpcResourceExhausted(_logger, reason);
                        DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                        ReleaseOneWayDispatchResources(
                            admittedCallState,
                            requestId,
                            requestCancellationMap,
                            connection,
                            requestOwner);
                        return ValueTask.CompletedTask;
                    }

                    if ((flags & ProtocolV2FrameFlags.HasMetadata) == 0)
                    {
                        // Preserve the original no-metadata decode/release/parse path.
                        // Do not snapshot the encoded sequence or compare its prefix.
                        payload = session.DecodeInboundPayload(
                            ProtocolV2FrameType.Request,
                            flags,
                            payload,
                            admittedCallState.InvocationToken,
                            out decodedRequestOwner);
                        retainedAdmissionPayload?.Dispose();
                        decodePermit!.CompleteDecode();
                        request = ReadRequestEnvelope(
                            session, payload, flags, request.RpcDeadline);
                    }
                    else
                    {
                        var encodedPayload = payload;
                        payload = session.DecodeInboundPayload(
                            ProtocolV2FrameType.Request,
                            flags,
                            payload,
                            admittedCallState.InvocationToken,
                            out decodedRequestOwner);
                        // Only metadata reuse needs both owners alive while comparing.
                        request = ServerRequestEnvelopeReader.ReadDecoded(
                            session, payload, encodedPayload, in request, flags,
                            _protocolOptions.MaxMetadataBytes, _runtimeContext.TimeProvider);
                        retainedAdmissionPayload?.Dispose();
                        decodePermit!.CompleteDecode();
                    }
                }
            }
            catch (SharpLinkException exception) when (
                exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
            {
                retainedAdmissionPayload?.Dispose();
                session.ReturnDecodedPayload(decodedRequestOwner);
                decodedRequestOwner = null;
                requestOwner.ReleaseDecodeResources();
                Interlocked.Increment(ref _rejectedOneWayCalls);
                LogOnewayRpcDispatchFailed(_logger, exception);
                DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                ReleaseOneWayDispatchResources(
                    admittedCallState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
                return ValueTask.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                retainedAdmissionPayload?.Dispose();
                session.ReturnDecodedPayload(decodedRequestOwner);
                decodedRequestOwner = null;
                requestOwner.ReleaseDecodeResources();
                DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                ReleaseOneWayDispatchResources(
                    admittedCallState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
                return ValueTask.CompletedTask;
            }
            catch
            {
                retainedAdmissionPayload?.Dispose();
                session.ReturnDecodedPayload(decodedRequestOwner);
                decodedRequestOwner = null;
                requestOwner.ReleaseDecodeResources();
                DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                ReleaseOneWayDispatchResources(
                    admittedCallState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
                throw;
            }

            if (IsDeadlineExceeded(request.RpcDeadline) || serverLoopToken.IsCancellationRequested)
            {
                session.ReturnDecodedPayload(decodedRequestOwner);
                decodedRequestOwner = null;
                requestOwner.ReleaseDecodeResources();
                DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                ReleaseOneWayDispatchResources(
                    admittedCallState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
                return ValueTask.CompletedTask;
            }

            if (admittedCallState is not null)
            {
                if (!admittedCallState.TryActivateRequest(requestOwner))
                {
                    session.ReturnDecodedPayload(decodedRequestOwner);
                    decodedRequestOwner = null;
                    requestOwner.ReleaseDecodeResources();
                    DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                    ReleaseOneWayDispatchResources(
                        admittedCallState,
                        requestId,
                        requestCancellationMap,
                        connection,
                        requestOwner);
                    return ValueTask.CompletedTask;
                }
            }
            else
            {
                requestOwner.Activate();
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

            var callContext = CreateCallContext(
                connection, serviceInfo.Stub, request.MethodHash, requestId,
                request.RpcDeadline, request.Metadata, invokeToken);
            try
            {
                // #299 deliberately excludes OneWay from generic pre-invocation reservation. Install
                // the same promoted route here, before interceptors can short-circuit, and retain it
                // until local OneWay completion so typed-input abandonment has a stable owner.
                ReservePreInvocationRequestStreams(
                    session,
                    descriptor.ClientStreamCount,
                    requestId,
                    invokeToken,
                    retainUntilLocalCompletion: true);

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
                    if (callContext is SharpLinkServerInvocationContext
                        {
                            Status: SharpLinkInvocationStatus.Pending
                        } interceptorContext)
                        interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
                    DrainCompletedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                    TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken);
                    ReleaseOneWayDispatchResources(
                        callState,
                        requestId,
                        requestCancellationMap,
                        connection,
                        requestOwner);
                    return ValueTask.CompletedTask;
                }

                callState = EnsureTrackedCallState(
                    connection, callState, requestId, request.RpcDeadline,
                    serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
                return new ValueTask(AwaitOneWayDispatchAsync(
                    invokeTask,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    callContext,
                    session,
                    serviceInfo.Stub,
                    request.MethodHash,
                    descriptor.ClientStreamCount,
                    invokeToken,
                    requestOwner));
            }
            catch (Exception ex)
            {
                DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
                {
                    LogOnewayRpcDispatchFailed(_logger, MapServiceException(
                        ex, callContext, session, serviceInfo.Stub, request.MethodHash, requestId, invokeToken));
                }
                ReleaseOneWayDispatchResources(
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
                return ValueTask.CompletedTask;
            }
        }
        finally
        {
            if (ownsAdmissionProgramUse)
                admissionProgram!.ReleaseUse();
        }
    }
}
