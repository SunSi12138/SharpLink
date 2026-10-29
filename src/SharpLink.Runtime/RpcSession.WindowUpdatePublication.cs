namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    // Only receive-credit accounting uses this trusted typed route. Arbitrary
    // encoded packets continue through preparation and full envelope validation.
    internal void SendWindowUpdateCredit(long requestId, ushort streamId, int credit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credit);
        if (requestId == 0)
            throw new ArgumentOutOfRangeException(nameof(requestId), "WindowUpdate requires a non-zero request ID.");
        if (Volatile.Read(ref _terminal) is { } terminal)
            throw terminal.Exception;

        var protocolState = Volatile.Read(ref _protocolState);
        if (protocolState.Phase is RpcSessionProtocolPhase.Stopping or RpcSessionProtocolPhase.Terminal &&
            Volatile.Read(ref _terminal) is { } phaseTerminal)
        {
            throw phaseTerminal.Exception;
        }
        var maxFramePayloadBytes = protocolState.Options?.MaxFramePayloadBytes ??
            RuntimeContext.Protocol.MaxFramePayloadBytes;
        if (OutgoingWindowUpdate.PayloadBytes > maxFramePayloadBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Outbound frame payload exceeds the negotiated {maxFramePayloadBytes}-byte limit.");
        }
        EnsureOutboundFrameAllowed(protocolState.Phase, ProtocolV2FrameType.WindowUpdate);
        if (protocolState.FlowController is null)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "WindowUpdate cannot be sent without negotiated flow control.");
        }

        // Construction/validation cannot strand a node: acquire the pump first,
        // then transfer the rented node to the pump's existing enqueue ownership.
        var pump = GetOrCreatePump();
        var update = RuntimeContext.WindowUpdatePool.Rent(requestId, streamId, credit);
        var result = pump.TryEnqueue(new OwnedFrame(update));
        if (result == SendEnqueueResult.Full)
        {
            throw SharpLinkResourceExhaustion.Create(
                SharpLinkResourceExhaustion.SendQueueCapacity,
                $"Session send queue exceeded its {RuntimeContext.FlowControl.MaxSendQueueBytes}-byte limit (send_queue_capacity).");
        }
        if (result == SendEnqueueResult.Closed)
            throw GetTerminalException();
    }
}
