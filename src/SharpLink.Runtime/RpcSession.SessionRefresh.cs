namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal event Action<ProtocolV2SessionRefreshRequested>? SessionRefreshRequested;

    internal bool SupportsSessionRefreshReplacement
        => _transport is not AnonymousPipeTransportConnection;

    private void HandleSessionRefreshRequested(
        in ProtocolV2FrameHeader header,
        ReadOnlySequence<byte> payload)
    {
        EnsureInboundFrameAllowed(header.Type);
        if (Role != RpcSessionRole.Client)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "SessionRefreshRequested is valid only from a server to a client.");
        }
        if ((NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "SessionRefreshRequested requires the negotiated SessionRefresh capability.");
        }
        if (header.RequestId != 0 || header.Flags != ProtocolV2FrameFlags.None)
            throw ProtocolV2FrameParser.Violation("SessionRefreshRequested must be an unflagged connection-level frame.");

        var request = ProtocolV2PayloadCodec.ReadSessionRefreshRequested(payload);
        // This frame is consumed inside TryReadInboundFrame rather than returned to the
        // receive loop, so account for it here exactly once before notifying its owner.
        SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
        MarkActive();
        SessionRefreshRequested?.Invoke(request);
    }
}
