namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask DispatchOneWayRpc(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState = null,
        bool admissionGranted = false,
        int admittedClientStreamCount = 0,
        ServerRetainedAdmissionPayload? retainedAdmissionPayload = null)
    {
        var session = connection.Session;
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var isCompressed = (flags & ProtocolV2FrameFlags.Compressed) != 0;
        var request = ReadRequestEnvelope(
            session, payload, flags, admittedCallState?.Deadline ?? default);
        if (!Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo))
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

        if (_admissionController is not null && !admissionGranted)
        {
            admittedCallState = CreateAdmissionWaitState(
                connection,
                requestId,
                request.RpcDeadline,
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
                    request.RpcDeadline,
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
                return ValueTask.CompletedTask;
            }
            if (!admissionTask.IsCompletedSuccessfully)
            {
                if (!TryCopyAdmissionPayload(payload, flags, out var retainedPayload))
                {
                    admittedCallState.TryCancel(ServerCallCancellationReason.AdmissionResourceExhausted);
                    return new ValueTask(RejectQueuedAdmissionForRetainedBudgetAsync(
                        admissionTask,
                        connection,
                        requestId,
                        requestCancellationMap,
                        admittedCallState,
                        oneWay: true,
                        descriptor.ClientStreamCount).AsTask());
                }

                ReservePreAdmissionRequestStreams(
                    session,
                    requestId,
                    descriptor.ClientStreamCount,
                    admittedCallState);
                return new ValueTask(AwaitOneWayAdmissionAsync(
                    admissionTask,
                    retainedPayload!,
                    connection,
                    requestId,
                    flags,
                    requestCancellationMap,
                    serverLoopToken,
                    descriptor.ClientStreamCount,
                    admittedCallState));
            }

            var decision = admissionTask.Result;
            if (!decision.IsAcquired)
            {
                DrainRejectedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                _ = RejectAdmission(connection.Session, requestId, decision, oneWay: true);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
                return ValueTask.CompletedTask;
            }
            admittedCallState.AttachAdmissionLease(decision.Lease!);
        }

        var admission = TryReserveCall(connection, out var requestPermit);
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
                TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken);
                DrainCompletedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
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

    private async Task AwaitOneWayDispatchAsync(
        ValueTask invokeTask,
        ServerCallCancellationState callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        RpcSession session,
        IRpcStub stub,
        long methodId,
        int clientStreamCount,
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
            DrainCompletedOneWayStreams(session, requestId, clientStreamCount);
            ReleaseOneWayDispatchResources(
                callState,
                requestId,
                requestCancellationMap,
                connection,
                requestPermit);
        }
    }

    private async Task AwaitOneWayAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
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
            var dispatchTask = DispatchOneWayRpc(
                connection,
                requestId,
                flags,
                retainedPayload.Payload,
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                admittedClientStreamCount: clientStreamCount,
                retainedAdmissionPayload: retainedPayload);
            transferred = true;
            if (!dispatchTask.IsCompletedSuccessfully)
                await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            retainedPayload.Dispose();
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

    private async ValueTask AwaitRpcAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerRetainedAdmissionPayload retainedPayload,
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
                    callState: callState,
                    cancellationToken: connection.ConnectionToken).ConfigureAwait(false);
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            var dispatchTask = DispatchRpcAsync(
                connection,
                requestId,
                flags,
                retainedPayload.Payload,
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                retainedAdmissionPayload: retainedPayload);
            transferred = true;
            if ((flags & ProtocolV2FrameFlags.Compressed) != 0)
                retainedPayload.Dispose();
            if (!dispatchTask.IsCompletedSuccessfully)
                await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            retainedPayload.Dispose();
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

    private async ValueTask RejectQueuedAdmissionForRetainedBudgetAsync(
        ValueTask<AdmissionDecision> admissionTask,
        ServerConnectionState connection,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerCallCancellationState callState,
        bool oneWay,
        int clientStreamCount = 0)
    {
        var rejection = CreateRetainedCompressedResourceExhaustion();
        try
        {
            if (oneWay)
            {
                Interlocked.Increment(ref _rejectedOneWayCalls);
                DrainRejectedOneWayStreams(connection.Session, requestId, clientStreamCount);
                LogOnewayRpcResourceExhausted(
                    _logger,
                    SharpLinkResourceExhaustion.ServerRetainedCompressedBytes);
            }
            else
            {
                await connection.Session.SendRpcErrorWithBackpressureAsync(
                    requestId,
                    rejection,
                    connection.ConnectionToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                var decision = await admissionTask.ConfigureAwait(false);
                decision.Lease?.Dispose();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (oneWay)
                    ReleaseAdmissionCallState(requestCancellationMap, requestId, callState);
                else
                    ReleasePendingAdmissionState(
                        connection.Session, requestCancellationMap, requestId, callState);
            }
        }
    }

    private ServerCallCancellationState CreateAdmissionWaitState(
        ServerConnectionState connection,
        long requestId,
        RpcDeadline deadline,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap)
    {
        var callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            _runtimeContext.TimeProvider,
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
        ServerRequestEnvelope request)
        => new(
            descriptor.ContractId,
            descriptor.MethodId,
            descriptor.Kind,
            connection.Session.Id,
            connection.AuthenticationContext,
            request.Metadata);

    private static ValueTask TerminateUnresolvableOneWayRequest(
        RpcSession session,
        long requestId)
    {
        session.NotifyDisconnected(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            $"OneWay request {requestId} could not resolve its service or method registration; closing the connection because its client-stream shape is unknown."));
        return ValueTask.CompletedTask;
    }

    private ValueTask RejectAdmission(
        RpcSession session,
        long requestId,
        AdmissionDecision decision,
        bool oneWay,
        ServerCallCancellationState? callState = null,
        CancellationToken cancellationToken = default)
    {
        if (!oneWay && callState is not null && !callState.TryClaimResponse())
        {
            return session.SendRpcErrorWithBackpressureAsync(
                requestId,
                MapServerCancellationException(callState, callState.Deadline),
                cancellationToken);
        }

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

    private ValueTask PublishAdmissionError(
        RpcSession session,
        long requestId,
        ServerCallCancellationState callState,
        SharpLinkException admissionError,
        CancellationToken cancellationToken)
    {
        var terminalError = callState.TryClaimResponse()
            ? admissionError
            : MapServerCancellationException(callState, callState.Deadline);
        return session.SendRpcErrorWithBackpressureAsync(
            requestId,
            terminalError,
            cancellationToken);
    }

    private bool ShouldLogOneWayAdmissionRejection()
        => _oneWayAdmissionLogThrottle.ShouldLog(
            _runtimeContext.TimeProvider.GetTimestamp(),
            out _);

    private static string GetAdmissionResourceExhaustionReason(string reason)
        => reason switch
        {
            "concurrency" => SharpLinkResourceExhaustion.AdmissionConcurrency,
            "queue_count" or "queue_bytes" => SharpLinkResourceExhaustion.AdmissionQueue,
            "pre_admission_stream_bytes" => SharpLinkResourceExhaustion.ServerPreAdmissionStreamBytes,
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
            ServerCallCancellationReason.PreAdmissionStreamResourceExhausted => AdmissionDecision.Reject(
                "pre_admission_stream_bytes", SharpLinkErrorCode.ResourceExhausted),
            ServerCallCancellationReason.ServerStopping or ServerCallCancellationReason.ModuleDraining =>
                AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable),
            _ => AdmissionDecision.Reject("cancelled", SharpLinkErrorCode.Cancelled)
        };

    private static void ReleasePendingAdmissionState(
        RpcSession session,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId,
        ServerCallCancellationState callState)
    {
        session.StreamManager.CompleteRequestStreams(
            requestId,
            new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "Call ended before stream admission completed."));
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
        ServerConnectionState connection,
        ServerRequestPermit requestPermit)
    {
        _ = connection;
        if (callState is not null)
        {
            requestPermit.TransferDecodedBytesTo(callState);
            requestCancellationMap.TryRemove(requestId, callState);
            callState.Dispose();
        }
        requestPermit.Dispose();
    }

}
