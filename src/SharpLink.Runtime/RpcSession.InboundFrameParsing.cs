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

        // Before negotiation completes, parsing uses the endpoint-local configured frame limit.
        // Once immutable negotiated options are published, every subsequent inbound frame uses the
        // Session limit while local metadata/error safety bounds remain unchanged.
        var negotiated = NegotiatedOptions;
        return negotiated is null
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
    }
}
