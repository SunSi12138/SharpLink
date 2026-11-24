namespace SharpLink.Runtime;

internal static class ProtocolV2ContractManifestNegotiation
{
    internal static ProtocolV2NegotiationPolicy CreateImplementedPolicy(
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        IReadOnlyList<SharpLinkCompressionProviderBinding> compressionProviders,
        bool enableSessionRefresh = true,
        bool enableNStudyCoordination = false)
    {
        ArgumentNullException.ThrowIfNull(compressionProviders);
        var capabilities = ProtocolV2Negotiator.AlwaysImplementedCapabilities |
                           ProtocolV2Capabilities.ContractManifest;
        if (enableSessionRefresh)
            capabilities |= ProtocolV2Capabilities.SessionRefresh;
        if (enableNStudyCoordination)
            capabilities |= NStudyWire.CoordinationCapability | NStudyWire.TargetDeclarationCapability |
                NStudyWire.OfferAdmissionCapability;
        if (compressionProviders.Count != 0)
            capabilities |= ProtocolV2Capabilities.Compression;
        return ProtocolV2NegotiationPolicy.Create(
            ProtocolV2Constants.MinorVersion,
            capabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes,
            connectionReceiveWindowBytes,
            compressionProviders);
    }
}
