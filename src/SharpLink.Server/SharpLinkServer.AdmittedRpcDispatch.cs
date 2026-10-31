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

            var request = ReadRequestEnvelope(
                session, payload, flags, admittedCallState?.Deadline ?? default);
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
            if (!serviceSelection.TryGetService(out var serviceInfo))
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

            var resolved = new ResolvedMethodCall(serviceInfo, retainedAdmissionPayload!.SelectedMethod);
            if (!resolved.Method.IsKnown)
            {
                try
                {
                    return session.SendRpcErrorWithBackpressureAsync(requestId,
                        new SharpLinkException(ReferenceEquals(resolved.Method, RpcMethodFacts.UnknownMethod)
                                ? SharpLinkErrorCode.Unimplemented : SharpLinkErrorCode.Internal,
                            ReferenceEquals(resolved.Method, RpcMethodFacts.UnknownMethod)
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
                            out var resourceRejection))
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
