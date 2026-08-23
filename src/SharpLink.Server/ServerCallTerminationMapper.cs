namespace SharpLink.Server;

internal static class ServerCallTerminationMapper
{
    internal static ServerCallCancellationReason MapRemoteCancellationReason(
        ProtocolV2CancelReason reason)
        => reason switch
        {
            ProtocolV2CancelReason.DeadlineExceeded => ServerCallCancellationReason.DeadlineExceeded,
            ProtocolV2CancelReason.ConsumerAbandoned => ServerCallCancellationReason.ConsumerAbandoned,
            ProtocolV2CancelReason.Unspecified or
            ProtocolV2CancelReason.UserCancellation => ServerCallCancellationReason.RemoteCancel,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };

    internal static string GetTerminationReasonTag(ServerCallCancellationReason reason)
        => reason switch
        {
            ServerCallCancellationReason.RemoteCancel => "remote_cancel",
            ServerCallCancellationReason.ConsumerAbandoned => "consumer_abandoned",
            ServerCallCancellationReason.DeadlineExceeded => "deadline_exceeded",
            ServerCallCancellationReason.ModuleDraining => "module_draining",
            ServerCallCancellationReason.ServerStopping => "server_stopping",
            ServerCallCancellationReason.ConnectionClosed => "connection_closed",
            ServerCallCancellationReason.AdmissionResourceExhausted => "admission_resource_exhausted",
            ServerCallCancellationReason.PreAdmissionStreamResourceExhausted =>
                "pre_admission_stream_resource_exhausted",
            _ => "unknown"
        };

    internal static SharpLinkException CreateRemoteCancellationException(
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

    internal static SharpLinkException CreateServerCancellationException(
        ServerCallCancellationReason? reason,
        bool deadlineExceeded)
        => (reason ?? (deadlineExceeded
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
            ServerCallCancellationReason.PreAdmissionStreamResourceExhausted =>
                SharpLinkResourceExhaustion.CreateWire(
                    SharpLinkResourceExhaustion.ServerPreAdmissionStreamBytes,
                    "Pre-admission stream retained-byte capacity was exhausted."),
            _ => new SharpLinkException(SharpLinkErrorCode.Cancelled, "Request canceled.")
        };
}
