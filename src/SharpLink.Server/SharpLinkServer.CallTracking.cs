namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
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
                ServerCallTerminationMapper.GetTerminationReasonTag(callState.Reason));
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

        SharpLinkTelemetry.RecordAbandonedCall(
            "server",
            ServerCallTerminationMapper.GetTerminationReasonTag(reason));
        LogRpcCallAbandoned(_logger, reason);
        return false;
    }

    private static SharpLinkException MapServerCancellationException(
        ServerCallCancellationState? callState,
        long deadlineTimestamp)
        => ServerCallTerminationMapper.CreateServerCancellationException(
            callState?.Reason,
            callState is null && IsDeadlineExceeded(deadlineTimestamp));

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

}
