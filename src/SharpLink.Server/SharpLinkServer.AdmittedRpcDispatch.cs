namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Dedicated queued continuation: admission identity never enters immediate dispatch.
    // The post-admission resource skeleton mirrors the immediate method and requires parity tests.
    private ValueTask DispatchAdmittedRpcAsync(
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
        ServerRetainedAdmissionPayload? retainedAdmissionPayload = null)
    {
        var ownsAdmissionProgramUse = admissionProgram is not null && !admissionGranted;
        try
        {
            var session = connection.Session;
            var isCompressed = (flags & ProtocolV2FrameFlags.Compressed) != 0;

            var request = (retainedAdmissionPayload ?? throw new InvalidOperationException(
                "Queued dispatch requires its exact retained request envelope.")).SelectedRequest;
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
            var selected = new SelectedMethodCall(serviceSelection, retainedAdmissionPayload!.SelectedMethod);
            if (!selected.Method.IsKnown)
            {
                try
                {
                    return session.SendRpcErrorWithBackpressureAsync(requestId,
                        new SharpLinkException(ReferenceEquals(selected.Method, RpcMethodFacts.UnknownMethod)
                                ? SharpLinkErrorCode.Unimplemented : SharpLinkErrorCode.Internal,
                            ReferenceEquals(selected.Method, RpcMethodFacts.UnknownMethod)
                                ? $"Method {request.MethodHash} is not implemented." : "The RPC stub returned invalid method facts."),
                        connection.ConnectionToken);
                }
                finally
                {
                    if (admittedCallState is not null)
                        ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
                }
            }

            var admission = TryReserveCall(connection, mayDecode: isCompressed, out var requestPermit);
            if (admission != ServerCallAdmissionResult.Acquired || requestPermit is null)
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
            var requestOwner = requestPermit;

            ResolvedMethodCall resolved;
            bool protectedSelection;
            try
            {
                protectedSelection = TryProtectSelectedInvocation(selected.Selection, selected.Method, requestOwner, out resolved);
            }
            catch
            {
                try { retainedAdmissionPayload?.Dispose(); }
                finally { ReleaseDispatchResources(admittedCallState, requestId, requestCancellationMap, connection, requestOwner); }
                throw;
            }
            if (!protectedSelection)
            {
                return RejectRetiredSelectedRpcAsync(connection, requestId, admittedCallState,
                    requestCancellationMap, requestOwner, retainedAdmissionPayload);
            }
            var serviceInfo = resolved.Registration;


            if (isCompressed && ShouldUsePersistentDecode(flags, serviceInfo, request, payload))
            {
                return DispatchRpcWithPersistentDecodeAsync(
                    connection,
                    requestId,
                    flags,
                    payload,
                    request,
                    in resolved,
                    requestCancellationMap,
                    serverLoopToken,
                    admittedCallState,
                    requestOwner,
                    retainedAdmissionPayload);
            }

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
                            out var resourceRejection,
                            request.FrameType))
                    {
                        retainedAdmissionPayload?.Dispose();
                        var rejection = resourceRejection ?? throw new InvalidOperationException(
                            "Compressed request decode resource rejection is missing its error.");
                        CompleteFailedRequestStreams(session, requestId, rejection);
                        var responseSend = session.SendRpcErrorWithBackpressureAsync(
                            requestId, rejection, connection.ConnectionToken);
                        return ReleaseDispatchResourcesAfterResponseAsync(
                            responseSend,
                            admittedCallState,
                            requestId,
                            requestCancellationMap,
                            connection,
                            requestOwner);
                    }

                    if ((flags & ProtocolV2FrameFlags.HasMetadata) == 0)
                    {
                        // Keep both owners alive until the exact admitted prefix is verified.
                        var encodedPayload = payload;
                        payload = session.DecodeInboundPayload(
                            request.FrameType,
                            flags,
                            payload,
                            admittedCallState.InvocationToken,
                            out decodedRequestOwner);
                        request = ServerRequestEnvelopeReader.ReadDecoded(
                            session, payload, encodedPayload, in request, flags,
                            _protocolOptions.MaxMetadataBytes, _runtimeContext.TimeProvider);
                        retainedAdmissionPayload?.Dispose();
                        decodePermit!.CompleteDecode();
                    }
                    else
                    {
                        var encodedPayload = payload;
                        payload = session.DecodeInboundPayload(
                            request.FrameType,
                            flags,
                            payload,
                            admittedCallState.InvocationToken,
                            out decodedRequestOwner);
                        // Rebind arguments without changing captured admission identity or deadline.
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
                CompleteFailedRequestStreams(session, requestId, exception);
                var responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId, exception, connection.ConnectionToken);
                return ReleaseDispatchResourcesAfterResponseAsync(
                    responseSend,
                    admittedCallState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
            }
            catch (OperationCanceledException exception)
            {
                retainedAdmissionPayload?.Dispose();
                session.ReturnDecodedPayload(decodedRequestOwner);
                decodedRequestOwner = null;
                CompleteFailedRequestStreams(session, requestId, exception);
                var responseSend = session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    MapServerCancellationException(admittedCallState, request.RpcDeadline),
                    connection.ConnectionToken);
                return ReleaseDispatchResourcesAfterResponseAsync(
                    responseSend,
                    admittedCallState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
            }
            catch (Exception exception)
            {
                retainedAdmissionPayload?.Dispose();
                session.ReturnDecodedPayload(decodedRequestOwner);
                CompleteFailedRequestStreams(session, requestId, exception);
                ReleaseDispatchResources(
                    admittedCallState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    requestOwner);
                throw;
            }

            return ContinueRpcDispatch(
                connection,
                requestId,
                flags,
                request,
                in resolved,
                requestCancellationMap,
                serverLoopToken,
                admittedCallState,
                requestOwner,
                decodedRequestOwner);
        }
        finally
        {
            if (ownsAdmissionProgramUse)
                admissionProgram!.ReleaseUse();
        }
    }
}
