namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
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
        if (IsDeadlineExceeded(request.RpcDeadline))
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
                if (callContext is SharpLinkServerInvocationContext
                    {
                        Status: SharpLinkInvocationStatus.Pending
                    } interceptorContext)
                    interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
                TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken);
                ReleaseOneWayDispatchResources(callState, requestId, requestCancellationMap, connection);
                return;
            }

            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.RpcDeadline,
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
            if (TryClaimCallCompletion(callState, request.RpcDeadline, serverLoopToken))
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
            request.Metadata,
            request.Deadline);

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
        var timeProvider = _runtimeContext.TimeProvider;
        var now = timeProvider.GetTimestamp();
        while (true)
        {
            var initialization = Volatile.Read(ref _oneWayAdmissionLogInitialized);
            if (initialization != 2)
            {
                if (initialization == 0 &&
                    Interlocked.CompareExchange(
                        ref _oneWayAdmissionLogInitialized,
                        1,
                        0) == 0)
                {
                    Volatile.Write(ref _oneWayAdmissionLogTimestamp, now);
                    Volatile.Write(ref _oneWayAdmissionLogInitialized, 2);
                    return true;
                }
                return false;
            }

            var previous = Volatile.Read(ref _oneWayAdmissionLogTimestamp);
            if (timeProvider.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(5))
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

}
