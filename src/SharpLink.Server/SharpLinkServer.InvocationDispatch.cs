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
        AdmissionProgram? admissionProgram,
        ServerRequestEnvelope request,
        in DispatchPreparation preparation,
        NStudyServerBindings.Use binding,
        ServerCallCancellationState? admittedCallState = null,
        bool admissionGranted = false,
        ServerRetainedAdmissionPayload? retainedAdmissionPayload = null)
    {
        var ownsAdmissionProgramUse = admissionProgram is not null && !admissionGranted;
        try
        {
            var session = connection.Session;
            var isCompressed = (flags & ProtocolV2FrameFlags.Compressed) != 0;

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
            if (!preparation.IsBound)
            {
                ValueTask responseSend;
                try
                {
                    responseSend = session.SendRpcErrorWithBackpressureAsync(
                        requestId,
                        new SharpLinkException(
                            SharpLinkErrorCode.Unavailable,
                            "The exact private binding is no longer available."),
                        connection.ConnectionToken);
                }
                finally
                {
                    if (admittedCallState is not null)
                        ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
                }
                return responseSend;
            }
            if (!preparation.AcceptsCalls)
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
            var facts = preparation.Facts;
            if (!preparation.HasMethod ||
                !IsSupportedNStudyTwoWayShape(facts.Descriptor, flags))
            {
                return session.SendRpcErrorWithBackpressureAsync(requestId,
                    new SharpLinkException(SharpLinkErrorCode.Unimplemented,
                        "This private checkpoint does not support that bound RPC shape."),
                    connection.ConnectionToken);
            }

            var moduleCancellation = preparation.ModuleCancellation;
            if (admissionProgram is not null && !admissionGranted)
            {
                admittedCallState = CreateAdmissionWaitState(
                    connection,
                    requestId,
                    request.RpcDeadline,
                    serverLoopToken,
                    moduleCancellation,
                    requestCancellationMap);
                admittedCallState.AttachAdmissionProgramUse(admissionProgram);
                ownsAdmissionProgramUse = false;
                var descriptor = facts.Descriptor;
                ValueTask<AdmissionDecision> admissionTask;
                try
                {
                    admissionTask = admissionProgram.Controller.AcquireAsync(
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
                        responseSend = PublishAdmissionError(
                            session,
                            requestId,
                            admittedCallState,
                            new SharpLinkException(
                                SharpLinkErrorCode.Internal,
                                "The admission partition selector failed.",
                                exception),
                            connection.ConnectionToken);
                        if (admittedCallState.Reason == ServerCallCancellationReason.Completed)
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
                    if (!TryCopyAdmissionPayload(payload, flags, out var queuedRetainedPayload))
                    {
                        admittedCallState.TryCancel(ServerCallCancellationReason.AdmissionResourceExhausted);
                        return RejectQueuedAdmissionForRetainedBudgetAsync(
                            admissionTask,
                            connection,
                            requestId,
                            requestCancellationMap,
                            admittedCallState,
                            oneWay: false);
                    }

                    ReservePreAdmissionRequestStreams(
                        connection,
                        requestId,
                        descriptor.ClientStreamCount,
                        admittedCallState);
                    return AwaitRpcAdmissionAsync(
                        admissionTask,
                        queuedRetainedPayload!,
                        connection,
                        requestId,
                        flags,
                        requestCancellationMap,
                        serverLoopToken,
                        admittedCallState,
                        admissionProgram,
                        binding,
                        facts,
                        moduleCancellation);
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
                            callState: admittedCallState,
                            cancellationToken: connection.ConnectionToken);
                    }
                    finally
                    {
                        ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
                    }
                    return rejectionSend;
                }
                admittedCallState.AttachAdmissionLease(decision.Lease!);
            }

            if (!binding.CanPrepareDispatch())
            {
                if (admittedCallState is not null)
                    ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
                return session.SendRpcErrorWithBackpressureAsync(
                    requestId, NStudyBindingRetiredError(), connection.ConnectionToken);
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

            if (isCompressed && ShouldUsePersistentDecode(flags, payload))
            {
                return DispatchRpcWithPersistentDecodeAsync(
                    connection,
                    requestId,
                    flags,
                    payload,
                    request,
                    moduleCancellation,
                    facts,
                    binding,
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
                        moduleCancellation,
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
                moduleCancellation,
                facts,
                binding,
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

    private static bool IsSupportedNStudyTwoWayShape(RpcMethodDescriptor method, ProtocolV2FrameFlags flags)
    {
        var hasReturn = (flags & ProtocolV2FrameFlags.HasReturn) != 0;
        return method.Kind switch
        {
            RpcMethodKind.Unary => !method.HasClientStreams && method.HasResponsePayload == hasReturn,
            RpcMethodKind.ClientStreaming => method.HasClientStreams && method.ClientStreamCount > 0 &&
                method.HasResponsePayload == hasReturn,
            RpcMethodKind.ServerStreaming => !method.HasClientStreams && !hasReturn,
            RpcMethodKind.DuplexStreaming => method.HasClientStreams && method.ClientStreamCount > 0 && !hasReturn,
            _ => false
        };
    }

    private async ValueTask AwaitDispatchRpcNoReturnAsync(
        ValueTask invokeTask,
        RpcSession session,
        long requestId,
        ServerCallCancellationState callState,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        RpcMethodDescriptor method,
        CancellationToken cancellationToken,
        ServerRequestPermit requestPermit)
    {
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
                        method,
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
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
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
        RpcMethodDescriptor method,
        CancellationToken cancellationToken,
        ServerRequestPermit requestPermit)
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
                        method,
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
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
        }
    }

    private void ReleaseDispatchResources(
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        ServerRequestPermit requestPermit)
    {
        _ = connection;
        if (requestPermit.IsReserved)
            requestPermit.ReleaseDecodeResources();
        if (callState is not null)
        {
            if (requestPermit.IsActive)
                requestPermit.TransferDecodedBytesTo(callState);
            requestCancellationMap.TryRemove(requestId, callState);
            callState.Dispose();
        }
        requestPermit.Dispose();
    }

    private ValueTask ReleaseDispatchResourcesAfterResponseAsync(
        ValueTask responseSend,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        ServerRequestPermit requestPermit)
    {
        if (requestPermit.IsReserved)
            requestPermit.ReleaseDecodeResources();

        if (responseSend.IsCompletedSuccessfully)
        {
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
            return ValueTask.CompletedTask;
        }

        return AwaitResponseAndReleaseDispatchResourcesAsync(
            responseSend,
            callState,
            requestId,
            requestCancellationMap,
            connection,
            requestPermit);
    }

    private async ValueTask AwaitResponseAndReleaseDispatchResourcesAsync(
        ValueTask responseSend,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        ServerRequestPermit requestPermit)
    {
        try
        {
            await responseSend.ConfigureAwait(false);
        }
        finally
        {
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
        }
    }

    private ValueTask CompletePayloadResponseAndReleaseDispatchResourcesAsync(
        ValueTask responseSend,
        RpcSession session,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        ServerRequestPermit requestPermit)
    {
        if (responseSend.IsCompletedSuccessfully)
        {
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
            return ValueTask.CompletedTask;
        }

        return AwaitPayloadResponseAndReleaseDispatchResourcesAsync(
            responseSend,
            session,
            callState,
            requestId,
            requestCancellationMap,
            connection,
            requestPermit);
    }

    private async ValueTask AwaitPayloadResponseAndReleaseDispatchResourcesAsync(
        ValueTask responseSend,
        RpcSession session,
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        ServerRequestPermit requestPermit)
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
            ReleaseDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
        }
    }

}
