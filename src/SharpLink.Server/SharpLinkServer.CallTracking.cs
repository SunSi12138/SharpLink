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
                GetTerminationReasonTag(callState.Reason));
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

        SharpLinkTelemetry.RecordAbandonedCall("server", GetTerminationReasonTag(reason));
        LogRpcCallAbandoned(_logger, reason);
        return false;
    }

    private static SharpLinkException CreateServerCancellationException(
        ServerCallCancellationState? callState,
        long deadlineTimestamp)
        => (callState?.Reason ?? (IsDeadlineExceeded(deadlineTimestamp)
            ? ServerCallCancellationReason.DeadlineExceeded
            : ServerCallCancellationReason.RemoteCancel)) switch
        {
            ServerCallCancellationReason.DeadlineExceeded => new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."),
            ServerCallCancellationReason.ServerStopping => new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "Server is stopping."),
            ServerCallCancellationReason.ModuleDraining => new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC module is draining"),
            ServerCallCancellationReason.ConnectionClosed => new SharpLinkException(
                SharpLinkErrorCode.ConnectionClosed,
                "Connection closed."),
            ServerCallCancellationReason.AdmissionResourceExhausted => new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "Admission queue retained-byte capacity was exhausted."),
            _ => new SharpLinkException(SharpLinkErrorCode.Cancelled, "Request canceled.")
        };

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

    private static ServerCallCancellationReason MapRemoteCancellationReason(
        ProtocolV2CancelReason reason)
        => reason switch
        {
            ProtocolV2CancelReason.DeadlineExceeded => ServerCallCancellationReason.DeadlineExceeded,
            ProtocolV2CancelReason.ConsumerAbandoned => ServerCallCancellationReason.ConsumerAbandoned,
            ProtocolV2CancelReason.Unspecified or
            ProtocolV2CancelReason.UserCancellation => ServerCallCancellationReason.RemoteCancel,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };

    private static string GetTerminationReasonTag(ServerCallCancellationReason reason)
        => reason switch
        {
            ServerCallCancellationReason.RemoteCancel => "remote_cancel",
            ServerCallCancellationReason.ConsumerAbandoned => "consumer_abandoned",
            ServerCallCancellationReason.DeadlineExceeded => "deadline_exceeded",
            ServerCallCancellationReason.ModuleDraining => "module_draining",
            ServerCallCancellationReason.ServerStopping => "server_stopping",
            ServerCallCancellationReason.ConnectionClosed => "connection_closed",
            ServerCallCancellationReason.AdmissionResourceExhausted => "admission_resource_exhausted",
            _ => "unknown"
        };

    private static SharpLinkException CreateRemoteCancellationException(
        ProtocolV2CancelReason reason)
        => reason switch
        {
            ProtocolV2CancelReason.DeadlineExceeded => new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Remote RPC deadline exceeded."),
            ProtocolV2CancelReason.ConsumerAbandoned => new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "Remote consumer abandoned the RPC stream."),
            _ => new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "Remote caller cancelled the RPC stream.")
        };

}
