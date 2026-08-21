namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerCallCancellationState? CreateTrackedCallState(
        ServerConnectionState connection,
        long requestId,
        RpcDeadline deadline,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        bool supportsCooperativeCancellation,
        bool acceptsRemoteCancellation,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        AdmissionLease? admissionLease = null)
    {
        if (!supportsCooperativeCancellation && !moduleDrainingToken.CanBeCanceled && admissionLease is null)
            return null;

        var callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            _runtimeContext.TimeProvider,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation,
            acceptsRemoteCancellation,
            serverStoppingFlowsThroughConnection: true);
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
        RpcDeadline deadline,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        bool acceptsRemoteCancellation,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap)
    {
        if (callState is not null)
            return callState;

        callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            _runtimeContext.TimeProvider,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation: false,
            acceptsRemoteCancellation,
            serverStoppingFlowsThroughConnection: true);
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
                ServerCallTerminationMapper.GetTerminationReasonTag(callState.Reason));
            LogRpcCallAbandoned(_logger, callState.Reason);
        }
        return false;
    }

    private bool TryClaimCallCompletion(
        ServerCallCancellationState? callState,
        RpcDeadline deadline,
        CancellationToken serverLoopToken)
    {
        if (callState is not null)
            return TryClaimCallCompletion(callState);

        var reason = IsDeadlineExceeded(deadline)
            ? ServerCallCancellationReason.DeadlineExceeded
            : serverLoopToken.IsCancellationRequested
                ? ServerCallCancellationReason.ConnectionClosed
                : ServerCallCancellationReason.None;
        if (reason == ServerCallCancellationReason.None)
            return true;

        SharpLinkTelemetry.RecordAbandonedCall(
            "server",
            ServerCallTerminationMapper.GetTerminationReasonTag(reason));
        LogRpcCallAbandoned(_logger, reason);
        return false;
    }

    private SharpLinkException MapServerCancellationException(
        ServerCallCancellationState? callState,
        RpcDeadline deadline)
        => ServerCallTerminationMapper.CreateServerCancellationException(
            callState?.Reason,
            callState is null && IsDeadlineExceeded(deadline));

    private static ValueTask TrySendModuleDrainError(
        ServerCallCancellationState? callState,
        RpcSession session,
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

}
