namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2ErrorCompatibilityTests
{
    [Test]
    public async Task StructuredErrorShapeShouldRejectPublishedMinorThreePeers()
    {
        Ensure(ProtocolV2Constants.MinorVersion == 1, "Protocol3 uses the fixed revision 1");
        Ensure(
            ProtocolV2Constants.MinimumCompatibleMinorVersion == 1,
            "published minor-3 peers must be rejected before decoding the structured error shape");

        var authority = new Protocol3BindingAuthority(
            new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), Protocol3BindingLimits.Default);
        var policy = ProtocolV2NegotiationPolicy.Create(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Negotiator.AlwaysImplementedCapabilities,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>(),
            bindingLimits: authority.Limits,
            serverInstanceId: authority.ServerInstanceId,
            registryEpoch: authority.RegistryEpoch);
        var currentOffer = ProtocolV2Negotiator.CreateClientOffer(
            policy,
            ProtocolV2Capabilities.None,
            ReadOnlyMemory<byte>.Empty);
        var currentResponse = new ProtocolV2HandshakeResponse(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Negotiator.AlwaysImplementedCapabilities,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Authority: authority);
        Ensure(ProtocolV2Negotiator.NegotiateServer(currentOffer, policy).Response == currentResponse,
            "the current-revision server baseline must be valid");
        _ = ProtocolV2Negotiator.ValidateServerResponse(currentOffer, currentResponse, policy);

        const ushort legacyMinor = 3;
        var legacyOffer = currentOffer with { MinorVersion = legacyMinor };

        var serverFailure = Capture(() => ProtocolV2Negotiator.NegotiateServer(legacyOffer, policy));
        await Assert.That(serverFailure).IsAssignableTo<SharpLinkException>();
        await Assert.That((serverFailure as SharpLinkException)?.Code)
            .IsEqualTo(SharpLinkErrorCode.Unimplemented);

        var legacyResponse = currentResponse with { MinorVersion = legacyMinor };

        var clientFailure = Capture(() =>
            ProtocolV2Negotiator.ValidateServerResponse(currentOffer, legacyResponse, policy));
        await Assert.That(clientFailure).IsAssignableTo<SharpLinkException>();
        await Assert.That((clientFailure as SharpLinkException)?.Code)
            .IsEqualTo(SharpLinkErrorCode.Unimplemented);
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
