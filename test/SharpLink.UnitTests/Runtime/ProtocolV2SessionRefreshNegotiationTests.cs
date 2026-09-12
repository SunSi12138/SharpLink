namespace SharpLink.UnitTests.Runtime;

public sealed class ProtocolV2SessionRefreshNegotiationTests
{
    [Test]
    public void ImplementedPolicyShouldAdvertiseSessionRefreshWhenEnabled()
    {
        var policy = ProtocolV2ContractManifestNegotiation.CreateImplementedPolicy(
            SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
            64 * 1024,
            256 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>(),
            enableSessionRefresh: true);

        Ensure((policy.SupportedCapabilities & ProtocolV2Capabilities.SessionRefresh) != 0,
            "enabled session refresh should be advertised");
    }

    [Test]
    public void ImplementedPolicyShouldOmitSessionRefreshWhenReplacementIsUnsupported()
    {
        var policy = ProtocolV2ContractManifestNegotiation.CreateImplementedPolicy(
            SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
            64 * 1024,
            256 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>(),
            enableSessionRefresh: false);

        Ensure((policy.SupportedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0,
            "one-shot transports should safely degrade to future-only convergence");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
