namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal bool TryReadInboundFrame(
        ref ReadOnlySequence<byte> buffer,
        SharpLinkProtocolOptions localLimits,
        out ProtocolV2FrameHeader header,
        out ReadOnlySequence<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(localLimits);

        while (true)
        {
            // Before negotiation completes, parsing uses the endpoint-local configured frame limit.
            // Once immutable negotiated options are published, every subsequent inbound frame uses the
            // Session limit while local metadata/error safety bounds remain unchanged.
            var negotiated = NegotiatedOptions;
            var read = negotiated is null
                ? ProtocolV2FrameParser.TryReadFrame(
                    ref buffer,
                    localLimits,
                    out header,
                    out payload)
                : ProtocolV2FrameParser.TryReadFrame(
                    ref buffer,
                    localLimits,
                    negotiated.MaxFramePayloadBytes,
                    out header,
                    out payload);
            if (!read)
                return false;
            if (header.Type != ProtocolV2FrameType.SessionRefreshRequested)
                return true;

            // Session refresh is a connection-level administrative signal rather than an RPC
            // response. Dispatch it at the session boundary so every client topology shares the
            // same wire validation while its owner decides how to perform replacement.
            HandleSessionRefreshRequested(header, payload);
        }
    }
}
